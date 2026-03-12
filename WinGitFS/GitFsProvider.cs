using Meziantou.Framework.Win32.ProjectedFileSystem;
using Microsoft.Extensions.Logging;

namespace WinGitFS;

// Read-only ProjFS provider that projects a Git repository into a virtualization root.
// Directory listings are served from GitProcessClient's in-memory tree (instant).
// File content is fetched on demand via the persistent cat-file --batch process.
// ProjFS itself caches hydrated files on disk, so no application-level content cache is needed.
internal sealed class GitFsProvider : ProjectedFileSystemBase
{
    private readonly GitFsOptions _options;
    private readonly IGitClient _git;
    private readonly ILogger<GitFsProvider> _logger;
    private readonly SingleFlight _singleFlight = new();

    public GitFsProvider(
        GitFsOptions options,
        IGitClient git,
        ILogger<GitFsProvider> logger)
        : base(options.VirtualizationRoot)
    {
        _options = options;
        _git = git;
        _logger = logger;
    }

    protected override ProjectedFileSystemEntry? GetEntry(string path)
    {
        _logger.LogInformation("GetEntry: {Path}", path ?? "(null)");
        try
        {
            if (string.IsNullOrEmpty(path) || path == "\\")
            {
                _logger.LogInformation("  GetEntry: returning root directory");
                return ProjectedFileSystemEntry.Directory("");
            }

            var normalized = path.TrimStart('\\');
            var lastSep = normalized.LastIndexOf('\\');
            var parentPath = lastSep > 0 ? normalized[..lastSep] : "";
            var entryName = lastSep >= 0 ? normalized[(lastSep + 1)..] : normalized;

            _logger.LogDebug("  Looking for '{EntryName}' in parent '{ParentPath}'", entryName, parentPath);

            var mapped = VirtualPathMapper.Map(parentPath, _options.DefaultBranch, _options.RemotePath);
            var entries = GetDirectoryEntries(mapped);

            var entry = entries.FirstOrDefault(e =>
                e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase));

            if (entry is not null)
            {
                if (entry.IsDirectory)
                {
                    _logger.LogInformation("  GetEntry result: {Name} isDir=True", entry.Name);
                    return ProjectedFileSystemEntry.Directory(entry.Name);
                }

                var fileSize = entry.Length;
                if (fileSize <= 0)
                {
                    var filePath = string.IsNullOrEmpty(parentPath) ? entryName : $"{parentPath}\\{entryName}";
                    var fileMapped = VirtualPathMapper.Map(filePath, _options.DefaultBranch, _options.RemotePath);
                    fileSize = _singleFlight.DoAsync($"size::{fileMapped.RepoPath}", async () =>
                    {
                        var size = await _git.GetFileSizeAsync(fileMapped.VersionType, fileMapped.Version, fileMapped.RepoPath, CancellationToken.None).ConfigureAwait(false);
                        return (int)Math.Min(size, int.MaxValue);
                    }).GetAwaiter().GetResult();
                    _logger.LogDebug("  Fetched file size for {Name}: {Size}", entry.Name, fileSize);
                }

                _logger.LogInformation("  GetEntry result: {Name} isDir=False length={Length}", entry.Name, fileSize);
                return ProjectedFileSystemEntry.File(entry.Name, fileSize);
            }

            _logger.LogWarning("  GetEntry: NOT FOUND - '{EntryName}' not in parent '{ParentPath}'", entryName, parentPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetEntry FAILED for path: {Path}", path);
            return null;
        }
    }

    protected override IEnumerable<ProjectedFileSystemEntry> GetEntries(string path)
    {
        _logger.LogInformation("GetEntries called: {Path}", path ?? "(null)");
        try
        {
            var mapped = VirtualPathMapper.Map(path ?? "", _options.DefaultBranch, _options.RemotePath);
            _logger.LogInformation("  Mapped: {VirtualPath} -> {VersionType}:{Branch}:{RepoPath}",
                path ?? "(root)", mapped.VersionType, mapped.Version, mapped.RepoPath);

            var entries = GetDirectoryEntries(mapped);
            var entryList = entries.ToList();

            _logger.LogInformation("  Found {Count} entries:", entryList.Count);
            foreach (var e in entryList)
            {
                _logger.LogInformation("    - {Name} (IsDir={IsDir}, Length={Length})", e.Name, e.IsDirectory, e.Length);
            }
            return entryList;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetEntries FAILED for path: {Path}", path);
            throw;
        }
    }

    private IReadOnlyList<ProjectedFileSystemEntry> GetDirectoryEntries(VirtualPathMapper.MappedPath mapped)
    {
        var scopePath = mapped.RepoPath;
        if (string.IsNullOrEmpty(scopePath))
            scopePath = "/";

        var items = _git.ListItemsAsync(mapped.VersionType, mapped.Version, scopePath, CancellationToken.None)
            .GetAwaiter().GetResult();

        return items.Select(i =>
        {
            if (i.IsFolder)
                return ProjectedFileSystemEntry.Directory(i.Name);

            var size = i.Size <= 0 ? 0 : (int)Math.Min(i.Size, int.MaxValue);
            return ProjectedFileSystemEntry.File(i.Name, size);
        }).ToArray();
    }

    protected override Stream OpenRead(string path)
    {
        _logger.LogInformation("OpenRead called: {Path}", path);
        try
        {
            var mapped = VirtualPathMapper.Map(path, _options.DefaultBranch, _options.RemotePath);
            _logger.LogInformation("  OpenRead: {VirtualPath} -> {Branch}:{RepoPath}", path, mapped.Version, mapped.RepoPath);

            if (mapped.RepoPath.EndsWith("/", StringComparison.Ordinal))
                throw new IOException("Cannot open a directory for reading.");

            var bytes = _singleFlight.DoAsync($"file::{mapped.RepoPath}", async () =>
            {
                var fetched = await _git.GetFileBytesAsync(mapped.VersionType, mapped.Version, mapped.RepoPath, CancellationToken.None).ConfigureAwait(false);
                return fetched ?? Array.Empty<byte>();
            }).GetAwaiter().GetResult();

            if (bytes.Length == 0)
                throw new FileNotFoundException("File not found in repository.", path);

            return new MemoryStream(bytes, writable: false);
        }
        catch (Exception ex) when (ex is not FileNotFoundException and not IOException)
        {
            _logger.LogError(ex, "OpenRead failed for path: {Path}", path);
            throw new IOException($"Failed to read file: {path}", ex);
        }
    }
}
