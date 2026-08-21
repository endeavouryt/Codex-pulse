using System.IO;
using System.Text;
using System.Text.Json;
using CodexPulse.Models;

namespace CodexPulse.Services;

internal sealed class LocalStateProvider
{
    private const int MaxTailBytes = 1024 * 1024;
    private static readonly TimeSpan CompletedDisplayWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StaleActiveWindow = TimeSpan.FromMinutes(30);
    private readonly Func<IEnumerable<string>> _codexHomeProvider;

    public LocalStateProvider(Func<IEnumerable<string>> codexHomeProvider)
    {
        _codexHomeProvider = codexHomeProvider;
    }

    public Task<ProviderObservation> ReadAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => ReadSynchronously(cancellationToken), cancellationToken);
    }

    private ProviderObservation ReadSynchronously(CancellationToken cancellationToken)
    {
        var files = new List<FileInfo>();
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
        foreach (var file in files
                     .GroupBy(item => item.FullName, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First())
                     .OrderByDescending(item => item.LastWriteTimeUtc)
                     .Take(32))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = ReadFile(file, cancellationToken);
            if (observation is not null)
            {
                observations.Add(observation);
            }
        }

        if (observations.Count == 0)
        {
            return new ProviderObservation
            {
                ProviderAvailable = false,
                SourceName = "local session",
                Detail = "未找到可读取的 Codex session JSONL。"
            };
        }

        var active = observations
            .Where(item => item.Status == PulseStatus.Working)
            .OrderByDescending(item => item.LastActivityAt)
            .FirstOrDefault();
        var recentCompleted = observations
            .Where(item => item.Status == PulseStatus.Completed)
            .OrderByDescending(item => item.StatusAt ?? item.LastActivityAt)
            .FirstOrDefault();
        var metricObservation = (active is not null &&
                                 (active.ContextRemainingPercent.HasValue || active.QuotaRemainingPercent.HasValue)
                ? active
                : observations
                .Where(item => item.ContextRemainingPercent.HasValue || item.QuotaRemainingPercent.HasValue)
                .OrderByDescending(item => item.LastTokenAt ?? item.LastActivityAt)
                .FirstOrDefault()) ?? observations.OrderByDescending(item => item.LastActivityAt).First();

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
            Sessions = observations
                .Select(item => new SessionObservation
                {
                    SessionId = item.SessionId,
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

    private static LocalFileObservation? ReadFile(FileInfo file, CancellationToken cancellationToken)
    {
        try
        {
            var taskStartedAt = default(DateTimeOffset?);
            var taskCompletedAt = default(DateTimeOffset?);
            var lastTokenAt = default(DateTimeOffset?);
            double? contextRemaining = null;
            double? quotaRemaining = null;
            var lastActivityAt = new DateTimeOffset(file.LastWriteTimeUtc);

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
                    if (!string.Equals(eventType, "event_msg", StringComparison.Ordinal) ||
                        !JsonHelpers.TryGetProperty(root, out var payload, "payload") ||
                        payload.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var payloadType = JsonHelpers.TryGetString(payload, "type");
                    if (string.Equals(payloadType, "task_started", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!taskStartedAt.HasValue || timestamp >= taskStartedAt)
                        {
                            taskStartedAt = timestamp;
                        }
                    }
                    else if (string.Equals(payloadType, "task_complete", StringComparison.OrdinalIgnoreCase))
                    {
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
                SessionId = ExtractSessionId(file.Name),
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

    private sealed class LocalFileObservation
    {
        public string FileName { get; init; } = string.Empty;
        public string SessionId { get; init; } = string.Empty;
        public DateTimeOffset LastActivityAt { get; init; }
        public DateTimeOffset? LastTokenAt { get; init; }
        public DateTimeOffset? WorkStartedAt { get; init; }
        public double? ContextRemainingPercent { get; init; }
        public double? QuotaRemainingPercent { get; init; }
        public PulseStatus Status { get; init; }
        public DateTimeOffset? StatusAt { get; init; }
    }
}
