using System.Net.Mail;

namespace WebMail.Services;

public sealed class MailSyncPlanner
{
    public bool IsAllowedSender(string rawSender, IReadOnlyCollection<string> allowedSenders)
    {
        var sender = ExtractAddress(rawSender);
        return allowedSenders.Any(x => string.Equals(x.Trim(), sender, StringComparison.OrdinalIgnoreCase));
    }

    public string BuildGmailSenderQuery(IReadOnlyCollection<string> allowedSenders)
    {
        var senders = allowedSenders.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return senders.Length == 0 ? string.Empty : string.Join(" OR ", senders.Select(x => $"from:{x}"));
    }

    private static string ExtractAddress(string rawSender)
    {
        try { return new MailAddress(rawSender).Address; }
        catch (FormatException) { return rawSender.Trim(); }
    }
}
