namespace ChatWithCode.Core.Options;

/// <summary>Bound from the "Security" configuration section.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Browser origins allowed to call the API. The extension runs on
    /// https://{publisher}.gallerycdn.vsassets.io, so that origin goes here.
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = [];

    /// <summary>Folder for the ASP.NET Data Protection key ring that encrypts stored API keys.</summary>
    public string DataProtectionKeysPath { get; set; } = "/var/lib/chatwithcode/keys";

    /// <summary>
    /// In Azure: a blob (e.g. https://acct.blob.core.windows.net/dataprotection/keys.xml) that holds the
    /// key ring instead of a folder, shared by every replica. Used with <see cref="DataProtectionKeyVaultKeyId"/>.
    /// </summary>
    public Uri? DataProtectionBlobUri { get; set; }

    /// <summary>In Azure: a Key Vault key that encrypts the key ring at rest (e.g. https://vault.vault.azure.net/keys/dataprotection).</summary>
    public Uri? DataProtectionKeyVaultKeyId { get; set; }

    /// <summary>Basic-auth credentials Azure DevOps service hooks use when posting push notifications.</summary>
    public string? WebhookUsername { get; set; }
    public string? WebhookPassword { get; set; }
}
