using Google.Apis.Gmail.v1.Data;
using WebMail.Domain;
using WebMail.Services.EmailProviders;
using Xunit;

namespace WebMail.Tests;

public sealed class GmailProviderTests
{
    [Fact]
    public void BuildGmailQueryCombinesSendersAndSinceDate()
    {
        var since = new DateTimeOffset(2026, 5, 27, 0, 0, 0, TimeSpan.Zero);
        var query = GmailProvider.BuildGmailQuery(["a@example.com", "b@example.com"], since);

        Assert.Equal("(from:a@example.com OR from:b@example.com) after:2026/05/27", query);
    }

    [Fact]
    public void BuildGmailQueryOmitsFromFilterWhenNoSenders()
    {
        var since = new DateTimeOffset(2026, 5, 27, 0, 0, 0, TimeSpan.Zero);
        var query = GmailProvider.BuildGmailQuery([], since);

        Assert.Equal("after:2026/05/27", query);
    }

    [Fact]
    public void MapFolderReturnsJunkWhenSpamLabelPresent() =>
        Assert.Equal(MailFolder.Junk, GmailProvider.MapFolder(["SPAM", "CATEGORY_PERSONAL"]));

    [Fact]
    public void MapFolderReturnsInboxOtherwise() =>
        Assert.Equal(MailFolder.Inbox, GmailProvider.MapFolder(["INBOX"]));

    [Fact]
    public void ExtractBodyFindsNestedPlainText()
    {
        // "hello" base64url-encoded is "aGVsbG8".
        var payload = new MessagePart
        {
            MimeType = "multipart/alternative",
            Parts =
            [
                new MessagePart { MimeType = "text/plain", Body = new MessagePartBody { Data = "aGVsbG8" } }
            ]
        };

        Assert.Equal("hello", GmailProvider.ExtractBody(payload, "text/plain"));
    }
}
