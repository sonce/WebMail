using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using WebMail.Domain;

namespace WebMail.Services.EmailProviders;

public sealed class OutlookProvider(IConfiguration configuration, HttpClient httpClient) : IEmailProvider
{
    private const string AuthorizeEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    private const string TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const string GraphEndpoint = "https://graph.microsoft.com/v1.0";
    private const string Scope = "offline_access User.Read Mail.Read";

    public string Name => "Outlook";

    public OAuthStartResult BuildAuthorizationUrl(string cardNo)
    {
        var state = cardNo;
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = configuration["OutlookOAuth:ClientId"] ?? string.Empty,
            ["redirect_uri"] = configuration["OutlookOAuth:RedirectUri"] ?? string.Empty,
            ["response_type"] = "code",
            ["scope"] = Scope,
            ["response_mode"] = "query",
            ["state"] = state
        };

        return new OAuthStartResult(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(AuthorizeEndpoint, query), state);
    }

    public async Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, CancellationToken cancellationToken)
    {
        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = RequiredConfig("OutlookOAuth:ClientId"),
            ["client_secret"] = RequiredConfig("OutlookOAuth:ClientSecret"),
            ["code"] = code,
            ["redirect_uri"] = RequiredConfig("OutlookOAuth:RedirectUri"),
            ["grant_type"] = "authorization_code",
            ["scope"] = Scope
        }, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{GraphEndpoint}/me?$select=id,mail,userPrincipalName");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = payload.RootElement;
        var id = root.GetProperty("id").GetString() ?? string.Empty;
        var email = ReadString(root, "mail");
        if (string.IsNullOrWhiteSpace(email))
        {
            email = ReadString(root, "userPrincipalName");
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException("Microsoft Graph did not return an Outlook email address.");
        }

        return new OAuthCallbackResult(email, id, token.RefreshToken);
    }

    public async Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = RequiredConfig("OutlookOAuth:ClientId"),
            ["client_secret"] = RequiredConfig("OutlookOAuth:ClientSecret"),
            ["refresh_token"] = refreshToken,
            ["redirect_uri"] = RequiredConfig("OutlookOAuth:RedirectUri"),
            ["grant_type"] = "refresh_token",
            ["scope"] = Scope
        }, cancellationToken);

        var url = BuildMessagesUrl(allowedSenders, since);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!payload.RootElement.TryGetProperty("value", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return messages.EnumerateArray().Select(m => MapMessage(m, MailFolder.Inbox)).ToArray();
    }

    private string BuildMessagesUrl(IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since)
    {
        var filters = new List<string>();
        var senderFilter = BuildSenderFilter(allowedSenders);
        if (!string.IsNullOrWhiteSpace(senderFilter))
        {
            filters.Add(senderFilter);
        }

        if (since is not null)
        {
            filters.Add($"receivedDateTime ge {since.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}");
        }

        var query = new Dictionary<string, string?>
        {
            ["$top"] = "50",
            ["$orderby"] = "receivedDateTime desc",
            ["$select"] = "id,conversationId,from,toRecipients,subject,receivedDateTime,body,hasAttachments"
        };

        if (filters.Count > 0)
        {
            query["$filter"] = string.Join(" and ", filters);
        }

        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString($"{GraphEndpoint}/me/messages", query);
    }

    private static string BuildSenderFilter(IReadOnlyCollection<string> allowedSenders)
    {
        var senders = allowedSenders
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => $"from/emailAddress/address eq '{x.Replace("'", "''", StringComparison.Ordinal)}'")
            .ToArray();

        return senders.Length == 0 ? string.Empty : $"({string.Join(" or ", senders)})";
    }

    private async Task<TokenResult> RequestTokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = payload.RootElement;
        var accessToken = root.GetProperty("access_token").GetString();
        var refreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : form.GetValueOrDefault("refresh_token");

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Microsoft token response did not include required tokens.");
        }

        return new TokenResult(accessToken, refreshToken);
    }

    private static ProviderMessage MapMessage(JsonElement message, MailFolder folder)
    {
        var body = message.TryGetProperty("body", out var bodyElement) ? bodyElement : default;
        var bodyType = body.ValueKind == JsonValueKind.Object ? ReadString(body, "contentType") : string.Empty;
        var bodyContent = body.ValueKind == JsonValueKind.Object ? ReadString(body, "content") : null;
        var sentAt = DateTimeOffset.TryParse(ReadString(message, "receivedDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedSentAt)
            ? parsedSentAt
            : DateTimeOffset.UtcNow;

        return new ProviderMessage(
            ReadString(message, "id") ?? string.Empty,
            ReadString(message, "conversationId"),
            ReadNestedString(message, "from", "emailAddress", "address") ?? string.Empty,
            string.Join(", ", ReadRecipients(message)),
            ReadString(message, "subject") ?? string.Empty,
            sentAt,
            string.Equals(bodyType, "text", StringComparison.OrdinalIgnoreCase) ? bodyContent : null,
            string.Equals(bodyType, "html", StringComparison.OrdinalIgnoreCase) ? bodyContent : null,
            message.TryGetProperty("hasAttachments", out var hasAttachments) && hasAttachments.GetBoolean() ? """{"hasAttachments":true}""" : null,
            folder);
    }

    private string RequiredConfig(string key) =>
        configuration[key] ?? throw new InvalidOperationException($"{key} is required for Outlook OAuth.");

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static string? ReadNestedString(JsonElement element, params string[] propertyPath)
    {
        var current = element;
        foreach (var property in propertyPath)
        {
            if (!current.TryGetProperty(property, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.Null ? null : current.GetString();
    }

    private static IEnumerable<string> ReadRecipients(JsonElement message)
    {
        if (!message.TryGetProperty("toRecipients", out var recipients) || recipients.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var recipient in recipients.EnumerateArray())
        {
            var address = ReadNestedString(recipient, "emailAddress", "address");
            if (!string.IsNullOrWhiteSpace(address))
            {
                yield return address;
            }
        }
    }

    private sealed record TokenResult(string AccessToken, string RefreshToken);
}
