using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebMail.Services.Auth;
using WebMail.Services.Localization;

namespace WebMail.Pages.Culture;

[AllowAnonymous]
public sealed class SetModel : PageModel
{
    public IActionResult OnGet(string? culture, string? returnUrl)
    {
        var target = AuthRouting.IsLocalUrl(returnUrl) ? returnUrl! : "/";

        if (!string.IsNullOrEmpty(culture) && LocalizationConfig.SupportedCultureNames.Contains(culture))
        {
            Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    IsEssential = true,
                    Path = "/",
                    SameSite = SameSiteMode.Lax,
                    Secure = Request.IsHttps,
                });
        }

        return Redirect(target);
    }
}
