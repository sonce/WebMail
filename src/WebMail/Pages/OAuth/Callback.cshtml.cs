using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using WebMail;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using WebMail.Services.EmailProviders;
using WebMail.Services.Security;

namespace WebMail.Pages.OAuth;

public sealed class CallbackModel(
    WebMailDbContext db,
    BuyerRuleService ruleService,
    BuyerReviewService reviewService,
    IEmailProviderResolver providers,
    IStringLocalizer<SharedResource> loc,
    IOAuthStateStore stateStore,
    ITokenProtector tokenProtector) : PageModel
{
    public string? Card { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            ErrorMessage = loc["OAuth.AuthorizationFailed", error];
            return Page();
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            ErrorMessage = loc["OAuth.CallbackInvalid"];
            return Page();
        }

        // Validate the state against the server-issued nonce (CSRF) and recover the bound provider
        // and card. The provider is taken from the encrypted state cookie rather than the query
        // string, because some providers (e.g. Microsoft) drop query parameters from the redirect URI.
        var oauthState = stateStore.Consume(state);
        if (oauthState is null)
        {
            ErrorMessage = loc["OAuth.CallbackInvalid"];
            return Page();
        }

        var provider = oauthState.Provider;
        var card = oauthState.Card;

        Card = card;

        var buyer = await db.Buyers.FirstOrDefaultAsync(x => x.CardNo == card && !x.IsDeleted, cancellationToken);
        if (buyer is null)
        {
            ErrorMessage = loc["Buyer.LinkInvalidOrExpired"];
            return Page();
        }

        var emailProvider = providers.Resolve(provider);
        // Must match the redirect_uri used to start the flow (same host the buyer began on).
        var redirectUri = $"{Request.Scheme}://{Request.Host}/oauth/callback";
        var authorization = await emailProvider.CompleteAuthorizationAsync(code, state, redirectUri, cancellationToken);
        var existing = await db.EmailAccounts.FirstOrDefaultAsync(x => x.BuyerId == buyer.Id, cancellationToken);

        var isNewOrChangedAccount = existing is null
            || !string.Equals(existing.Provider, emailProvider.Name, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(existing.Email, authorization.Email, StringComparison.OrdinalIgnoreCase);

        if (existing is not null && isNewOrChangedAccount
            && !ruleService.ResolveBuyerMailAction(buyer).HasFlag(BuyerMailAction.ChangeEmail))
        {
            ErrorMessage = loc[ruleService.BuyerUnlinkBlockedMessageKey];
            return Page();
        }

        if (existing is null)
        {
            db.EmailAccounts.Add(new EmailAccount
            {
                BuyerId = buyer.Id,
                Provider = emailProvider.Name,
                Email = authorization.Email,
                ProviderUserId = authorization.ProviderUserId,
                EncryptedRefreshToken = tokenProtector.Protect(authorization.RefreshToken)
            });
        }
        else
        {
            existing.Provider = emailProvider.Name;
            existing.Email = authorization.Email;
            existing.ProviderUserId = authorization.ProviderUserId;
            existing.EncryptedRefreshToken = tokenProtector.Protect(authorization.RefreshToken);
        }

        buyer.Stage = BuyerStage.Submitted;
        buyer.CardUsedAt ??= DateTimeOffset.UtcNow;
        if (isNewOrChangedAccount)
        {
            buyer.EmailStatus = EmailAuthorizationStatus.Authorized;
            if (buyer.AutoApprove)
            {
                await reviewService.ApplyReviewAsync(buyer, ReviewStatus.Approved,
                    adminId: null, writeAuditLog: false, cancellationToken);
            }
            else
            {
                buyer.ReviewStatus = ReviewStatus.Pending;
            }
        }
        else if (buyer.EmailStatus == EmailAuthorizationStatus.Abnormal)
        {
            // Same mailbox re-authorized: token refreshed; restore health, leave review/supplier intact.
            buyer.EmailStatus = EmailAuthorizationStatus.Authorized;
        }
        await db.SaveChangesAsync(cancellationToken);

        return Page();
    }
}
