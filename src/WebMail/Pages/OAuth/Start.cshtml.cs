using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using WebMail;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services.EmailProviders;
using WebMail.Services.Security;

namespace WebMail.Pages.OAuth;

public sealed class StartModel(WebMailDbContext db, IEmailProviderResolver providers, IStringLocalizer<SharedResource> loc, IOAuthStateStore stateStore) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string provider, string card)
    {
        if (string.IsNullOrWhiteSpace(card))
        {
            return BadRequest(loc["OAuth.Start.MissingCard"].Value);
        }

        var buyer = await db.Buyers.FirstOrDefaultAsync(x => x.CardNo == card && !x.IsDeleted);
        if (buyer is null)
        {
            return BadRequest(loc["OAuth.Start.InvalidCard"].Value);
        }

        var emailProvider = providers.Resolve(provider);
        var state = stateStore.Issue(provider, card);
        // Derive the callback from the current request so it matches whichever host the buyer
        // reached us on (localhost / .cn / .com). The state cookie is host-scoped, so the
        // redirect_uri must keep the buyer on the same host or the callback cannot read it back.
        var redirectUri = $"{Request.Scheme}://{Request.Host}/oauth/callback";
        var authorization = emailProvider.BuildAuthorizationUrl(state, redirectUri);

        return Redirect(authorization.RedirectUrl);
    }
}
