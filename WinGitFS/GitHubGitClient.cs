using Microsoft.Extensions.Logging;
using Octokit;

namespace WinGitFS;

internal sealed class GitHubGitClient : IGitClient
{
    private readonly GitHubClient _client;
    private readonly string _owner;
    private readonly string _repo;
    private readonly ILogger<GitHubGitClient> _logger;

    private GitHubGitClient(
        GitHubClient client,
        string owner,
        string repository,
        ILogger<GitHubGitClient> logger)
    {
        _client = client;
        _owner = owner;
        _repo = repository;
        _logger = logger;
    }

    /// <summary>Creates a client using PAT authentication.</summary>
    public static GitHubGitClient WithPat(
        string owner,
        string repository,
        string personalAccessToken,
        ILogger<GitHubGitClient> logger)
    {
        var client = new GitHubClient(new ProductHeaderValue("WinGitFS"))
        {
            Credentials = new Credentials(personalAccessToken)
        };
        return new GitHubGitClient(client, owner, repository, logger);
    }

    /// <summary>Creates a client using GitHub Device Flow (interactive login).</summary>
    /// <remarks>
    /// Device flow requires a registered GitHub OAuth App with device flow enabled.
    /// For now, this is a placeholder - users should use PAT authentication.
    /// </remarks>
    public static Task<GitHubGitClient> WithDeviceFlowAsync(
        string owner,
        string repository,
        ILogger<GitHubGitClient> logger)
    {
        // GitHub Device Flow requires a registered OAuth App with device flow enabled.
        // The Octokit library's device flow types are internal, so we cannot easily use them.
        // For now, throw with a helpful message directing users to use PAT.
        throw new NotSupportedException(
            "GitHub device flow authentication is not yet supported. " +
            "Please use --pat with a GitHub Personal Access Token instead. " +
            "You can create a PAT at: https://github.com/settings/tokens");
    }

    public async Task ValidateAsync(CancellationToken ct = default)
    {
        // Validate PAT if credentials are set
        if (_client.Credentials.AuthenticationType != AuthenticationType.Anonymous)
        {
            try
            {
                var user = await _client.User.Current().ConfigureAwait(false);
                _logger.LogInformation("Authenticated as GitHub user: {Login}", user.Login);
            }
            catch (AuthorizationException)
            {
                throw new ArgumentException(
                    "Invalid or expired GitHub Personal Access Token. " +
                    "Create a new one at: https://github.com/settings/tokens");
            }
        }

        // Validate repository access
        try
        {
            var repo = await _client.Repository.Get(_owner, _repo).ConfigureAwait(false);
            _logger.LogInformation("Repository: {FullName} ({Visibility})",
                repo.FullName, repo.Private ? "private" : "public");
        }
        catch (NotFoundException)
        {
            throw new ArgumentException(
                $"Repository '{_owner}/{_repo}' not found or you don't have access to it.");
        }
        catch (AuthorizationException)
        {
            throw new ArgumentException(
                $"Repository '{_owner}/{_repo}' requires authentication. " +
                "Use --pat with a Personal Access Token.");
        }
    }

    public async Task<string> GetDefaultBranchAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Fetching default branch for {Owner}/{Repo}", _owner, _repo);

        var repo = await _client.Repository.Get(_owner, _repo).ConfigureAwait(false);
        var defaultBranch = repo.DefaultBranch ?? "main";

        _logger.LogInformation("Default branch: {Branch}", defaultBranch);
        return defaultBranch;
    }

    public async Task<IReadOnlyList<GitItem>> ListItemsAsync(
        VirtualPathMapper.VersionType versionType,
        string version,
        string scopePath,
        CancellationToken ct)
    {
        _logger.LogInformation("Listing items at {Path} ({VersionType}:{Version})", scopePath, versionType, version);

        try
        {
            // Normalize path - GitHub API doesn't want leading slash
            var path = scopePath.TrimStart('/').TrimEnd('/');

            IReadOnlyList<RepositoryContent> contents;
            if (string.IsNullOrEmpty(path))
            {
                contents = await _client.Repository.Content.GetAllContentsByRef(_owner, _repo, version).ConfigureAwait(false);
            }
            else
            {
                contents = await _client.Repository.Content.GetAllContentsByRef(_owner, _repo, path, version).ConfigureAwait(false);
            }

            var result = new List<GitItem>();
            foreach (var item in contents)
            {
                var isFolder = item.Type == ContentType.Dir;
                var size = item.Size; // int in Octokit

                _logger.LogDebug("  Item: {Name} isFolder={IsFolder} size={Size}", item.Name, isFolder, size);
                result.Add(new GitItem(item.Name, "/" + item.Path, isFolder, size));
            }

            _logger.LogDebug("Listed {Count} items at {Path}", result.Count, scopePath);
            return result;
        }
        catch (NotFoundException ex)
        {
            _logger.LogWarning(ex, "Path not found: {Path}", scopePath);
            return Array.Empty<GitItem>();
        }
        catch (ApiException ex)
        {
            _logger.LogError(ex, "GitHub API error for path: {Path}", scopePath);
            throw new IOException($"GitHub API error for path: {scopePath}", ex);
        }
    }

    public async Task<long> GetFileSizeAsync(
        VirtualPathMapper.VersionType versionType,
        string version,
        string path,
        CancellationToken ct)
    {
        try
        {
            // Normalize path
            var normalizedPath = path.TrimStart('/');

            var contents = await _client.Repository.Content.GetAllContentsByRef(_owner, _repo, normalizedPath, version).ConfigureAwait(false);

            if (contents.Count > 0 && contents[0].Type == ContentType.File)
            {
                var size = contents[0].Size; // int in Octokit
                _logger.LogDebug("GetFileSizeAsync for {Path}: {Size} bytes", path, size);
                return size;
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get file size for {Path}", path);
            return 0;
        }
    }

    public async Task<byte[]?> GetFileBytesAsync(
        VirtualPathMapper.VersionType versionType,
        string version,
        string path,
        CancellationToken ct)
    {
        try
        {
            // Normalize path
            var normalizedPath = path.TrimStart('/');

            var rawContent = await _client.Repository.Content.GetRawContentByRef(_owner, _repo, normalizedPath, version).ConfigureAwait(false);
            return rawContent;
        }
        catch (NotFoundException ex)
        {
            _logger.LogWarning(ex, "File not found: {Path}", path);
            return null;
        }
        catch (ApiException ex)
        {
            _logger.LogWarning(ex, "Failed to get file content for {Path}", path);
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken ct)
    {
        try
        {
            var branches = await _client.Repository.Branch.GetAll(_owner, _repo).ConfigureAwait(false);

            return branches
                .Select(b => b.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (ApiException ex)
        {
            _logger.LogWarning(ex, "Failed to list branches");
            return Array.Empty<string>();
        }
    }

    public void Dispose()
    {
        // GitHubClient doesn't implement IDisposable, nothing to dispose
    }
}
