// -----------------------------------------------------------------------
// <copyright file="IClock.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Persistence;

using System;

/// <summary>
/// Abstraction for current time, allowing tests to control time progression.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>
/// Real system clock implementation.
/// </summary>
public class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
