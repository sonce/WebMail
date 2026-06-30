using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using WebMail;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;

namespace WebMail.Pages.Buyer;

public class EmailModel : PageModel
{
    private readonly WebMailDbContext _db;
    private readonly BuyerRuleService _ruleService;
    private readonly IStringLocalizer<SharedResource> _loc;

    public EmailModel(WebMailDbContext db, BuyerRuleService ruleService, IStringLocalizer<SharedResource> loc)
    {
        _db = db;
        _ruleService = ruleService;
        _loc = loc;
    }

    public string? Card { get; private set; }
    public string? ErrorMessage { get; private set; }
    public BuyerStage Stage { get; private set; }
    public EmailAuthorizationStatus EmailStatus { get; private set; }
    public ReviewStatus ReviewStatus { get; private set; }
    public SupplierProcessingStatus SupplierStatus { get; private set; }
    public BuyerMailAction Actions { get; private set; }
    public EmailAccount? EmailAccount { get; private set; }

    public async Task<IActionResult> OnGetAsync(string card)
    {
        var buyer = await LoadBuyerAsync(card);
        if (buyer is null)
        {
            ErrorMessage = _loc["Buyer.LinkInvalidOrExpired"];
            return Page();
        }

        Card = card;
        var account = await _db.EmailAccounts.FirstOrDefaultAsync(a => a.BuyerId == buyer.Id);
        return Render(buyer, account);
    }

    public async Task<IActionResult> OnPostChangeEmailAsync(string card)
    {
        var buyer = await LoadBuyerAsync(card);
        if (buyer is null)
        {
            ErrorMessage = _loc["Buyer.LinkInvalidOrExpired"];
            return Page();
        }

        Card = card;
        var account = await _db.EmailAccounts.FirstOrDefaultAsync(a => a.BuyerId == buyer.Id);

        if (!_ruleService.ResolveBuyerMailAction(buyer).HasFlag(BuyerMailAction.ChangeEmail))
        {
            ErrorMessage = _loc[_ruleService.BuyerUnlinkBlockedMessageKey];
            return Render(buyer, account);
        }

        if (account is not null)
        {
            _db.EmailAccounts.Remove(account);
        }
        buyer.Stage = BuyerStage.NotSubmitted;
        buyer.EmailStatus = EmailAuthorizationStatus.NotAuthorized;
        buyer.ReviewStatus = ReviewStatus.Pending;
        buyer.SupplierStatus = SupplierProcessingStatus.Unprocessed;
        await _db.SaveChangesAsync();

        return Render(buyer, null);
    }

    public async Task<IActionResult> OnPostClearAuthAsync(string card)
    {
        var buyer = await LoadBuyerAsync(card);
        if (buyer is null)
        {
            ErrorMessage = _loc["Buyer.LinkInvalidOrExpired"];
            return Page();
        }

        Card = card;
        var account = await _db.EmailAccounts.FirstOrDefaultAsync(a => a.BuyerId == buyer.Id);

        if (!_ruleService.ResolveBuyerMailAction(buyer).HasFlag(BuyerMailAction.ClearAuth))
        {
            ErrorMessage = _loc[_ruleService.BuyerUnlinkBlockedMessageKey];
            return Render(buyer, account);
        }

        if (account is not null)
        {
            _db.EmailAccounts.Remove(account);
        }
        buyer.EmailStatus = EmailAuthorizationStatus.NotAuthorized;
        // Keep Approved+Completed as the terminal "cleared" state; otherwise reset to a fresh cycle.
        if (!(buyer.ReviewStatus == ReviewStatus.Approved && buyer.SupplierStatus == SupplierProcessingStatus.Completed))
        {
            buyer.Stage = BuyerStage.NotSubmitted;
            buyer.ReviewStatus = ReviewStatus.Pending;
            buyer.SupplierStatus = SupplierProcessingStatus.Unprocessed;
        }
        await _db.SaveChangesAsync();

        return Render(buyer, null);
    }

    private async Task<Domain.Buyer?> LoadBuyerAsync(string card)
    {
        if (string.IsNullOrWhiteSpace(card))
        {
            return null;
        }

        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.CardNo == card && !b.IsDeleted);
        if (buyer is null)
        {
            return null;
        }

        return buyer;
    }

    private IActionResult Render(Domain.Buyer buyer, EmailAccount? account)
    {
        Stage = buyer.Stage;
        EmailStatus = buyer.EmailStatus;
        ReviewStatus = buyer.ReviewStatus;
        SupplierStatus = buyer.SupplierStatus;
        Actions = _ruleService.ResolveBuyerMailAction(buyer);
        EmailAccount = account;
        return Page();
    }
}
