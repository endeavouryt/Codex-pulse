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

    public SessionObservation? Select(
        IReadOnlyList<SessionObservation> candidates,
        bool chatGptFocused = false,
        string? focusedRootSessionId = null)
    {
        lock (_gate)
        {
            var roots = candidates
                .Where(item => item.IsRoot)
                .ToArray();
            var current = FindById(roots, CurrentSessionId);

            if (!AutoFollow && !string.IsNullOrWhiteSpace(PinnedSessionId))
            {
                var pinned = FindById(roots, PinnedSessionId);
                if (pinned is not null)
                {
                    return Remember(pinned);
                }

                return LastSelectedObservation is not null &&
                       string.Equals(LastSelectedObservation.SessionId, PinnedSessionId, StringComparison.OrdinalIgnoreCase)
                    ? Remember(IdleCopy(LastSelectedObservation))
                    : null;
            }

            if (chatGptFocused)
            {
                // A focused Codex window is the user's explicit context. Do not
                // let another root that happens to be working take over the card.
                var focused = FindById(roots, focusedRootSessionId);
                if (focused is not null)
                {
                    return Remember(focused);
                }

                // If the desktop focus hint is temporarily unavailable, hold the
                // last root instead of falling through to another active window.
                if (current is not null)
                {
                    return Remember(current);
                }

                if (LastSelectedObservation is not null)
                {
                    return Remember(IdleCopy(LastSelectedObservation));
                }

                // During the first refresh, use a deterministic root while the
                // desktop focus hint is still being discovered. Do not select a
                // different root merely because it is currently busy.
                var focusedFallback = roots
                    .OrderBy(item => item.CreatedAt ?? DateTimeOffset.MaxValue)
                    .ThenBy(item => item.SessionId, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                return focusedFallback is null ? null : Remember(focusedFallback);
            }

            var working = roots
                .Where(item => item.Status == PulseStatus.Working)
                .OrderByDescending(GetWorkStart)
                .ToArray();

            if (working.Length > 0)
            {
                var newestWorking = working[0];
                if (current is not null && current.Status == PulseStatus.Working)
                {
                    if (GetWorkStart(newestWorking) <= GetWorkStart(current))
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

            // No previous monitor and no active root: choose a deterministic root
            // without using recent activity as a proxy for the monitored thread.
            var first = roots
                .OrderBy(item => item.CreatedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(item => item.SessionId, StringComparer.OrdinalIgnoreCase)
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
            ParentSessionId = null,
            CreatedAt = observation.CreatedAt,
            Status = PulseStatus.Idle,
            StatusAt = observation.StatusAt,
            LastActivityAt = observation.LastActivityAt,
            WorkStartedAt = observation.WorkStartedAt,
            ContextRemainingPercent = observation.ContextRemainingPercent,
            SourceName = observation.SourceName,
            Detail = observation.Detail
        };
    }

    private static DateTimeOffset GetWorkStart(SessionObservation observation)
    {
        return observation.WorkStartedAt ?? observation.StatusAt ?? DateTimeOffset.MinValue;
    }
}
