using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Pages.Buyer;

public class VerifyModel : PageModel
{
    private readonly WebMailDbContext _db;

    public VerifyModel(WebMailDbContext db)
    {
        _db = db;
    }

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(string card, long? saleid)
    {
        if (string.IsNullOrWhiteSpace(card))
        {
            ErrorMessage = "链接无效";
            return Page();
        }

        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.CardNo == card && !b.IsDeleted);

        if (buyer is null || buyer.CardStatus == CardStatus.DeletedOrDisabled)
        {
            ErrorMessage = "链接无效或已失效";
            return Page();
        }

        if (buyer.SaleId is null && saleid.HasValue)
        {
            buyer.SaleId = saleid;
        }

        if (buyer.CardStatus == CardStatus.Unused)
        {
            buyer.CardStatus = CardStatus.Entered;
        }

        await _db.SaveChangesAsync();

        return RedirectToPage("Email", new { card });
    }
}
