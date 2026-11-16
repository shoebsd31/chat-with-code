using ChatWithCode.Core.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace ChatWithCode.Data.Settings;

/// <summary>Encrypts stored API keys with the shared Data Protection key ring.</summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("ChatWithCode.Secrets.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue)
    {
        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new InvalidOperationException(
                "A stored API key can't be decrypted. The encryption key ring (the 'keys' volume) was probably " +
                "lost or replaced. Enter the API key again in the settings.", ex);
        }
    }
}
