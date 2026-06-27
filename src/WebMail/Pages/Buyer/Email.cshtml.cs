using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;

namespace WebMail.Pages.Buyer;

public class EmailModel : PageModel
{
    private readonly WebMailDbContext _db;
    private readonly BuyerRuleService _ruleService;

    public EmailModel(WebMailDbContext db, BuyerRuleService ruleService)
    {
        _db = db;
        _ruleService = ruleService;
    }

    public string? Card { get; private set; }
    public string? ErrorMessage { get; private set; }
    public CardStatus CardStatus { get; private set; }
    public EmailAuthorizationStatus EmailStatus { get; private set; }
    public BuyerStatus BuyerStatus { get; private set; }
    public SupplierProcessingStatus SupplierStatus { get; private set; }
    public BuyerMailAction Actions { get; private set; }
    public EmailAccount? EmailAccount { get; private set; }
    public IReadOnlyList<EmailMessage> Messages { get; private set; } = Array.Empty<EmailMessage>();

    public async Task<IActionResult> OnGetAsync(string card)
    {
        var buyer = await LoadBuyerAsync(card);
        if (buyer is null)
        {
            ErrorMessage = "链接无效或已失效";
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
            ErrorMessage = "链接无效或已失效";
            return Page();
        }

        Card = card;
        var account = await _db.EmailAccounts.FirstOrDefaultAsync(a => a.BuyerId == buyer.Id);

        if (!_ruleService.ResolveBuyerMailAction(buyer).HasFlag(BuyerMailAction.ChangeEmail))
        {
            ErrorMessage = _ruleService.BuyerUnlinkBlockedMessage;
            return Render(buyer, account);
        }

        if (account is not null)
        {
            _db.EmailAccounts.Remove(account);
        }
        buyer.EmailStatus = EmailAuthorizationStatus.NotAuthorized;
        buyer.BuyerStatus = BuyerStatus.NotSubmitted;
        buyer.SupplierStatus = SupplierProcessingStatus.Unprocessed;
        await _db.SaveChangesAsync();

        return Render(buyer, null);
    }

    public async Task<IActionResult> OnPostClearAuthAsync(string card)
    {
        var buyer = await LoadBuyerAsync(card);
        if (buyer is null)
        {
            ErrorMessage = "链接无效或已失效";
            return Page();
        }

        Card = card;
        var account = await _db.EmailAccounts.FirstOrDefaultAsync(a => a.BuyerId == buyer.Id);

        if (!_ruleService.ResolveBuyerMailAction(buyer).HasFlag(BuyerMailAction.ClearAuth))
        {
            ErrorMessage = _ruleService.BuyerUnlinkBlockedMessage;
            return Render(buyer, account);
        }

        if (account is not null)
        {
            _db.EmailAccounts.Remove(account);
        }
        buyer.EmailStatus = EmailAuthorizationStatus.NotAuthorized;
        // Keep Approved+Completed as the terminal "cleared" state; otherwise reset to a fresh cycle.
        if (!(buyer.BuyerStatus == BuyerStatus.Approved && buyer.SupplierStatus == SupplierProcessingStatus.Completed))
        {
            buyer.BuyerStatus = BuyerStatus.NotSubmitted;
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
        if (buyer is null || buyer.CardStatus == CardStatus.DeletedOrDisabled)
        {
            return null;
        }

        return buyer;
    }

    private IActionResult Render(Domain.Buyer buyer, EmailAccount? account)
    {
        CardStatus = buyer.CardStatus;
        EmailStatus = buyer.EmailStatus;
        BuyerStatus = buyer.BuyerStatus;
        SupplierStatus = buyer.SupplierStatus;
        Actions = _ruleService.ResolveBuyerMailAction(buyer);
        EmailAccount = account;
        Messages = Array.Empty<EmailMessage>();
        return Page();
    }
}
