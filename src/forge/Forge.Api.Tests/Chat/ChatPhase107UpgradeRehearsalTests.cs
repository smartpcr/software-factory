// -----------------------------------------------------------------------
// <copyright file="ChatPhase107UpgradeRehearsalTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using Forge.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Upgrade rehearsal for Phase 107 against a COPY of a real control DB.
/// Phases 98/100 regenerate the chat model (including the new ChildKey index) with
/// IF NOT EXISTS, so on an existing DB Phase 107 must add the columns first or boot
/// fails with "no such column". Fresh-DB tests cannot see this; only an existing
/// pre-107 database can. Opt-in: set FORGE_UPGRADE_DB to the DB file to copy. The
/// source file is only copied, never opened.
/// </summary>
public sealed class ChatPhase107UpgradeRehearsalTests
{
    [Fact]
    public async Task Phase107_Then_The_Chat_Migrations_Upgrade_An_Existing_Control_Db()
    {
        var source = Environment.GetEnvironmentVariable("FORGE_UPGRADE_DB");
        if (string.IsNullOrEmpty(source) || !File.Exists(source)) return; // opt-in rehearsal

        var copy = Path.Combine(Path.GetTempPath(), $"forge-p107-{Guid.NewGuid():N}.db");
        File.Copy(source, copy);
        try
        {
            long bindingsBefore;
            await using (var raw = new SqliteConnection($"Data Source={copy};Pooling=False"))
            {
                await raw.OpenAsync();
                await using var count = raw.CreateCommand();
                count.CommandText = "SELECT count(*) FROM chat_conversation_bindings";
                bindingsBefore = (long)(await count.ExecuteScalarAsync())!;
            }

            var options = new DbContextOptionsBuilder<ControlDbContext>().UseSqlite($"Data Source={copy};Pooling=False").Options;
            await using (var control = new ControlDbContext(options))
            {
                // The runner order: 107 at the head, then the chat phases that rebuild the model.
                await Phase107Migration.ApplyAsync(control, null!);
                await Phase98Migration.ApplyAsync(control, null!);
                await Phase100Migration.ApplyAsync(control, null!);
                await Phase104Migration.ApplyAsync(control, null!);
                await Phase105Migration.ApplyAsync(control, null!);
                await Phase107Migration.ApplyAsync(control, null!); // idempotent on a second boot

                // Every mapped column is readable through EF.
                var bindings = await control.ChatConversationBindings.AsNoTracking().ToListAsync();
                Assert.Equal(bindingsBefore, bindings.Count);
                Assert.All(bindings, b => Assert.Equal("", b.ChildKey));
                Assert.All(await control.ChatSubscriptions.AsNoTracking().ToListAsync(), s => Assert.Equal(ChatConversationScopes.Story, s.ConversationScope));
                Assert.All(await control.ChatConnections.AsNoTracking().ToListAsync(), c => Assert.False(c.AllProducts));
                Assert.Empty(await control.ChatHostDefaults.AsNoTracking().ToListAsync());
            }

            await using (var raw = new SqliteConnection($"Data Source={copy};Pooling=False"))
            {
                await raw.OpenAsync();
                await using var indexes = raw.CreateCommand();
                indexes.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'chat_conversation_bindings'";
                var found = new Dictionary<string, string?>();
                await using (var reader = await indexes.ExecuteReaderAsync())
                    while (await reader.ReadAsync()) found[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
                Assert.DoesNotContain("IX_chat_conversation_bindings_active_revision", found.Keys);
                Assert.Contains("ChildKey", found["IX_chat_conversation_bindings_active_child"]);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(copy);
        }
    }
}