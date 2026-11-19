// -----------------------------------------------------------------------
// <copyright file="HostOperatorCommand.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using System.Diagnostics;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Host-local CLI; exits before the web host and background workers are constructed.</summary>
public static class HostOperatorCommand
{
    public static async Task RunAsync(string[] args)
    {
        string? Value(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var action = Value("--operator");
        var id = Value("--user-id");
        if (string.IsNullOrWhiteSpace(id) && action != "open-browser")
            throw new ArgumentException("Specify --user-id with a stable Forge user ID.");
        using var configuration = new ConfigurationManager();
        configuration.SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables();
        if (Value("--db-folder") is { } folder)
        {
            configuration["Forge:Db:Folder"] = folder;
            configuration["Forge:Db:Path"] = null;
        }
        var paths = new ForgeDbPaths(configuration);
        await using var db = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlite(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = paths.ControlDbPath() }.ToString()).Options);
        var identities = new HostIdentityStore(configuration["Forge:Identity:StorePath"] ?? HostIdentityStore.DefaultPath());
        if (action == "open-browser" && string.IsNullOrWhiteSpace(id))
        {
            var enrolledIds = identities.EnrolledUserIds.ToList();
            var candidates = await db.Users.Where(u => enrolledIds.Contains(u.Id)).Select(u => u.Id).ToListAsync();
            if (candidates.Count != 1)
            {
                Console.WriteLine(candidates.Count == 0
                    ? "No enrolled operator. Enroll your existing Forge user before opening a browser session."
                    : "Multiple operators enrolled. Use --operator open-browser --user-id <stable-id>.");
                return;
            }
            id = candidates[0];
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        switch (action)
        {
            case "open-browser":
                if (!await db.Users.AnyAsync(u => u.Id == id)) throw new InvalidOperationException("Unknown Forge user. Enroll first.");
                var browserChallenge = identities.IssueBrowserChallenge(id);
                // A fragment is never sent in HTTP requests or Referer. The UI removes it
                // before redemption; only the trusted local OS/browser receives this value.
                Process.Start(new ProcessStartInfo("http://localhost:5181/workstreams#forge-pair=" + browserChallenge)
                {
                    UseShellExecute = true,
                });
                Console.WriteLine("Opened Forge with your existing operator account.");
                break;
            case "enroll":
                await db.Database.EnsureCreatedAsync();
                if (!await db.Users.AnyAsync(u => u.Id == id))
                {
                    db.Users.Add(new UserRow { Id = id, DisplayName = Value("--display-name") ?? id, Role = "human" });
                    await db.SaveChangesAsync();
                }
                identities.Enroll(id, args.Contains("--host-admin", StringComparer.Ordinal));
                Console.WriteLine("Operator enrolled. Product roles must be granted explicitly.");
                break;
            case "pair":
                if (!await db.Users.AnyAsync(u => u.Id == id)) throw new InvalidOperationException("Unknown Forge user. Enroll first.");
                var challenge = identities.IssueBrowserChallenge(id);
                // The challenge never appears in argv, URLs, console output, or logs.
                var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true };
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "Set-Clipboard -Value ([Console]::In.ReadToEnd())" }) start.ArgumentList.Add(arg);
                using (var clipboard = Process.Start(start)!)
                {
                    await clipboard.StandardInput.WriteAsync(challenge);
                    clipboard.StandardInput.Close();
                    await clipboard.WaitForExitAsync();
                    if (clipboard.ExitCode != 0) throw new IOException("Could not copy pairing challenge to the clipboard.");
                }
                Console.WriteLine("Pairing code copied to clipboard. Paste into Forge's Pair browser form within two minutes.");
                break;
            case "revoke":
                identities.RevokeEnrollment(id);
                Console.WriteLine("Enrollment, browser sessions, and provider links revoked.");
                break;
            case "grant-role":
                var productId = Value("--product-id");
                var roleName = Value("--role");
                if (roleName is null || !ForgeCapabilities.Roles.Contains(roleName) || productId is null || productId == "all"
                    || identities.GetEnrollment(id) is not { Enabled: true } || !await db.Users.AnyAsync(u => u.Id == id)
                    || !await db.Products.AnyAsync(p => p.Id == productId)) throw new ArgumentException("Specify an enrolled user, existing product ID, and supported role.");
                // The CLI can run against a control DB the API has not upgraded yet.
                await OperatorRoleSchemaMigration.ApplyAsync(db);
                var role = await db.UserProductRoles.FindAsync(id, productId);
                if (role is null) { role = new() { UserId = id, ProductId = productId, GrantedBy = "host-cli" }; db.UserProductRoles.Add(role); }
                role.Role = roleName;
                role.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
                Console.WriteLine("Product role saved.");
                break;
            default: throw new ArgumentException("Use --operator enroll|open-browser|pair|revoke|grant-role --user-id <stable-id>.");
        }
    }
}
