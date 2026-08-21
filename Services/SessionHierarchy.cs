using CodexPulse.Models;

namespace CodexPulse.Services;

internal static class SessionHierarchy
{
    public static string? FindRootSessionId(
        string? sessionId,
        IReadOnlyList<SessionObservation> observations)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var byId = observations
            .Where(item => !string.IsNullOrWhiteSpace(item.SessionId))
            .GroupBy(item => item.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        return FindRootId(sessionId, byId);
    }

    public static IReadOnlyList<SessionObservation> CollapseToRootSessions(
        IReadOnlyList<SessionObservation> observations)
    {
        var byId = observations
            .Where(item => !string.IsNullOrWhiteSpace(item.SessionId))
            .GroupBy(item => item.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        var grouped = new Dictionary<string, List<SessionObservation>>(StringComparer.OrdinalIgnoreCase);
        foreach (var observation in byId.Values)
        {
            var rootId = FindRootId(observation.SessionId, byId);
            if (rootId is null)
            {
                // A child without its root is deliberately ignored. Treating it as
                // a root would recreate the monitoring bug this layer is meant to prevent.
                continue;
            }

            if (!grouped.TryGetValue(rootId, out var members))
            {
                members = new List<SessionObservation>();
                grouped[rootId] = members;
            }

            members.Add(observation);
        }

        return grouped.Values
            .Select(BuildRootObservation)
            .OrderByDescending(item => item.WorkStartedAt ?? item.StatusAt ?? item.CreatedAt ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    private static string? FindRootId(
        string sessionId,
        IReadOnlyDictionary<string, SessionObservation> observations)
    {
        var currentId = sessionId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (observations.TryGetValue(currentId, out var current) &&
               !string.IsNullOrWhiteSpace(current.ParentSessionId))
        {
            if (!visited.Add(currentId))
            {
                return null;
            }

            currentId = current.ParentSessionId!;
        }

        return observations.TryGetValue(currentId, out var root) && root.IsRoot
            ? currentId
            : null;
    }

    private static SessionObservation BuildRootObservation(IReadOnlyList<SessionObservation> members)
    {
        var root = members.First(item => item.IsRoot);
        var descendants = members
            .Where(item => !string.Equals(item.SessionId, root.SessionId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var workingDescendants = descendants
            .Where(item => item.Status == PulseStatus.Working)
            .ToArray();
        var rootIsWorking = root.Status == PulseStatus.Working;
        var isWorking = rootIsWorking || workingDescendants.Length > 0;

        var statusAt = isWorking
            ? Max(root.StatusAt, workingDescendants.Select(item => item.StatusAt).MaxOrNull())
            : root.StatusAt;
        var workStartedAt = Max(
            root.WorkStartedAt,
            workingDescendants.Select(item => item.WorkStartedAt).MaxOrNull());
        var detailParts = members
            .Select(item => item.Detail)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (descendants.Length > 0)
        {
            detailParts.Add(workingDescendants.Length > 0
                ? "root 下存在正在运行的 subagent descendant"
                : "root 下存在 subagent descendant");
        }

        return new SessionObservation
        {
            SessionId = root.SessionId,
            ParentSessionId = null,
            CreatedAt = root.CreatedAt,
            Status = isWorking ? PulseStatus.Working : root.Status,
            StatusAt = statusAt,
            LastActivityAt = members.Max(item => item.LastActivityAt),
            WorkStartedAt = workStartedAt,
            // CTX is intentionally taken from the root only.
            ContextRemainingPercent = root.ContextRemainingPercent,
            SourceName = string.Join(
                " + ",
                members
                    .Select(item => item.SourceName)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)),
            Detail = string.Join("；", detailParts)
        };
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

    private static DateTimeOffset? MaxOrNull(this IEnumerable<DateTimeOffset?> values)
    {
        var result = default(DateTimeOffset?);
        foreach (var value in values)
        {
            result = Max(result, value);
        }

        return result;
    }
}
