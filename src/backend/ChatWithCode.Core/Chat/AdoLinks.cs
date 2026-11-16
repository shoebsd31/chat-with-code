namespace ChatWithCode.Core.Chat;

/// <summary>Links into the Azure DevOps web UI.</summary>
public static class AdoLinks
{
    /// <summary>Opens a file at a branch with the line range highlighted.</summary>
    public static Uri FileLines(string organization, string project, string repository, string branchRef, string path, int startLine, int endLine)
    {
        ArgumentNullException.ThrowIfNull(branchRef);
        ArgumentNullException.ThrowIfNull(path);
        var branch = branchRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? branchRef["refs/heads/".Length..] : branchRef;
        var query = string.Join('&',
            $"path={Uri.EscapeDataString("/" + path.TrimStart('/'))}",
            $"version=GB{Uri.EscapeDataString(branch)}",
            $"line={startLine}",
            $"lineEnd={endLine + 1}",
            "lineStartColumn=1",
            "lineEndColumn=1",
            "lineStyle=plain",
            "_a=contents");
        return new Uri($"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repository)}?{query}");
    }
}
