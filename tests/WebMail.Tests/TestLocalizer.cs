using Microsoft.Extensions.Localization;
using WebMail;

namespace WebMail.Tests;

/// <summary>测试用本地化桩：按 key 回显，使断言与翻译文本解耦。</summary>
public static class TestLocalizer
{
    public static IStringLocalizer<SharedResource> Shared { get; } = new EchoLocalizer();

    private sealed class EchoLocalizer : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => new(name, name, resourceNotFound: false);
        public LocalizedString this[string name, params object[] arguments] =>
            new(name, arguments.Length == 0 ? name : $"{name}|{string.Join(",", arguments)}", resourceNotFound: false);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            Enumerable.Empty<LocalizedString>();
    }
}
