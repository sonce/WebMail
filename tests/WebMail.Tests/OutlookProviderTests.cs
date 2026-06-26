using WebMail.Services.EmailProviders;
using Xunit;

namespace WebMail.Tests;

public sealed class OutlookProviderTests
{
    [Fact]
    public void BuildFolderMessagesUrlTargetsJunkFolderWithSenderFilter()
    {
        var url = OutlookProvider.BuildFolderMessagesUrl("junkemail", ["orders@example.com"], null);
        var decoded = Uri.UnescapeDataString(url);

        Assert.Contains("/me/mailFolders/junkemail/messages", url);
        Assert.Contains("from/emailAddress/address eq 'orders@example.com'", decoded);
    }

    [Fact]
    public void BuildFolderMessagesUrlTargetsInboxFolder()
    {
        var url = OutlookProvider.BuildFolderMessagesUrl("inbox", [], null);
        Assert.Contains("/me/mailFolders/inbox/messages", url);
    }
}
