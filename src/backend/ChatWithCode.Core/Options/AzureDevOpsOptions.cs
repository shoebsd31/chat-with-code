using System.ComponentModel.DataAnnotations;

namespace ChatWithCode.Core.Options;

/// <summary>
/// Connection to the Azure DevOps (Services) organization this backend serves.
/// Bound from the "AzureDevOps" configuration section.
/// </summary>
public sealed class AzureDevOpsOptions
{
    public const string SectionName = "AzureDevOps";

    /// <summary>Organization name, as in https://dev.azure.com/{Organization}.</summary>
    [Required]
    [RegularExpression("^[A-Za-z0-9][A-Za-z0-9-]{0,49}$", ErrorMessage = "AzureDevOps:Organization must be the organization name only (the part after https://dev.azure.com/), not the full URL.")]
    public string Organization { get; set; } = string.Empty;

    /// <summary>
    /// Secret of the published extension (Marketplace publisher portal → extension → Certificate).
    /// Used to validate tokens sent by the extension. Required from phase 3 on.
    /// </summary>
    public string? ExtensionSecret { get; set; }

    /// <summary>How the backend itself reads repositories when indexing.</summary>
    public ServicePrincipalOptions? ServicePrincipal { get; set; }

    /// <summary>Alternative to a service principal: a personal access token with Code (Read) scope.</summary>
    public string? PersonalAccessToken { get; set; }

    public Uri OrganizationUrl => new($"https://dev.azure.com/{Organization}/");
}

public sealed class ServicePrincipalOptions
{
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}
