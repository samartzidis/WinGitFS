using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;

namespace WinGitFS;

internal sealed class AzureDevOpsGitClient : IGitClient
{
    private readonly VssConnection _connection;
    private readonly GitHttpClient _gitClient;
    private readonly string _project;
    private readonly string _repo;
    private readonly ILogger<AzureDevOpsGitClient> _logger;

    private AzureDevOpsGitClient(
      VssConnection connection,
      string project,
      string repository,
      ILogger<AzureDevOpsGitClient> logger)
    {
        _connection = connection;
        _gitClient = connection.GetClient<GitHttpClient>();
        _project = project;
        _repo = repository;
        _logger = logger;
    }

    /// <summary>Creates a client using PAT authentication.</summary>
    public static AzureDevOpsGitClient WithPat(
      Uri organizationUrl,
      string project,
      string repository,
      string personalAccessToken,
      ILogger<AzureDevOpsGitClient> logger)
    {
        var credentials = new VssBasicCredential(string.Empty, personalAccessToken);
        var connection = new VssConnection(organizationUrl, credentials);
        return new AzureDevOpsGitClient(connection, project, repository, logger);
    }

    /// <summary>Creates a client using Azure AD SSO (interactive browser login).</summary>
    public static async Task<AzureDevOpsGitClient> WithSsoAsync(
      Uri organizationUrl,
      string project,
      string repository,
      ILogger<AzureDevOpsGitClient> logger)
    {
        // Use MSAL to get Azure AD token for Azure DevOps
        const string AzureDevOpsResourceId = "499b84ac-1321-427f-aa17-267ca6975798";
        const string ClientId = "872cd9fa-d31f-45e0-9eab-6e460a02d1f1"; // VS Code client ID

        var app = PublicClientApplicationBuilder
          .Create(ClientId)
          .WithAuthority("https://login.microsoftonline.com/organizations")
          .WithDefaultRedirectUri()
          .Build();

        var scopes = new[] { $"{AzureDevOpsResourceId}/.default" };

        AuthenticationResult? result = null;

        // Try silent auth first (cached token)
        var accounts = await app.GetAccountsAsync();
        var account = accounts.FirstOrDefault();
        if (account is not null)
        {
            try
            {
                result = await app.AcquireTokenSilent(scopes, account).ExecuteAsync();
            }
            catch (MsalUiRequiredException) { }
        }

        // Fall back to interactive login
        if (result is null)
        {
            logger.LogInformation("Opening browser for Azure AD sign-in...");
            result = await app.AcquireTokenInteractive(scopes)
              .WithPrompt(Prompt.SelectAccount)
              .ExecuteAsync();
        }

        // Use the access token with the SDK
        var credentials = new VssOAuthAccessTokenCredential(result.AccessToken);
        var connection = new VssConnection(organizationUrl, credentials);
        return new AzureDevOpsGitClient(connection, project, repository, logger);
    }

    public async Task ValidateAsync(CancellationToken ct = default)
    {
        try
        {
            // Validate by fetching repository info
            var repo = await _gitClient.GetRepositoryAsync(_project, _repo, cancellationToken: ct).ConfigureAwait(false);
            _logger.LogInformation("Repository: {Project}/{Repo}", _project, repo.Name);
        }
        catch (VssUnauthorizedException)
        {
            throw new ArgumentException(
                "Invalid or expired Azure DevOps credentials. " +
                "Check your PAT or try --sso for browser login.");
        }
        catch (VssServiceException ex) when (ex.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                                              ex.Message.Contains("could not be found", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Repository '{_project}/{_repo}' not found or you don't have access to it.");
        }
    }

    /// <summary>Gets the repository's default branch name (e.g., "main" or "master").</summary>
    public async Task<string> GetDefaultBranchAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Fetching default branch for {Project}/{Repo}", _project, _repo);

        var repo = await _gitClient.GetRepositoryAsync(_project, _repo, cancellationToken: ct).ConfigureAwait(false);

        // DefaultBranch is like "refs/heads/main" - extract just the branch name
        var defaultBranch = repo.DefaultBranch ?? "refs/heads/master";
        const string prefix = "refs/heads/";
        if (defaultBranch.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            defaultBranch = defaultBranch[prefix.Length..];

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

        var versionDescriptor = new GitVersionDescriptor
        {
            Version = version,
            VersionType = versionType == VirtualPathMapper.VersionType.Branch
            ? GitVersionType.Branch
            : GitVersionType.Commit
        };

        try
        {
            var items = await _gitClient.GetItemsAsync(
              project: _project,
              repositoryId: _repo,
              scopePath: scopePath,
              recursionLevel: VersionControlRecursionType.OneLevel,
              includeContentMetadata: true,
              versionDescriptor: versionDescriptor,
              cancellationToken: ct).ConfigureAwait(false);

            // Normalize scopePath for comparison
            var normalizedScope = scopePath.TrimEnd('/');
            if (!normalizedScope.StartsWith('/'))
                normalizedScope = "/" + normalizedScope;

            var result = new List<GitItem>();
            foreach (var item in items)
            {
                // Skip the parent folder itself
                var normalizedPath = item.Path.TrimEnd('/');
                if (normalizedPath.Equals(normalizedScope, StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = Path.GetFileName(item.Path.Replace('/', '\\').TrimEnd('\\'));
                if (string.IsNullOrEmpty(name))
                    continue;

                // Size is not directly available in content metadata for list operations
                // We'll get it on-demand when needed
                var isFolder = item.IsFolder;

                _logger.LogDebug("  Item: {Name} isFolder={IsFolder}", name, isFolder);
                result.Add(new GitItem(name, item.Path, isFolder, Size: 0));
            }

            _logger.LogDebug("Listed {Count} items at {Path}", result.Count, scopePath);
            return result;
        }
        catch (VssServiceException ex)
        {
            _logger.LogError(ex, "ADO list failed for path: {Path}", scopePath);
            throw new IOException($"ADO API error for path: {scopePath}", ex);
        }
    }

    public async Task<long> GetFileSizeAsync(
      VirtualPathMapper.VersionType versionType,
      string version,
      string path,
      CancellationToken ct)
    {
        var versionDescriptor = new GitVersionDescriptor
        {
            Version = version,
            VersionType = versionType == VirtualPathMapper.VersionType.Branch
            ? GitVersionType.Branch
            : GitVersionType.Commit
        };

        try
        {
            // Get file content stream to determine actual size
            using var stream = await _gitClient.GetItemContentAsync(
              project: _project,
              repositoryId: _repo,
              path: path,
              versionDescriptor: versionDescriptor,
              cancellationToken: ct).ConfigureAwait(false);

            // If the stream is seekable, we can get length directly
            if (stream.CanSeek)
            {
                var size = stream.Length;
                _logger.LogDebug("GetFileSizeAsync for {Path}: {Size} bytes", path, size);
                return size;
            }

            // Otherwise, read and count bytes
            var buffer = new byte[81920];
            long totalSize = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                totalSize += read;
            }
            _logger.LogDebug("GetFileSizeAsync for {Path}: {Size} bytes (counted)", path, totalSize);
            return totalSize;
        }
        catch (VssServiceException ex)
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
        var versionDescriptor = new GitVersionDescriptor
        {
            Version = version,
            VersionType = versionType == VirtualPathMapper.VersionType.Branch
            ? GitVersionType.Branch
            : GitVersionType.Commit
        };

        try
        {
            using var stream = await _gitClient.GetItemContentAsync(
              project: _project,
              repositoryId: _repo,
              path: path,
              versionDescriptor: versionDescriptor,
              cancellationToken: ct).ConfigureAwait(false);

            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            return ms.ToArray();
        }
        catch (VssServiceException ex)
        {
            _logger.LogWarning(ex, "Failed to get file content for {Path}", path);
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken ct)
    {
        try
        {
            var refs = await _gitClient.GetRefsAsync(
              project: _project,
              repositoryId: _repo,
              filter: "heads/",
              cancellationToken: ct).ConfigureAwait(false);

            var branches = refs
              .Select(r => r.Name)
              .Where(n => n.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
              .Select(n => n["refs/heads/".Length..])
              .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
              .ToList();

            return branches;
        }
        catch (VssServiceException ex)
        {
            _logger.LogWarning(ex, "Failed to list branches");
            return Array.Empty<string>();
        }
    }

    public void Dispose()
    {
        _gitClient.Dispose();
        _connection.Dispose();
    }

}
