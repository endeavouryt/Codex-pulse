using System.Globalization;
using System.Text.Json;

namespace CodexPulse.Services;

internal static class JsonHelpers
{
    public static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    public static string? TryGetString(JsonElement element, params string[] names)
    {
        return TryGetProperty(element, out var value, names) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    public static double? TryGetNumber(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }

    public static DateTimeOffset? TryGetTimestamp(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unixSeconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    public static double? ClampPercent(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }

        return Math.Clamp(value.Value, 0d, 100d);
    }

    public static double? RemainingPercentFromUsed(double? usedPercent)
    {
        return usedPercent.HasValue ? ClampPercent(100d - usedPercent.Value) : null;
    }

    public static CodexPulse.Models.QuotaWindows ReadQuotaWindows(JsonElement snapshot)
    {
        if (TryGetProperty(snapshot, out var buckets, "rateLimitsByLimitId", "rate_limits_by_limit_id") &&
            buckets.ValueKind == JsonValueKind.Object && buckets.TryGetProperty("codex", out var codex))
            return ReadQuotaWindows(codex);
        if (TryGetProperty(snapshot, out var nested, "rateLimits", "rate_limits"))
            return ReadQuotaWindows(nested);
        CodexPulse.Models.QuotaWindow? fiveHour = null, weekly = null;
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!TryGetProperty(snapshot, out var window, name)) continue;
            var minutes = TryGetNumber(window, "windowDurationMins", "window_duration_mins", "window_minutes");
            // Duration identifies the window even when primary/secondary are reordered.
            var value = new CodexPulse.Models.QuotaWindow
            {
                RemainingPercent = RemainingPercentFromUsed(TryGetNumber(window, "usedPercent", "used_percent")),
                ResetsAt = TryGetTimestamp(window, "resetsAt", "resets_at")
            };
            if (minutes == 300) fiveHour = value;
            else if (minutes == 10080) weekly = value;
        }
        return new CodexPulse.Models.QuotaWindows { FiveHour = fiveHour, Weekly = weekly };
    }

    public static double? ReadContextRemaining(JsonElement tokenUsage)
    {
        if (tokenUsage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var window = TryGetNumber(tokenUsage, "modelContextWindow", "model_context_window");
        if (!window.HasValue || window <= 0)
        {
            return null;
        }

        JsonElement currentUsage = default;
        var hasCurrentUsage = TryGetProperty(tokenUsage, out currentUsage, "last", "lastTokenUsage", "last_token_usage");
        if (!hasCurrentUsage)
        {
            hasCurrentUsage = TryGetProperty(tokenUsage, out currentUsage, "info");
            if (hasCurrentUsage && currentUsage.ValueKind == JsonValueKind.Object)
            {
                hasCurrentUsage = TryGetProperty(currentUsage, out currentUsage, "lastTokenUsage", "last_token_usage", "last");
            }
        }

        var used = hasCurrentUsage
            ? TryGetNumber(currentUsage, "totalTokens", "total_tokens")
            : TryGetNumber(tokenUsage, "totalTokens", "total_tokens");

        if (!used.HasValue)
        {
            return null;
        }

        return ClampPercent((window.Value - used.Value) / window.Value * 100d);
    }
}
