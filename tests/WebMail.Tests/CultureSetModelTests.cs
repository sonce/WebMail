using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebMail.Pages.Culture;
using Xunit;

namespace WebMail.Tests;

public sealed class CultureSetModelTests
{
    private static SetModel CreateModel(out HttpContext ctx)
    {
        ctx = new DefaultHttpContext();
        return new SetModel { PageContext = new PageContext { HttpContext = ctx } };
    }

    [Fact]
    public void SupportedCulture_WritesCookie_AndRedirectsToLocalReturnUrl()
    {
        var model = CreateModel(out var ctx);

        var result = model.OnGet("zh-CN", "/Admin/Buyers");

        Assert.Equal("/Admin/Buyers", Assert.IsType<RedirectResult>(result).Url);
        var setCookie = ctx.Response.Headers["Set-Cookie"].ToString();
        Assert.Contains(CookieRequestCultureProvider.DefaultCookieName, setCookie);
        Assert.Contains("zh-CN", setCookie);
    }

    [Fact]
    public void NonLocalReturnUrl_RedirectsToRoot()
    {
        var model = CreateModel(out _);

        var result = model.OnGet("en", "https://evil.com");

        Assert.Equal("/", Assert.IsType<RedirectResult>(result).Url);
    }

    [Fact]
    public void UnsupportedCulture_DoesNotWriteCookie()
    {
        var model = CreateModel(out var ctx);

        var result = model.OnGet("fr-FR", "/");

        Assert.Equal("/", Assert.IsType<RedirectResult>(result).Url);
        Assert.True(string.IsNullOrEmpty(ctx.Response.Headers["Set-Cookie"].ToString()));
    }
}
