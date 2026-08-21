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
                    limit = 20,
                    sortKey = "updated_at",
                    sortDirection = "desc",
                    useStateDbOnly = true
                },
                cancellationToken);

            await Task.WhenAll(rateLimitsTask, usageTask, threadsTask).ConfigureAwait(false);

            var rateLimits = await rateLimitsTask.ConfigureAwait(false);
            var threads = await threadsTask.ConfigureAwait(false);
            var sessions = ReadSessions(threads, out var status, out var statusKnown, out var statusAt, out var statusDetail);

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

        var statusType = JsonHelpers.TryGetString(parameters, "statusType", "status_type");
        if (JsonHelpers.TryGetProperty(parameters, out var status, "status") && status.ValueKind == JsonValueKind.Object)
        {
            statusType = JsonHelpers.TryGetString(status, "type") ?? statusType;
        }
        else
        {
            statusType ??= JsonHelpers.TryGetString(parameters, "status");
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

    private IReadOnlyList<SessionObservation> ReadSessions(
        JsonElement? threadResult,
        out PulseStatus status,
        out bool known,
        out DateTimeOffset? statusAt,
        out string? detail)
    {
        var sessions = new Dictionary<string, SessionObservation>(StringComparer.OrdinalIgnoreCase);
        var data = default(JsonElement);
        var hasThreadArray = threadResult.HasValue &&
                             threadResult.Value.ValueKind == JsonValueKind.Object &&
                             JsonHelpers.TryGetProperty(threadResult.Value, out data, "data") &&
                             data.ValueKind == JsonValueKind.Array;

        var hasAnyThread = false;
        if (hasThreadArray)
        {
            foreach (var thread in data.EnumerateArray())
            {
                var threadId = ReadThreadId(thread);
                if (string.IsNullOrWhiteSpace(threadId))
                {
                    continue;
                }

                hasAnyThread = true;
                var updatedAt = JsonHelpers.TryGetTimestamp(thread, "updatedAt", "updated_at") ?? DateTimeOffset.Now;
                var statusType = JsonHelpers.TryGetProperty(thread, out var statusElement, "status")
                    ? JsonHelpers.TryGetString(statusElement, "type")
                    : null;
                var pulseStatus = string.Equals(statusType, "active", StringComparison.OrdinalIgnoreCase)
                    ? PulseStatus.Working
                    : PulseStatus.Idle;

                lock (_stateGate)
                {
                    var runtime = GetOrCreateState(threadId);
                    if (runtime.Status != pulseStatus)
                    {
                        runtime.StatusAt = updatedAt;
                        if (pulseStatus == PulseStatus.Working)
                        {
                            runtime.WorkStartedAt = updatedAt;
                        }
                    }

                    runtime.Status = pulseStatus;
                    runtime.LastActivityAt = Max(runtime.LastActivityAt, updatedAt);
                    runtime.StatusAt ??= updatedAt;
                    sessions[threadId] = ToObservation(threadId, runtime, updatedAt);
                }
            }
        }

        lock (_stateGate)
        {
            foreach (var pair in _threadStates)
            {
                if (sessions.ContainsKey(pair.Key) || pair.Value.LastActivityAt == DateTimeOffset.MinValue)
                {
                    continue;
                }

                sessions[pair.Key] = ToObservation(pair.Key, pair.Value, pair.Value.LastActivityAt);
            }
        }

        var ordered = sessions.Values
            .OrderByDescending(item => item.WorkStartedAt ?? item.LastActivityAt)
            .ToArray();
        var active = ordered
            .Where(item => item.Status == PulseStatus.Working)
            .OrderByDescending(item => item.WorkStartedAt ?? item.LastActivityAt)
            .FirstOrDefault();

        status = active?.Status ?? PulseStatus.Idle;
        statusAt = active?.StatusAt;
        known = hasThreadArray && hasAnyThread || ordered.Length > 0;
        detail = !hasThreadArray
            ? "thread 数据不可用"
            : ordered.Length == 0
                ? "未找到线程"
                : active is not null ? "检测到 active thread" : "线程当前未 active";

        return ordered;
    }

    private static SessionObservation ToObservation(string threadId, ThreadRuntimeState state, DateTimeOffset fallbackActivityAt)
    {
        var activityAt = state.LastActivityAt == DateTimeOffset.MinValue
            ? fallbackActivityAt
            : state.LastActivityAt;
        return new SessionObservation
        {
            SessionId = threadId,
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
            return JsonHelpers.TryGetString(thread, "id", "threadId", "thread_id");
        }

        return null;
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
        public PulseStatus Status { get; set; } = PulseStatus.Idle;
        public DateTimeOffset? StatusAt { get; set; }
        public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset? WorkStartedAt { get; set; }
        public double? ContextRemainingPercent { get; set; }
    }
}
