namespace Forge.Api.Tests.Auth;

using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class BuiltInProductOwnerTests
{
    [Fact]
    public async Task BuiltIn_Owner_Sees_All_Products_Without_Granting_Host_Admin_Or_Bypassing_Revocation()
    {
        await using var app = new ForgeWebApplicationFactory();
        await OperatorTestIdentity.SeedProductAsync(app, "p");
        await OperatorTestIdentity.SeedProductAsync(app, "q");
        await app.EnrollAsync(RequireRole.BuiltInProductOwnerId);
        app.Link(RequireRole.BuiltInProductOwnerId);
        var actor = await app.ProviderActorAsync();
        using var scope = app.Services.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<ForgeCapabilityAuthorizer>();
        var readable = await auth.ReadableProductIdsAsync(actor);
        Assert.Contains("p", readable);
        Assert.Contains("q", readable);
        Assert.Equal(await scope.ServiceProvider.GetRequiredService<ControlDbContext>().Products.CountAsync(), readable.Count);
        Assert.True(await auth.IsProductOwnerAsync(actor, "p"));
        Assert.True(await auth.IsProductOwnerAsync(actor, "q"));
        Assert.False(await auth.IsProductOwnerAsync(actor, "missing"));
        Assert.False(await auth.IsAllowedAsync(actor, ForgeCapabilities.IdentityManage, new()));
        await app.EnrollAsync("other");
        app.Link("other", sender: "456");
        var other = await app.ProviderActorAsync(sender: "456");
        Assert.Empty(await auth.ReadableProductIdsAsync(other));
        Assert.False(await auth.IsProductOwnerAsync(other, "p"));
        app.Services.GetRequiredService<HostIdentityStore>().RevokeEnrollment(RequireRole.BuiltInProductOwnerId);
        Assert.Empty(await auth.ReadableProductIdsAsync(actor));
        Assert.False(await auth.IsProductOwnerAsync(actor, "p"));
    }

    [Fact]
    public async Task Legacy_Role_Schema_Is_Repaired_Without_Losing_Grants_And_Is_Idempotent()
    {
        await using var app = new ForgeWebApplicationFactory();
        await OperatorTestIdentity.SeedProductAsync(app);
        await app.EnrollAsync("existing", productId: "p");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE user_product_roles DROP COLUMN UpdatedAt");
        await OperatorRoleSchemaMigration.ApplyAsync(db);
        await OperatorRoleSchemaMigration.ApplyAsync(db);
        var grant = await db.UserProductRoles.AsNoTracking().SingleAsync();
        Assert.Equal("existing", grant.UserId);
        Assert.Equal("product-owner", grant.Role);
        Assert.Equal(DateTimeOffset.UnixEpoch, grant.UpdatedAt);
    }
}
