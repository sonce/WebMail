using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services.EmailProviders;

namespace WebMail.Pages.OAuth;

public sealed class StartModel(WebMailDbContext db, IEmailProviderResolver providers) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string provider, string card)
    {
        if (string.IsNullOrWhiteSpace(card))
        {
            return BadRequest("Missing card.");
        }

        var buyer = await db.Buyers.FirstOrDefaultAsync(x => x.CardNo == card && !x.IsDeleted);
        if (buyer is null || buyer.CardStatus == CardStatus.DeletedOrDisabled)
        {
            return BadRequest("Invalid card.");
        }

        var emailProvider = providers.Resolve(provider);
        var authorization = emailProvider.BuildAuthorizationUrl(card);

        return Redirect(authorization.RedirectUrl);
    }
}
