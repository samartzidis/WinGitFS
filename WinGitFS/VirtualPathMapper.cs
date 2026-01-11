namespace WinGitFS;

internal static class VirtualPathMapper
{
    internal enum VersionType
    {
        Branch,
        Commit,
    }

    internal sealed record MappedPath(VersionType VersionType, string Version, string RepoPath);

    // virtualRelativePath uses '\' separators (ProjFS is Windows-only).
    // Layout: root maps directly to the specified branch (e.g., master)
    // remotePath is the base path in the repo to use as root (no leading/trailing slashes)
    public static MappedPath Map(string virtualRelativePath, string branch, string remotePath = "")
    {
        var p = (virtualRelativePath ?? "").TrimStart('\\');
        var parts = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        // Build the repo path: remotePath + virtual path parts
        string repoPath;
        if (parts.Length == 0)
        {
            // Virtual root -> remote path root (or repo root if no remote path)
            repoPath = string.IsNullOrEmpty(remotePath) ? "/" : "/" + remotePath;
        }
        else
        {
            // Combine remote path with virtual path
            var virtualPart = string.Join('/', parts);
            repoPath = string.IsNullOrEmpty(remotePath)
              ? "/" + virtualPart
              : "/" + remotePath + "/" + virtualPart;
        }

        return new MappedPath(VersionType.Branch, branch, repoPath);
    }
}
