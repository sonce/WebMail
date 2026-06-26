using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class MailSyncPlannerTests
{
    [Fact] public void MatchesAllowedSenderCaseInsensitive() => Assert.True(new MailSyncPlanner().IsAllowedSender("Buyer <Orders@Example.com>", ["orders@example.com"]));
    [Fact] public void RejectsUnconfiguredSender() => Assert.False(new MailSyncPlanner().IsAllowedSender("spam@example.com", ["orders@example.com"]));
    [Fact] public void BuildsGmailQueryForAllowedSenders() => Assert.Equal("from:a@example.com OR from:b@example.com", new MailSyncPlanner().BuildGmailSenderQuery(["a@example.com", "b@example.com"]));
    [Fact] public void EmptyAllowedSenderListDisablesSyncQuery() => Assert.Equal(string.Empty, new MailSyncPlanner().BuildGmailSenderQuery([]));
}
