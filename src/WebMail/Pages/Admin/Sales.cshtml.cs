using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using WebMail;
using WebMail.Domain;
using WebMail.Services;

namespace WebMail.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class SalesModel : PageModel
{
    private readonly UserAdminService _users;
    private readonly IStringLocalizer<SharedResource> _loc;

    public SalesModel(UserAdminService users, IStringLocalizer<SharedResource> loc)
    {
        _users = users;
        _loc = loc;
    }

    protected virtual UserRole Role => UserRole.Sales;
    public string RoleNounKey => Role == UserRole.Supplier ? "RoleNoun.Supplier" : "RoleNoun.Sales";

    public IReadOnlyList<UserListItem> Users { get; private set; } = Array.Empty<UserListItem>();
    public string? Message { get; private set; }

    [BindProperty] public string NewUserName { get; set; } = string.Empty;
    [BindProperty] public string NewDisplayName { get; set; } = string.Empty;
    [BindProperty] public string NewPassword { get; set; } = string.Empty;

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        Message = _loc[(await _users.CreateAsync(Role, NewUserName, NewDisplayName, NewPassword, AdminId())).Message];
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(long id, string password)
    {
        Message = _loc[(await _users.ResetPasswordAsync(id, password, AdminId(), Role)).Message];
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSetActiveAsync(long id, bool isActive)
    {
        Message = _loc[(await _users.SetActiveAsync(id, isActive, AdminId(), Role)).Message];
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync() => Users = await _users.ListByRoleAsync(Role);

    private long? AdminId() =>
        long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
