// -----------------------------------------------------------------------
// <copyright file="PolicyParityTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests;

using System;
using System.Linq;
using System.Reflection;
using ExecutionEngine.Policies;
using Xunit;

/// <summary>
/// Parity tests for RetryPolicy and CircuitBreakerPolicy nested shapes.
/// </summary>
[Trait("Category", "DtoParity")]
public class PolicyParityTests
{
    [Fact]
    public void RetryPolicy_Has7Properties()
    {
        var expected = new[]
        {
            "DoNotRetryOn", "InitialDelay", "MaxAttempts", "MaxDelay",
            "Multiplier", "RetryOn", "Strategy",
        };
        var actual = typeof(RetryPolicy)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RetryPolicy_MaxAttemptsDefault_Is3()
    {
        var policy = new RetryPolicy();
        Assert.Equal(3, policy.MaxAttempts);
    }

    [Fact]
    public void CircuitBreakerPolicy_Has4Properties()
    {
        var expected = new[]
        {
            "FailureThreshold", "HalfOpenSuccesses", "MinimumThroughput", "OpenDuration",
        };
        var actual = typeof(CircuitBreakerPolicy)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CircuitBreakerPolicy_OpenDurationDefault_Is30Seconds()
    {
        var policy = new CircuitBreakerPolicy();
        Assert.Equal(TimeSpan.FromSeconds(30), policy.OpenDuration);
    }
}
