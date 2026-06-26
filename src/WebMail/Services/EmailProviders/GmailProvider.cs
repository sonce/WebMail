namespace WebMail.Services.EmailProviders;

public sealed class GmailProvider(IConfiguration configuration) : IEmailProvider
{
    public string Name => "Gmail";

    public OAuthStartResult BuildAuthorizationUrl(string cardNo)
    {
        var clientId = configuration["GoogleOAuth:ClientId"] ?? string.Empty;
        var redirectUri = Uri.EscapeDataString(configuration["GoogleOAuth:RedirectUri"] ?? string.Empty);
        var state = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cardNo));
        var scope = Uri.EscapeDataString("https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/userinfo.email");
        var url = $"https://accounts.google.com/o/oauth2/v2/auth?client_id={clientId}&redirect_uri={redirectUri}&response_type=code&scope={scope}&access_type=offline&prompt=consent&state={Uri.EscapeDataString(state)}";
        return new OAuthStartResult(url, state);
    }

    public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, CancellationToken cancellationToken) => throw new NotImplementedException("Configure Google OAuth credentials before enabling token exchange.");
    public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string encryptedRefreshToken, string senderQuery, DateTimeOffset? since, CancellationToken cancellationToken) => throw new NotImplementedException("Token encryption and Gmail fetch are implemented after the skeleton is verified.");
}
