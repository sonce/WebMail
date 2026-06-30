using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Pages.OAuth;
using WebMail.Services.EmailProviders;
using Xunit;

namespace WebMail.Tests;

public sealed class StartModelTests
{
    [Fact]
    public async Task RedirectsWithIssuedStateForValidCard()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "c1", Stage = BuyerStage.NotSubmitted });
        await db.SaveChangesAsync();

        var model = CreateModel(db);

        var result = await model.OnGetAsync("Gmail", "c1");

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Contains("state-Gmail-c1", redirect.Url);
    }

    [Fact]
    public async Task MissingCardReturnsBadRequest()
    {
        await using var db = CreateDb();
        var model = CreateModel(db);

        Assert.IsType<BadRequestObjectResult>(await model.OnGetAsync("Gmail", ""));
    }

    [Fact]
    public async Task UnknownCardReturnsBadRequest()
    {
        await using var db = CreateDb();
        var model = CreateModel(db);

        Assert.IsType<BadRequestObjectResult>(await model.OnGetAsync("Gmail", "no-such-card"));
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static StartModel CreateModel(WebMailDbContext db) =>
        new(db, new EmailProviderResolver([new UrlEchoProvider("Gmail")]), TestLocalizer.Shared, new FakeOAuthStateStore())
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext { Request = { Scheme = "https", Host = new HostString("localhost", 7121) } } }
        };

    private sealed class UrlEchoProvider(string name) : IEmailProvider
    {
        public string Name { get; } = name;
        public OAuthStartResult BuildAuthorizationUrl(string state, string redirectUri) => new($"https://provider/auth?state={state}", state);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, string redirectUri, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
