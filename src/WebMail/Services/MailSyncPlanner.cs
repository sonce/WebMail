using System.Net.Mail;

namespace WebMail.Services;

public sealed class MailSyncPlanner
{
    public bool IsAllowedSender(string rawSender, IReadOnlyCollection<string> allowedSenders)
    {
        var sender = ExtractAddress(rawSender);
        return allowedSenders.Any(x => string.Equals(x.Trim(), sender, StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractAddress(string rawSender)
    {
        try { return new MailAddress(rawSender).Address; }
        catch (FormatException) { return rawSender.Trim(); }
    }
}
