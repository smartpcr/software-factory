// -----------------------------------------------------------------------
// <copyright file="QaE2eConvergenceAndRotationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Collections.Generic;
using System.Linq;
using Forge.Api.Services;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Regressions for the two qa-e2e orchestration gaps that let
/// common-infra:HTTP-MEMORY stage 2.1-e2e burn 40 iterations × 3 pair
/// attempts (120 generator runs, score frozen at 68) before terminal-failing:
/// <list type="number">
///   <item><b>Convergence detector blind to prose notes.</b>
///   <see cref="DeliverableLoop.DetectConvergenceLoop"/> only saw items under
///   a "What still needs work" checklist; the qa-e2e evaluator writes a
///   paragraph, so the detector never fired and the loop ran to the iter cap.</item>
///   <item><b>Generator never rotated.</b>
///   <see cref="StorySpawnOrchestrator.ExtractPriorGenerators"/> feeds the
///   qa-e2e re-pick the set of already-tried generators so a reassignment
///   retry draws a fresh crew-qa member instead of re-running the same one.</item>
/// </list>
/// </summary>
public class QaE2eConvergenceAndRotationTests
{
    // The byte-identical evaluator note observed on every one of the 40 iters
    // of common-infra:HTTP-MEMORY 2.1-e2e — prose, no checklist markers.
    private const string QaE2eProseNote =
        "The .feature file covers all three required acceptance scenarios and "
        + "every referenced step has a binding. However, the bindings validate "
        + "private in-file stubs instead of the real Http.Trace "
        + "RequestTraceExpressions/IBodyByteCapacityTrace implementation, and the "
        + "compile-failure scenario uses a synthetic Roslyn library rather than an "
        + "external in-tree TestServices referencing assembly. The tests could "
        + "pass with no production implementation, so acceptance coverage is "
        + "superficial.";

    private static (int Iter, int Score, string Verdict, string Notes) Iter(int iter, string notes) =>
        (iter, 68, "iterate", notes);

    // ── Fix #1: convergence detection on prose evaluator notes ──────────────

    [Fact]
    public void DetectConvergenceLoop_Fires_On_Repeated_Prose_Notes()
    {
        // Five iters of byte-identical prose feedback — the real 2.1-e2e shape.
        var chain = Enumerable.Range(1, 5)
            .Select(i => Iter(i, QaE2eProseNote))
            .ToList();

        var (looping, recurring) = DeliverableLoop.DetectConvergenceLoop(chain);

        Assert.True(looping, "Identical prose evaluator feedback across the window must be detected as a convergence loop.");
        Assert.True(recurring.Count >= 2, "Expected at least two recurring prose items.");
    }

    [Fact]
    public void DetectConvergenceLoop_Does_Not_Fire_When_Prose_Feedback_Changes()
    {
        // Each iter raises a genuinely different concern → not a loop.
        var chain = new List<(int, int, string, string)>
        {
            Iter(1, "The scenario for header capping is missing entirely; add a Given/When/Then that exercises the request header path end to end."),
            Iter(2, "The async-flow nesting scenario does not restore the ambient scope on exception; wrap the assertion in a try/finally and verify Current after."),
            Iter(3, "The pipeline YAML stub references a non-existent stage name; align it with the e2e job id declared in the base file."),
            Iter(4, "The reflection probe asserts the wrong sealed modifier; check IsSealed on the runtime type, not the declaring assembly."),
            Iter(5, "The default-caps scenario hardcodes 4096 but the contract default is 8192; update the expected value and the comment."),
        };

        var (looping, _) = DeliverableLoop.DetectConvergenceLoop(chain);

        Assert.False(looping, "Distinct, evolving feedback must not be flagged as a convergence loop.");
    }

    [Fact]
    public void DetectConvergenceLoop_Still_Fires_On_Repeated_Checklist_Items()
    {
        // Regression guard: the established "What still needs work" checklist
        // format must keep working unchanged after the prose fallback.
        const string note =
            "## What still needs work\n"
            + "- [ ] 1. §3.1 the StreamEvaluator binding still validates a private stub instead of the shipped RequestTraceExpressions type.\n"
            + "- [ ] 2. §4.2 the compile-failure scenario uses a synthetic Roslyn library rather than the in-tree TestServices assembly.\n";
        var chain = Enumerable.Range(1, 4).Select(i => Iter(i, note)).ToList();

        var (looping, recurring) = DeliverableLoop.DetectConvergenceLoop(chain);

        Assert.True(looping, "Repeated checklist items must still be detected (no regression from the prose fallback).");
        Assert.True(recurring.Count >= 2);
    }

    // ── Fix #2: prior-generator extraction for pair rotation ────────────────

    [Fact]
    public void ExtractPriorGenerators_Returns_Empty_For_Null_Or_Blank()
    {
        Assert.Empty(StorySpawnOrchestrator.ExtractPriorGenerators(null));
        Assert.Empty(StorySpawnOrchestrator.ExtractPriorGenerators(""));
        Assert.Empty(StorySpawnOrchestrator.ExtractPriorGenerators("   "));
    }

    [Fact]
    public void ExtractPriorGenerators_Reads_Generator_Slot_Even_When_Evaluator_Empty()
    {
        // The exact malformed shape recorded for the 2.1-e2e work item:
        // single pair, empty evaluator slot. The generator must still be read
        // so the next attempt excludes it.
        var gens = StorySpawnOrchestrator.ExtractPriorGenerators("[[\"JS2\",\"\"]]");

        Assert.Equal(new[] { "JS2" }, gens.OrderBy(g => g).ToArray());
    }

    [Fact]
    public void ExtractPriorGenerators_Collects_All_Distinct_Generators()
    {
        var gens = StorySpawnOrchestrator.ExtractPriorGenerators(
            "[[\"JS2\",\"Q2\"],[\"DC\",\"Q2\"],[\"WG\",\"Q2\"]]");

        Assert.Equal(new[] { "DC", "JS2", "WG" }, gens.OrderBy(g => g).ToArray());
    }

    [Fact]
    public void ExtractPriorGenerators_Tolerates_Malformed_Json()
    {
        Assert.Empty(StorySpawnOrchestrator.ExtractPriorGenerators("not-json"));
        Assert.Empty(StorySpawnOrchestrator.ExtractPriorGenerators("[\"flat\",\"array\"]"));
    }
}
