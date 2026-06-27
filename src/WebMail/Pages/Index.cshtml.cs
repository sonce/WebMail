using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebMail.Services.Auth;

namespace WebMail.Pages;

public class IndexModel : PageModel
{
    // 带卡号 → 买家入口；否则已登录按角色落地、未登录去登录页。
    public IActionResult OnGet(string? card, long? saleid)
    {
        if (!string.IsNullOrWhiteSpace(card))
        {
            return RedirectToPage("/Buyer/Verify", new { card, saleid });
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToPage(AuthRouting.LandingPage(User.FindFirstValue(ClaimTypes.Role)));
        }

        return RedirectToPage("/Login");
    }
}
