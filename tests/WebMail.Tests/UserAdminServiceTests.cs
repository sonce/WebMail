using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class UserAdminServiceTests
{
    [Fact]
    public async Task CreateAddsActiveUserWithHashedPasswordAndAudit()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());

        var result = await svc.CreateAsync(UserRole.Sales, "alice", "Alice", "secret1", actingAdminId: 9);

        Assert.True(result.Success);
        var user = await db.Users.SingleAsync();
        Assert.Equal("alice", user.UserName);
        Assert.Equal(UserRole.Sales, user.Role);
        Assert.True(user.IsActive);
        Assert.NotEqual("secret1", user.PasswordHash);
        Assert.Single(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task CreateRejectsDuplicateUserNameCaseInsensitive()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());
        await svc.CreateAsync(UserRole.Sales, "alice", "Alice", "secret1", null);

        var result = await svc.CreateAsync(UserRole.Supplier, "ALICE", "Other", "secret1", null);

        Assert.False(result.Success);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task CreateRejectsShortPassword()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());

        var result = await svc.CreateAsync(UserRole.Sales, "bob", "Bob", "12345", null);

        Assert.False(result.Success);
        Assert.Empty(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task CreateRejectsEmptyUserName()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());

        var result = await svc.CreateAsync(UserRole.Sales, "   ", "X", "secret1", null);

        Assert.False(result.Success);
        Assert.Empty(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task ResetPasswordChangesHash()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());
        await svc.CreateAsync(UserRole.Sales, "alice", "Alice", "secret1", null);
        var before = (await db.Users.SingleAsync()).PasswordHash;

        var result = await svc.ResetPasswordAsync((await db.Users.SingleAsync()).Id, "newpass1", null);

        Assert.True(result.Success);
        Assert.NotEqual(before, (await db.Users.SingleAsync()).PasswordHash);
    }

    [Fact]
    public async Task SetActiveTogglesFlag()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());
        await svc.CreateAsync(UserRole.Sales, "alice", "Alice", "secret1", null);
        var id = (await db.Users.SingleAsync()).Id;

        await svc.SetActiveAsync(id, false, null);

        Assert.False((await db.Users.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task ResetPasswordRejectsRoleMismatch()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());
        db.Users.Add(new AppUser { UserName = "alice", Role = UserRole.Sales, DisplayName = "Alice", PasswordHash = "orig" });
        await db.SaveChangesAsync();
        var id = (await db.Users.SingleAsync()).Id;

        var result = await svc.ResetPasswordAsync(id, "newpass1", null, UserRole.Supplier);

        Assert.False(result.Success);
        Assert.Equal("orig", (await db.Users.SingleAsync()).PasswordHash);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task SetActiveRejectsRoleMismatch()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());
        db.Users.Add(new AppUser { UserName = "alice", Role = UserRole.Sales, DisplayName = "Alice", IsActive = true });
        await db.SaveChangesAsync();
        var id = (await db.Users.SingleAsync()).Id;

        var result = await svc.SetActiveAsync(id, false, null, UserRole.Supplier);

        Assert.False(result.Success);
        Assert.True((await db.Users.SingleAsync()).IsActive);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task ResetPasswordAllowsMatchingRole()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());
        db.Users.Add(new AppUser { UserName = "alice", Role = UserRole.Sales, DisplayName = "Alice", PasswordHash = "orig" });
        await db.SaveChangesAsync();
        var id = (await db.Users.SingleAsync()).Id;

        var result = await svc.ResetPasswordAsync(id, "newpass1", null, UserRole.Sales);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ListByRoleCountsSalesBuyersExcludingDeleted()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());
        await svc.CreateAsync(UserRole.Sales, "alice", "Alice", "secret1", null);
        var saleId = (await db.Users.SingleAsync()).Id;
        db.Buyers.Add(new Buyer { CardNo = "a", SaleId = saleId });
        db.Buyers.Add(new Buyer { CardNo = "b", SaleId = saleId, IsDeleted = true });
        await db.SaveChangesAsync();

        var list = await svc.ListByRoleAsync(UserRole.Sales);

        Assert.Equal(1, Assert.Single(list).LinkedBuyerCount);
    }

    [Fact]
    public async Task ListByRoleCountsSupplierAssignmentsExcludingDeletedBuyers()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());
        await svc.CreateAsync(UserRole.Supplier, "sup", "Sup", "secret1", null);
        var supId = (await db.Users.SingleAsync()).Id;
        var b1 = new Buyer { CardNo = "a" };
        var b2 = new Buyer { CardNo = "b", IsDeleted = true };
        db.Buyers.AddRange(b1, b2);
        await db.SaveChangesAsync();
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = b1.Id, SupplierId = supId });
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = b2.Id, SupplierId = supId });
        await db.SaveChangesAsync();

        var list = await svc.ListByRoleAsync(UserRole.Supplier);

        Assert.Equal(1, Assert.Single(list).LinkedBuyerCount);
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
