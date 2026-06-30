using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace WebMail.Services.Security;

public sealed class CookieOAuthStateStore : IOAuthStateStore
{
    private const string CookieName = "oauth_state";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly IHttpContextAccessor _accessor;
    private readonly ITimeLimitedDataProtector _protector;

    public CookieOAuthStateStore(IHttpContextAccessor accessor, IDataProtectionProvider provider)
    {
        _accessor = accessor;
        _protector = provider.CreateProtector("WebMail.OAuthState.v1").ToTimeLimitedDataProtector();
    }

    public string Issue(string provider, string card)
    {
        var nonce = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var payload = JsonSerializer.Serialize(new StatePayload(provider, card, nonce));
        var protectedPayload = _protector.Protect(payload, Lifetime);

        Context.Response.Cookies.Append(CookieName, protectedPayload, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/oauth",
            MaxAge = Lifetime
        });

        return nonce;
    }

    public OAuthStateResult? Consume(string state)
    {
        if (!Context.Request.Cookies.TryGetValue(CookieName, out var protectedPayload) || string.IsNullOrEmpty(protectedPayload))
        {
            return null;
        }

        Context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/oauth" });

        StatePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<StatePayload>(_protector.Unprotect(protectedPayload));
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null)
        {
            return null;
        }

        return FixedTimeEquals(payload.Nonce, state) ? new OAuthStateResult(payload.Provider, payload.Card) : null;
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a),
            System.Text.Encoding.UTF8.GetBytes(b));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private HttpContext Context =>
        _accessor.HttpContext ?? throw new InvalidOperationException("No active HttpContext for OAuth state.");

    private sealed record StatePayload(string Provider, string Card, string Nonce);
}
