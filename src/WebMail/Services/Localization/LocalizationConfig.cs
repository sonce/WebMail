using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;

namespace WebMail.Services.Localization;

public static class LocalizationConfig
{
    public const string DefaultCultureName = "en";
    public static readonly string[] SupportedCultureNames = { "en", "zh-CN" };

    public static RequestLocalizationOptions Build()
    {
        var cultures = SupportedCultureNames.Select(c => new CultureInfo(c)).ToList();
        return new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture(DefaultCultureName, DefaultCultureName),
            SupportedCultures = cultures,
            SupportedUICultures = cultures,
        };
        // 默认 Provider 顺序为 [QueryString, Cookie, AcceptLanguageHeader]——
        // Cookie 先于 Accept-Language，满足"手动切换覆盖浏览器检测"，无需自定义。
    }
}
