namespace WebMail.Services.EmailProviders;

public interface IEmailProviderResolver
{
    IEmailProvider Resolve(string providerName);
}

public sealed class EmailProviderResolver(IEnumerable<IEmailProvider> providers) : IEmailProviderResolver
{
    private readonly IReadOnlyDictionary<string, IEmailProvider> _providers = providers.ToDictionary(
        provider => provider.Name,
        StringComparer.OrdinalIgnoreCase);

    public IEmailProvider Resolve(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName) || !_providers.TryGetValue(providerName.Trim(), out var provider))
        {
            throw new InvalidOperationException($"Unsupported email provider: {providerName}");
        }

        return provider;
    }
}
