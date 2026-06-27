using WebMail.Domain;

namespace WebMail.Services.Auth;

public static class AuthRouting
{
    // 角色 Claim 值 → 对应后台首页路径。未知或空回退到登录页。
    public static string LandingPage(string? role) => role switch
    {
        nameof(UserRole.Administrator) => "/Admin/Buyers",
        nameof(UserRole.Sales) => "/Sales/Buyers",
        nameof(UserRole.Supplier) => "/Supplier/Buyers",
        _ => "/Login",
    };

    // 仅接受以单个 '/' 开头的站内相对地址（不接受 '~/'，因为 Redirect 不会解析 '~'），用于阻断开放重定向。
    public static bool IsLocalUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }
        return url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
    }
}
