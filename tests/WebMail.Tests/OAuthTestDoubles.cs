using WebMail.Services.Security;

namespace WebMail.Tests;

/// <summary>In-memory OAuth state store: a consumed state returns the provider and card it was issued for.</summary>
public sealed class FakeOAuthStateStore : IOAuthStateStore
{
    private readonly Dictionary<string, (string Provider, string Card)> _issued = new();

    public string Issue(string provider, string card)
    {
        var state = $"state-{provider}-{card}";
        _issued[state] = (provider, card);
        return state;
    }

    public OAuthStateResult? Consume(string state) =>
        _issued.TryGetValue(state, out var v)
            ? new OAuthStateResult(v.Provider, v.Card)
            : null;
}

/// <summary>Marker-based token protector so tests can assert encrypt/decrypt actually happened.</summary>
public sealed class FakeTokenProtector : ITokenProtector
{
    public string Protect(string plaintext) => "enc:" + plaintext;

    public string Unprotect(string protectedText) =>
        protectedText.StartsWith("enc:", StringComparison.Ordinal)
            ? protectedText["enc:".Length..]
            : throw new InvalidOperationException("Value was not protected.");
}
