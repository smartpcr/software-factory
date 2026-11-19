// -----------------------------------------------------------------------
// <copyright file="ProductDbMoveCommand.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence.Split;

using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Host-local CLI, run with Forge.Api stopped:
/// <c>Forge.Api.exe --db move-product --product-id &lt;id&gt; [--dry-run] [--db-folder &lt;dir&gt;]</c>.
/// Exits before the web host and background workers are constructed.
/// </summary>
public static class ProductDbMoveCommand
{
    private const int ApiPort = 5180;

    public static async Task<int> RunAsync(string[] args)
    {
        string? Value(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (Value("--db") != "move-product")
        {
            Console.Error.WriteLine("Use --db move-product --product-id <id> [--dry-run] [--db-folder <dir>].");
            return 2;
        }
        var productId = Value("--product-id");
        if (string.IsNullOrWhiteSpace(productId))
        {
            Console.Error.WriteLine("Specify --product-id.");
            return 2;
        }
        var dryRun = args.Contains("--dry-run", StringComparer.Ordinal);

        using var configuration = new ConfigurationManager();
        configuration.SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables();
        var folder = Value("--db-folder");
        if (folder is not null)
        {
            configuration["Forge:Db:Folder"] = folder;
            configuration["Forge:Db:Path"] = null;
        }

        // A scratch --db-folder copy is safe to move while the server runs;
        // the live folder is not (DEC-4).
        if (!dryRun && folder is null && IsListening(ApiPort))
        {
            Console.Error.WriteLine($"Forge.Api is listening on port {ApiPort}. Stop it before moving a product (scripts\\run-forge-ui.ps1 -Stop).");
            return 3;
        }

        var paths = new ForgeDbPaths(configuration);
        var service = new ProductDbMoveService(paths, new ProductDbProvisioner(paths));
        var result = await service.MoveAsync(productId, dryRun);

        Console.WriteLine($"Product '{result.ProductId}' (slug '{result.Slug}') — {(result.DryRun ? "dry run, nothing written" : "move")}");
        Console.WriteLine($"Control DB: {paths.ControlDbPath()}");
        foreach (var t in result.Tables.Where(t => t.SourceRows > 0 || t.CopiedRows > 0))
            Console.WriteLine($"  {t.Table,-28} source {t.SourceRows,8}  copied {t.CopiedRows,8}  removed {t.DeletedRows,8}");
        Console.WriteLine($"  total rows: {result.Tables.Sum(t => t.SourceRows)}");
        if (result.BackupPath is not null) Console.WriteLine($"Backup (kept): {result.BackupPath}");
        if (result.ProductDbPath is not null) Console.WriteLine($"Product DB: {result.ProductDbPath}");
        foreach (var e in result.Errors) Console.Error.WriteLine("ERROR: " + e);
        if (!result.Success && !result.DryRun && result.BackupPath is not null)
            Console.Error.WriteLine("The control DB was left unchanged and no product DB file was kept.");
        return result.Success ? 0 : 1;
    }

    private static bool IsListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromSeconds(1)) && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }
}