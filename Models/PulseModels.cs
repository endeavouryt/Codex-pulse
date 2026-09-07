namespace CodexPulse.Models;

public enum PulseStatus
{
    Idle,
    Working,
    Completed
}

public sealed class SessionObservation
{
    public string SessionId { get; init; } = string.Empty;
    public string? ParentSessionId { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public PulseStatus Status { get; init; } = PulseStatus.Idle;
    public DateTimeOffset? StatusAt { get; init; }
    public DateTimeOffset LastActivityAt { get; init; }
    public DateTimeOffset? WorkStartedAt { get; init; }
    public double? ContextRemainingPercent { get; init; }
    public string SourceName { get; init; } = string.Empty;
    public string? Detail { get; init; }

    public bool IsRoot => string.IsNullOrWhiteSpace(ParentSessionId);
}

public sealed class ProviderObservation
{
    public bool ProviderAvailable { get; init; }
    public double? ContextRemainingPercent { get; init; }
    public QuotaWindows Quotas { get; init; } = new();
    public PulseStatus Status { get; init; } = PulseStatus.Idle;
    public bool StatusKnown { get; init; }
    public DateTimeOffset? StatusAt { get; init; }
    public DateTimeOffset? LastUpdatedAt { get; init; }
    public string SourceName { get; init; } = string.Empty;
    public string? Detail { get; init; }
    public IReadOnlyList<SessionObservation> Sessions { get; init; } = Array.Empty<SessionObservation>();
}

public sealed class PulseSnapshot
{
    public double? ContextRemainingPercent { get; init; }
    public QuotaWindows Quotas { get; init; } = new();
    public PulseStatus Status { get; init; } = PulseStatus.Idle;
    public DateTimeOffset? StatusAt { get; init; }
    public string SourceName { get; init; } = "NO DATA";
    public string Detail { get; init; } = "数据源缺失";
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public string? MonitoredSessionId { get; init; }
    public bool AutoFollow { get; init; } = true;

    public bool HasContext => ContextRemainingPercent.HasValue;
    public bool HasQuota => Quotas.HasValue;
    public bool HasMetrics => HasContext || HasQuota;
}

public sealed class QuotaWindow
{
    public double? RemainingPercent { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
}
public sealed class QuotaWindows
{
    public QuotaWindow? FiveHour { get; init; }
    public QuotaWindow? Weekly { get; init; }
    public bool HasValue => FiveHour?.RemainingPercent.HasValue == true || Weekly?.RemainingPercent.HasValue == true;
}
