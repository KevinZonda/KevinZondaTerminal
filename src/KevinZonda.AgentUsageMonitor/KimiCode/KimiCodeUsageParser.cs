using System.Text.Json;
using KevinZonda.AgentUsageMonitor.Internal;

namespace KevinZonda.AgentUsageMonitor.KimiCode;

internal static class KimiCodeUsageParser
{
    private static readonly TimeSpan WeeklyWindow = TimeSpan.FromDays(7);
    private static readonly TimeSpan DefaultRateWindow = TimeSpan.FromHours(5);
    private const decimal FixedPointCents = 1_000_000m;

    internal static UsageSnapshot Parse(
        ReadOnlySpan<byte> data,
        UsageSource source,
        DateTimeOffset updatedAt)
    {
        using var document = JsonDocument.Parse(data.ToArray());
        var root = document.RootElement;
        var primary = root.Property("usage") is { } usage
            ? TryParseDetail(usage, "7-day usage", WeeklyWindow)
            : null;

        var limitWindows = ParseLimits(root.Property("limits"));
        if (root.Property("usages") is { } quotas)
        {
            primary = ParseQuota(quotas.Property("limit_7d"), "Weekly usage", WeeklyWindow) ?? primary;
            if (ParseQuota(quotas.Property("limit_5h"), "5-hour usage", DefaultRateWindow) is { } fiveHour)
                limitWindows.Insert(0, fiveHour);
            if (ParseQuota(quotas.Property("limit_month_total"), "Monthly usage", null) is { } monthly)
                limitWindows.Add(monthly);
        }
        var (credits, budget) = ParseBoosterWallet(root.Property("boosterWallet", "booster_wallet"));
        if (primary is null && limitWindows.Count == 0 && credits is null && budget is null)
        {
            throw new UsageException(
                UsageErrorCode.InvalidResponse,
                "Kimi Code response contains no usage data.");
        }

        return new UsageSnapshot(
            UsageProvider.KimiCode,
            source,
            primary,
            limitWindows.FirstOrDefault(),
            limitWindows.Skip(1).ToArray(),
            credits,
            budget,
            null,
            null,
            updatedAt);
    }

    private static UsageWindow? ParseQuota(JsonElement? value, string name, TimeSpan? window)
    {
        if (value?.Double("used_ratio") is not { } ratio || !double.IsFinite(ratio)) return null;
        DateTimeOffset? resetsAt = DateTimeOffset.TryParse(value.Value.String("reset_time"), out var reset) ? reset : null;
        return new UsageWindow(name, JsonHelpers.ClampPercent(ratio * 100), window, resetsAt);
    }

    private static List<UsageWindow> ParseLimits(JsonElement? value)
    {
        var result = new List<UsageWindow>();
        if (value is null || value.Value.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in value.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.Property("detail") is not { } detail)
            {
                continue;
            }

            var window = ParseWindow(item.Property("window")) ?? DefaultRateWindow;
            var name = item.String("name") ?? FormatWindowName(window);
            if (TryParseDetail(detail, name, window) is { } parsed)
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    private static UsageWindow? TryParseDetail(JsonElement detail, string name, TimeSpan window)
    {
        var limit = detail.Double("limit");
        var used = detail.Double("used");
        if (limit is null && used is null)
        {
            return null;
        }

        limit ??= 0;
        if (used is null && detail.Double("remaining") is { } remaining
            && remaining >= 0 && remaining <= limit)
        {
            used = limit - remaining;
        }

        used ??= 0;
        var usedPercent = limit > 0 ? JsonHelpers.ClampPercent(used.Value / limit.Value * 100) : 0;
        var reset = detail.String("resetTime", "resetAt", "reset_time", "reset_at");
        DateTimeOffset? resetsAt = DateTimeOffset.TryParse(reset, out var parsedReset) ? parsedReset : null;
        return new UsageWindow(name, usedPercent, window, resetsAt, used, limit);
    }

    private static TimeSpan? ParseWindow(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Object
            || value.Value.Int64("duration") is not { } duration || duration <= 0)
        {
            return null;
        }

        return value.Value.String("timeUnit", "time_unit") switch
        {
            "TIME_UNIT_MINUTE" => TimeSpan.FromMinutes(duration),
            "TIME_UNIT_HOUR" => TimeSpan.FromHours(duration),
            "TIME_UNIT_DAY" => TimeSpan.FromDays(duration),
            "TIME_UNIT_WEEK" => TimeSpan.FromDays(duration * 7),
            _ => null,
        };
    }

    private static string FormatWindowName(TimeSpan window)
    {
        if (window.TotalDays >= 1 && window.TotalDays == Math.Truncate(window.TotalDays))
        {
            return $"{window.TotalDays:0}-day limit";
        }
        if (window.TotalHours >= 1 && window.TotalHours == Math.Truncate(window.TotalHours))
        {
            return $"{window.TotalHours:0}-hour limit";
        }
        return "Rate limit";
    }

    private static (UsageCredits? Credits, UsageBudget? Budget) ParseBoosterWallet(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Object
            || value.Value.Property("balance") is not { } balance
            || !string.Equals(balance.String("type"), "BOOSTER", StringComparison.Ordinal)
            || balance.Double("amount") is not { } amount || amount <= 0)
        {
            return (null, null);
        }

        var totalCents = FixedPointToCents((decimal)amount);
        var balanceCents = balance.Double("amountLeft", "amount_left") is { } amountLeft
            ? FixedPointToCents((decimal)amountLeft)
            : 0;
        var monthlyLimitElement = value.Value.Property("monthlyChargeLimit", "monthly_charge_limit");
        var monthlyUsedElement = value.Value.Property("monthlyUsed", "monthly_used");
        var monthlyLimitCents = monthlyLimitElement?.Double("priceInCents", "price_in_cents") ?? 0;
        var monthlyUsedCents = monthlyUsedElement?.Double("priceInCents", "price_in_cents") ?? 0;
        var monthlyLimitEnabled = value.Value.Boolean(
            "monthlyChargeLimitEnabled",
            "monthly_charge_limit_enabled") == true;
        var currency = monthlyLimitElement?.String("currency")
            ?? monthlyUsedElement?.String("currency")
            ?? "USD";

        var credits = new UsageCredits((double)(balanceCents / 100m))
        {
            Total = (double)(totalCents / 100m),
            Currency = currency,
        };
        var limit = monthlyLimitEnabled && monthlyLimitCents > 0 ? monthlyLimitCents / 100d : 0;
        var used = monthlyUsedCents / 100d;
        var remainingPercent = limit > 0
            ? JsonHelpers.ClampPercent(100 - (used / limit * 100))
            : 100;
        var budget = new UsageBudget(
            "Monthly extra usage",
            limit,
            used,
            remainingPercent,
            null)
        {
            IsUnlimited = !monthlyLimitEnabled || monthlyLimitCents <= 0,
            Currency = currency,
        };
        return (credits, budget);
    }

    private static decimal FixedPointToCents(decimal value)
    {
        var cents = value / FixedPointCents;
        if (cents > 0 && cents < 1)
        {
            return 1;
        }
        return decimal.Round(cents, 0, MidpointRounding.AwayFromZero);
    }
}
