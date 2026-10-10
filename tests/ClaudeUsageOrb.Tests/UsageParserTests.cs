using System;
using System.Linq;
using Xunit;

namespace ClaudeUsageOrb.Tests;

public class UsageParserTests
{
    // Shape of a real /api/oauth/usage response (Oct 2026), trimmed to the fields the app reads.
    private const string CurrentShape = """
    {
      "five_hour": { "utilization": 14.0, "resets_at": "2026-10-10T13:10:00.103451+00:00" },
      "seven_day": { "utilization": 33.0, "resets_at": "2026-10-11T12:00:00.103476+00:00" },
      "seven_day_opus": null,
      "seven_day_sonnet": null,
      "limits": [
        { "kind": "session", "group": "session", "percent": 14, "severity": "normal", "resets_at": "2026-10-10T13:10:00.103451+00:00", "scope": null, "is_active": false },
        { "kind": "weekly_all", "group": "weekly", "percent": 33, "severity": "normal", "resets_at": "2026-10-11T12:00:00.103476+00:00", "scope": null, "is_active": true },
        { "kind": "weekly_scoped", "group": "weekly", "percent": 18, "severity": "normal", "resets_at": "2026-10-11T12:00:00.103687+00:00",
          "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null }, "is_active": false }
      ],
      "spend": { "used": { "amount_minor": 0, "currency": "USD", "exponent": 2 }, "limit": null, "enabled": false },
      "weekly_scoped_shares": [ { "limit_index": 2, "used_percent_of_weekly": 9, "allowance_percent_of_weekly": 50 } ]
    }
    """;

    private static readonly DateTimeOffset Now = new(2026, 10, 10, 11, 0, 0, TimeSpan.Zero);

    public UsageParserTests() => Loc.Init("en");

    [Fact]
    public void Parses_session_weekly_and_scoped_limits_in_order()
    {
        var snap = UsageParser.Parse(CurrentShape, Now);

        Assert.Equal(new[] { LimitKind.Session, LimitKind.WeeklyAll, LimitKind.WeeklyScoped }, snap.Limits.Select(l => l.Kind));
        Assert.Equal(86, snap.Limits[0].Remaining(Now));
        Assert.Equal(67, snap.Limits[1].Remaining(Now));
        Assert.Equal(82, snap.Limits[2].Remaining(Now));
        Assert.Equal("Fable", snap.Limits[2].ShortLabel);
        Assert.Equal("Weekly · Fable", snap.Limits[2].Label);
        Assert.Equal(TimeSpan.FromHours(5), snap.Limits[0].Window);
        Assert.Equal(TimeSpan.FromDays(7), snap.Limits[2].Window);
        Assert.Equal(50, snap.Limits[2].AllowanceOfWeekly);
        Assert.Null(snap.ExtraUsageText);
    }

    [Fact]
    public void Labels_follow_the_ui_language()
    {
        Loc.Init("ko");
        var snap = UsageParser.Parse(CurrentShape, Now);
        Assert.Equal("주간 · Fable", snap.Limits[2].Label);
        Assert.Equal("세션 (5시간)", snap.Limits[0].Label);
        Loc.Init("en");
    }

    [Fact]
    public void Falls_back_to_legacy_fields_without_limits_array()
    {
        const string legacy = """
        {
          "five_hour": { "utilization": 40.5, "resets_at": "2026-10-10T13:10:00+00:00" },
          "seven_day": { "utilization": 70, "resets_at": "2026-10-11T12:00:00+00:00" },
          "seven_day_opus": { "utilization": 12, "resets_at": "2026-10-11T12:00:00+00:00" },
          "seven_day_sonnet": null
        }
        """;
        var snap = UsageParser.Parse(legacy, Now);

        Assert.Equal(3, snap.Limits.Count);
        Assert.Equal(59.5, snap.Limits[0].Remaining(Now), 3);
        Assert.Equal("Opus", snap.Limits[2].ShortLabel);
    }

    [Fact]
    public void Unknown_future_limit_kinds_are_kept()
    {
        const string json = """
        { "limits": [
            { "kind": "session", "group": "session", "percent": 5, "resets_at": "2026-10-10T13:10:00+00:00" },
            { "kind": "monthly_surface", "group": "monthly", "percent": 61, "resets_at": "2026-11-01T00:00:00+00:00",
              "scope": { "model": null, "surface": { "display_name": "Cowork" } } }
        ] }
        """;
        var snap = UsageParser.Parse(json, Now);

        Assert.Equal(2, snap.Limits.Count);
        Assert.Equal(LimitKind.Other, snap.Limits[1].Kind);
        Assert.Equal("Cowork", snap.Limits[1].ShortLabel);
        Assert.Equal(39, snap.Limits[1].Remaining(Now));
    }

    [Fact]
    public void Scoped_limit_is_capped_by_the_weekly_budget()
    {
        string json = CurrentShape.Replace("\"utilization\": 33.0", "\"utilization\": 90.0").Replace("\"percent\": 33", "\"percent\": 90");
        var snap = UsageParser.Parse(json, Now);
        LimitInfo fable = snap.Limits[2];

        // 10% of the weekly budget is left and Fable may use 50% of it -> at most 20% of the Fable cap is usable.
        Assert.Equal(20, snap.EffectiveScopedRemaining(fable, Now)!.Value, 3);
        Assert.Null(UsageParser.Parse(CurrentShape, Now).EffectiveScopedRemaining(fable, Now));
    }

    [Fact]
    public void A_window_that_has_reset_counts_as_full_until_the_next_poll()
    {
        var snap = UsageParser.Parse(CurrentShape, Now);
        var afterReset = new DateTimeOffset(2026, 10, 10, 13, 11, 0, TimeSpan.Zero);

        Assert.True(snap.Limits[0].HasReset(afterReset));
        Assert.Equal(100, snap.Limits[0].Remaining(afterReset));
    }

    [Fact]
    public void Projects_running_out_before_the_reset()
    {
        // 50% used after 1h15m of a 5h window -> the rest lasts another 1h15m, well before the reset.
        var reset = Now.AddHours(3.75);
        var limit = new LimitInfo
        {
            Key = "session", Kind = LimitKind.Session, Label = "s", ShortLabel = "5h",
            UsedPercent = 50, ResetsAt = reset, Window = TimeSpan.FromHours(5),
        };

        TimeSpan? left = limit.ProjectedExhaustion(Now);
        Assert.NotNull(left);
        Assert.Equal(75, left!.Value.TotalMinutes, 1);
        Assert.Equal(0.25, limit.ElapsedFraction(Now)!.Value, 3);
    }

    [Fact]
    public void Extra_usage_is_formatted_from_minor_units()
    {
        string json = CurrentShape.Replace(
            "\"spend\": { \"used\": { \"amount_minor\": 0, \"currency\": \"USD\", \"exponent\": 2 }, \"limit\": null, \"enabled\": false }",
            "\"spend\": { \"used\": { \"amount_minor\": 1234, \"currency\": \"USD\", \"exponent\": 2 }, \"limit\": { \"amount_minor\": 5000, \"currency\": \"USD\", \"exponent\": 2 }, \"enabled\": true }");
        var snap = UsageParser.Parse(json, Now);

        Assert.Equal("Extra usage $12.34 / $50.00", snap.ExtraUsageText);
    }

    [Theory]
    [InlineData(86.0, "86")]
    [InlineData(99.99, "99")]
    [InlineData(0.4, "<1")]
    [InlineData(0, "0")]
    [InlineData(100, "100")]
    public void Percent_never_rounds_up_or_hides_a_remainder(double remaining, string expected)
    {
        Assert.Equal(expected, Fmt.Percent(remaining));
    }

    [Fact]
    public void Durations_are_compact()
    {
        Assert.Equal("2h 26m", Fmt.Duration(TimeSpan.FromMinutes(146)));
        Assert.Equal("1d 3h", Fmt.Duration(TimeSpan.FromHours(27.2)));
        Assert.Equal("<1 min", Fmt.Duration(TimeSpan.FromSeconds(20)));
    }
}
