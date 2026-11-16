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

    /// <summary>Basic-auth credentials Azure DevOps service hooks use when posting push notifications.</summary>
    public string? WebhookUsername { get; set; }
    public string? WebhookPassword { get; set; }
}
