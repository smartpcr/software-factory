// -----------------------------------------------------------------------
// <copyright file="IdGenerator.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Files;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkflowUi.Server.Persistence;

/// <summary>
/// Generates unique workflow ids for duplicate operations, resolving collisions.
/// </summary>
public class IdGenerator
{
    private readonly IWorkflowStore store;

    public IdGenerator(IWorkflowStore store)
    {
        this.store = store;
    }

    /// <summary>
    /// Generates a unique id based on the given base id. If "copy-of-{base}" already exists,
    /// appends "-2", "-3", etc. until a unique id is found.
    /// </summary>
    public async Task<string> GenerateUniqueIdAsync(
        string baseId,
        System.Security.Claims.ClaimsPrincipal user,
        CancellationToken ct)
    {
        var candidateId = $"copy-of-{baseId}";
        var attempt = 1;

        while (true)
        {
            var existing = await this.store.GetAsync(candidateId, user, ct);
            if (existing == null)
            {
                return candidateId;
            }

            attempt++;
            candidateId = $"copy-of-{baseId}-{attempt}";

            if (attempt > 100)
            {
                throw new InvalidOperationException($"Cannot generate unique id for {baseId} after 100 attempts");
            }
        }
    }
}
