using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class CardGenerationServiceTests
{
    [Fact] public void GenerateCardNoUsesConfiguredLength() => Assert.Equal(32, new CardGenerationService().GenerateCardNo(32).Length);
    [Fact] public void GenerateCardNoUsesUrlSafeCharacters() => Assert.Matches("^[A-Za-z0-9_-]+$", new CardGenerationService().GenerateCardNo(64));
    [Fact] public void GenerateCardNoRejectsTooShortLength() => Assert.Throws<ArgumentOutOfRangeException>(() => new CardGenerationService().GenerateCardNo(8));
}
