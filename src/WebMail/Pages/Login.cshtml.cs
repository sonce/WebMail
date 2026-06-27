using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services.Auth;

namespace WebMail.Pages;

public class LoginModel : PageModel
{
    private readonly WebMailDbContext _db;
    private readonly IPasswordHasher<AppUser> _hasher;

    public LoginModel(WebMailDbContext db, IPasswordHasher<AppUser> hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    [BindProperty] public string UserName { get; set; } = string.Empty;
    [BindProperty] public string Password { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? ErrorMessage { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var normalized = UserName.ToLower();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserName.ToLower() == normalized);
        if (user is null ||
            _hasher.VerifyHashedPassword(user, user.PasswordHash, Password) == PasswordVerificationResult.Failed)
        {
            ErrorMessage = "用户名或密码错误";
            return Page();
        }

        if (!user.IsActive)
        {
            ErrorMessage = "账号已被禁用";
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Role, user.Role.ToString()),
            new("DisplayName", user.DisplayName),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        if (AuthRouting.IsLocalUrl(ReturnUrl))
        {
            return Redirect(ReturnUrl!);
        }
        return RedirectToPage(AuthRouting.LandingPage(user.Role.ToString()));
    }
}
