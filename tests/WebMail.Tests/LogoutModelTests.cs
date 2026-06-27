using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebMail.Pages;
using Xunit;

namespace WebMail.Tests;

public sealed class LogoutModelTests
{
    [Fact]
    public async Task LogoutSignsOutAndRedirectsToLogin()
    {
        var (ctx, auth) = TestHttpContext.WithAuth();
        var model = new LogoutModel { PageContext = new PageContext { HttpContext = ctx } };

        var result = await model.OnPostAsync();

        Assert.Equal("/Login", Assert.IsType<RedirectToPageResult>(result).PageName);
        Assert.True(auth.SignedOut);
    }
}
