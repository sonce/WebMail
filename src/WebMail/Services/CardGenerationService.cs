using System.Security.Cryptography;

namespace WebMail.Services;

public sealed class CardGenerationService
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";

    public string GenerateCardNo(int length = 32)
    {
        if (length < 24) throw new ArgumentOutOfRangeException(nameof(length), "Card number length must be at least 24 characters.");
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        return new string(bytes.ToArray().Select(b => Alphabet[b % Alphabet.Length]).ToArray());
    }
}
