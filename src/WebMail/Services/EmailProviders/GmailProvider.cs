using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using WebMail.Domain;

namespace WebMail.Services.EmailProviders;

public sealed class GmailProvider(IConfiguration configuration, HttpClient httpClient) : IEmailProvider
{
    public string Name => "Gmail";

    public OAuthStartResult BuildAuthorizationUrl(string state, string redirectUri)
    {
        var clientId = RequiredConfig("GoogleOAuth:ClientId");
        var escapedRedirect = Uri.EscapeDataString(redirectUri);
        var scope = Uri.EscapeDataString("https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/userinfo.email");
        var url = $"https://accounts.google.com/o/oauth2/v2/auth?client_id={clientId}&redirect_uri={escapedRedirect}&response_type=code&scope={scope}&access_type=offline&prompt=consent&state={Uri.EscapeDataString(state)}";
        return new OAuthStartResult(url, state);
    }

    public async Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, string redirectUri, CancellationToken cancellationToken)
    {
        using var tokenResponse = await httpClient.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = RequiredConfig("GoogleOAuth:ClientId"),
            ["client_secret"] = RequiredConfig("GoogleOAuth:ClientSecret"),
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        }), cancellationToken);
        await EnsureSuccessOrThrowAsync(tokenResponse, "Google token exchange", cancellationToken);

        using var tokenPayload = await JsonDocument.ParseAsync(await tokenResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var tokenRoot = tokenPayload.RootElement;
        var accessToken = tokenRoot.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Google token response missing access_token.");
        var refreshToken = tokenRoot.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Google token response missing refresh_token.");
        }

        using var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var userResponse = await httpClient.SendAsync(userRequest, cancellationToken);
        await EnsureSuccessOrThrowAsync(userResponse, "Google userinfo", cancellationToken);

        using var userPayload = await JsonDocument.ParseAsync(await userResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var userRoot = userPayload.RootElement;
        var email = userRoot.TryGetProperty("email", out var e) ? e.GetString() : null;
        var sub = userRoot.TryGetProperty("sub", out var s) ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException("Google userinfo did not return an email.");
        }

        return new OAuthCallbackResult(email, sub ?? string.Empty, refreshToken);
    }

    public async Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        try
        {
            using var service = CreateService(refreshToken);

            var listRequest = service.Users.Messages.List("me");
            listRequest.Q = BuildGmailQuery(allowedSenders, since);
            listRequest.IncludeSpamTrash = true;
            listRequest.MaxResults = 50;

            var listResponse = await listRequest.ExecuteAsync(cancellationToken);
            if (listResponse.Messages is null)
            {
                return [];
            }

            var results = new List<ProviderMessage>();
            foreach (var reference in listResponse.Messages)
            {
                var getRequest = service.Users.Messages.Get("me", reference.Id);
                getRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
                var message = await getRequest.ExecuteAsync(cancellationToken);
                results.Add(MapMessage(message));
            }

            return results;
        }
        catch (Google.Apis.Auth.OAuth2.Responses.TokenResponseException ex) when (ex.Error?.Error == "invalid_grant")
        {
            throw new ProviderAuthorizationException("Gmail refresh token rejected.", ex);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new ProviderAuthorizationException("Gmail authorization failed.", ex);
        }
    }

    private GmailService CreateService(string refreshToken)
    {
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = RequiredConfig("GoogleOAuth:ClientId"),
                ClientSecret = RequiredConfig("GoogleOAuth:ClientSecret")
            }
        });
        var credential = new UserCredential(flow, "user", new TokenResponse { RefreshToken = refreshToken });
        return new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "WebMail"
        });
    }

    private static ProviderMessage MapMessage(Message message)
    {
        var headers = message.Payload?.Headers ?? new List<MessagePartHeader>();
        string Header(string name) => headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

        var sentAt = message.InternalDate is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.UtcNow;

        return new ProviderMessage(
            message.Id ?? string.Empty,
            message.ThreadId,
            Header("From"),
            Header("To"),
            Header("Subject"),
            sentAt,
            ExtractBody(message.Payload, "text/plain"),
            ExtractBody(message.Payload, "text/html"),
            null,
            MapFolder(message.LabelIds));
    }

    internal static string BuildGmailQuery(IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since)
    {
        var senders = allowedSenders
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var parts = new List<string>();
        if (senders.Length > 0)
        {
            parts.Add("(" + string.Join(" OR ", senders.Select(x => $"from:{x}")) + ")");
        }

        if (since is not null)
        {
            parts.Add($"after:{since.Value.UtcDateTime:yyyy/MM/dd}");
        }

        return string.Join(" ", parts);
    }

    internal static MailFolder MapFolder(IList<string>? labelIds) =>
        labelIds is not null && labelIds.Contains("SPAM") ? MailFolder.Junk : MailFolder.Inbox;

    internal static string? ExtractBody(MessagePart? payload, string mimeType)
    {
        if (payload is null)
        {
            return null;
        }

        if (string.Equals(payload.MimeType, mimeType, StringComparison.OrdinalIgnoreCase) && payload.Body?.Data is { Length: > 0 } data)
        {
            return DecodeBase64Url(data);
        }

        if (payload.Parts is null)
        {
            return null;
        }

        foreach (var part in payload.Parts)
        {
            var found = ExtractBody(part, mimeType);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string DecodeBase64Url(string data)
    {
        var normalized = data.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty
        };
        return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
    }

    private string RequiredConfig(string key) =>
        configuration[key] ?? throw new InvalidOperationException($"{key} is required for Gmail OAuth.");

    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, string context, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"{context} failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
    }
}
