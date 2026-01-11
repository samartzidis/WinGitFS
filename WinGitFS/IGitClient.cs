namespace WinGitFS;

/// <summary>
/// Abstraction over Git hosting providers (Azure DevOps, GitHub, etc.)
/// </summary>
internal interface IGitClient : IDisposable
{
    /// <summary>Validates credentials and repository access. Throws ArgumentException on failure.</summary>
    Task ValidateAsync(CancellationToken ct = default);

    /// <summary>Gets the repository's default branch name (e.g., "main" or "master").</summary>
    Task<string> GetDefaultBranchAsync(CancellationToken ct = default);

    /// <summary>Lists items (files and folders) at the specified path.</summary>
    Task<IReadOnlyList<GitItem>> ListItemsAsync(
        VirtualPathMapper.VersionType versionType,
        string version,
        string scopePath,
        CancellationToken ct);

    /// <summary>Gets the size of a file in bytes.</summary>
    Task<long> GetFileSizeAsync(
        VirtualPathMapper.VersionType versionType,
        string version,
        string path,
        CancellationToken ct);

    /// <summary>Gets the content of a file as a byte array.</summary>
    Task<byte[]?> GetFileBytesAsync(
        VirtualPathMapper.VersionType versionType,
        string version,
        string path,
        CancellationToken ct);

    /// <summary>Lists all branches in the repository.</summary>
    Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken ct);
}

/// <summary>Represents a file or folder in the Git repository.</summary>
internal sealed record GitItem(string Name, string Path, bool IsFolder, long Size);
