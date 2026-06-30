using Microsoft.AspNetCore.DataProtection;

namespace WebMail.Services.Security;

public sealed class DataProtectionTokenProtector : ITokenProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionTokenProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("WebMail.EmailTokens.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedText) => _protector.Unprotect(protectedText);
}
