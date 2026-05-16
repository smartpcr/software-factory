// -----------------------------------------------------------------------
// <copyright file="ForgeDbPaths.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Extensions.Configuration;

/// <summary>
/// Resolves Forge SQLite paths from configuration. Phase A of the
/// per-product DB refactor (see <c>docs/forge/split-db.md</c>).
///
/// <para>Resolution order:</para>
/// <list type="number">
///   <item>If <c>Forge:Db:Path</c> is set (legacy), use it verbatim for the
///         control DB and derive the folder from its directory. Preserves
///         existing test factory + Docker setups that point at a specific
///         file path.</item>
///   <item>Otherwise, read <c>Forge:Db:Folder</c> (default <c>E:\forge</c>),
///         resolve the control DB at <c>{folder}\forge.db</c>.</item>
/// </list>
///
/// <para>Per-product DB files (<c>{folder}\{slug}.db</c>) are addressable
/// today but only exist after Phase F runs the auto-split migration; Phase A
/// keeps everything in the control DB so <see cref="ProductDbPath"/> is
/// useful for tooling (Phase B splitter dry-run) and the eventual Phase F
/// migration. Live request routing through per-product files lands in
/// Phase D.</para>
///
/// <para>The folder is the canonical "Forge state root" — the storyworkspaces/,
/// attachments/, and msal-cache directories all hang off it.</para>
/// </summary>
public interface IForgeDbPaths
{
    /// <summary>
    /// The root folder that hosts the control DB, per-product DBs, and
    /// sibling state directories (<c>storyworkspaces/</c>, <c>attachments/</c>).
    /// Created on construction if it doesn't exist.
    /// </summary>
    string Folder { get; }

    /// <summary>
    /// Absolute path to the control DB. Phase A: holds every Forge entity.
    /// Phase D+: holds only cross-product entities (products, repos, users,
    /// crews, agents, agent_usage, agent_claims, audit, idempotency).
    /// </summary>
    string ControlDbPath();

    /// <summary>
    /// Absolute path to a product's dedicated DB. The file may not yet
    /// exist; use <see cref="ProductDbExists"/> to check.
    /// </summary>
    /// <param name="productSlug">The product slug column value (Phase 8.1
    /// — lowercase, hyphenated). Defense-in-depth: only <c>[a-z0-9-]</c>
    /// is accepted; anything that could escape the folder is rejected
    /// with <see cref="ArgumentException"/>.</param>
    string ProductDbPath(string productSlug);

    /// <summary>Whether <see cref="ProductDbPath"/> resolves to an existing file.</summary>
    bool ProductDbExists(string productSlug);
}

/// <inheritdoc cref="IForgeDbPaths" />
public sealed class ForgeDbPaths : IForgeDbPaths
{
    /// <summary>The hardcoded production default folder when no config is set.</summary>
    public const string DefaultFolder = @"E:\forge";

    /// <summary>The canonical control DB file name within the folder.</summary>
    public const string ControlDbFileName = "forge.db";

    /// <summary>
    /// Slugs that cannot be used as product slugs because they would
    /// collide with system files inside the folder or with Windows
    /// reserved device names (which SQLite cannot open).
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // The control DB lives at {folder}\forge.db. A product slug of
        // "forge" would resolve ProductDbPath() to the same file and
        // shadow the control DB.
        "forge",

        // Windows reserved device names. Even with `.db` extension,
        // `CON.db`, `NUL.db`, etc. fail to open on Windows because the
        // OS intercepts the base name before the filesystem sees it.
        "con", "nul", "aux", "prn",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>
    /// Validate a product slug for use as a per-product DB filename.
    /// Server-side check used by <c>ProductsEndpoints.CreateProduct</c> so the
    /// UI can surface a clear 400 instead of failing later in
    /// <see cref="ProductDbPath"/> with an exception.
    /// </summary>
    /// <param name="productSlug">Candidate slug (typically from the
    /// <c>Slug</c> column or derived from <c>Name</c>).</param>
    /// <param name="error">Populated with a human-readable error when the
    /// slug is invalid; <c>null</c> on success.</param>
    /// <returns><c>true</c> when valid; <c>false</c> otherwise.</returns>
    public static bool TryValidateSlug(string? productSlug, out string? error)
    {
        if (string.IsNullOrWhiteSpace(productSlug))
        {
            error = "Product slug cannot be empty. Either supply 'slug' explicitly or use a name that contains at least one letter or digit.";
            return false;
        }

        if (productSlug.Length > 100)
        {
            error = $"Product slug '{productSlug}' is {productSlug.Length} characters; the maximum is 100.";
            return false;
        }

        if (productSlug.StartsWith('-') || productSlug.EndsWith('-'))
        {
            error = $"Product slug '{productSlug}' cannot start or end with a hyphen.";
            return false;
        }

        if (productSlug.Contains("--", StringComparison.Ordinal))
        {
            error = $"Product slug '{productSlug}' cannot contain consecutive hyphens.";
            return false;
        }

        foreach (var c in productSlug)
        {
            var isLowerAscii = c >= 'a' && c <= 'z';
            var isUpperAscii = c >= 'A' && c <= 'Z';
            var isDigit = c >= '0' && c <= '9';
            if (!(isLowerAscii || isUpperAscii || isDigit || c == '-'))
            {
                error = $"Product slug '{productSlug}' contains invalid character '{c}'. Only [a-zA-Z0-9-] are allowed.";
                return false;
            }
        }

        if (ReservedSlugs.Contains(productSlug))
        {
            error = $"Product slug '{productSlug}' is reserved. Reserved slugs: {string.Join(", ", ReservedSlugs)}.";
            return false;
        }

        error = null;
        return true;
    }

    private readonly string folder;
    private readonly string controlDbPath;

    public ForgeDbPaths(IConfiguration config)
    {
        var legacy = config["Forge:Db:Path"];
        if (!string.IsNullOrWhiteSpace(legacy))
        {
            // Legacy behavior: Forge:Db:Path wins. The control DB lives
            // at the exact path the caller specified; the folder is its
            // directory. Used by ForgeWebApplicationFactory (per-test
            // temp DB) and the existing appsettings.json shape.
            this.controlDbPath = Path.IsPathFullyQualified(legacy)
                ? legacy
                : Path.GetFullPath(legacy);
            this.folder = Path.GetDirectoryName(this.controlDbPath) ?? DefaultFolder;
        }
        else
        {
            var configured = config["Forge:Db:Folder"];
            this.folder = string.IsNullOrWhiteSpace(configured)
                ? DefaultFolder
                : (Path.IsPathFullyQualified(configured) ? configured : Path.GetFullPath(configured));
            this.controlDbPath = Path.Combine(this.folder, ControlDbFileName);
        }

        // Idempotent: CreateDirectory is a no-op when the folder exists.
        Directory.CreateDirectory(this.folder);
    }

    /// <inheritdoc />
    public string Folder => this.folder;

    /// <inheritdoc />
    public string ControlDbPath() => this.controlDbPath;

    /// <inheritdoc />
    public string ProductDbPath(string productSlug)
    {
        var safe = SanitizeSlug(productSlug);
        return Path.Combine(this.folder, safe + ".db");
    }

    /// <inheritdoc />
    public bool ProductDbExists(string productSlug) =>
        File.Exists(this.ProductDbPath(productSlug));

    /// <summary>
    /// Defensive slug sanitization. The Phase 8 contract guarantees slugs
    /// are already lowercase + hyphenated, but path-traversal would be
    /// catastrophic (a slug like <c>..\..\malicious</c> would let a
    /// caller open arbitrary files), so we enforce <c>[a-z0-9-]</c>
    /// explicitly here. Throws on invalid input; for non-throwing
    /// validation (e.g., HTTP 400 responses) use
    /// <see cref="TryValidateSlug"/>.
    /// </summary>
    internal static string SanitizeSlug(string productSlug)
    {
        if (!TryValidateSlug(productSlug, out var error))
        {
            throw new ArgumentException(error, nameof(productSlug));
        }

        // Lowercase normalization (slug column is already lowercase per
        // Phase 8.1 contract; this is defensive for handcrafted callers).
        return productSlug.ToLowerInvariant();
    }
}
