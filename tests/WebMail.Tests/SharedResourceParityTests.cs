using System.Globalization;
using System.Resources;
using WebMail;
using Xunit;

namespace WebMail.Tests;

public sealed class SharedResourceParityTests
{
    private static HashSet<string> KeysFor(string culture)
    {
        var rm = new ResourceManager("WebMail.Resources.SharedResource", typeof(SharedResource).Assembly);
        using var set = rm.GetResourceSet(new CultureInfo(culture), createIfNotExists: true, tryParents: false)
            ?? throw new Xunit.Sdk.XunitException($"No resource set for {culture}");
        return set.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();
    }

    [Fact]
    public void EnglishAndChineseHaveIdenticalKeys()
    {
        var en = KeysFor("en");
        var zh = KeysFor("zh-CN");
        Assert.Empty(en.Except(zh));   // 中文缺失的 key
        Assert.Empty(zh.Except(en));   // 英文缺失的 key
        Assert.True(en.Count > 50, $"Expected full catalog, got {en.Count}");
    }
}
