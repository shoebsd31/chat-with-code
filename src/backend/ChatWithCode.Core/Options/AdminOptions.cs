namespace ChatWithCode.Core.Options;

/// <summary>
/// Who may change Chat with code settings. Bound from the "Admin" configuration section
/// (CHAT_ADMINS in .env). Kept in the backend's configuration on purpose: it can't be changed
/// from Azure DevOps, and checking Azure DevOps admin roles would need a high-privilege scope.
/// </summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>Comma- or semicolon-separated sign-in emails (or Azure DevOps user ids).</summary>
    public string? Users { get; set; }

    public IReadOnlySet<string> UserSet =>
        (Users ?? string.Empty)
            .Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool IsAdmin(string userId, string? email)
    {
        var admins = UserSet;
        return admins.Contains(userId) || (email is not null && admins.Contains(email));
    }
}
