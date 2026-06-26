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
            ["OutlookOAuth:ClientId"] = "outlook-client",
            ["OutlookOAuth:RedirectUri"] = "https://localhost:5001/oauth/callback?provider=Outlook"
        }), new HttpClient());

        var result = provider.BuildAuthorizationUrl("CARD-001");
        var uri = new Uri(result.RedirectUrl);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);

        Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/authorize", result.RedirectUrl.Split('?')[0]);
        Assert.Equal("outlook-client", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("https://localhost:5001/oauth/callback?provider=Outlook", query["redirect_uri"]);
        Assert.Contains("offline_access", query["scope"].ToString());
        Assert.Contains("Mail.Read", query["scope"].ToString());
        Assert.Equal("CARD-001", result.State);
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
        public OAuthStartResult BuildAuthorizationUrl(string cardNo) => new(cardNo, cardNo);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
