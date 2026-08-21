using System.IO;
using CodexPulse.Models;

namespace CodexPulse.Services;

internal sealed class CodexDataService : IDisposable
{
    private readonly AppServerClient _appServerClient = new();
    private readonly AppServerProvider _appServerProvider;
    private readonly LocalStateProvider _localStateProvider;
    private readonly SessionMonitorState _sessionMonitor = new();

    public CodexDataService()
    {
        _appServerProvider = new AppServerProvider(_appServerClient);
        _localStateProvider = new LocalStateProvider(GetCodexHomeCandidates);
    }

    public async Task<PulseSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var appTask = _appServerProvider.ReadAsync(cancellationToken);
        var localTask = _localStateProvider.ReadAsync(cancellationToken);

        await Task.WhenAll(appTask, localTask).ConfigureAwait(false);
        var app = await appTask.ConfigureAwait(false);
        var local = await localTask.ConfigureAwait(false);

        var candidates = MergeSessions(app.Sessions, local.Sessions);
        var selected = _sessionMonitor.Select(candidates);
        var context = selected?.ContextRemainingPercent;
        var quota = app.QuotaRemainingPercent ?? local.QuotaRemainingPercent;
        var status = selected?.Status ?? PulseStatus.Idle;
        var statusAt = selected?.StatusAt;

        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSource(sources, selected?.SourceName);
        if (app.QuotaRemainingPercent.HasValue)
        {
            sources.Add("APP");
        }
        else if (local.QuotaRemainingPercent.HasValue)
        {
            sources.Add("FILE");
        }

        var detailParts = new[] { app.Detail, local.Detail }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selected is not null)
        {
            detailParts.Add($"当前监控会话：{selected.SessionId}");
        }
        else if (candidates.Count > 0)
        {
            detailParts.Add("当前监控会话数据缺失");
        }

        var detail = detailParts.Count == 0
            ? "数据源缺失：未获取到 app-server 或本地 session 数据。"
            : string.Join("\n", detailParts.Distinct(StringComparer.OrdinalIgnoreCase));

        return new PulseSnapshot
        {
            ContextRemainingPercent = context,
            QuotaRemainingPercent = quota,
            Status = status,
            StatusAt = statusAt,
            SourceName = sources.Count == 0 ? "NO DATA" : string.Join(" + ", sources),
            Detail = detail,
            MonitoredSessionId = _sessionMonitor.CurrentSessionId,
            AutoFollow = _sessionMonitor.AutoFollow,
            CapturedAt = DateTimeOffset.Now
        };
    }

    // Reserved internal hooks for a future manual session picker.
    internal void PinSession(string sessionId)
    {
        _sessionMonitor.PinSession(sessionId);
    }

    internal void EnableAutoFollow()
    {
        _sessionMonitor.EnableAutoFollow();
    }

    private static IReadOnlyList<SessionObservation> MergeSessions(
        IReadOnlyList<SessionObservation> appSessions,
        IReadOnlyList<SessionObservation> localSessions)
    {
        var merged = new Dictionary<string, SessionObservation>(StringComparer.OrdinalIgnoreCase);
        foreach (var observation in appSessions.Concat(localSessions))
        {
            if (string.IsNullOrWhiteSpace(observation.SessionId))
            {
                continue;
            }

            if (!merged.TryGetValue(observation.SessionId, out var existing))
            {
                merged[observation.SessionId] = observation;
                continue;
            }

            merged[observation.SessionId] = Combine(existing, observation);
        }

        return merged.Values
            .OrderByDescending(item => item.WorkStartedAt ?? item.LastActivityAt)
            .ToArray();
    }

    private static SessionObservation Combine(SessionObservation first, SessionObservation second)
    {
        var status = first.Status == PulseStatus.Working || second.Status == PulseStatus.Working
            ? PulseStatus.Working
            : first.Status == PulseStatus.Completed || second.Status == PulseStatus.Completed
                ? PulseStatus.Completed
                : PulseStatus.Idle;
        var sourceName = string.Join(
            " + ",
            new[] { first.SourceName, second.SourceName }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase));

        return new SessionObservation
        {
            SessionId = first.SessionId,
            Status = status,
            StatusAt = Max(first.StatusAt, second.StatusAt),
            LastActivityAt = first.LastActivityAt >= second.LastActivityAt
                ? first.LastActivityAt
                : second.LastActivityAt,
            WorkStartedAt = Max(first.WorkStartedAt, second.WorkStartedAt),
            ContextRemainingPercent = second.ContextRemainingPercent ?? first.ContextRemainingPercent,
            SourceName = sourceName,
            Detail = string.Join(
                "；",
                new[] { first.Detail, second.Detail }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
        };
    }

    private static void AddSource(ISet<string> sources, string? sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return;
        }

        if (sourceName.Contains("app-server", StringComparison.OrdinalIgnoreCase))
        {
            sources.Add("APP");
        }

        if (sourceName.Contains("local session", StringComparison.OrdinalIgnoreCase))
        {
            sources.Add("FILE");
        }
    }

    private static DateTimeOffset? Max(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (!first.HasValue)
        {
            return second;
        }

        if (!second.HasValue)
        {
            return first;
        }

        return first > second ? first : second;
    }

    private IEnumerable<string> GetCodexHomeCandidates()
    {
        var candidates = new List<string>();
        AddCandidate(candidates, Environment.GetEnvironmentVariable("CODEX_HOME"));
        AddCandidate(candidates, _appServerClient.CodexHome);
        var environmentUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(environmentUserProfile))
        {
            AddCandidate(candidates, Path.Combine(environmentUserProfile, ".codex"));
        }
        AddCandidate(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));

        return candidates;
    }

    private static void AddCandidate(ICollection<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!candidates.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(fullPath);
            }
        }
        catch (Exception)
        {
            // Ignore malformed optional paths.
        }
    }

    public void Dispose()
    {
        _appServerClient.Dispose();
    }
}
