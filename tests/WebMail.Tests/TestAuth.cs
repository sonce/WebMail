using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace WebMail.Tests;

// 记录式假认证服务：SignInAsync / SignOutAsync 扩展方法会从 RequestServices 解析它。
public sealed class FakeAuthenticationService : IAuthenticationService
{
    public ClaimsPrincipal? SignedInPrincipal { get; private set; }
    public AuthenticationProperties? SignInProperties { get; private set; }
    public bool SignedOut { get; private set; }

    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        => Task.FromResult(AuthenticateResult.NoResult());

    public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        => Task.CompletedTask;

    public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        => Task.CompletedTask;

    public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
    {
        SignedInPrincipal = principal;
        SignInProperties = properties;
        return Task.CompletedTask;
    }

    public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
    {
        SignedOut = true;
        return Task.CompletedTask;
    }
}

public static class TestHttpContext
{
    public static (DefaultHttpContext ctx, FakeAuthenticationService auth) WithAuth(ClaimsPrincipal? user = null)
    {
        var auth = new FakeAuthenticationService();
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(auth);
        var ctx = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = user ?? new ClaimsPrincipal(new ClaimsIdentity()),
        };
        return (ctx, auth);
    }
}
