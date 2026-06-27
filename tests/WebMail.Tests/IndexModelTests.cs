using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebMail.Pages;
using Xunit;

namespace WebMail.Tests;

public sealed class IndexModelTests
{
    [Fact]
    public void RedirectsToBuyerVerifyWhenCardPresent()
    {
        var model = new IndexModel { PageContext = Ctx(Anonymous()) };

        var result = Assert.IsType<RedirectToPageResult>(model.OnGet("CARD123", 7));

        Assert.Equal("/Buyer/Verify", result.PageName);
        Assert.Equal("CARD123", result.RouteValues!["card"]);
        Assert.Equal(7L, result.RouteValues!["saleid"]);
    }

    [Fact]
    public void RedirectsToLoginWhenNoCardAndAnonymous()
    {
        var model = new IndexModel { PageContext = Ctx(Anonymous()) };

        var result = Assert.IsType<RedirectToPageResult>(model.OnGet(null, null));

        Assert.Equal("/Login", result.PageName);
    }

    [Fact]
    public void RedirectsToRoleLandingWhenAuthenticated()
    {
        var model = new IndexModel { PageContext = Ctx(WithRole("Sales")) };

        var result = Assert.IsType<RedirectToPageResult>(model.OnGet(null, null));

        Assert.Equal("/Sales/Buyers", result.PageName);
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());
    private static ClaimsPrincipal WithRole(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
    private static PageContext Ctx(ClaimsPrincipal user) =>
        new() { HttpContext = new DefaultHttpContext { User = user } };
}
