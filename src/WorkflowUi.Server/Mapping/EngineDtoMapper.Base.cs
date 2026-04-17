// -----------------------------------------------------------------------
// <copyright file="EngineDtoMapper.Base.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Mapping;

using System;
using System.Collections.Generic;
using System.Linq;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;
using ExecutionEngine.Policies;

/// <summary>
/// Bidirectional mapper between UI DTOs and engine NodeDefinition types.
/// Base partial handles shared fields common to all runtime kinds.
/// </summary>
public static partial class EngineDtoMapper
{
    /// <summary>
    /// Maps shared base fields from engine NodeDefinition to DTO.
    /// </summary>
    public static void MapBaseToDto(NodeDefinition source, NodeDto target)
    {
        target.RuntimeType = source.RuntimeType.ToString();
        target.NodeId = source.NodeId;
        target.NodeName = source.NodeName;
        target.Description = source.Description;
        target.JoinType = source.JoinType.ToString();
        target.Priority = source.Priority.ToString();
        target.MaxConcurrentExecutions = source.MaxConcurrentExecutions;
        target.CompensationNodeId = source.CompensationNodeId;
        target.FallbackNodeId = source.FallbackNodeId;
        target.Configuration = source.Configuration;
        target.RetryPolicy = MapRetryPolicyToDto(source.RetryPolicy);
        target.CircuitBreakerPolicy = MapCircuitBreakerPolicyToDto(source.CircuitBreakerPolicy);
    }

    /// <summary>
    /// Maps shared base fields from DTO back to engine NodeDefinition.
    /// </summary>
    public static void MapBaseFromDto(NodeDto source, NodeDefinition target)
    {
        target.NodeId = source.NodeId;
        target.NodeName = source.NodeName;
        target.Description = source.Description;
        target.JoinType = Enum.Parse<JoinType>(source.JoinType);
        target.Priority = Enum.Parse<NodePriority>(source.Priority);
        target.MaxConcurrentExecutions = source.MaxConcurrentExecutions;
        target.CompensationNodeId = source.CompensationNodeId;
        target.FallbackNodeId = source.FallbackNodeId;
        target.Configuration = source.Configuration;
        target.RetryPolicy = MapRetryPolicyFromDto(source.RetryPolicy);
        target.CircuitBreakerPolicy = MapCircuitBreakerPolicyFromDto(source.CircuitBreakerPolicy);
    }

    /// <summary>
    /// Maps engine RetryPolicy to DTO, preserving null semantics.
    /// </summary>
    public static RetryPolicyDto? MapRetryPolicyToDto(RetryPolicy? policy)
    {
        if (policy == null)
        {
            return null;
        }

        return new RetryPolicyDto
        {
            Strategy = policy.Strategy.ToString(),
            MaxAttempts = policy.MaxAttempts,
            InitialDelay = policy.InitialDelay.ToString(),
            MaxDelay = policy.MaxDelay.ToString(),
            Multiplier = policy.Multiplier,
            RetryOn = policy.RetryOn?.Select(t => t.AssemblyQualifiedName ?? t.FullName ?? t.Name).ToList() ?? [],
            DoNotRetryOn = policy.DoNotRetryOn?.Select(t => t.AssemblyQualifiedName ?? t.FullName ?? t.Name).ToList() ?? [],
        };
    }

    /// <summary>
    /// Maps DTO back to engine RetryPolicy, preserving null semantics.
    /// </summary>
    public static RetryPolicy? MapRetryPolicyFromDto(RetryPolicyDto? dto)
    {
        if (dto == null)
        {
            return null;
        }

        return new RetryPolicy
        {
            Strategy = Enum.Parse<RetryStrategy>(dto.Strategy),
            MaxAttempts = dto.MaxAttempts,
            InitialDelay = TimeSpan.Parse(dto.InitialDelay),
            MaxDelay = TimeSpan.Parse(dto.MaxDelay),
            Multiplier = dto.Multiplier,
            RetryOn = dto.RetryOn?.Select(ResolveType).Where(t => t != null).Select(t => t!).ToList() ?? [],
            DoNotRetryOn = dto.DoNotRetryOn?.Select(ResolveType).Where(t => t != null).Select(t => t!).ToList() ?? [],
        };
    }

    /// <summary>
    /// Maps engine CircuitBreakerPolicy to DTO, preserving null semantics.
    /// </summary>
    public static CircuitBreakerPolicyDto? MapCircuitBreakerPolicyToDto(CircuitBreakerPolicy? policy)
    {
        if (policy == null)
        {
            return null;
        }

        return new CircuitBreakerPolicyDto
        {
            FailureThreshold = policy.FailureThreshold,
            HalfOpenSuccesses = policy.HalfOpenSuccesses,
            MinimumThroughput = policy.MinimumThroughput,
            OpenDuration = policy.OpenDuration.ToString(),
        };
    }

    /// <summary>
    /// Maps DTO back to engine CircuitBreakerPolicy, preserving null semantics.
    /// </summary>
    public static CircuitBreakerPolicy? MapCircuitBreakerPolicyFromDto(CircuitBreakerPolicyDto? dto)
    {
        if (dto == null)
        {
            return null;
        }

        return new CircuitBreakerPolicy
        {
            FailureThreshold = dto.FailureThreshold,
            HalfOpenSuccesses = dto.HalfOpenSuccesses,
            MinimumThroughput = dto.MinimumThroughput,
            OpenDuration = TimeSpan.Parse(dto.OpenDuration),
        };
    }

    /// <summary>
    /// Resolves a fully-qualified type name to a Type, returning null on failure.
    /// </summary>
    private static Type? ResolveType(string typeName)
    {
        return Type.GetType(typeName, throwOnError: false);
    }
}
