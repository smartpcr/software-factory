// -----------------------------------------------------------------------
// <copyright file="SplitDryRunReport.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence.Split;

/// <summary>
/// Phase B — JSON shape returned by <c>POST /api/admin/db/split-dry-run</c>.
/// The operator inspects this BEFORE Phase F's apply step touches data;
/// orphan counts &gt; 0 or unknown tables surface here so the boundary can
/// be tightened first.
/// </summary>
public sealed record SplitDryRunReport(
    DateTimeOffset GeneratedAt,
    string ControlDbPath,
    long ControlDbSizeBytes,
    int TotalRows,
    int ControlRows,
    int ProductRows,
    int OrphanRows,
    int UnknownRows,
    IReadOnlyList<TableSummary> Tables,
    IReadOnlyList<ProductPartition> Products,
    IReadOnlyList<OrphanWarning> Orphans,
    IReadOnlyList<UnknownTable> UnknownTables);

/// <summary>Per-table summary inside <see cref="SplitDryRunReport"/>.</summary>
public sealed record TableSummary(
    string TableName,
    string Kind,            // "control" | "product"
    string Strategy,        // PartitionStrategy.ToString().ToLowerInvariant()
    string? PartitionColumn,
    int TotalRows,
    int ClassifiedRows,
    int OrphanRows);

/// <summary>One row in the per-product breakdown.</summary>
/// <param name="ProductId">The product's row id.</param>
/// <param name="ProductSlug">The product's slug (== the future per-product DB filename without .db).</param>
/// <param name="TotalRows">Sum across all product-DB tables that resolve to this product.</param>
/// <param name="RowsPerTable">Dictionary keyed on table name → row count for this product.</param>
public sealed record ProductPartition(
    string ProductId,
    string ProductSlug,
    int TotalRows,
    IReadOnlyDictionary<string, int> RowsPerTable);

/// <summary>One orphan-bucket warning per (table, kind) combination.</summary>
/// <param name="TableName">Source table.</param>
/// <param name="Kind">
/// <c>"null-partition-key"</c> — partition column is NULL when it shouldn't be.<br/>
/// <c>"unknown-product"</c> — partition value doesn't resolve to a known product.<br/>
/// <c>"broken-foreign-key"</c> — FK chain hop failed (e.g., RepoId not in repos).<br/>
/// <c>"no-partition-strategy"</c> — table has <see cref="PartitionStrategy.NoPartitionKey"/>; rows will fall to default product on apply.
/// </param>
/// <param name="Count">How many rows are in this bucket.</param>
/// <param name="Detail">Free-form human description so the operator can fix it.</param>
public sealed record OrphanWarning(
    string TableName,
    string Kind,
    int Count,
    string Detail);

/// <summary>A table that exists in <c>forge.db</c> but isn't in <see cref="DbBoundary.AllTables"/>.</summary>
public sealed record UnknownTable(string TableName, int RowCount);
