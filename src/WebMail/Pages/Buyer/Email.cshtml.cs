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
        CardStatus = buyer.CardStatus;
        EmailStatus = buyer.EmailStatus;

        EmailAccount = await _db.EmailAccounts.FirstOrDefaultAsync(a => a.BuyerId == buyer.Id);
        if (EmailAccount is not null)
        {
            Messages = await LoadMessagesAsync(EmailAccount.Id);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostUnlinkAsync(string card)
    {
        var buyer = await LoadBuyerAsync(card);
        if (buyer is null)
        {
            ErrorMessage = "链接无效或已失效";
            return Page();
        }

        Card = card;

        var account = await _db.EmailAccounts.FirstOrDefaultAsync(a => a.BuyerId == buyer.Id);

        if (!_ruleService.CanBuyerUnlink(buyer.EmailStatus))
        {
            ErrorMessage = _ruleService.BuyerUnlinkBlockedMessage;
        }
        else if (account is not null)
        {
            _db.EmailAccounts.Remove(account);
            buyer.EmailStatus = EmailAuthorizationStatus.NotAuthorized;
            await _db.SaveChangesAsync();
            account = null;
        }

        return await RenderAsync(buyer, account);
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

    private async Task<IActionResult> RenderAsync(Domain.Buyer buyer, EmailAccount? account)
    {
        CardStatus = buyer.CardStatus;
        EmailStatus = buyer.EmailStatus;
        EmailAccount = account;
        if (account is not null)
        {
            Messages = await LoadMessagesAsync(account.Id);
        }
        else
        {
            Messages = Array.Empty<EmailMessage>();
        }

        return Page();
    }

    private Task<List<EmailMessage>> LoadMessagesAsync(long emailAccountId) =>
        _db.EmailMessages
            .Where(m => m.EmailAccountId == emailAccountId)
            .OrderByDescending(m => m.SentAt)
            .ToListAsync();
}
