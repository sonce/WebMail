using WebMail.Domain;
using Xunit;

namespace WebMail.Tests;

public sealed class EmailMessageTests
{
    [Fact]
    public void NewEmailMessageDefaultsToInboxFolder() =>
        Assert.Equal(MailFolder.Inbox, new EmailMessage().Folder);
}
