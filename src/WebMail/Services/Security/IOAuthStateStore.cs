namespace WebMail.Services.Security;

/// <summary>The provider and card recovered from a validated OAuth state.</summary>
public sealed record OAuthStateResult(string Provider, string Card);

/// <summary>
/// Issues and validates a one-time OAuth <c>state</c> value bound to the user's browser,
/// protecting the authorization callback against CSRF. The provider and card are carried
/// inside the server-issued state rather than trusted from the query string, so the callback
/// works even when the provider (e.g. Microsoft) strips query parameters from the redirect URI.
/// </summary>
public interface IOAuthStateStore
{
    /// <summary>Issues a random state for the given provider/card and persists the binding. Returns the state value to send to the provider.</summary>
    string Issue(string provider, string card);

    /// <summary>Validates the returned state and returns the bound provider and card, or null if invalid/forged/expired. Single-use.</summary>
    OAuthStateResult? Consume(string state);
}
