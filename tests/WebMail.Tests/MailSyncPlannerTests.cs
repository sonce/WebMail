using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class MailSyncPlannerTests
{
    [Fact] public void MatchesAllowedSenderCaseInsensitive() => Assert.True(new MailSyncPlanner().IsAllowedSender("Buyer <Orders@Example.com>", ["orders@example.com"]));
    [Fact] public void RejectsUnconfiguredSender() => Assert.False(new MailSyncPlanner().IsAllowedSender("spam@example.com", ["orders@example.com"]));
}
