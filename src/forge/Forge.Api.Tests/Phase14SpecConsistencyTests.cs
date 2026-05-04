// -----------------------------------------------------------------------
// <copyright file="Phase14SpecConsistencyTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.RegularExpressions;
using Forge.Api;
using Xunit;

/// <summary>
/// Phase 14.14 — fidelity gate. Parses
/// <c>docs/forge/scoring-and-promotion.md</c> + <c>agents/_preamble.md</c>
/// and asserts that the locked numbers in <see cref="ScoringConstants"/>
/// (and the embedded preamble rubric) match the spec verbatim. If you
/// bump a number on one side without bumping the other, this fails CI —
/// preventing silent drift between the prose spec and the running code.
/// </summary>
public class Phase14SpecConsistencyTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "agents"))
                && Directory.Exists(Path.Combine(dir.FullName, "docs", "forge")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static string ReadSpec()
    {
        var path = Path.Combine(RepoRoot(), "docs", "forge", "scoring-and-promotion.md");
        Assert.True(File.Exists(path), $"Spec missing: {path}");
        return File.ReadAllText(path);
    }

    private static string ReadPreamble()
    {
        var path = Path.Combine(RepoRoot(), "agents", "_preamble.md");
        Assert.True(File.Exists(path), $"Preamble missing: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Rubric_Weights_Match_Spec()
    {
        var spec = ReadSpec();

        // The rubric table in §2 has rows like "| Correctness | 40 |".
        Assert.Equal(ScoringConstants.CorrectnessWeight, ParseWeight(spec, "Correctness"));
        Assert.Equal(ScoringConstants.QualityWeight,     ParseWeight(spec, "Quality"));
        Assert.Equal(ScoringConstants.TestsWeight,       ParseWeight(spec, "Tests"));
        Assert.Equal(ScoringConstants.ScopeWeight,       ParseWeight(spec, "Scope discipline"));
        Assert.Equal(ScoringConstants.RiskWeight,        ParseWeight(spec, "Risk / safety"));

        // Sum invariant — the spec body says "weights sum to 100".
        Assert.Equal(100, ScoringConstants.RubricTotal);
    }

    [Fact]
    public void Verdict_Thresholds_Match_Spec()
    {
        var spec = ReadSpec();

        // §2 verdict block: "score ≥ 90 → pass" and "70 ≤ score < 90 → iterate".
        Assert.Contains("score ≥ 90", spec);
        Assert.Contains("70 ≤ score < 90", spec);
        Assert.Contains("score < 70", spec);

        Assert.Equal(90, ScoringConstants.PassThreshold);
        Assert.Equal(70, ScoringConstants.IterateThreshold(1));
    }

    [Fact]
    public void Iteration_Cap_Matches_Spec()
    {
        var spec = ReadSpec();

        // §3: "**Hard cap: 5 iterations.**"
        Assert.Matches(new Regex(@"Hard cap:\s*5\s+iterations", RegexOptions.IgnoreCase), spec);
        Assert.Equal(5, ScoringConstants.MaxIterations);
    }

    [Fact]
    public void Schedule_Window_Matches_Spec()
    {
        var spec = ReadSpec();

        // §1: "9:00 → 21:00 (12h), Monday–Saturday".
        Assert.Contains("9:00 → 21:00", spec);
        Assert.Contains("Monday–Saturday", spec);

        Assert.Equal(9, ScoringConstants.ShiftStartHour);
        Assert.Equal(21, ScoringConstants.ShiftEndHour);
        Assert.Equal(12, ScoringConstants.ShiftHoursPerDay);

        // Mon–Sat = 6 working days; Sunday excluded.
        Assert.Equal(6, ScoringConstants.WorkDays.Length);
        Assert.DoesNotContain(DayOfWeek.Sunday, ScoringConstants.WorkDays);
    }

    [Fact]
    public void Default_Timezone_Matches_Spec()
    {
        var spec = ReadSpec();
        Assert.Contains("America/Los_Angeles", spec);
        Assert.Equal("America/Los_Angeles", ScoringConstants.DefaultTimezone);
    }

    [Fact]
    public void Hourly_Curve_Matches_Spec()
    {
        var spec = ReadSpec();

        // §5: "hourlyUsd = base + level * stepUsd      // base = $4, step = $3".
        Assert.Matches(new Regex(@"base\s*\+\s*level\s*\*\s*stepUsd"), spec);
        Assert.Matches(new Regex(@"base\s*=\s*\$?4"), spec);
        Assert.Matches(new Regex(@"step\s*=\s*\$?3"), spec);

        Assert.Equal(4m, ScoringConstants.HourlyBaseUsd);
        Assert.Equal(3m, ScoringConstants.HourlyStepUsd);

        // Spot-check the published table:
        //   level 0 → $4/hr; level 5 → $19/hr; level 10 → $34/hr.
        Assert.Equal(4m,  ScoringConstants.HourlyForLevel(0));
        Assert.Equal(19m, ScoringConstants.HourlyForLevel(5));
        Assert.Equal(34m, ScoringConstants.HourlyForLevel(10));

        // Floor + cap behaviour.
        Assert.Equal(4m,  ScoringConstants.HourlyForLevel(-3));
        Assert.Equal(34m, ScoringConstants.HourlyForLevel(99));
    }

    [Fact]
    public void Level_Range_Matches_Spec()
    {
        var spec = ReadSpec();
        // §4: "integer 0..10".
        Assert.Contains("0..10", spec);
        Assert.Equal(0, ScoringConstants.MinLevel);
        Assert.Equal(10, ScoringConstants.MaxLevel);
    }

    [Fact]
    public void Promotion_Window_And_Thresholds_Match_Spec()
    {
        var spec = ReadSpec();

        // §4: trailing 20 deliverables, ≥ 92 to promote, < 75 to demote, 30-day cooldown.
        Assert.Matches(new Regex(@"trailing\s+\**\s*20\s+deliverables", RegexOptions.IgnoreCase), spec);
        Assert.Matches(new Regex(@"rolling-avg\s+score\s+≥\s*92", RegexOptions.IgnoreCase), spec);
        Assert.Matches(new Regex(@"rolling\s+avg\s+`?<\s*75`?", RegexOptions.IgnoreCase), spec);
        Assert.Matches(new Regex(@"30\s+calendar\s+days", RegexOptions.IgnoreCase), spec);

        Assert.Equal(20, ScoringConstants.RollingWindow);
        Assert.Equal(92m, ScoringConstants.PromoteAvgMin);
        Assert.Equal(75m, ScoringConstants.DemoteAvgMax);
        Assert.Equal(30, ScoringConstants.PromotionCooldownDays);
    }

    [Fact]
    public void Lead_Floor_Matches_Spec()
    {
        var spec = ReadSpec();
        // §4: "lead requires `level ≥ 7`".
        Assert.Matches(new Regex(@"level\s*≥\s*7", RegexOptions.IgnoreCase), spec);
        Assert.Equal(7, ScoringConstants.LeadMinLevel);
    }

    [Fact]
    public void Preamble_Embeds_The_Same_Rubric()
    {
        var preamble = ReadPreamble();

        // The agent-facing preamble must repeat the rubric weights, the
        // 90/70 thresholds, the 5-iteration cap, the 92/75 promotion line,
        // and the schedule window. If any of these drift, agents and the
        // graders disagree on the contract.
        Assert.Contains($"Correctness ({ScoringConstants.CorrectnessWeight})", preamble);
        Assert.Contains($"Quality ({ScoringConstants.QualityWeight})",         preamble);
        Assert.Contains($"Tests ({ScoringConstants.TestsWeight})",             preamble);
        Assert.Contains($"Scope discipline ({ScoringConstants.ScopeWeight})",  preamble);
        Assert.Contains($"Risk / safety ({ScoringConstants.RiskWeight})",      preamble);

        Assert.Contains($"≥ {ScoringConstants.PassThreshold}",     preamble);
        Assert.Contains($"pts 1 → {ScoringConstants.IterateThreshold(1)}",  preamble);
        Assert.Contains($"up to {ScoringConstants.MaxIterations} iterations", preamble);
        Assert.Contains($"≥ {ScoringConstants.PromoteAvgMin:0}",   preamble);
        Assert.Contains($"< {ScoringConstants.DemoteAvgMax:0}",    preamble);
        Assert.Contains($"{ScoringConstants.ShiftStartHour:00}:00–{ScoringConstants.ShiftEndHour:00}:00", preamble);
    }

    private static int ParseWeight(string spec, string dimension)
    {
        // Match the spec's table rows of the form
        //   "| Correctness | 40 | …"
        // tolerant of surrounding whitespace.
        var pattern = new Regex(@"\|\s*" + Regex.Escape(dimension) + @"\s*\|\s*(\d+)\s*\|", RegexOptions.IgnoreCase);
        var m = pattern.Match(spec);
        Assert.True(m.Success, $"Could not find rubric row for '{dimension}' in spec.");
        return int.Parse(m.Groups[1].Value);
    }
}
