// -----------------------------------------------------------------------
// <copyright file="CallChainContextTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory.UnitTests;

using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class CallChainContextTests
{
    private InMemoryAgentMemoryStore store = null!;
    private ContextBuilder builder = null!;

    [TestInitialize]
    public void Setup()
    {
        this.store = new InMemoryAgentMemoryStore();
        this.builder = new ContextBuilder(this.store, graphAdapter: null, callChainStore: this.store);
    }

    [TestMethod]
    public async Task BuildContext_IncludesCallChainsForMatchingSymbols()
    {
        var chain = new CallChain
        {
            Id = "cc-1",
            EntrySymbolId = "MyApp.Parser.Parse",
            TerminalSymbolId = "MyApp.Tokenizer.Tokenize",
            Confidence = 0.9,
            Source = CallChainSource.Observed,
            Steps =
            [
                new CallChainStep { SymbolId = "MyApp.Parser.Parse", StepIndex = 0 },
                new CallChainStep { SymbolId = "MyApp.Tokenizer.Tokenize", StepIndex = 1 }
            ]
        };
        await this.store.UpsertCallChainAsync(chain);

        var context = await this.builder.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "Fix parser",
            SymbolIds = ["MyApp.Parser.Parse"]
        });

        context.CallChains.Should().ContainSingle(c => c.Id == "cc-1");
    }

    [TestMethod]
    public async Task BuildContext_ExcludesCallChainsWithoutMatchingSymbols()
    {
        var chain = new CallChain
        {
            Id = "cc-unrelated",
            EntrySymbolId = "MyApp.Logger.Log",
            Steps = [new CallChainStep { SymbolId = "MyApp.Logger.Log", StepIndex = 0 }],
            Confidence = 0.9
        };
        await this.store.UpsertCallChainAsync(chain);

        var context = await this.builder.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "Fix parser",
            SymbolIds = ["MyApp.Parser.Parse"]
        });

        context.CallChains.Should().BeEmpty();
    }

    [TestMethod]
    public async Task BuildContext_RanksObservedChainsHigherThanInferred()
    {
        var observed = new CallChain
        {
            Id = "cc-observed",
            Source = CallChainSource.Observed,
            Confidence = 0.7,
            ObservationCount = 3,
            Steps = [new CallChainStep { SymbolId = "MyApp.Parser.Parse", StepIndex = 0 }]
        };
        var inferred = new CallChain
        {
            Id = "cc-inferred",
            Source = CallChainSource.Inferred,
            Confidence = 0.7,
            ObservationCount = 3,
            Steps = [new CallChainStep { SymbolId = "MyApp.Parser.Parse", StepIndex = 0 }]
        };

        await this.store.UpsertCallChainAsync(observed);
        await this.store.UpsertCallChainAsync(inferred);

        var context = await this.builder.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "Fix parser",
            SymbolIds = ["MyApp.Parser.Parse"]
        });

        context.CallChains.Should().HaveCount(2);
        context.CallChains[0].Id.Should().Be("cc-observed");
    }

    [TestMethod]
    public async Task BuildContext_RespectsMaxCallChains()
    {
        for (var i = 0; i < 20; i++)
        {
            await this.store.UpsertCallChainAsync(new CallChain
            {
                Id = $"cc-{i}",
                Confidence = 0.5,
                Steps = [new CallChainStep { SymbolId = "MyApp.Parser.Parse", StepIndex = 0 }]
            });
        }

        var context = await this.builder.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "Fix parser",
            SymbolIds = ["MyApp.Parser.Parse"],
            MaxCallChains = 5
        });

        context.CallChains.Should().HaveCount(5);
    }

    [TestMethod]
    public async Task BuildContext_WithoutCallChainStore_ReturnsEmptyCallChains()
    {
        var builderNoChains = new ContextBuilder(this.store);

        var context = await builderNoChains.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "Fix parser",
            SymbolIds = ["MyApp.Parser.Parse"]
        });

        context.CallChains.Should().BeEmpty();
    }

    [TestMethod]
    public async Task BuildContext_CallChainScoring_HigherSymbolOverlapScoresHigher()
    {
        var multiMatch = new CallChain
        {
            Id = "cc-multi",
            Confidence = 0.5,
            Steps =
            [
                new CallChainStep { SymbolId = "MyApp.Parser.Parse", StepIndex = 0 },
                new CallChainStep { SymbolId = "MyApp.Tokenizer.Tokenize", StepIndex = 1 }
            ]
        };
        var singleMatch = new CallChain
        {
            Id = "cc-single",
            Confidence = 0.5,
            Steps = [new CallChainStep { SymbolId = "MyApp.Parser.Parse", StepIndex = 0 }]
        };

        await this.store.UpsertCallChainAsync(multiMatch);
        await this.store.UpsertCallChainAsync(singleMatch);

        var context = await this.builder.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "Refactor parser and tokenizer",
            SymbolIds = ["MyApp.Parser.Parse", "MyApp.Tokenizer.Tokenize"]
        });

        context.CallChains.Should().HaveCountGreaterThanOrEqualTo(2);
        context.CallChains[0].Id.Should().Be("cc-multi");
    }
}
