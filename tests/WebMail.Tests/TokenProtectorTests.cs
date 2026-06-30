using Microsoft.AspNetCore.DataProtection;
using WebMail.Services.Security;
using Xunit;

namespace WebMail.Tests;

public sealed class TokenProtectorTests
{
    private static ITokenProtector CreateProtector() =>
        new DataProtectionTokenProtector(new EphemeralDataProtectionProvider());

    [Fact]
    public void ProtectThenUnprotectRoundTripsOriginalValue()
    {
        var protector = CreateProtector();

        var encrypted = protector.Protect("refresh-token-123");

        Assert.NotEqual("refresh-token-123", encrypted);
        Assert.Equal("refresh-token-123", protector.Unprotect(encrypted));
    }

    [Fact]
    public void UnprotectRejectsTamperedPayload()
    {
        var protector = CreateProtector();
        var encrypted = protector.Protect("refresh-token-123");

        Assert.ThrowsAny<System.Exception>(() => protector.Unprotect(encrypted + "x"));
    }
}
