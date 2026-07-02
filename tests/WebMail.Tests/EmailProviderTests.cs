using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebMail.Services.EmailProviders;

namespace WebMail.Tests;

public sealed class EmailProviderTests
{
    [Fact]
    public void OutlookAuthorizationUrlUsesMicrosoftIdentityEndpointAndMailScopes()
    {
        var provider = new OutlookProvider(CreateConfiguration(new Dictionary<string, string?>
        {
            ["OutlookOAuth:ClientId"] = "outlook-client"
        }), new HttpClient());

        var result = provider.BuildAuthorizationUrl("CARD-001", "https://localhost:7121/oauth/callback");
        var uri = new Uri(result.RedirectUrl);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);

        Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/authorize", result.RedirectUrl.Split('?')[0]);
        Assert.Equal("outlook-client", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("https://localhost:7121/oauth/callback", query["redirect_uri"]);
        Assert.Contains("offline_access", query["scope"].ToString());
        Assert.Contains("Mail.Read", query["scope"].ToString());
        Assert.Equal("CARD-001", result.State);
    }

    [Fact]
    public async Task OutlookRefreshSendsRedirectUriBuiltFromPublicBaseUrl()
    {
        var handler = new TokenCapturingHandler();
        var provider = new OutlookProvider(CreateConfiguration(new Dictionary<string, string?>
        {
            ["OutlookOAuth:ClientId"] = "outlook-client",
            ["OutlookOAuth:ClientSecret"] = "secret",
            ["WebMail:PublicBaseUrl"] = "https://webmail.example/"
        }), new HttpClient(handler));

        await provider.FetchMessagesAsync("refresh-token", Array.Empty<string>(), null, CancellationToken.None);

        Assert.Equal("https://webmail.example/oauth/callback", handler.RefreshRedirectUri);
    }

    [Fact]
    public async Task OutlookRefreshThrowsWhenPublicBaseUrlMissing()
    {
        var provider = new OutlookProvider(CreateConfiguration(new Dictionary<string, string?>
        {
            ["OutlookOAuth:ClientId"] = "outlook-client",
            ["OutlookOAuth:ClientSecret"] = "secret"
        }), new HttpClient(new TokenCapturingHandler()));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.FetchMessagesAsync("refresh-token", Array.Empty<string>(), null, CancellationToken.None));
    }

    [Fact]
    public void EmailProviderResolverFindsProvidersCaseInsensitive()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmailProvider>(new StubProvider("Gmail"));
        services.AddSingleton<IEmailProvider>(new StubProvider("Outlook"));
        services.AddSingleton<IEmailProviderResolver, EmailProviderResolver>();

        var resolver = services.BuildServiceProvider().GetRequiredService<IEmailProviderResolver>();

        Assert.Equal("Outlook", resolver.Resolve("outlook").Name);
        Assert.Equal("Gmail", resolver.Resolve("GMAIL").Name);
    }

    [Fact]
    public void EmailProviderResolverRejectsUnknownProvider()
    {
        var resolver = new EmailProviderResolver([new StubProvider("Gmail")]);

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve("Outlook"));

        Assert.Contains("Unsupported email provider", ex.Message);
    }

    private static IConfiguration CreateConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class StubProvider(string name) : IEmailProvider
    {
        public string Name { get; } = name;
        public OAuthStartResult BuildAuthorizationUrl(string cardNo, string redirectUri) => new(cardNo, cardNo);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, string redirectUri, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class TokenCapturingHandler : HttpMessageHandler
    {
        public string? RefreshRedirectUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null && request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.OrdinalIgnoreCase))
            {
                var form = await (request.Content?.ReadAsStringAsync(cancellationToken) ?? Task.FromResult(string.Empty));
                foreach (var pair in form.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split('=');
                    if (kv.Length == 2 && kv[0] == "redirect_uri")
                    {
                        RefreshRedirectUri = Uri.UnescapeDataString(kv[1]);
                    }
                }

                var json = """{"access_token":"at","refresh_token":"rt"}""";
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            }

            // Microsoft Graph folder messages: return an empty value array so FetchMessagesAsync completes.
            var empty = """{"value":[]}""";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(empty, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
