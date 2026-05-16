// -----------------------------------------------------------------------
// <copyright file="ProviderCache.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Forge.Api.Contracts;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 5.10 — single ingestion point for provider-fetched cards / PRs.
/// Upserts each row, tracks the per-row remote revision so the UI can
/// surface a "remote changed since" badge (Phase 5.9), and fans out
/// SignalR <c>card.changed</c> / <c>pr.changed</c> events.
/// </summary>
public class ProviderCache
{
    private readonly ProductDbContext db;
    private readonly IHubContext<ForgeHub> hub;

    public ProviderCache(ProductDbContext db, IHubContext<ForgeHub> hub)
    {
        this.db = db;
        this.hub = hub;
    }

    /// <summary>Upsert a batch of cards. Emits one <c>card.changed</c> event per row that actually changed.</summary>
    public async Task<int> UpsertCardsAsync(string providerKind, IReadOnlyList<Card> cards, CancellationToken ct = default)
    {
        if (cards.Count == 0) return 0;
        var changed = 0;
        var existing = await this.db.Cards
            .Where(c => cards.Select(x => x.Id).Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        foreach (var card in cards)
        {
            if (existing.TryGetValue(card.Id, out var row))
            {
                var snapshot = CardMapper.ToContract(row);
                if (snapshot == card) continue;
                CardMapper.ApplyContract(row, card);
                row.RemoteRevision = $"{providerKind}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                changed++;
                await this.hub.Clients.All.SendAsync("card.changed", new { kind = "synced", source = providerKind, card = CardMapper.ToContract(row) }, ct);
            }
            else
            {
                var maxSort = await this.db.Cards.Select(c => (int?)c.Sort).MaxAsync(ct) ?? -1;
                var fresh = CardMapper.ApplyContract(new CardRow { Id = card.Id, Sort = maxSort + 1 }, card);
                fresh.RemoteRevision = $"{providerKind}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                // Phase 8.9 — provider-imported cards land under the default
                // repo so they show up under the active product. Phase 9 will
                // route them to the matching repo from the multi-org config.
                fresh.RepoId ??= ForgeSeed.DefaultRepoId;
                this.db.Cards.Add(fresh);
                changed++;
                await this.hub.Clients.All.SendAsync("card.changed", new { kind = "imported", source = providerKind, card }, ct);
            }
        }

        if (changed > 0)
        {
            await this.db.SaveChangesAsync(ct);
        }
        return changed;
    }

    /// <summary>Upsert a batch of PRs. Emits one <c>pr.changed</c> event per row that actually changed.</summary>
    public async Task<int> UpsertPrsAsync(string providerKind, IReadOnlyList<Pr> prs, CancellationToken ct = default)
    {
        if (prs.Count == 0) return 0;
        var changed = 0;
        var existing = await this.db.Prs
            .Where(p => prs.Select(x => x.Id).Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        foreach (var pr in prs)
        {
            if (existing.TryGetValue(pr.Id, out var row))
            {
                var snapshot = PrMapper.ToContract(row);
                if (snapshot == pr) continue;
                row.Col = pr.Col;
                row.Title = pr.Title;
                row.Branch = pr.Branch;
                row.Base = pr.Base;
                row.Additions = pr.Additions;
                row.Deletions = pr.Deletions;
                row.Files = pr.Files;
                row.Commits = pr.Commits;
                row.Age = pr.Age;
                row.Conflicts = pr.Conflicts;
                row.HumanGate = pr.HumanGate;
                row.GateReason = pr.GateReason;
                row.AuthorJson = CardMapper.Serialize(pr.Author);
                if (pr.Build is not null) row.BuildJson = CardMapper.Serialize(pr.Build);
                if (pr.Review is not null) row.ReviewJson = CardMapper.Serialize(pr.Review);
                changed++;
                await this.hub.Clients.All.SendAsync("pr.changed", new { kind = "synced", source = providerKind, pr = PrMapper.ToContract(row) }, ct);
            }
            else
            {
                var maxSort = await this.db.Prs.Select(p => (int?)p.Sort).MaxAsync(ct) ?? -1;
                var fresh = new PrRow
                {
                    Id = pr.Id,
                    Sort = maxSort + 1,
                    Col = pr.Col,
                    Epic = pr.Epic,
                    EpicColor = pr.EpicColor,
                    Workstream = pr.Workstream,
                    Base = pr.Base,
                    Branch = pr.Branch,
                    Title = pr.Title,
                    AuthorJson = CardMapper.Serialize(pr.Author),
                    Additions = pr.Additions,
                    Deletions = pr.Deletions,
                    Files = pr.Files,
                    Commits = pr.Commits,
                    Age = pr.Age,
                    BuildJson = pr.Build is null ? null : CardMapper.Serialize(pr.Build),
                    ReviewJson = pr.Review is null ? null : CardMapper.Serialize(pr.Review),
                    Conflicts = pr.Conflicts,
                    HumanGate = pr.HumanGate,
                    GateReason = pr.GateReason,
                    // Phase 8.9 — provider-imported PRs default to the seed repo;
                    // Phase 9 routes them by org/repo metadata.
                    RepoId = ForgeSeed.DefaultRepoId,
                };
                this.db.Prs.Add(fresh);
                changed++;
                await this.hub.Clients.All.SendAsync("pr.changed", new { kind = "imported", source = providerKind, pr }, ct);
            }
        }

        if (changed > 0)
        {
            await this.db.SaveChangesAsync(ct);
        }
        return changed;
    }
}
