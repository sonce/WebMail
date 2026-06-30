using WebMail.Domain;

namespace WebMail.Extensions;

public static class MailFolderExtensions
{
    public static string GetIconClass(this MailFolder folder) => folder switch
    {
        MailFolder.Inbox => "bi-inbox",
        MailFolder.Sent => "bi-send",
        MailFolder.Drafts => "bi-file-earmark-text",
        MailFolder.Archive => "bi-archive",
        MailFolder.Trash => "bi-trash",
        _ => "bi-envelope"
    };

    public static string GetLocalizedLabel(this MailFolder folder) => folder switch
    {
        MailFolder.Inbox => "MailFolder_Inbox",
        MailFolder.Sent => "MailFolder_Sent",
        MailFolder.Drafts => "MailFolder_Drafts",
        MailFolder.Archive => "MailFolder_Archive",
        MailFolder.Trash => "MailFolder_Trash",
        _ => "MailFolder_Unknown"
    };
}
