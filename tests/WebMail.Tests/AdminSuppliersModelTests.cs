using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using WebMail.Pages.Admin;
using Xunit;

namespace WebMail.Tests;

public sealed class AdminSuppliersModelTests
{
    [Fact]
    public async Task CreateAddsSupplierUser()
    {
        await using var db = CreateDb();
        var model = CreateModel(db);
        model.NewUserName = "sup";
        model.NewDisplayName = "Sup";
        model.NewPassword = "secret1";

        await model.OnPostCreateAsync();

        Assert.Equal(UserRole.Supplier, (await db.Users.SingleAsync()).Role);
    }

    [Fact]
    public async Task GetLoadsOnlySupplierUsers()
    {
        await using var db = CreateDb();
        db.Users.Add(new AppUser { UserName = "s", Role = UserRole.Sales, DisplayName = "s" });
        db.Users.Add(new AppUser { UserName = "p", Role = UserRole.Supplier, DisplayName = "p" });
        await db.SaveChangesAsync();
        var model = CreateModel(db);

        await model.OnGetAsync();

        Assert.Single(model.Users);
        Assert.Equal("p", model.Users[0].UserName);
    }

    [Fact]
    public async Task SetActiveDisablesSupplier()
    {
        await using var db = CreateDb();
        db.Users.Add(new AppUser { UserName = "p", Role = UserRole.Supplier, DisplayName = "p" });
        await db.SaveChangesAsync();
        var id = (await db.Users.SingleAsync()).Id;
        var model = CreateModel(db);

        await model.OnPostSetActiveAsync(id, false);

        Assert.False((await db.Users.SingleAsync()).IsActive);
    }

    private static SuppliersModel CreateModel(WebMailDbContext db)
    {
        var model = new SuppliersModel(new UserAdminService(db, new PasswordHasher<AppUser>()), TestLocalizer.Shared);
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
        };
        model.PageContext = new PageContext { HttpContext = ctx };
        return model;
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
