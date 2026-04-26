// -----------------------------------------------------------------------
// <copyright file="DragPolicy.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Domain;

using Forge.Api.Contracts;

/// <summary>
/// Server-side mirror of <c>src/forge/Forge.UI/src/board/policy.ts</c>'s
/// <c>checkDragPolicy</c>. Behaviour MUST stay byte-identical; the
/// <c>DragPolicyParityTests</c> drives the same scenarios on both
/// implementations and asserts equality.
/// </summary>
public static class DragPolicy
{
    public static readonly IReadOnlyList<string> ColOrder = new[]
    {
        "backlog", "design", "build", "review", "done",
    };

    public static DragPolicyResult Check(string fromCol, string toCol, Card card)
    {
        if (fromCol == toCol)
        {
            return new DragPolicyResult(true, null, null, null, null, false);
        }

        var fromIdx = IndexOf(fromCol);
        var toIdx = IndexOf(toCol);
        var distance = toIdx - fromIdx;

        if (distance < 0)
        {
            if (fromCol == "done")
            {
                return new DragPolicyResult(
                    Allow: true,
                    Toast: "Item re-opened — agents will re-investigate",
                    Title: null,
                    Text: null,
                    Warn: "Re-open shipped item?",
                    Confirm: false);
            }
            return new DragPolicyResult(
                Allow: true,
                Toast: "Card moved back · agent reassigned",
                Title: null,
                Text: null,
                Warn: null,
                Confirm: false);
        }

        if (distance > 1)
        {
            if (toCol == "done" && fromCol != "review")
            {
                return new DragPolicyResult(
                    Allow: true,
                    Toast: null,
                    Title: "Bypass review?",
                    Text: $"You're marking <strong>{card.Id}</strong> as done without going through review. Code will not be reviewed by an agent and CI gates are skipped.",
                    Warn: null,
                    Confirm: true);
            }
            var skipped = distance - 1;
            var phaseSuffix = skipped > 1 ? "s" : string.Empty;
            return new DragPolicyResult(
                Allow: true,
                Toast: null,
                Title: "Skip phase?",
                Text: $"You're moving <strong>{card.Id}</strong> directly from <strong>{fromCol}</strong> to <strong>{toCol}</strong>. This bypasses the {skipped} intermediate phase{phaseSuffix}.",
                Warn: null,
                Confirm: true);
        }

        if (toCol == "done" && card.HumanGate)
        {
            return new DragPolicyResult(
                Allow: true,
                Toast: null,
                Title: "Approve human-gated change?",
                Text: $"<strong>{card.Id}</strong> has been escalated for your review because: {card.GateReason}. Approving will merge to main.",
                Warn: null,
                Confirm: true);
        }

        return new DragPolicyResult(
            Allow: true,
            Toast: $"Moved to {toCol}",
            Title: null,
            Text: null,
            Warn: null,
            Confirm: false);
    }

    private static int IndexOf(string col)
    {
        for (var i = 0; i < ColOrder.Count; i++)
        {
            if (ColOrder[i] == col) return i;
        }
        return -1;
    }
}

/// <summary>
/// Result of <see cref="DragPolicy.Check"/>. Field order matches the TS shape.
/// </summary>
public sealed record DragPolicyResult(
    bool Allow,
    string? Toast,
    string? Title,
    string? Text,
    string? Warn,
    bool Confirm);
