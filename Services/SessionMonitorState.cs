using CodexPulse.Models;

namespace CodexPulse.Services;

internal sealed class SessionMonitorState
{
    private readonly object _gate = new();

    public bool AutoFollow { get; private set; } = true;
    public string? PinnedSessionId { get; private set; }
    public string? CurrentSessionId { get; private set; }
    public SessionObservation? LastSelectedObservation { get; private set; }

    // Reserved for the future manual-session picker. The MVP keeps AutoFollow enabled.
    public void PinSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_gate)
        {
            PinnedSessionId = sessionId;
            AutoFollow = false;
            CurrentSessionId = sessionId;
        }
    }

    public void EnableAutoFollow()
    {
        lock (_gate)
        {
            PinnedSessionId = null;
            AutoFollow = true;
        }
    }

    public SessionObservation? Select(IReadOnlyList<SessionObservation> candidates)
    {
        lock (_gate)
        {
            var current = FindById(candidates, CurrentSessionId);

            if (!AutoFollow && !string.IsNullOrWhiteSpace(PinnedSessionId))
            {
                var pinned = FindById(candidates, PinnedSessionId);
                if (pinned is not null)
                {
                    return Remember(pinned);
                }

                return LastSelectedObservation is not null &&
                       string.Equals(LastSelectedObservation.SessionId, PinnedSessionId, StringComparison.OrdinalIgnoreCase)
                    ? Remember(IdleCopy(LastSelectedObservation))
                    : null;
            }

            var working = candidates
                .Where(item => item.Status == PulseStatus.Working)
                .OrderByDescending(item => item.WorkStartedAt ?? item.LastActivityAt)
                .ToArray();

            if (working.Length > 0)
            {
                var newestWorking = working[0];
                if (current is not null && current.Status == PulseStatus.Working)
                {
                    var currentStart = current.WorkStartedAt ?? current.LastActivityAt;
                    var newestStart = newestWorking.WorkStartedAt ?? newestWorking.LastActivityAt;
                    if (newestStart <= currentStart)
                    {
                        return Remember(current);
                    }
                }

                return Remember(newestWorking);
            }

            // No session is working: keep the current object even if another idle
            // session has a newer timestamp.
            if (current is not null)
            {
                return Remember(current);
            }

            if (LastSelectedObservation is not null)
            {
                return Remember(IdleCopy(LastSelectedObservation));
            }

            var first = candidates
                .OrderByDescending(item => item.LastActivityAt)
                .FirstOrDefault();
            return first is null ? null : Remember(first);
        }
    }

    private SessionObservation Remember(SessionObservation observation)
    {
        CurrentSessionId = observation.SessionId;
        LastSelectedObservation = observation;
        return observation;
    }

    private static SessionObservation? FindById(IReadOnlyList<SessionObservation> candidates, string? sessionId)
    {
        return string.IsNullOrWhiteSpace(sessionId)
            ? null
            : candidates.FirstOrDefault(item =>
                string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
    }

    private static SessionObservation IdleCopy(SessionObservation observation)
    {
        return new SessionObservation
        {
            SessionId = observation.SessionId,
            Status = PulseStatus.Idle,
            StatusAt = observation.StatusAt,
            LastActivityAt = observation.LastActivityAt,
            WorkStartedAt = observation.WorkStartedAt,
            ContextRemainingPercent = observation.ContextRemainingPercent,
            SourceName = observation.SourceName,
            Detail = observation.Detail
        };
    }
}
