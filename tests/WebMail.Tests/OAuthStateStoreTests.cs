using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using WebMail.Services.Security;
using Xunit;

namespace WebMail.Tests;

public sealed class OAuthStateStoreTests
{
    private static readonly IDataProtectionProvider Dp = new EphemeralDataProtectionProvider();

    [Fact]
    public void IssueThenConsumeReturnsBoundProviderAndCard()
    {
        var issueCtx = new DefaultHttpContext();
        var nonce = NewStore(issueCtx).Issue("Gmail", "card-1");

        var consumeCtx = new DefaultHttpContext();
        TransferCookies(issueCtx, consumeCtx);

        var result = NewStore(consumeCtx).Consume(nonce);

        Assert.NotNull(result);
        Assert.Equal("Gmail", result!.Provider);
        Assert.Equal("card-1", result.Card);
    }

    [Fact]
    public void ConsumeRecoversProviderFromCookieRegardlessOfQueryString()
    {
        // The provider is no longer supplied on the callback query string (Microsoft drops it),
        // so it must be recovered from the encrypted state cookie that was issued.
        var issueCtx = new DefaultHttpContext();
        var nonce = NewStore(issueCtx).Issue("Outlook", "card-9");

        var consumeCtx = new DefaultHttpContext();
        TransferCookies(issueCtx, consumeCtx);

        var result = NewStore(consumeCtx).Consume(nonce);

        Assert.NotNull(result);
        Assert.Equal("Outlook", result!.Provider);
        Assert.Equal("card-9", result.Card);
    }

    [Fact]
    public void ConsumeRejectsForgedState()
    {
        var issueCtx = new DefaultHttpContext();
        NewStore(issueCtx).Issue("Gmail", "card-1");

        var consumeCtx = new DefaultHttpContext();
        TransferCookies(issueCtx, consumeCtx);

        Assert.Null(NewStore(consumeCtx).Consume("attacker-supplied-state"));
    }

    [Fact]
    public void ConsumeReturnsNullWhenNoCookiePresent()
    {
        Assert.Null(NewStore(new DefaultHttpContext()).Consume("anything"));
    }

    private static CookieOAuthStateStore NewStore(HttpContext ctx) =>
        new(new HttpContextAccessor { HttpContext = ctx }, Dp);

    private static void TransferCookies(HttpContext from, HttpContext to)
    {
        var jar = new List<string>();
        foreach (var setCookie in (StringValues)from.Response.Headers.SetCookie)
        {
            if (!string.IsNullOrEmpty(setCookie))
            {
                jar.Add(setCookie.Split(';')[0]);
            }
        }

        to.Request.Headers.Cookie = string.Join("; ", jar);
    }
}
