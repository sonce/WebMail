using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using WebMail;
using WebMail.Domain;
using WebMail.Services;

namespace WebMail.Pages.Admin;

public enum CardKeyTab { NotSent = 1, Sent = 2 }

[Authorize(Policy = "AdminOnly")]
public class CardKeysModel : PageModel
{
    private readonly CardKeyService _cardKeys;
    private readonly IStringLocalizer<SharedResource> _loc;

    public CardKeysModel(CardKeyService cardKeys, IStringLocalizer<SharedResource> loc)
    {
        _cardKeys = cardKeys;
        _loc = loc;
    }

    public IReadOnlyList<CardKeyListItem> Cards { get; private set; } = Array.Empty<CardKeyListItem>();
    public IReadOnlyList<SaleOption> Sales { get; private set; } = Array.Empty<SaleOption>();
    public string? Message { get; private set; }

    [BindProperty(SupportsGet = true)] public BuyerStage? StageFilter { get; set; }
    [BindProperty(SupportsGet = true)] public long? SaleFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? CardNo { get; set; }

    [BindProperty(SupportsGet = true)] public CardKeyTab Tab { get; set; } = CardKeyTab.NotSent;

    [BindProperty] public long[] SelectedIds { get; set; } = Array.Empty<long>();
    [BindProperty] public long? SendSaleId { get; set; }
    [BindProperty] public bool SendAutoApprove { get; set; }

    [BindProperty] public int GenerateCount { get; set; } = 1;
    [BindProperty] public long? GenerateSaleId { get; set; }
    [BindProperty] public bool GenerateAutoApprove { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostGenerateAsync()
    {
        Message = _loc[(await _cardKeys.GenerateAsync(GenerateCount, GenerateSaleId, GenerateAutoApprove, AdminId())).Message];
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        Message = _loc[(await _cardKeys.DeleteAsync(id, AdminId())).Message];
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSendAsync()
    {
        var result = await _cardKeys.SendAsync(SelectedIds, SendSaleId, SendAutoApprove, AdminId());
        Message = result.Success
            ? _loc["CardKey.Sent", result.GeneratedCount]
            : _loc[result.Message];
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Cards = await _cardKeys.ListAsync(StageFilter, SaleFilter, CardNo, Tab == CardKeyTab.Sent);
        Sales = await _cardKeys.ListSalesAsync();
    }

    private long? AdminId() =>
        long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
