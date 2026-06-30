using WebMail.Domain;
using WebMail.Services;
using WebMail.Services.EmailProviders;
using Xunit;

namespace WebMail.Tests;

public sealed class MailCacheServiceTests
{
    [Fact]
    public void ProjectLatestKeepsLimitNewestBySentAtDescending()
    {
        var baseTime = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
        var messages = Enumerable.Range(0, 15)
            .Select(i => new ProviderMessage(
                $"m-{i}", null, "s@x.com", "r@x.com", "sub",
                baseTime.AddMinutes(i), null, null, null, MailFolder.Inbox))
            .ToArray();

        var result = MailCacheService.ProjectLatest(messages, limit: 10);

        Assert.Equal(10, result.Count);
        // 最新的 10 条 = m-6 .. m-14，倒序排列（m-14 在前）
        Assert.Equal("m-14", result[0].Id);
        Assert.Equal("m-5", result[9].Id);
    }

    [Fact]
    public void ProjectLatestMapsFieldsAndFolder()
    {
        var sentAt = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var messages = new[]
        {
            new ProviderMessage("id-1", "t", "sender@x.com", "r", "Hello",
                sentAt, "body", null, null, MailFolder.Junk)
        };

        var result = MailCacheService.ProjectLatest(messages, limit: 10);

        var view = Assert.Single(result);
        Assert.Equal("id-1", view.Id);
        Assert.Equal("sender@x.com", view.Sender);
        Assert.Equal("Hello", view.Subject);
        Assert.Equal(sentAt, view.SentAt);
        Assert.Equal(MailFolder.Junk, view.Folder);
    }

    [Fact]
    public void ProjectLatestReturnsAllWhenFewerThanLimit()
    {
        var messages = new[]
        {
            new ProviderMessage("m-0", null, "s", "r", "x",
                DateTimeOffset.UtcNow, null, null, null, MailFolder.Inbox)
        };

        var result = MailCacheService.ProjectLatest(messages, limit: 10);

        Assert.Single(result);
    }
}
