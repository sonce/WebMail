using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using WebMail;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Pages.Buyer;

public class VerifyModel : PageModel
{
    private readonly WebMailDbContext _db;
    private readonly IStringLocalizer<SharedResource> _loc;

    public VerifyModel(WebMailDbContext db, IStringLocalizer<SharedResource> loc)
    {
        _db = db;
        _loc = loc;
    }

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(string card, long? saleid)
    {
        if (string.IsNullOrWhiteSpace(card))
        {
            ErrorMessage = _loc["Buyer.LinkInvalid"];
            return Page();
        }

        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.CardNo == card && !b.IsDeleted);

        if (buyer is null)
        {
            ErrorMessage = _loc["Buyer.LinkInvalidOrExpired"];
            return Page();
        }

        if (buyer.Stage is BuyerStage.NotSent or BuyerStage.Sent)
        {
            buyer.Stage = BuyerStage.Opened;
        }

        await _db.SaveChangesAsync();

        return RedirectToPage("Email", new { card });
    }
}
