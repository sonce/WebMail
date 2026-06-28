using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using WebMail;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services.EmailProviders;

namespace WebMail.Pages.OAuth;

public sealed class StartModel(WebMailDbContext db, IEmailProviderResolver providers, IStringLocalizer<SharedResource> loc) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string provider, string card)
    {
        if (string.IsNullOrWhiteSpace(card))
        {
            return BadRequest(loc["OAuth.Start.MissingCard"].Value);
        }

        var buyer = await db.Buyers.FirstOrDefaultAsync(x => x.CardNo == card && !x.IsDeleted);
        if (buyer is null || buyer.CardStatus == CardStatus.DeletedOrDisabled)
        {
            return BadRequest(loc["OAuth.Start.InvalidCard"].Value);
        }

        var emailProvider = providers.Resolve(provider);
        var authorization = emailProvider.BuildAuthorizationUrl(card);

        return Redirect(authorization.RedirectUrl);
    }
}
