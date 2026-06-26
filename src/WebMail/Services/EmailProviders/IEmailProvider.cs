namespace WebMail.Services.EmailProviders;

public sealed record OAuthStartResult(string RedirectUrl, string State);
public sealed record OAuthCallbackResult(string Email, string ProviderUserId, string RefreshToken);
public sealed record ProviderMessage(string ProviderMessageId, string? ProviderThreadId, string Sender, string Recipients, string Subject, DateTimeOffset SentAt, string? TextBody, string? HtmlBody, string? AttachmentMetadataJson, WebMail.Domain.MailFolder Folder);

public interface IEmailProvider
{
    string Name { get; }
    OAuthStartResult BuildAuthorizationUrl(string cardNo);
    Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken);
}
