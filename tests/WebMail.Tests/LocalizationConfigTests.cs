using System.Globalization;
using WebMail.Services.Localization;
using Xunit;

namespace WebMail.Tests;

public sealed class LocalizationConfigTests
{
    [Fact]
    public void DefaultCultureIsEnglish()
    {
        var options = LocalizationConfig.Build();
        Assert.Equal("en", options.DefaultRequestCulture.Culture.Name);
        Assert.Equal("en", options.DefaultRequestCulture.UICulture.Name);
    }

    [Fact]
    public void SupportsEnglishAndSimplifiedChineseOnly()
    {
        var options = LocalizationConfig.Build();
        var ui = options.SupportedUICultures!.Select(c => c.Name).OrderBy(n => n).ToArray();
        var cultures = options.SupportedCultures!.Select(c => c.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "en", "zh-CN" }, ui);
        Assert.Equal(new[] { "en", "zh-CN" }, cultures);
    }
}
