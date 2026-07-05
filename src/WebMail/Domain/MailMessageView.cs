namespace WebMail.Domain;

public sealed record MailMessageView(
    string Id,
    string Sender,
    string Subject,
    DateTimeOffset SentAt,
    MailFolder Folder,
    string? TextBody = null,
    string? HtmlBody = null);
