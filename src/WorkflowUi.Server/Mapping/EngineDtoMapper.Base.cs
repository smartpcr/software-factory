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
    /// Converts a TimeSpan to an ISO-8601-style duration string (hh:mm:ss format).
    /// Uses constant format to avoid culture-dependent drift.
    /// </summary>
    public static string ToIsoDuration(TimeSpan ts)
    {
        return ts.ToString(@"hh\:mm\:ss");
    }

    /// <summary>
    /// Parses an ISO-8601-style duration string back to TimeSpan.
    /// Supports both hh:mm:ss and d.hh:mm:ss formats.
    /// </summary>
    public static TimeSpan FromIsoDuration(string duration)
    {
        return TimeSpan.Parse(duration);
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
            InitialDelay = ToIsoDuration(policy.InitialDelay),
            MaxDelay = ToIsoDuration(policy.MaxDelay),
            Multiplier = policy.Multiplier,
            RetryOn = policy.RetryOn?.Select(t => t.AssemblyQualifiedName ?? t.FullName ?? t.Name).ToList() ?? [],
            DoNotRetryOn = policy.DoNotRetryOn?.Select(t => t.AssemblyQualifiedName ?? t.FullName ?? t.Name).ToList() ?? [],
        };
    }

    /// <summary>
    /// Maps DTO back to engine RetryPolicy, preserving null semantics.
    /// Unresolvable type names in RetryOn/DoNotRetryOn are preserved as-is in the
    /// UnresolvedRetryTypeNames diagnostic list rather than silently dropped.
    /// </summary>
    public static RetryPolicy? MapRetryPolicyFromDto(RetryPolicyDto? dto)
    {
        return MapRetryPolicyFromDto(dto, out _);
    }

    /// <summary>
    /// Maps DTO back to engine RetryPolicy with diagnostics for unresolvable type names.
    /// </summary>
    public static RetryPolicy? MapRetryPolicyFromDto(RetryPolicyDto? dto, out List<string> unresolvedTypeNames)
    {
        unresolvedTypeNames = [];

        if (dto == null)
        {
            return null;
        }

        var retryOn = new List<Type>();
        var doNotRetryOn = new List<Type>();

        foreach (var name in dto.RetryOn ?? [])
        {
            var resolved = ResolveType(name);
            if (resolved != null)
            {
                retryOn.Add(resolved);
            }
            else
            {
                unresolvedTypeNames.Add($"RetryOn:{name}");
            }
        }

        foreach (var name in dto.DoNotRetryOn ?? [])
        {
            var resolved = ResolveType(name);
            if (resolved != null)
            {
                doNotRetryOn.Add(resolved);
            }
            else
            {
                unresolvedTypeNames.Add($"DoNotRetryOn:{name}");
            }
        }

        return new RetryPolicy
        {
            Strategy = Enum.Parse<RetryStrategy>(dto.Strategy),
            MaxAttempts = dto.MaxAttempts,
            InitialDelay = FromIsoDuration(dto.InitialDelay),
            MaxDelay = FromIsoDuration(dto.MaxDelay),
            Multiplier = dto.Multiplier,
            RetryOn = retryOn,
            DoNotRetryOn = doNotRetryOn,
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
            OpenDuration = ToIsoDuration(policy.OpenDuration),
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
            OpenDuration = FromIsoDuration(dto.OpenDuration),
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
