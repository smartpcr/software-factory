// -----------------------------------------------------------------------
// <copyright file="Phase73TestCollection.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Xunit;

/// <summary>
/// Phase 73 follow-up — xUnit collection definition that serializes
/// every test class which mutates the process-wide
/// <see cref="Forge.Api.Persistence.SplitMarkerCache"/> static state.
/// Without serialization, parallel test execution causes false
/// positives where one test's <c>StampMarker</c> bleeds into another
/// test's cache probe.
///
/// <para><see cref="ICollectionFixture{T}"/> intentionally omitted —
/// we only need the parallelization gate, not a shared fixture.</para>
/// </summary>
[CollectionDefinition("Phase73SerialTests", DisableParallelization = true)]
public class Phase73TestCollection
{
}
