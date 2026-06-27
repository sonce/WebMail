using WebMail.Services.Auth;
using Xunit;

namespace WebMail.Tests;

public sealed class AuthRoutingTests
{
    [Theory]
    [InlineData("Administrator", "/Admin/Buyers")]
    [InlineData("Sales", "/Sales/Buyers")]
    [InlineData("Supplier", "/Supplier/Buyers")]
    [InlineData(null, "/Login")]
    [InlineData("Bogus", "/Login")]
    public void LandingPageMapsRole(string? role, string expected)
        => Assert.Equal(expected, AuthRouting.LandingPage(role));

    [Theory]
    [InlineData("/Admin/Buyers", true)]
    [InlineData("/", true)]
    [InlineData("~/x", false)]
    [InlineData("~//evil.com", false)]
    [InlineData("~/\\evil.com", false)]
    [InlineData("~/", false)]
    [InlineData("//evil.com", false)]
    [InlineData("/\\evil.com", false)]
    [InlineData("https://evil.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLocalUrlGuardsOpenRedirect(string? url, bool expected)
        => Assert.Equal(expected, AuthRouting.IsLocalUrl(url));
}
