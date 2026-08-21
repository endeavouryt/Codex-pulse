using System.Text.Json;
using CodexPulse.Models;

namespace CodexPulse.Services;

internal sealed class AppServerProvider
{
    private readonly AppServerClient _client;
    private readonly object _stateGate = new();
    private readonly Dictionary<string, ThreadRuntimeState> _threadStates = new(StringComparer.OrdinalIgnoreCase);

    public AppServerProvider(AppServerClient client)
    {
        _client = client;
        _client.NotificationReceived += OnNotificationReceived;
    }

    public async Task<ProviderObservation> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await _client.EnsureStartedAsync(cancellationToken).ConfigureAwait(false))
            {
                return Missing(_client.LastError ?? "app-server 不可用");
            }

            var rateLimitsTask = TryRequestAsync("account/rateLimits/read", null, cancellationToken);
            var usageTask = TryRequestAsync("account/usage/read", null, cancellationToken);
            var threadsTask = TryRequestAsync(
                "thread/list",
                new
                {
                    limit = 100,
                    sortKey = "updated_at",
                    sortDirection = "desc",
                    useStateDbOnly = true
                },
                cancellationToken);

            await Task.WhenAll(rateLimitsTask, usageTask, threadsTask).ConfigureAwait(false);

            var rateLimits = await rateLimitsTask.ConfigureAwait(false);
            var threads = await threadsTask.ConfigureAwait(false);
            var threadGraph = await ReadThreadGraphAsync(threads, cancellationToken).ConfigureAwait(false);
            var sessions = ReadSessions(threadGraph, out var status, out var statusKnown, out var statusAt, out var statusDetail);

            var quota = rateLimits.HasValue ? ReadQuotaRemaining(rateLimits.Value) : null;
            var context = sessions.Count == 1 ? sessions[0].ContextRemainingPercent : null;

            var details = new List<string> { "app-server" };
            if (rateLimits is null && usageTask.Result is null)
            {
                details.Add("account 数据不可用");
            }

            if (threads is null)
            {
                details.Add("thread 数据不可用");
            }

            if (!string.IsNullOrWhiteSpace(statusDetail))
            {
                details.Add(statusDetail!);
            }

            return new ProviderObservation
            {
                ProviderAvailable = true,
                ContextRemainingPercent = context,
                QuotaRemainingPercent = quota,
                Status = status,
                StatusKnown = statusKnown,
                StatusAt = statusAt,
                LastUpdatedAt = sessions.Count > 0
                    ? sessions.Max(item => item.LastActivityAt)
                    : DateTimeOffset.Now,
                SourceName = "app-server",
                Detail = string.Join("；", details),
                Sessions = sessions
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Missing(ex.Message);
        }
    }

    private async Task<JsonElement?> TryRequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnNotificationReceived(JsonElement message)
    {
        var method = JsonHelpers.TryGetString(message, "method");
        if (!JsonHelpers.TryGetProperty(message, out var parameters, "params") ||
            parameters.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (string.Equals(method, "thread/started", StringComparison.Ordinal) &&
            JsonHelpers.TryGetProperty(parameters, out var startedThread, "thread") &&
            startedThread.ValueKind == JsonValueKind.Object)
        {
            ApplyThreadSnapshot(startedThread);
        }

        var threadId = ReadThreadId(parameters);
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        if (string.Equals(method, "thread/tokenUsage/updated", StringComparison.Ordinal))
        {
            if (!JsonHelpers.TryGetProperty(parameters, out var tokenUsage, "tokenUsage", "token_usage"))
            {
                return;
            }

            var context = JsonHelpers.ReadContextRemaining(tokenUsage);
            lock (_stateGate)
            {
                var state = GetOrCreateState(threadId);
                state.ContextRemainingPercent = context ?? state.ContextRemainingPercent;
                state.LastActivityAt = DateTimeOffset.Now;
            }

            return;
        }

        var statusType = ReadStatusType(parameters);
        if (JsonHelpers.TryGetProperty(parameters, out var thread, "thread") &&
            thread.ValueKind == JsonValueKind.Object)
        {
            statusType = ReadStatusType(thread) ?? statusType;
        }

        if (string.Equals(method, "turn/started", StringComparison.Ordinal) ||
            string.Equals(method, "task/started", StringComparison.Ordinal))
        {
            statusType = "active";
        }
        else if (string.Equals(method, "turn/completed", StringComparison.Ordinal) ||
                 string.Equals(method, "task/completed", StringComparison.Ordinal))
        {
            statusType = "idle";
        }

        if (string.IsNullOrWhiteSpace(statusType))
        {
            return;
        }

        var pulseStatus = string.Equals(statusType, "active", StringComparison.OrdinalIgnoreCase)
            ? PulseStatus.Working
            : PulseStatus.Idle;
        lock (_stateGate)
        {
            var state = GetOrCreateState(threadId);
            var now = DateTimeOffset.Now;
            if (pulseStatus == PulseStatus.Working && state.Status != PulseStatus.Working)
            {
                state.WorkStartedAt = now;
            }

            state.Status = pulseStatus;
            state.StatusAt = now;
            state.LastActivityAt = now;
        }
    }

    private void ApplyThreadSnapshot(JsonElement thread)
    {
        var threadId = ReadThreadId(thread);
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        var parentThreadId = ReadParentThreadId(thread);
        var createdAt = JsonHelpers.TryGetTimestamp(thread, "createdAt", "created_at");
        var updatedAt = JsonHelpers.TryGetTimestamp(thread, "updatedAt", "updated_at") ?? DateTimeOffset.Now;
        var statusType = ReadStatusType(thread);

        lock (_stateGate)
        {
            var state = GetOrCreateState(threadId);
            state.ParentSessionId = parentThreadId;
            state.ParentKnown = true;
            state.CreatedAt = createdAt ?? state.CreatedAt;

            if (!string.IsNullOrWhiteSpace(statusType))
            {
                ApplyStatus(state, IsActiveStatus(statusType), updatedAt);
            }

            state.LastActivityAt = Max(state.LastActivityAt, updatedAt);
            state.StatusAt ??= updatedAt;
        }
    }

    private static void ApplyStatus(ThreadRuntimeState state, bool working, DateTimeOffset timestamp)
    {
        var pulseStatus = working ? PulseStatus.Working : PulseStatus.Idle;
        if (pulseStatus == PulseStatus.Working && state.Status != PulseStatus.Working)
        {
            state.WorkStartedAt = timestamp;
        }

        if (state.Status != pulseStatus)
        {
            state.StatusAt = timestamp;
        }

        state.Status = pulseStatus;
        state.StatusAt ??= timestamp;
    }

    private static bool IsActiveStatus(string statusType)
    {
        return string.Equals(statusType, "active", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(statusType, "inProgress", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(statusType, "in_progress", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<ThreadRecord>> ReadThreadGraphAsync(
        JsonElement? threadResult,
        CancellationToken cancellationToken)
    {
        var entries = await ReadThreadListPagesAsync(threadResult, null, cancellationToken).ConfigureAwait(false);
        var records = new Dictionary<string, ThreadRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var record = ReadThreadRecord(entry);
            if (record is not null)
            {
                records[record.ThreadId] = record;
            }
        }

        var rootIds = records.Values
            .Where(item => item.ParentKnown && string.IsNullOrWhiteSpace(item.ParentSessionId))
            .Select(item => item.ThreadId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var descendantPages = await Task.WhenAll(rootIds.Select(rootId =>
            ReadThreadListPagesAsync(null, rootId, cancellationToken))).ConfigureAwait(false);
        foreach (var page in descendantPages)
        {
            foreach (var entry in page)
            {
                var record = ReadThreadRecord(entry);
                if (record is not null)
                {
                    records[record.ThreadId] = record;
                }
            }
        }

        lock (_stateGate)
        {
            foreach (var pair in _threadStates)
            {
                if (records.ContainsKey(pair.Key) ||
                    !pair.Value.ParentKnown ||
                    pair.Value.LastActivityAt == DateTimeOffset.MinValue)
                {
                    continue;
                }

                records[pair.Key] = ToThreadRecord(pair.Key, pair.Value, pair.Value.LastActivityAt);
            }
        }

        return records.Values.ToArray();
    }

    private async Task<IReadOnlyList<JsonElement>> ReadThreadListPagesAsync(
        JsonElement? firstResult,
        string? ancestorThreadId,
        CancellationToken cancellationToken)
    {
        var entries = new List<JsonElement>();
        var page = firstResult;
        if (!page.HasValue)
        {
            if (string.IsNullOrWhiteSpace(ancestorThreadId))
            {
                return entries;
            }

            page = await TryRequestAsync(
                "thread/list",
                new Dictionary<string, object?>
                {
                    ["ancestorThreadId"] = ancestorThreadId,
                    ["limit"] = 100
                },
                cancellationToken).ConfigureAwait(false);
        }

        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        while (page.HasValue)
        {
            entries.AddRange(ReadThreadEntries(page.Value));
            var nextCursor = ReadNextCursor(page.Value);
            if (string.IsNullOrWhiteSpace(nextCursor) || !seenCursors.Add(nextCursor))
            {
                break;
            }

            var parameters = new Dictionary<string, object?>
            {
                ["cursor"] = nextCursor,
                ["limit"] = 100
            };
            if (!string.IsNullOrWhiteSpace(ancestorThreadId))
            {
                parameters["ancestorThreadId"] = ancestorThreadId;
            }
            else
            {
                parameters["sortKey"] = "updated_at";
                parameters["sortDirection"] = "desc";
                parameters["useStateDbOnly"] = true;
            }

            page = await TryRequestAsync("thread/list", parameters, cancellationToken).ConfigureAwait(false);
        }

        return entries;
    }

    private static IReadOnlyList<JsonElement> ReadThreadEntries(JsonElement result)
    {
        return result.ValueKind == JsonValueKind.Object &&
               JsonHelpers.TryGetProperty(result, out var data, "data") &&
               data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
    }

    private static string? ReadNextCursor(JsonElement result)
    {
        return result.ValueKind == JsonValueKind.Object
            ? JsonHelpers.TryGetString(result, "nextCursor", "next_cursor")
            : null;
    }

    private ThreadRecord? ReadThreadRecord(JsonElement thread)
    {
        var threadId = ReadThreadId(thread);
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        var parentThreadId = ReadParentThreadId(thread);
        var createdAt = JsonHelpers.TryGetTimestamp(thread, "createdAt", "created_at");
        var updatedAt = JsonHelpers.TryGetTimestamp(thread, "updatedAt", "updated_at") ?? DateTimeOffset.Now;
        var statusType = ReadStatusType(thread);

        lock (_stateGate)
        {
            var runtime = GetOrCreateState(threadId);
            runtime.ParentSessionId = parentThreadId;
            runtime.ParentKnown = true;
            runtime.CreatedAt = createdAt ?? runtime.CreatedAt;
            if (!string.IsNullOrWhiteSpace(statusType))
            {
                ApplyStatus(runtime, IsActiveStatus(statusType), updatedAt);
            }

            runtime.LastActivityAt = Max(runtime.LastActivityAt, updatedAt);
            runtime.StatusAt ??= updatedAt;
            return ToThreadRecord(threadId, runtime, updatedAt);
        }
    }

    private IReadOnlyList<SessionObservation> ReadSessions(
        IReadOnlyList<ThreadRecord> threadGraph,
        out PulseStatus status,
        out bool known,
        out DateTimeOffset? statusAt,
        out string? detail)
    {
        var observations = threadGraph
            .Select(record => ToObservation(record.ThreadId, record.RuntimeState, record.LastActivityAt))
            .ToArray();
        var roots = SessionHierarchy.CollapseToRootSessions(observations);
        var active = roots
            .Where(item => item.Status == PulseStatus.Working)
            .OrderByDescending(item => item.WorkStartedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        status = active?.Status ?? PulseStatus.Idle;
        statusAt = active?.StatusAt;
        known = threadGraph.Count > 0;
        detail = threadGraph.Count == 0
            ? "thread 数据不可用"
            : roots.Count == 0
                ? "未找到 root thread"
                : active is not null ? "检测到 root thread 工作中" : "root thread 当前未工作";

        return roots;
    }

    private static ThreadRecord ToThreadRecord(string threadId, ThreadRuntimeState state, DateTimeOffset fallbackActivityAt)
    {
        return new ThreadRecord
        {
            ThreadId = threadId,
            ParentSessionId = state.ParentSessionId,
            ParentKnown = state.ParentKnown,
            CreatedAt = state.CreatedAt,
            Status = state.Status,
            StatusAt = state.StatusAt,
            LastActivityAt = state.LastActivityAt == DateTimeOffset.MinValue
                ? fallbackActivityAt
                : state.LastActivityAt,
            WorkStartedAt = state.WorkStartedAt,
            ContextRemainingPercent = state.ContextRemainingPercent,
            RuntimeState = state
        };
    }

    private static SessionObservation ToObservation(string threadId, ThreadRuntimeState state, DateTimeOffset fallbackActivityAt)
    {
        var activityAt = state.LastActivityAt == DateTimeOffset.MinValue
            ? fallbackActivityAt
            : state.LastActivityAt;
        return new SessionObservation
        {
            SessionId = threadId,
            ParentSessionId = state.ParentSessionId,
            CreatedAt = state.CreatedAt,
            Status = state.Status,
            StatusAt = state.StatusAt ?? activityAt,
            LastActivityAt = activityAt,
            WorkStartedAt = state.WorkStartedAt,
            ContextRemainingPercent = state.ContextRemainingPercent,
            SourceName = "app-server",
            Detail = "app-server thread"
        };
    }

    private static string? ReadThreadId(JsonElement element)
    {
        var id = JsonHelpers.TryGetString(element, "threadId", "thread_id", "id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            return id;
        }

        if (JsonHelpers.TryGetProperty(element, out var thread, "thread") && thread.ValueKind == JsonValueKind.Object)
        {
            id = JsonHelpers.TryGetString(thread, "id", "threadId", "thread_id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        if (JsonHelpers.TryGetProperty(element, out var turn, "turn") && turn.ValueKind == JsonValueKind.Object)
        {
            return JsonHelpers.TryGetString(turn, "threadId", "thread_id");
        }

        return null;
    }

    private static string? ReadParentThreadId(JsonElement element)
    {
        if (JsonHelpers.TryGetProperty(element, out var thread, "thread") && thread.ValueKind == JsonValueKind.Object)
        {
            return JsonHelpers.TryGetString(thread, "parentThreadId", "parent_thread_id");
        }

        return JsonHelpers.TryGetString(element, "parentThreadId", "parent_thread_id");
    }

    private static string? ReadStatusType(JsonElement element)
    {
        if (JsonHelpers.TryGetProperty(element, out var status, "status"))
        {
            if (status.ValueKind == JsonValueKind.Object)
            {
                return JsonHelpers.TryGetString(status, "type");
            }

            if (status.ValueKind == JsonValueKind.String)
            {
                return status.GetString();
            }
        }

        return JsonHelpers.TryGetString(element, "statusType", "status_type", "type");
    }

    private ThreadRuntimeState GetOrCreateState(string threadId)
    {
        if (!_threadStates.TryGetValue(threadId, out var state))
        {
            state = new ThreadRuntimeState();
            _threadStates[threadId] = state;
        }

        return state;
    }

    private static double? ReadQuotaRemaining(JsonElement result)
    {
        if (JsonHelpers.TryGetProperty(result, out var byLimitId, "rateLimitsByLimitId", "rate_limits_by_limit_id") &&
            byLimitId.ValueKind == JsonValueKind.Object)
        {
            if (byLimitId.TryGetProperty("codex", out var codexSnapshot))
            {
                var remaining = JsonHelpers.ReadRemainingPercent(codexSnapshot);
                if (remaining.HasValue)
                {
                    return remaining;
                }
            }

            foreach (var property in byLimitId.EnumerateObject())
            {
                var remaining = JsonHelpers.ReadRemainingPercent(property.Value);
                if (remaining.HasValue)
                {
                    return remaining;
                }
            }
        }

        return JsonHelpers.TryGetProperty(result, out var snapshot, "rateLimits", "rate_limits")
            ? JsonHelpers.ReadRemainingPercent(snapshot)
            : null;
    }

    private static ProviderObservation Missing(string detail)
    {
        return new ProviderObservation
        {
            ProviderAvailable = false,
            SourceName = "app-server",
            Detail = detail
        };
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second)
    {
        return first > second ? first : second;
    }

    private sealed class ThreadRuntimeState
    {
        public string? ParentSessionId { get; set; }
        public bool ParentKnown { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
        public PulseStatus Status { get; set; } = PulseStatus.Idle;
        public DateTimeOffset? StatusAt { get; set; }
        public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset? WorkStartedAt { get; set; }
        public double? ContextRemainingPercent { get; set; }
    }

    private sealed class ThreadRecord
    {
        public string ThreadId { get; init; } = string.Empty;
        public string? ParentSessionId { get; init; }
        public bool ParentKnown { get; init; }
        public DateTimeOffset? CreatedAt { get; init; }
        public PulseStatus Status { get; init; }
        public DateTimeOffset? StatusAt { get; init; }
        public DateTimeOffset LastActivityAt { get; init; }
        public DateTimeOffset? WorkStartedAt { get; init; }
        public double? ContextRemainingPercent { get; init; }
        public ThreadRuntimeState RuntimeState { get; init; } = new();
    }
}
