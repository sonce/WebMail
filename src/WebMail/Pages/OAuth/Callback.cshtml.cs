using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using WebMail.Services.EmailProviders;

namespace WebMail.Pages.OAuth;

public sealed class CallbackModel(
    WebMailDbContext db,
    BuyerRuleService ruleService,
    IEmailProviderResolver providers) : PageModel
{
    public string? Card { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(string provider, string? code, string? state, string? error, CancellationToken cancellationToken)
    {
        Card = state;

        if (!string.IsNullOrWhiteSpace(error))
        {
            ErrorMessage = $"授权失败：{error}";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            ErrorMessage = "授权回调参数无效";
            return Page();
        }

        var buyer = await db.Buyers.FirstOrDefaultAsync(x => x.CardNo == state && !x.IsDeleted, cancellationToken);
        if (buyer is null || buyer.CardStatus == CardStatus.DeletedOrDisabled)
        {
            ErrorMessage = "链接无效或已失效";
            return Page();
        }

        var emailProvider = providers.Resolve(provider);
        var authorization = await emailProvider.CompleteAuthorizationAsync(code, state, cancellationToken);
        var existing = await db.EmailAccounts.FirstOrDefaultAsync(x => x.BuyerId == buyer.Id, cancellationToken);

        if (existing is not null
            && (!string.Equals(existing.Provider, emailProvider.Name, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.Email, authorization.Email, StringComparison.OrdinalIgnoreCase))
            && !ruleService.CanBuyerUnlink(buyer.EmailStatus))
        {
            ErrorMessage = ruleService.BuyerUnlinkBlockedMessage;
            return Page();
        }

        var isNewOrChangedAccount = existing is null
            || !string.Equals(existing.Provider, emailProvider.Name, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(existing.Email, authorization.Email, StringComparison.OrdinalIgnoreCase);

        if (existing is null)
        {
            db.EmailAccounts.Add(new EmailAccount
            {
                BuyerId = buyer.Id,
                Provider = emailProvider.Name,
                Email = authorization.Email,
                ProviderUserId = authorization.ProviderUserId,
                EncryptedRefreshToken = authorization.RefreshToken
            });
        }
        else
        {
            existing.Provider = emailProvider.Name;
            existing.Email = authorization.Email;
            existing.ProviderUserId = authorization.ProviderUserId;
            existing.EncryptedRefreshToken = authorization.RefreshToken;
        }

        buyer.CardStatus = CardStatus.Authorized;
        if (isNewOrChangedAccount)
        {
            buyer.EmailStatus = EmailAuthorizationStatus.PendingReview;
        }
        await db.SaveChangesAsync(cancellationToken);

        return Page();
    }
}
