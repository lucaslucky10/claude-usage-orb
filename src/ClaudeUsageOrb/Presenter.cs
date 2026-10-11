using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace ClaudeUsageOrb;

internal sealed record TrayModel(string? Number, Color Ring, double Fraction, bool Dim, string Tooltip);

/// <summary>Turns the poller state into what the orb, the hover card and the tray icon show.</summary>
internal static class Presenter
{
    public static readonly Color SessionColor = Color.FromRgb(0xF0, 0x8A, 0x5D);
    public static readonly Color WeeklyColor = Color.FromRgb(0x5A, 0xA9, 0xF8);
    public static readonly Color[] ScopedColors = { Color.FromRgb(0xB4, 0x8C, 0xFF), Color.FromRgb(0x3F, 0xD0, 0xA4), Color.FromRgb(0xF2, 0xC9, 0x4C) };
    public static readonly Color DangerColor = Color.FromRgb(0xFF, 0x5A, 0x5F);
    private static readonly Color WarnText = Color.FromRgb(0xF5, 0xB7, 0x3B);
    private static readonly Color NormalText = Color.FromRgb(0xF6, 0xF6, 0xF8);
    private static readonly Color MutedText = Color.FromRgb(0xC8, 0xC8, 0xCC);

    public static (OrbModel Orb, PopupModel Popup, TrayModel Tray) Build(Settings settings, UsagePoller poller, DateTimeOffset now)
    {
        UsageSnapshot? snap = poller.Snapshot;
        bool stale = poller.IsStale(now);
        bool authProblem = poller.Status is PollStatus.AuthExpired or PollStatus.NoCredentials;

        OrbIndicator indicator = authProblem && (snap is null || stale) ? OrbIndicator.Error
            : stale && poller.Status != PollStatus.Loading ? OrbIndicator.Stale
            : OrbIndicator.None;

        var rings = new List<RingModel>();
        var rows = new List<PopupRow>();
        LimitInfo? primary = null;
        Color primaryColor = Color.FromRgb(0x80, 0x80, 0x88);
        if (snap is not null)
        {
            int scopedIndex = 0;
            foreach (LimitInfo limit in snap.Limits)
            {
                Color color = limit.Kind switch
                {
                    LimitKind.Session => SessionColor,
                    LimitKind.WeeklyAll => WeeklyColor,
                    _ => ScopedColors[Math.Min(scopedIndex++, ScopedColors.Length - 1)],
                };
                double remaining = limit.Remaining(now);
                double? elapsed = limit.ElapsedFraction(now);
                double? marker = elapsed is { } e && settings.ShowPaceMarker ? 1 - e : null;
                if (rings.Count < 3)
                {
                    rings.Add(new RingModel(remaining < 10 ? DangerColor : color, remaining / 100, marker));
                }
                rows.Add(BuildRow(snap, limit, color, remaining, marker, now));
            }
            primary = settings.CenterMetric == "lowest" ? snap.Lowest(now) : snap.Session ?? snap.Limits.FirstOrDefault();
            if (primary is not null)
            {
                int index = snap.Limits.ToList().IndexOf(primary);
                primaryColor = index >= 0 && index < rows.Count ? rows[index].Color : primaryColor;
            }
        }

        double primaryLeft = primary?.Remaining(now) ?? 100;
        string centerText = primary is null
            ? (poller.Status == PollStatus.Loading ? "…" : "!")
            : Fmt.Percent(primaryLeft);
        Color centerColor = primary is null ? MutedText
            : primaryLeft < 10 ? DangerColor
            : primaryLeft < 25 ? WarnText
            : NormalText;
        var orb = new OrbModel(rings, centerText, primary?.ShortLabel ?? "", centerColor, indicator);

        string? status = StatusText(poller, settings, now, snap is not null);
        string updated = snap is null ? "" : Loc.UpdatedAgo(Fmt.Ago(now - snap.FetchedAt));
        if (poller.IsFetching)
        {
            updated = "⟳ " + updated;
        }
        var popup = new PopupModel(Loc.PopupTitle, PlanName(poller.Plan), updated, rows, snap?.ExtraUsageText, status, authProblem);

        string tip;
        if (snap is null)
        {
            tip = $"{Loc.AppName}\n{status ?? Loc.Loading}";
        }
        else
        {
            string parts = string.Join(" · ", snap.Limits.Select(l => $"{l.ShortLabel} {Fmt.Percent(l.Remaining(now))}%"));
            tip = $"Claude {(Loc.Ko ? "남은 양" : "left")}: {parts}\n{Loc.UpdatedAgo(Fmt.Ago(now - snap.FetchedAt))}";
            if (poller.Status is not (PollStatus.Ok or PollStatus.Loading))
            {
                tip += " ⚠";
            }
        }
        var tray = new TrayModel(primary is null ? null : Fmt.Percent(primaryLeft),
            primaryLeft < 10 && primary is not null ? DangerColor : primaryColor,
            primary is null ? 0 : primaryLeft / 100, indicator != OrbIndicator.None, tip);

        return (orb, popup, tray);
    }

    private static PopupRow BuildRow(UsageSnapshot snap, LimitInfo limit, Color color, double remaining, double? marker, DateTimeOffset now)
    {
        string sub;
        if (limit.HasReset(now))
        {
            sub = Loc.ResetPending;
        }
        else if (limit.ResetsAt is { } reset)
        {
            sub = $"{Loc.ResetsIn(Fmt.Duration(reset - now))} · {Fmt.ClockTime(reset)}";
        }
        else
        {
            sub = "";
        }

        string? note = null;
        if (snap.EffectiveScopedRemaining(limit, now) is { } effective)
        {
            note = Loc.BoundByWeekly(Fmt.Percent(effective));
        }
        else if (limit.ProjectedExhaustion(now) is { } runOut)
        {
            note = Loc.PaceWarning(Fmt.Duration(runOut));
        }
        return new PopupRow(limit.Label, color, remaining, Loc.Remaining(Fmt.Percent(remaining)), sub, marker, note, note is not null);
    }

    public static string? StatusText(UsagePoller poller, Settings settings, DateTimeOffset now, bool haveData)
    {
        string retry = poller.NextAttempt is { } next && next > now ? Fmt.Duration(next - now) : Loc.LessThanMinute;
        string? text = poller.Status switch
        {
            PollStatus.Loading => haveData ? null : Loc.Loading,
            PollStatus.NoCredentials => Loc.NoCredentials,
            PollStatus.AuthExpired => poller.IsRenewing ? Loc.AuthRenewing : settings.AutoRenewToken ? Loc.AuthRenewFailed : Loc.AuthExpired,
            PollStatus.RateLimited => Loc.RateLimited(retry),
            PollStatus.NetworkError => Loc.NetworkError(retry),
            PollStatus.ServerError => Loc.ServerError(poller.LastHttpStatus ?? 0, retry),
            _ => null,
        };
        if (text is not null && haveData && poller.Status != PollStatus.Loading)
        {
            text += $" ({Loc.ShowingCached})";
        }
        return text;
    }

    private static string? PlanName(string? plan) => plan?.ToLowerInvariant() switch
    {
        null or "" => null,
        "max" => "Max",
        "pro" => "Pro",
        "team" => "Team",
        "enterprise" => "Enterprise",
        _ => plan,
    };
}
