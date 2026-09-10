// -----------------------------------------------------------------------
// <copyright file="OperatorActionService.Receipts.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

public sealed partial class OperatorActionService
{
    private Task<OperatorActionOutcome> WithReceiptAsync(string action, string id, Func<Task<OperatorActionOutcome>> execute, CancellationToken ct)
        => this.chatActions.RunAsync(action, new(WorkstreamId: id), execute,
            reason => OperatorActionOutcome.Problem(409, null, reason, null), result => result.Ok, ct);
    public Task<OperatorActionOutcome> AcceptAsync(string id, string actor, CancellationToken ct)
        => this.WithReceiptAsync("workstream.accept", id, () => this.AcceptCoreAsync(id, actor, ct), ct);
    public Task<OperatorActionOutcome> CancelAsync(string id, CancellationToken ct)
        => this.WithReceiptAsync("workstream.cancel", id, () => this.CancelCoreAsync(id, ct), ct);
    public Task<OperatorActionOutcome> RetryAsync(string id, string actor, CancellationToken ct)
        => this.WithReceiptAsync("workstream.retry", id, () => this.RetryCoreAsync(id, actor, ct), ct);
    public Task<OperatorActionOutcome> RestartAsync(string id, CancellationToken ct)
        => this.WithReceiptAsync("workstream.restart", id, () => this.RestartCoreAsync(id, ct), ct);
}
