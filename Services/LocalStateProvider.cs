using System.IO;
using System.Text;
using System.Text.Json;
using CodexPulse.Models;

namespace CodexPulse.Services;

internal sealed class LocalStateProvider
{
    private const int MaxTailBytes = 1024 * 1024;
    private const int MaxTaskEventProbeBytes = 8 * 1024 * 1024;
    private const int MetadataProbeBytes = 64 * 1024;
    private const int MaxPreferredSessionFiles = 64;
    private static readonly TimeSpan CompletedDisplayWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StaleActiveWindow = TimeSpan.FromMinutes(30);
    private readonly Func<IEnumerable<string>> _codexHomeProvider;

    public LocalStateProvider(Func<IEnumerable<string>> codexHomeProvider)
    {
        _codexHomeProvider = codexHomeProvider;
    }

    public Task<ProviderObservation> ReadAsync(
        CancellationToken cancellationToken,
        string? preferredSessionId = null)
    {
        return Task.Run(
            () => ReadSynchronously(cancellationToken, preferredSessionId),
            cancellationToken);
    }

    private ProviderObservation ReadSynchronously(
        CancellationToken cancellationToken,
        string? preferredSessionId)
    {
        var files = new List<FileInfo>();
        var preferredFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var home in _codexHomeProvider().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionsDirectory = Path.Combine(home, "sessions");
            if (!Directory.Exists(sessionsDirectory))
            {
                continue;
            }

            try
            {
                files.AddRange(new DirectoryInfo(sessionsDirectory)
                    .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Take(24));

                if (IsSessionId(preferredSessionId))
                {
                    foreach (var file in ReadPreferredSessionGraphFiles(
                                 sessionsDirectory,
                                 preferredSessionId!,
                                 cancellationToken))
                    {
                        files.Add(file);
                        preferredFilePaths.Add(file.FullName);
                    }
                }
            }
            catch (IOException)
            {
                // A session directory may be mid-update or unavailable.
            }
            catch (UnauthorizedAccessException)
            {
                // Continue with the next possible Codex home.
            }
        }

        var observations = new List<LocalFileObservation>();
        var uniqueFiles = files
            .GroupBy(item => item.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var prioritizedFiles = uniqueFiles
            .Where(file => preferredFilePaths.Contains(file.FullName) ||
                           string.Equals(
                               ExtractSessionId(file.Name),
                               preferredSessionId,
                               StringComparison.OrdinalIgnoreCase))
            .Concat(uniqueFiles
                .Where(file => !preferredFilePaths.Contains(file.FullName) &&
                               !string.Equals(
                                   ExtractSessionId(file.Name),
                                   preferredSessionId,
                                   StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.LastWriteTimeUtc))
            .Take(IsSessionId(preferredSessionId) ? MaxPreferredSessionFiles : 32);

        foreach (var file in prioritizedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = ReadFile(file, cancellationToken);
            if (observation is not null)
            {
                observations.Add(observation);
            }
        }

        var sessionObservations = CollapseDuplicateSessions(observations);
        if (sessionObservations.Count == 0)
        {
            return new ProviderObservation
            {
                ProviderAvailable = false,
                SourceName = "local session",
                Detail = "未找到可读取的 Codex session JSONL。"
            };
        }

        var active = sessionObservations
            .Where(item => item.Status == PulseStatus.Working)
            .OrderByDescending(item => item.LastActivityAt)
            .FirstOrDefault();
        var recentCompleted = sessionObservations
            .Where(item => item.Status == PulseStatus.Completed)
            .OrderByDescending(item => item.StatusAt ?? item.LastActivityAt)
            .FirstOrDefault();
        var metricObservation = (active is not null &&
                                 (active.ContextRemainingPercent.HasValue || active.QuotaRemainingPercent.HasValue)
                ? active
                : sessionObservations
                .Where(item => item.ContextRemainingPercent.HasValue || item.QuotaRemainingPercent.HasValue)
                .OrderByDescending(item => item.LastTokenAt ?? item.LastActivityAt)
                .FirstOrDefault()) ?? sessionObservations.OrderByDescending(item => item.LastActivityAt).First();

        var statusObservation = active ?? recentCompleted;
        var status = statusObservation?.Status ?? PulseStatus.Idle;
        var statusKnown = statusObservation is not null;
        var detail = metricObservation.FileName;
        if (active is not null)
        {
            detail += "；检测到未完成 task";
        }
        else if (recentCompleted is not null)
        {
            detail += "；最近 task 已完成";
        }

        return new ProviderObservation
        {
            ProviderAvailable = true,
            ContextRemainingPercent = metricObservation.ContextRemainingPercent,
            QuotaRemainingPercent = metricObservation.QuotaRemainingPercent,
            Status = status,
            StatusKnown = statusKnown,
            StatusAt = statusObservation?.StatusAt,
            LastUpdatedAt = metricObservation.LastActivityAt,
            SourceName = "local session",
            Detail = detail,
            Sessions = sessionObservations
                .Select(item => new SessionObservation
                {
                    SessionId = item.SessionId,
                    ParentSessionId = item.ParentSessionId,
                    Status = item.Status,
                    StatusAt = item.StatusAt,
                    LastActivityAt = item.LastActivityAt,
                    WorkStartedAt = item.WorkStartedAt,
                    ContextRemainingPercent = item.ContextRemainingPercent,
                    SourceName = "local session",
                    Detail = item.FileName
                })
                .ToArray()
        };
    }

    private static IReadOnlyList<LocalFileObservation> CollapseDuplicateSessions(
        IReadOnlyList<LocalFileObservation> observations)
    {
        return observations
            .Where(item => !string.IsNullOrWhiteSpace(item.SessionId))
            .GroupBy(item => item.SessionId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.ToArray();
                var latestStatus = items
                    .OrderByDescending(item => item.StatusAt ?? item.LastActivityAt)
                    .First();
                var latestContext = items
                    .Where(item => item.ContextRemainingPercent.HasValue)
                    .OrderByDescending(item => item.LastTokenAt ?? item.LastActivityAt)
                    .FirstOrDefault();
                var latestQuota = items
                    .Where(item => item.QuotaRemainingPercent.HasValue)
                    .OrderByDescending(item => item.LastTokenAt ?? item.LastActivityAt)
                    .FirstOrDefault();
                var latestWork = items
                    .Where(item => item.WorkStartedAt.HasValue)
                    .OrderByDescending(item => item.WorkStartedAt)
                    .FirstOrDefault();

                return new LocalFileObservation
                {
                    FileName = (latestContext ?? latestQuota ?? latestStatus).FileName,
                    SessionId = group.Key,
                    ParentSessionId = items
                        .Select(item => item.ParentSessionId)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                    LastActivityAt = items.Max(item => item.LastActivityAt),
                    LastTokenAt = items
                        .Where(item => item.LastTokenAt.HasValue)
                        .Select(item => item.LastTokenAt)
                        .OrderByDescending(value => value)
                        .FirstOrDefault(),
                    WorkStartedAt = latestWork?.WorkStartedAt,
                    ContextRemainingPercent = latestContext?.ContextRemainingPercent,
                    QuotaRemainingPercent = latestQuota?.QuotaRemainingPercent,
                    Status = latestStatus.Status,
                    StatusAt = latestStatus.StatusAt
                };
            })
            .OrderByDescending(item => item.WorkStartedAt ?? item.StatusAt ?? item.LastActivityAt)
            .ToArray();
    }

    private static IReadOnlyList<FileInfo> ReadPreferredSessionGraphFiles(
        string sessionsDirectory,
        string preferredSessionId,
        CancellationToken cancellationToken)
    {
        var files = new List<SessionMetadata>();
        try
        {
            foreach (var file in new DirectoryInfo(sessionsDirectory)
                         .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(ReadSessionMetadata(file, cancellationToken));
            }
        }
        catch (IOException)
        {
            return Array.Empty<FileInfo>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<FileInfo>();
        }

        var selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            preferredSessionId
        };

        // Include ancestors so a focus hint that points at a descendant still
        // resolves back to its root thread.
        var ancestor = files.FirstOrDefault(item =>
            string.Equals(item.SessionId, preferredSessionId, StringComparison.OrdinalIgnoreCase));
        while (ancestor is not null && !string.IsNullOrWhiteSpace(ancestor.ParentSessionId) &&
               selectedIds.Add(ancestor.ParentSessionId!))
        {
            ancestor = files.FirstOrDefault(item =>
                string.Equals(item.SessionId, ancestor.ParentSessionId, StringComparison.OrdinalIgnoreCase));
        }

        // Then include every direct or nested descendant of the selected root.
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var metadata in files)
            {
                if (!string.IsNullOrWhiteSpace(metadata.ParentSessionId) &&
                    selectedIds.Contains(metadata.ParentSessionId) &&
                    selectedIds.Add(metadata.SessionId))
                {
                    changed = true;
                }
            }
        }

        return files
            .Where(item => selectedIds.Contains(item.SessionId))
            .Select(item => item.File)
            .ToArray();
    }

    private static SessionMetadata ReadSessionMetadata(
        FileInfo file,
        CancellationToken cancellationToken)
    {
        var sessionId = ExtractSessionId(file.Name);
        string? parentSessionId = null;
        foreach (var line in ReadHeadLines(file.FullName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!string.Equals(
                        JsonHelpers.TryGetString(root, "type"),
                        "session_meta",
                        StringComparison.OrdinalIgnoreCase) ||
                    !JsonHelpers.TryGetProperty(root, out var payload, "payload") ||
                    payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                sessionId = JsonHelpers.TryGetString(payload, "id", "threadId", "thread_id") ?? sessionId;
                parentSessionId = JsonHelpers.TryGetString(payload, "parentThreadId", "parent_thread_id");
                break;
            }
            catch (JsonException)
            {
                // Ignore an incomplete first line and continue probing.
            }
        }

        return new SessionMetadata(file, sessionId, parentSessionId);
    }

    private static LocalFileObservation? ReadFile(FileInfo file, CancellationToken cancellationToken)
    {
        try
        {
            var taskStartedAt = default(DateTimeOffset?);
            var taskCompletedAt = default(DateTimeOffset?);
            var lastTokenAt = default(DateTimeOffset?);
            // session_meta is written at the beginning of the rollout file, while
            // task/token events are read from the tail. Keep the hierarchy metadata
            // from the head so descendants can be folded into their root session.
            var metadata = ReadSessionMetadata(file, cancellationToken);
            var sessionId = metadata.SessionId;
            string? parentThreadId = metadata.ParentSessionId;
            double? contextRemaining = null;
            double? quotaRemaining = null;
            var lastActivityAt = new DateTimeOffset(file.LastWriteTimeUtc);
            var sawTaskEvent = false;

            foreach (var line in ReadTailLines(file.FullName))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var timestamp = JsonHelpers.TryGetTimestamp(root, "timestamp") ?? lastActivityAt;
                    if (timestamp > lastActivityAt)
                    {
                        lastActivityAt = timestamp;
                    }

                    var eventType = JsonHelpers.TryGetString(root, "type");
                    if (!JsonHelpers.TryGetProperty(root, out var payload, "payload") ||
                        payload.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (string.Equals(eventType, "session_meta", StringComparison.OrdinalIgnoreCase))
                    {
                        sessionId = JsonHelpers.TryGetString(payload, "id", "threadId", "thread_id") ?? sessionId;
                        parentThreadId = JsonHelpers.TryGetString(payload, "parentThreadId", "parent_thread_id");
                        continue;
                    }

                    if (!string.Equals(eventType, "event_msg", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var payloadType = JsonHelpers.TryGetString(payload, "type");
                    if (string.Equals(payloadType, "task_started", StringComparison.OrdinalIgnoreCase))
                    {
                        sawTaskEvent = true;
                        if (!taskStartedAt.HasValue || timestamp >= taskStartedAt)
                        {
                            taskStartedAt = timestamp;
                        }
                    }
                    else if (string.Equals(payloadType, "task_complete", StringComparison.OrdinalIgnoreCase))
                    {
                        sawTaskEvent = true;
                        if (!taskCompletedAt.HasValue || timestamp >= taskCompletedAt)
                        {
                            taskCompletedAt = timestamp;
                        }
                    }
                    else if (string.Equals(payloadType, "token_count", StringComparison.OrdinalIgnoreCase))
                    {
                        var tokenContext = ReadTokenContext(payload);
                        if (tokenContext.HasValue)
                        {
                            contextRemaining = tokenContext;
                            lastTokenAt = timestamp;
                        }

                        var tokenQuota = ReadTokenQuota(payload);
                        if (tokenQuota.HasValue)
                        {
                            quotaRemaining = tokenQuota;
                        }
                    }
                }
                catch (JsonException)
                {
                    // A line can be incomplete while Codex is appending it.
                }
            }

            // Large rollout output can push the current task_started marker
            // outside the normal token tail. Probe a larger recent tail for the
            // latest lifecycle marker while the file is still being written.
            if (!sawTaskEvent &&
                DateTime.UtcNow - file.LastWriteTimeUtc <= StaleActiveWindow)
            {
                var latestTaskEvent = ReadLatestTaskEvent(file.FullName, cancellationToken);
                if (latestTaskEvent.HasValue)
                {
                    taskStartedAt = latestTaskEvent.Value.IsStarted
                        ? latestTaskEvent.Value.Timestamp
                        : null;
                    taskCompletedAt = latestTaskEvent.Value.IsStarted
                        ? null
                        : latestTaskEvent.Value.Timestamp;
                }
            }

            PulseStatus status;
            DateTimeOffset? statusAt;
            if (taskStartedAt.HasValue && (!taskCompletedAt.HasValue || taskStartedAt > taskCompletedAt))
            {
                status = DateTimeOffset.UtcNow - lastActivityAt <= StaleActiveWindow
                    ? PulseStatus.Working
                    : PulseStatus.Idle;
                statusAt = taskStartedAt;
            }
            else if (taskCompletedAt.HasValue && DateTimeOffset.UtcNow - taskCompletedAt <= CompletedDisplayWindow)
            {
                status = PulseStatus.Completed;
                statusAt = taskCompletedAt;
            }
            else
            {
                status = PulseStatus.Idle;
                statusAt = taskCompletedAt ?? taskStartedAt;
            }

            return new LocalFileObservation
            {
                FileName = file.Name,
                SessionId = sessionId,
                ParentSessionId = parentThreadId,
                LastActivityAt = lastActivityAt,
                LastTokenAt = lastTokenAt,
                WorkStartedAt = taskStartedAt,
                ContextRemainingPercent = contextRemaining,
                QuotaRemainingPercent = quotaRemaining,
                Status = status,
                StatusAt = statusAt
            };
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static double? ReadTokenContext(JsonElement payload)
    {
        if (!JsonHelpers.TryGetProperty(payload, out var info, "info") || info.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var window = JsonHelpers.TryGetNumber(info, "model_context_window", "modelContextWindow");
        var usage = JsonHelpers.TryGetProperty(info, out var last, "last_token_usage", "lastTokenUsage", "last")
            ? JsonHelpers.TryGetNumber(last, "total_tokens", "totalTokens")
            : null;

        if (!window.HasValue || window <= 0 || !usage.HasValue)
        {
            return null;
        }

        return JsonHelpers.ClampPercent((window.Value - usage.Value) / window.Value * 100d);
    }

    private static TaskEvent? ReadLatestTaskEvent(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var lowerBound = Math.Max(0L, stream.Length - MaxTaskEventProbeBytes);
            stream.Seek(lowerBound, SeekOrigin.Begin);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 64 * 1024);
            if (lowerBound > 0)
            {
                // Discard the partial line created by seeking into the file.
                reader.ReadLine();
            }

            TaskEvent? latestTaskEvent = null;
            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = reader.ReadLine();
                if (line is not null &&
                    TryReadTaskEvent(line, out var taskEvent))
                {
                    latestTaskEvent = taskEvent;
                }
            }

            return latestTaskEvent;
        }
        catch (IOException)
        {
            // The desktop can rotate or append the file while it is read.
        }
        catch (UnauthorizedAccessException)
        {
            // Status remains based on the normal tail when probing is unavailable.
        }

        return null;
    }

    private static bool TryReadTaskEvent(string line, out TaskEvent taskEvent)
    {
        taskEvent = default;
        if (!line.Contains("\"event_msg\"", StringComparison.Ordinal) ||
            (!line.Contains("\"task_started\"", StringComparison.Ordinal) &&
             !line.Contains("\"task_complete\"", StringComparison.Ordinal)))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!string.Equals(
                    JsonHelpers.TryGetString(root, "type"),
                    "event_msg",
                    StringComparison.Ordinal) ||
                !JsonHelpers.TryGetProperty(root, out var payload, "payload") ||
                payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var payloadType = JsonHelpers.TryGetString(payload, "type");
            var timestamp = JsonHelpers.TryGetTimestamp(root, "timestamp");
            if (!timestamp.HasValue ||
                (!string.Equals(payloadType, "task_started", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(payloadType, "task_complete", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            taskEvent = new TaskEvent(
                timestamp.Value,
                string.Equals(payloadType, "task_started", StringComparison.OrdinalIgnoreCase));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static double? ReadTokenQuota(JsonElement payload)
    {
        if (!JsonHelpers.TryGetProperty(payload, out var limits, "rate_limits", "rateLimits") ||
            limits.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (JsonHelpers.TryGetProperty(limits, out var byLimitId, "rate_limits_by_limit_id", "rateLimitsByLimitId") &&
            byLimitId.ValueKind == JsonValueKind.Object &&
            byLimitId.TryGetProperty("codex", out var codexSnapshot))
        {
            var codexRemaining = JsonHelpers.ReadRemainingPercent(codexSnapshot);
            if (codexRemaining.HasValue)
            {
                return codexRemaining;
            }
        }

        return JsonHelpers.ReadRemainingPercent(limits);
    }

    private static IEnumerable<string> ReadTailLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0L, stream.Length - MaxTailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var length = checked((int)(stream.Length - start));
        var bytes = new byte[length];
        var read = 0;
        while (read < bytes.Length)
        {
            var current = stream.Read(bytes, read, bytes.Length - read);
            if (current == 0)
            {
                break;
            }

            read += current;
        }

        var content = Encoding.UTF8.GetString(bytes, 0, read);
        if (start > 0)
        {
            var firstNewline = content.IndexOf('\n');
            content = firstNewline >= 0 ? content[(firstNewline + 1)..] : string.Empty;
        }

        return content.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string ExtractSessionId(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length >= 36)
        {
            var possibleId = stem[^36..];
            if (Guid.TryParse(possibleId, out _))
            {
                return possibleId;
            }
        }

        return stem;
    }

    private static IEnumerable<string> ReadHeadLines(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = checked((int)Math.Min(MetadataProbeBytes, stream.Length));
            var bytes = new byte[length];
            var read = 0;
            while (read < bytes.Length)
            {
                var current = stream.Read(bytes, read, bytes.Length - read);
                if (current == 0)
                {
                    break;
                }

                read += current;
            }

            return Encoding.UTF8
                .GetString(bytes, 0, read)
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsSessionId(string? sessionId)
    {
        return Guid.TryParse(sessionId, out _);
    }

    private sealed record SessionMetadata(
        FileInfo File,
        string SessionId,
        string? ParentSessionId);

    private readonly record struct TaskEvent(DateTimeOffset Timestamp, bool IsStarted);

    private sealed class LocalFileObservation
    {
        public string FileName { get; init; } = string.Empty;
        public string SessionId { get; init; } = string.Empty;
        public string? ParentSessionId { get; init; }
        public DateTimeOffset LastActivityAt { get; init; }
        public DateTimeOffset? LastTokenAt { get; init; }
        public DateTimeOffset? WorkStartedAt { get; init; }
        public double? ContextRemainingPercent { get; init; }
        public double? QuotaRemainingPercent { get; init; }
        public PulseStatus Status { get; init; }
        public DateTimeOffset? StatusAt { get; init; }
    }
}
