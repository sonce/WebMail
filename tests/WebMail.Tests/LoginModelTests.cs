using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Pages;
using Xunit;

namespace WebMail.Tests;

public sealed class LoginModelTests
{
    [Fact]
    public async Task ValidCredentialsSignInPersistentAndRedirectToRoleLanding()
    {
        await using var db = CreateDb();
        var hasher = new PasswordHasher<AppUser>();
        await SeedUser(db, hasher, "sue", "pw", UserRole.Sales);
        var (ctx, auth) = TestHttpContext.WithAuth();
        var model = new LoginModel(db, hasher)
        {
            PageContext = new PageContext { HttpContext = ctx },
            UserName = "sue",
            Password = "pw",
        };

        var result = await model.OnPostAsync();

        Assert.Equal("/Sales/Buyers", Assert.IsType<RedirectToPageResult>(result).PageName);
        Assert.NotNull(auth.SignedInPrincipal);
        Assert.True(auth.SignInProperties!.IsPersistent);
        Assert.Equal("Sales", auth.SignedInPrincipal!.FindFirstValue(ClaimTypes.Role));
    }

    [Fact]
    public async Task WrongPasswordShowsErrorAndDoesNotSignIn()
    {
        await using var db = CreateDb();
        var hasher = new PasswordHasher<AppUser>();
        await SeedUser(db, hasher, "sue", "pw", UserRole.Sales);
        var (ctx, auth) = TestHttpContext.WithAuth();
        var model = new LoginModel(db, hasher)
        {
            PageContext = new PageContext { HttpContext = ctx },
            UserName = "sue",
            Password = "wrong",
        };

        var result = await model.OnPostAsync();

        Assert.IsType<PageResult>(result);
        Assert.Equal("用户名或密码错误", model.ErrorMessage);
        Assert.Null(auth.SignedInPrincipal);
    }

    [Fact]
    public async Task UnknownUserShowsErrorAndDoesNotSignIn()
    {
        await using var db = CreateDb();
        var hasher = new PasswordHasher<AppUser>();
        var (ctx, auth) = TestHttpContext.WithAuth();
        var model = new LoginModel(db, hasher)
        {
            PageContext = new PageContext { HttpContext = ctx },
            UserName = "nobody",
            Password = "pw",
        };

        var result = await model.OnPostAsync();

        Assert.IsType<PageResult>(result);
        Assert.Equal("用户名或密码错误", model.ErrorMessage);
        Assert.Null(auth.SignedInPrincipal);
    }

    [Fact]
    public async Task LocalReturnUrlIsHonored()
    {
        await using var db = CreateDb();
        var hasher = new PasswordHasher<AppUser>();
        await SeedUser(db, hasher, "sue", "pw", UserRole.Sales);
        var (ctx, _) = TestHttpContext.WithAuth();
        var model = new LoginModel(db, hasher)
        {
            PageContext = new PageContext { HttpContext = ctx },
            UserName = "sue",
            Password = "pw",
            ReturnUrl = "/Admin/Buyers",
        };

        var result = await model.OnPostAsync();

        Assert.Equal("/Admin/Buyers", Assert.IsType<RedirectResult>(result).Url);
    }

    [Fact]
    public async Task NonLocalReturnUrlIsIgnored()
    {
        await using var db = CreateDb();
        var hasher = new PasswordHasher<AppUser>();
        await SeedUser(db, hasher, "sue", "pw", UserRole.Sales);
        var (ctx, _) = TestHttpContext.WithAuth();
        var model = new LoginModel(db, hasher)
        {
            PageContext = new PageContext { HttpContext = ctx },
            UserName = "sue",
            Password = "pw",
            ReturnUrl = "https://evil.com",
        };

        var result = await model.OnPostAsync();

        Assert.Equal("/Sales/Buyers", Assert.IsType<RedirectToPageResult>(result).PageName);
    }

    [Fact]
    public async Task UpperCaseUserNameStillSignsIn()
    {
        await using var db = CreateDb();
        var hasher = new PasswordHasher<AppUser>();
        await SeedUser(db, hasher, "sue", "pw", UserRole.Sales);
        var (ctx, auth) = TestHttpContext.WithAuth();
        var model = new LoginModel(db, hasher)
        {
            PageContext = new PageContext { HttpContext = ctx },
            UserName = "SUE",
            Password = "pw",
        };

        var result = await model.OnPostAsync();

        Assert.Equal("/Sales/Buyers", Assert.IsType<RedirectToPageResult>(result).PageName);
        Assert.NotNull(auth.SignedInPrincipal);
    }

    private static async Task SeedUser(WebMailDbContext db, PasswordHasher<AppUser> hasher, string name, string pw, UserRole role)
    {
        var user = new AppUser { UserName = name, Role = role, DisplayName = name };
        user.PasswordHash = hasher.HashPassword(user, pw);
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
