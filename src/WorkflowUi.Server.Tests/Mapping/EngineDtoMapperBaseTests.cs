// -----------------------------------------------------------------------
// <copyright file="EngineDtoMapperBaseTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Mapping;

using System;
using System.Collections.Generic;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;
using ExecutionEngine.Policies;
using WorkflowUi.Server.Mapping;
using Xunit;

/// <summary>
/// Unit tests for the base mapper: shared fields, policy mapping, null semantics.
/// </summary>
public class EngineDtoMapperBaseTests
{
    [Fact]
    public void MapBase_roundtrips_all_shared_fields()
    {
        var source = new NoopNodeDefinition
        {
            NodeId = "node-1",
            NodeName = "Test Node",
            Description = "A test node",
            JoinType = JoinType.All,
            Priority = NodePriority.High,
            MaxConcurrentExecutions = 5,
            CompensationNodeId = "comp-1",
            FallbackNodeId = "fallback-1",
            Configuration = new Dictionary<string, object> { ["key1"] = "value1" },
        };

        var dto = EngineDtoMapper.MapNoop(source);
        var roundTripped = EngineDtoMapper.MapNoopFromDto(dto);

        Assert.Equal(source.NodeId, roundTripped.NodeId);
        Assert.Equal(source.NodeName, roundTripped.NodeName);
        Assert.Equal(source.Description, roundTripped.Description);
        Assert.Equal(source.JoinType, roundTripped.JoinType);
        Assert.Equal(source.Priority, roundTripped.Priority);
        Assert.Equal(source.MaxConcurrentExecutions, roundTripped.MaxConcurrentExecutions);
        Assert.Equal(source.CompensationNodeId, roundTripped.CompensationNodeId);
        Assert.Equal(source.FallbackNodeId, roundTripped.FallbackNodeId);
        Assert.NotNull(roundTripped.Configuration);
    }

    [Fact]
    public void MapBase_roundtrips_retry_policy_populated()
    {
        var source = new NoopNodeDefinition
        {
            NodeId = "retry-test",
            NodeName = "Retry Test",
            RetryPolicy = new RetryPolicy
            {
                Strategy = RetryStrategy.Exponential,
                MaxAttempts = 5,
                InitialDelay = TimeSpan.FromSeconds(2),
                MaxDelay = TimeSpan.FromMinutes(2),
                Multiplier = 3.0,
                RetryOn = [typeof(TimeoutException)],
                DoNotRetryOn = [typeof(ArgumentException)],
            },
        };

        var dto = EngineDtoMapper.MapNoop(source);
        Assert.NotNull(dto.RetryPolicy);
        Assert.Equal("Exponential", dto.RetryPolicy!.Strategy);
        Assert.Equal(5, dto.RetryPolicy.MaxAttempts);
        Assert.Single(dto.RetryPolicy.RetryOn);
        Assert.Single(dto.RetryPolicy.DoNotRetryOn);

        var roundTripped = EngineDtoMapper.MapNoopFromDto(dto);
        Assert.NotNull(roundTripped.RetryPolicy);
        Assert.Equal(RetryStrategy.Exponential, roundTripped.RetryPolicy!.Strategy);
        Assert.Equal(5, roundTripped.RetryPolicy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), roundTripped.RetryPolicy.InitialDelay);
        Assert.Equal(TimeSpan.FromMinutes(2), roundTripped.RetryPolicy.MaxDelay);
        Assert.Equal(3.0, roundTripped.RetryPolicy.Multiplier);
    }

    [Fact]
    public void MapBase_roundtrips_retry_policy_null()
    {
        var source = new NoopNodeDefinition
        {
            NodeId = "no-retry",
            NodeName = "No Retry",
            RetryPolicy = null,
        };

        var dto = EngineDtoMapper.MapNoop(source);
        Assert.Null(dto.RetryPolicy);

        var roundTripped = EngineDtoMapper.MapNoopFromDto(dto);
        Assert.Null(roundTripped.RetryPolicy);
    }

    [Fact]
    public void MapBase_roundtrips_circuit_breaker_populated()
    {
        var source = new NoopNodeDefinition
        {
            NodeId = "cb-test",
            NodeName = "CB Test",
            CircuitBreakerPolicy = new CircuitBreakerPolicy
            {
                FailureThreshold = 75,
                HalfOpenSuccesses = 5,
                MinimumThroughput = 20,
                OpenDuration = TimeSpan.FromMinutes(1),
            },
        };

        var dto = EngineDtoMapper.MapNoop(source);
        Assert.NotNull(dto.CircuitBreakerPolicy);
        Assert.Equal(75, dto.CircuitBreakerPolicy!.FailureThreshold);

        var roundTripped = EngineDtoMapper.MapNoopFromDto(dto);
        Assert.NotNull(roundTripped.CircuitBreakerPolicy);
        Assert.Equal(75, roundTripped.CircuitBreakerPolicy!.FailureThreshold);
        Assert.Equal(5, roundTripped.CircuitBreakerPolicy.HalfOpenSuccesses);
        Assert.Equal(20, roundTripped.CircuitBreakerPolicy.MinimumThroughput);
        Assert.Equal(TimeSpan.FromMinutes(1), roundTripped.CircuitBreakerPolicy.OpenDuration);
    }

    [Fact]
    public void MapBase_roundtrips_circuit_breaker_null()
    {
        var source = new NoopNodeDefinition
        {
            NodeId = "no-cb",
            NodeName = "No CB",
            CircuitBreakerPolicy = null,
        };

        var dto = EngineDtoMapper.MapNoop(source);
        Assert.Null(dto.CircuitBreakerPolicy);

        var roundTripped = EngineDtoMapper.MapNoopFromDto(dto);
        Assert.Null(roundTripped.CircuitBreakerPolicy);
    }

    [Fact]
    public void MapBase_roundtrips_retry_type_name_lists()
    {
        var retryOn = typeof(TimeoutException);
        var doNotRetryOn = typeof(ArgumentException);

        var source = new NoopNodeDefinition
        {
            NodeId = "fqtn-test",
            NodeName = "FQTN Test",
            RetryPolicy = new RetryPolicy
            {
                Strategy = RetryStrategy.Fixed,
                RetryOn = [retryOn],
                DoNotRetryOn = [doNotRetryOn],
            },
        };

        var dto = EngineDtoMapper.MapNoop(source);
        Assert.Single(dto.RetryPolicy!.RetryOn);
        Assert.Single(dto.RetryPolicy.DoNotRetryOn);
        Assert.Contains("TimeoutException", dto.RetryPolicy.RetryOn[0]);
        Assert.Contains("ArgumentException", dto.RetryPolicy.DoNotRetryOn[0]);

        var roundTripped = EngineDtoMapper.MapNoopFromDto(dto);
        Assert.Single(roundTripped.RetryPolicy!.RetryOn);
        Assert.Single(roundTripped.RetryPolicy.DoNotRetryOn);
        Assert.Equal(typeof(TimeoutException), roundTripped.RetryPolicy.RetryOn[0]);
        Assert.Equal(typeof(ArgumentException), roundTripped.RetryPolicy.DoNotRetryOn[0]);
    }

    [Fact]
    public void Registry_dispatches_all_13_runtime_types()
    {
        Assert.Equal(13, EngineDtoMapper.SupportedRuntimeTypes.Count);
    }

    [Fact]
    public void MapToDto_and_MapFromDto_roundtrip_noop()
    {
        var source = new NoopNodeDefinition
        {
            NodeId = "noop-rt",
            NodeName = "Noop RT",
        };

        var dto = EngineDtoMapper.MapToDto(source);
        Assert.Equal("Noop", dto.RuntimeType);

        var roundTripped = EngineDtoMapper.MapFromDto(dto);
        Assert.IsType<NoopNodeDefinition>(roundTripped);
        Assert.Equal("noop-rt", roundTripped.NodeId);
    }

    [Fact]
    public void MapFromDto_rejects_unknown_runtime_type()
    {
        var dto = new NodeDto { RuntimeType = "UnknownKind", NodeId = "bad" };
        Assert.Throws<ArgumentException>(() => EngineDtoMapper.MapFromDto(dto));
    }

    [Fact]
    public void Duration_uses_iso8601_format()
    {
        var source = new NoopNodeDefinition
        {
            NodeId = "dur-test",
            NodeName = "Duration Test",
            RetryPolicy = new RetryPolicy
            {
                Strategy = RetryStrategy.Fixed,
                InitialDelay = TimeSpan.FromSeconds(2),
                MaxDelay = TimeSpan.FromMinutes(1),
            },
            CircuitBreakerPolicy = new CircuitBreakerPolicy
            {
                OpenDuration = TimeSpan.FromSeconds(30),
            },
        };

        var dto = EngineDtoMapper.MapNoop(source);

        // Verify durations use ISO-8601 format (PT*S, PT*M, etc.)
        Assert.Equal("PT2S", dto.RetryPolicy!.InitialDelay);
        Assert.Equal("PT1M", dto.RetryPolicy.MaxDelay);
        Assert.Equal("PT30S", dto.CircuitBreakerPolicy!.OpenDuration);

        // Verify round-trip
        var rt = EngineDtoMapper.MapNoopFromDto(dto);
        Assert.Equal(TimeSpan.FromSeconds(2), rt.RetryPolicy!.InitialDelay);
        Assert.Equal(TimeSpan.FromMinutes(1), rt.RetryPolicy.MaxDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), rt.CircuitBreakerPolicy!.OpenDuration);
    }

    [Fact]
    public void Duration_preserves_values_greater_than_24_hours()
    {
        var source = new NoopNodeDefinition
        {
            NodeId = "long-dur",
            NodeName = "Long Duration",
            RetryPolicy = new RetryPolicy
            {
                Strategy = RetryStrategy.Fixed,
                InitialDelay = TimeSpan.FromHours(25),
                MaxDelay = TimeSpan.FromDays(2),
            },
            CircuitBreakerPolicy = new CircuitBreakerPolicy
            {
                OpenDuration = TimeSpan.FromHours(36),
            },
        };

        var dto = EngineDtoMapper.MapNoop(source);

        // ISO-8601 durations preserve >24h using day components
        Assert.Equal("P1DT1H", dto.RetryPolicy!.InitialDelay);
        Assert.Equal("P2D", dto.RetryPolicy.MaxDelay);
        Assert.Equal("P1DT12H", dto.CircuitBreakerPolicy!.OpenDuration);

        // Verify round-trip
        var rt = EngineDtoMapper.MapNoopFromDto(dto);
        Assert.Equal(TimeSpan.FromHours(25), rt.RetryPolicy!.InitialDelay);
        Assert.Equal(TimeSpan.FromDays(2), rt.RetryPolicy.MaxDelay);
        Assert.Equal(TimeSpan.FromHours(36), rt.CircuitBreakerPolicy!.OpenDuration);
    }

    [Fact]
    public void Unresolved_retry_type_names_surfaced_in_diagnostics()
    {
        var dto = new RetryPolicyDto
        {
            Strategy = "Fixed",
            MaxAttempts = 1,
            InitialDelay = "PT1S",
            MaxDelay = "PT1M",
            Multiplier = 1.0,
            RetryOn = ["System.TimeoutException, System.Runtime", "Bogus.FakeException, Fake.Assembly"],
            DoNotRetryOn = ["Another.Missing, Bad.Assembly"],
        };

        var policy = EngineDtoMapper.MapRetryPolicyFromDto(dto, out var unresolved);
        Assert.NotNull(policy);

        // The resolvable type should be found
        Assert.Single(policy!.RetryOn);
        Assert.Equal(typeof(TimeoutException), policy.RetryOn[0]);

        // Unresolvable types should be surfaced in diagnostics
        Assert.Equal(2, unresolved.Count);
        Assert.Contains(unresolved, u => u.Contains("Bogus.FakeException"));
        Assert.Contains(unresolved, u => u.Contains("Another.Missing"));
    }

    [Fact]
    public void Unresolved_types_collected_through_workflow_mapping()
    {
        var dto = new WorkflowDefinitionDto
        {
            WorkflowId = "diag-wf",
            Nodes =
            [
                new NodeDto
                {
                    RuntimeType = "Noop",
                    NodeId = "n1",
                    NodeName = "N1",
                    JoinType = "Any",
                    Priority = "Normal",
                    RetryPolicy = new RetryPolicyDto
                    {
                        Strategy = "Fixed",
                        InitialDelay = "PT1S",
                        MaxDelay = "PT1M",
                        RetryOn = ["Bogus.Type, Fake.Asm"],
                    },
                },
            ],
        };

        var engine = EngineDtoMapper.MapWorkflowFromDto(dto, out var unresolvedTypeNames);
        Assert.NotNull(engine);
        Assert.Single(unresolvedTypeNames);
        Assert.Contains("Bogus.Type", unresolvedTypeNames[0]);
    }
}
