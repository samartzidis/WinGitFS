using Meziantou.Framework.Win32.ProjectedFileSystem;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace WinGitFS;

// Read-only provider that projects a Git repository (Azure DevOps or GitHub) into a ProjFS virtualization root.
// Root level shows the contents of the configured branch (e.g., main).
internal sealed class GitFsProvider : ProjectedFileSystemBase
{
    private readonly GitFsOptions _options;
    private readonly IGitClient _git;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GitFsProvider> _logger;
    private readonly SingleFlight _singleFlight = new();

    public GitFsProvider(
        GitFsOptions options,
        IGitClient git,
        IMemoryCache cache,
        ILogger<GitFsProvider> logger)
        : base(options.VirtualizationRoot)
    {
        _options = options;
        _git = git;
        _cache = cache;
        _logger = logger;
    }

    // Single entry lookup - called when ProjFS needs info about a specific file/directory.
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

            // Get the parent directory and find this entry in it
            var normalized = path.TrimStart('\\');
            var lastSep = normalized.LastIndexOf('\\');
            var parentPath = lastSep > 0 ? normalized[..lastSep] : "";
            var entryName = lastSep >= 0 ? normalized[(lastSep + 1)..] : normalized;

            _logger.LogDebug("  Looking for '{EntryName}' in parent '{ParentPath}'", entryName, parentPath);

            var mapped = VirtualPathMapper.Map(parentPath, _options.DefaultBranch, _options.RemotePath);
            var entries = GetDirectoryEntriesCached(mapped);

            foreach (var e in entries)
            {
                _logger.LogDebug("    Entry: {Name} IsDir={IsDir}", e.Name, e.IsDirectory);
            }

            var entry = entries.FirstOrDefault(e =>
                e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase));

            if (entry is not null)
            {
                if (entry.IsDirectory)
                {
                    _logger.LogInformation("  GetEntry result: {Name} isDir=True", entry.Name);
                    return ProjectedFileSystemEntry.Directory(entry.Name);
                }

                // For files, fetch actual size if unknown (the list API doesn't return sizes for ADO)
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

    // Directory listing callback.
    protected override IEnumerable<ProjectedFileSystemEntry> GetEntries(string path)
    {
        _logger.LogInformation("GetEntries called: {Path}", path ?? "(null)");
        try
        {
            var mapped = VirtualPathMapper.Map(path ?? "", _options.DefaultBranch, _options.RemotePath);
            _logger.LogInformation("  Mapped: {VirtualPath} -> {VersionType}:{Branch}:{RepoPath}",
                path ?? "(root)", mapped.VersionType, mapped.Version, mapped.RepoPath);

            var entries = GetDirectoryEntriesCached(mapped);
            var entryList = entries.ToList(); // Force evaluation inside try-catch

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
            throw; // Re-throw so ProjFS knows it failed
        }
    }

    private IEnumerable<ProjectedFileSystemEntry> GetDirectoryEntriesCached(VirtualPathMapper.MappedPath mapped)
    {
        var key = $"dir::{mapped.VersionType}::{mapped.Version}::{mapped.RepoPath}";
        _logger.LogDebug("GetDirectoryEntriesCached: key={Key}", key);

        try
        {
            return _singleFlight.DoAsync(key, async () =>
            {
                _logger.LogDebug("SingleFlight executing for key={Key}", key);
                var entries = await GetDirectoryEntriesAsync(mapped, CancellationToken.None).ConfigureAwait(false);
                _logger.LogDebug("GetDirectoryEntriesAsync returned {Count} entries", entries.Count);
                return entries.ToArray();
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDirectoryEntriesCached FAILED for key={Key}", key);
            throw;
        }
    }

    // File content callback (full-file hydration). ProjFS will stream from the returned Stream.
    protected override Stream OpenRead(string path)
    {
        _logger.LogInformation("OpenRead called: {Path}", path);
        try
        {
            var mapped = VirtualPathMapper.Map(path, _options.DefaultBranch, _options.RemotePath);
            _logger.LogInformation("  OpenRead: {VirtualPath} -> {Branch}:{RepoPath}", path, mapped.Version, mapped.RepoPath);

            if (mapped.RepoPath.EndsWith("/", StringComparison.Ordinal))
                throw new IOException("Cannot open a directory for reading.");

            var key = $"file::{mapped.VersionType}::{mapped.Version}::{mapped.RepoPath}";
            var bytes = _singleFlight.DoAsync(key, async () =>
            {
                if (_cache.TryGetValue(key, out byte[]? cached) && cached is not null)
                    return cached;

                var fetched = await _git.GetFileBytesAsync(mapped.VersionType, mapped.Version, mapped.RepoPath, CancellationToken.None).ConfigureAwait(false);
                if (fetched is null)
                    return Array.Empty<byte>();

                _cache.Set(key, fetched, new MemoryCacheEntryOptions
                {
                    Size = Math.Max(1, fetched.LongLength),
                    SlidingExpiration = TimeSpan.FromMinutes(5),
                });

                return fetched;
            }).GetAwaiter().GetResult();

            if (bytes.Length == 0)
                throw new FileNotFoundException("File not found in repository.", path);

            // Return a non-writable stream.
            return new MemoryStream(bytes, writable: false);
        }
        catch (Exception ex) when (ex is not FileNotFoundException and not IOException)
        {
            _logger.LogError(ex, "OpenRead failed for path: {Path}", path);
            throw new IOException($"Failed to read file: {path}", ex);
        }
    }

    private async Task<IReadOnlyList<ProjectedFileSystemEntry>> GetDirectoryEntriesAsync(VirtualPathMapper.MappedPath mapped, CancellationToken ct)
    {
        var scopePath = mapped.RepoPath;
        if (string.IsNullOrEmpty(scopePath))
            scopePath = "/";

        var cacheKey = $"dircache::{mapped.VersionType}::{mapped.Version}::{scopePath}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<ProjectedFileSystemEntry>? cached) && cached is not null)
        {
            _logger.LogDebug("Cache hit for {Path}: {Count} entries", scopePath, cached.Count);
            return cached;
        }

        var items = await _git.ListItemsAsync(mapped.VersionType, mapped.Version, scopePath, ct).ConfigureAwait(false);
        _logger.LogDebug("Git client returned {Count} items for {Path}", items.Count, scopePath);

        var entries = items.Select(i =>
        {
            if (i.IsFolder)
                return ProjectedFileSystemEntry.Directory(i.Name);

            // Size from list API may be 0 for some providers; actual size is fetched in GetEntry when needed
            var size = i.Size <= 0 ? 0 : (int)Math.Min(i.Size, int.MaxValue);
            return ProjectedFileSystemEntry.File(i.Name, size);
        }).ToArray();

        _cache.Set(cacheKey, entries, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = _options.DirectoryCacheTtl,
        });

        return entries;
    }
}
