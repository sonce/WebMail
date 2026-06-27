namespace WebMail.Services.EmailProviders;

/// <summary>
/// Thrown by a provider when the stored authorization (refresh token / granted permission)
/// is no longer usable: revoked, expired, or rejected with an authorization error.
/// </summary>
public sealed class ProviderAuthorizationException : Exception
{
    public ProviderAuthorizationException(string message) : base(message) { }
    public ProviderAuthorizationException(string message, Exception innerException) : base(message, innerException) { }
}
