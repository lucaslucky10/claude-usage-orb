using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ClaudeUsageOrb;

internal enum LimitKind { Session, WeeklyAll, WeeklyScoped, Other }

internal sealed class LimitInfo
{
    public required string Key { get; init; }
    public required LimitKind Kind { get; init; }
    public required string Label { get; init; }
    public required string ShortLabel { get; init; }
    public required double UsedPercent { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
    public TimeSpan Window { get; init; }

    /// <summary>Share of the all-models weekly budget this scoped limit is allowed (e.g. 50 for Fable).</summary>
    public double? AllowanceOfWeekly { get; init; }

    public bool HasReset(DateTimeOffset now) => ResetsAt is { } r && r <= now;

    public double Remaining(DateTimeOffset now) => HasReset(now) ? 100 : Math.Clamp(100 - UsedPercent, 0, 100);

    /// <summary>How much of the window has elapsed, 0..1.</summary>
    public double? ElapsedFraction(DateTimeOffset now)
    {
        if (ResetsAt is not { } reset || Window <= TimeSpan.Zero)
        {
            return null;
        }
        if (reset <= now)
        {
            return 0;
        }
        return Math.Clamp(1 - (reset - now) / Window, 0, 1);
    }

    /// <summary>If the current burn rate continues, when does this limit hit 100% (only when that is before the reset).</summary>
    public TimeSpan? ProjectedExhaustion(DateTimeOffset now)
    {
        if (ResetsAt is not { } reset || reset <= now || ElapsedFraction(now) is not { } elapsed)
        {
            return null;
        }
        double used = UsedPercent;
        if (used < 5 || used >= 100 || elapsed < 0.05)
        {
            return null;
        }
        TimeSpan elapsedTime = Window * elapsed;
        double perSecond = used / elapsedTime.TotalSeconds;
        var left = TimeSpan.FromSeconds((100 - used) / perSecond);
        return left < reset - now ? left : null;
    }
}

internal sealed class UsageSnapshot
{
    public required IReadOnlyList<LimitInfo> Limits { get; init; }
    public required DateTimeOffset FetchedAt { get; init; }
    public string? ExtraUsageText { get; init; }

    public LimitInfo? Session => Limits.FirstOrDefault(l => l.Kind == LimitKind.Session);
    public LimitInfo? WeeklyAll => Limits.FirstOrDefault(l => l.Kind == LimitKind.WeeklyAll);

    public LimitInfo? Lowest(DateTimeOffset now) => Limits.OrderBy(l => l.Remaining(now)).FirstOrDefault();

    /// <summary>
    /// A scoped weekly limit (e.g. Fable) also draws from the all-models weekly budget,
    /// so when that budget is nearly gone the scoped limit cannot actually be used up.
    /// Returns the effective remaining percentage of the scoped cap when it is lower than the raw one.
    /// </summary>
    public double? EffectiveScopedRemaining(LimitInfo limit, DateTimeOffset now)
    {
        if (limit.Kind != LimitKind.WeeklyScoped || limit.AllowanceOfWeekly is not { } allowance || allowance <= 0 || WeeklyAll is not { } weekly)
        {
            return null;
        }
        double effective = Math.Clamp(weekly.Remaining(now) / allowance * 100, 0, 100);
        return effective < limit.Remaining(now) - 0.5 ? effective : null;
    }
}

internal static class UsageParser
{
    public static UsageSnapshot Parse(string json, DateTimeOffset fetchedAt)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        var limits = new List<LimitInfo>();

        if (root.TryGetProperty("limits", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            var shares = ParseShares(root);
            int index = -1;
            foreach (JsonElement item in array.EnumerateArray())
            {
                index++;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                string kind = Str(item, "kind") ?? "";
                string group = Str(item, "group") ?? "";
                double? percent = Num(item, "percent") ?? Num(item, "utilization");
                if (percent is null)
                {
                    continue;
                }
                DateTimeOffset? resets = Date(item, "resets_at");
                TimeSpan window = group == "session" || kind == "session" ? TimeSpan.FromHours(5)
                    : group == "weekly" || kind.StartsWith("weekly", StringComparison.Ordinal) ? TimeSpan.FromDays(7)
                    : TimeSpan.Zero;

                switch (kind)
                {
                    case "session":
                        limits.Add(new LimitInfo
                        {
                            Key = "session", Kind = LimitKind.Session, Label = Loc.SessionLabel, ShortLabel = Loc.SessionShort,
                            UsedPercent = Precise(root, "five_hour", percent.Value), ResetsAt = resets, Window = TimeSpan.FromHours(5),
                        });
                        break;
                    case "weekly_all":
                        limits.Add(new LimitInfo
                        {
                            Key = "weekly_all", Kind = LimitKind.WeeklyAll, Label = Loc.WeeklyLabel, ShortLabel = Loc.WeeklyShort,
                            UsedPercent = Precise(root, "seven_day", percent.Value), ResetsAt = resets, Window = TimeSpan.FromDays(7),
                        });
                        break;
                    default:
                        string name = ScopeName(item) ?? Humanize(kind);
                        bool scoped = kind.Contains("scoped", StringComparison.Ordinal);
                        limits.Add(new LimitInfo
                        {
                            Key = $"{kind}:{name}",
                            Kind = scoped ? LimitKind.WeeklyScoped : LimitKind.Other,
                            Label = window == TimeSpan.FromDays(7) ? Loc.WeeklyScopedLabel(name) : name,
                            ShortLabel = name,
                            UsedPercent = percent.Value,
                            ResetsAt = resets,
                            Window = window,
                            AllowanceOfWeekly = shares.TryGetValue(index, out double allowance) ? allowance : null,
                        });
                        break;
                }
            }
        }

        if (limits.Count == 0)
        {
            AddLegacy(root, limits);
        }

        var ordered = limits
            .Select((l, i) => (l, i))
            .OrderBy(t => t.l.Kind switch { LimitKind.Session => 0, LimitKind.WeeklyAll => 1, LimitKind.WeeklyScoped => 2, _ => 3 })
            .ThenBy(t => t.i)
            .Select(t => t.l)
            .ToList();

        return new UsageSnapshot { Limits = ordered, FetchedAt = fetchedAt, ExtraUsageText = ParseSpend(root) };
    }

    /// <summary>Older response shape without the "limits" array.</summary>
    private static void AddLegacy(JsonElement root, List<LimitInfo> limits)
    {
        void Add(string prop, LimitKind kind, string key, string label, string shortLabel, TimeSpan window)
        {
            if (root.TryGetProperty(prop, out var obj) && obj.ValueKind == JsonValueKind.Object && Num(obj, "utilization") is { } used)
            {
                limits.Add(new LimitInfo
                {
                    Key = key, Kind = kind, Label = label, ShortLabel = shortLabel, UsedPercent = used,
                    ResetsAt = Date(obj, "resets_at"), Window = window,
                });
            }
        }

        Add("five_hour", LimitKind.Session, "session", Loc.SessionLabel, Loc.SessionShort, TimeSpan.FromHours(5));
        Add("seven_day", LimitKind.WeeklyAll, "weekly_all", Loc.WeeklyLabel, Loc.WeeklyShort, TimeSpan.FromDays(7));
        Add("seven_day_opus", LimitKind.WeeklyScoped, "weekly_scoped:Opus", Loc.WeeklyScopedLabel("Opus"), "Opus", TimeSpan.FromDays(7));
        Add("seven_day_sonnet", LimitKind.WeeklyScoped, "weekly_scoped:Sonnet", Loc.WeeklyScopedLabel("Sonnet"), "Sonnet", TimeSpan.FromDays(7));
    }

    private static Dictionary<int, double> ParseShares(JsonElement root)
    {
        var result = new Dictionary<int, double>();
        if (root.TryGetProperty("weekly_scoped_shares", out var shares) && shares.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement share in shares.EnumerateArray())
            {
                if (Num(share, "limit_index") is { } idx && Num(share, "allowance_percent_of_weekly") is { } allowance)
                {
                    result[(int)idx] = allowance;
                }
            }
        }
        return result;
    }

    private static string? ParseSpend(JsonElement root)
    {
        if (!root.TryGetProperty("spend", out var spend) || spend.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!spend.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True)
        {
            return null;
        }
        string? used = Money(spend, "used");
        if (used is null)
        {
            return null;
        }
        string? limit = Money(spend, "limit");
        return limit is null ? Loc.ExtraUsageNoLimit(used) : Loc.ExtraUsage(used, limit);
    }

    private static string? Money(JsonElement parent, string prop)
    {
        if (!parent.TryGetProperty(prop, out var m) || m.ValueKind != JsonValueKind.Object || Num(m, "amount_minor") is not { } minor)
        {
            return null;
        }
        int exponent = (int)(Num(m, "exponent") ?? 2);
        string currency = Str(m, "currency") ?? "USD";
        decimal amount = (decimal)minor / (decimal)Math.Pow(10, exponent);
        string number = amount.ToString("N" + exponent, CultureInfo.InvariantCulture);
        return currency == "USD" ? $"${number}" : $"{number} {currency}";
    }

    /// <summary>Prefer the float utilization of the legacy object when it describes the same window.</summary>
    private static double Precise(JsonElement root, string prop, double fallback)
    {
        if (root.TryGetProperty(prop, out var obj) && obj.ValueKind == JsonValueKind.Object && Num(obj, "utilization") is { } u && Math.Abs(u - fallback) < 1)
        {
            return u;
        }
        return fallback;
    }

    private static string? ScopeName(JsonElement item)
    {
        if (!item.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (string part in new[] { "model", "surface" })
        {
            if (scope.TryGetProperty(part, out var p))
            {
                if (p.ValueKind == JsonValueKind.Object && (Str(p, "display_name") ?? Str(p, "id")) is { Length: > 0 } name)
                {
                    return name;
                }
                if (p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s)
                {
                    return Humanize(s);
                }
            }
        }
        return null;
    }

    private static string Humanize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "?";
        }
        string spaced = raw.Replace('_', ' ').Trim();
        return char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static DateTimeOffset? Date(JsonElement e, string prop) =>
        Str(e, prop) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

internal static class Fmt
{
    /// <summary>Remaining percentage as shown to the user: floor, but never claims 0 while something is left.</summary>
    public static string Percent(double remaining)
    {
        if (remaining >= 1)
        {
            return ((int)Math.Floor(remaining)).ToString(CultureInfo.InvariantCulture);
        }
        return remaining > 0 ? "<1" : "0";
    }

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
        {
            return Loc.LessThanMinute;
        }
        int days = (int)span.TotalDays;
        int hours = span.Hours;
        int minutes = span.Minutes;
        if (days > 0)
        {
            return hours > 0 ? $"{Loc.Days(days)} {Loc.Hours(hours)}" : Loc.Days(days);
        }
        if (hours > 0)
        {
            return minutes > 0 ? $"{Loc.Hours(hours)} {Loc.Minutes(minutes)}" : Loc.Hours(hours);
        }
        return Loc.Minutes(Math.Max(1, (int)Math.Ceiling(span.TotalMinutes)));
    }

    public static string Ago(TimeSpan span) => span < TimeSpan.FromSeconds(45) ? Loc.JustNow : Loc.Ago(Duration(span));

    public static string ClockTime(DateTimeOffset when)
    {
        // Round to the nearest minute: resets are reported as e.g. 11:59:59.9 or 12:00:00.1.
        DateTime local = when.AddSeconds(30).LocalDateTime;
        DateTime today = DateTime.Today;
        string time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today)
        {
            return $"{Loc.Today} {time}";
        }
        if (local.Date == today.AddDays(1))
        {
            return $"{Loc.Tomorrow} {time}";
        }
        var culture = Loc.Ko ? CultureInfo.GetCultureInfo("ko-KR") : CultureInfo.CurrentCulture;
        string day = local.ToString(Loc.Ko ? "M/d (ddd)" : "ddd M/d", culture);
        return $"{day} {time}";
    }
}
