namespace WebMail.Services.Security;

/// <summary>Encrypts/decrypts OAuth refresh tokens at rest.</summary>
public interface ITokenProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}
