using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services.Auth;
using Xunit;

namespace WebMail.Tests;

public sealed class IdentitySeederTests
{
    [Fact]
    public async Task SeedsAdminWhenMissing()
    {
        await using var db = CreateDb();
        var hasher = new PasswordHasher<AppUser>();

        await IdentitySeeder.EnsureAdminSeededAsync(db, hasher, "admin", "Admin@123");

        var user = await db.Users.SingleAsync();
        Assert.Equal("admin", user.UserName);
        Assert.Equal(UserRole.Administrator, user.Role);
        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(user, user.PasswordHash, "Admin@123"));
    }

    [Fact]
    public async Task SeedIsIdempotentAndKeepsOriginalPassword()
    {
        await using var db = CreateDb();
        var hasher = new PasswordHasher<AppUser>();

        await IdentitySeeder.EnsureAdminSeededAsync(db, hasher, "admin", "Admin@123");
        await IdentitySeeder.EnsureAdminSeededAsync(db, hasher, "admin", "different");

        Assert.Equal(1, await db.Users.CountAsync());
        var user = await db.Users.SingleAsync();
        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(user, user.PasswordHash, "Admin@123"));
    }

    [Fact]
    public async Task SeedIsIdempotentAcrossCase()
    {
        await using var db = CreateDb();
        var hasher = new PasswordHasher<AppUser>();

        await IdentitySeeder.EnsureAdminSeededAsync(db, hasher, "admin", "Admin@123");
        await IdentitySeeder.EnsureAdminSeededAsync(db, hasher, "ADMIN", "Admin@123");

        Assert.Equal(1, await db.Users.CountAsync());
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
