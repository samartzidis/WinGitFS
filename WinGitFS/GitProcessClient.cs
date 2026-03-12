using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WinGitFS;

/// <summary>
/// Pure git.exe IGitClient with zero per-request process spawns:
///   - Blobless bare clone at startup (one-time)
///   - Full directory tree built in memory from a single "ls-tree -r -t" call (one-time)
///   - Persistent "cat-file --batch-check" process for size-only queries (no content)
///   - Persistent "cat-file --batch" process for on-demand blob content
/// </summary>
internal sealed class GitProcessClient : IGitClient
{
    private readonly string _bareRepoDir;
    private readonly ILogger _logger;

    // In-memory directory tree keyed by normalized parent dir path.
    // Built once from "git ls-tree -r -t <branch>".
    private readonly Dictionary<string, List<GitItem>> _dirTree = new(StringComparer.OrdinalIgnoreCase);

    // Persistent cat-file --batch process for full blob reads (content).
    private Process? _batchProcess;
    private Stream? _batchIn;
    private Stream? _batchOut;
    private readonly SemaphoreSlim _batchSemaphore = new(1, 1);

    // Persistent cat-file --batch-check process for size-only queries (header only, no content).
    private Process? _batchCheckProcess;
    private Stream? _batchCheckIn;
    private Stream? _batchCheckOut;
    private readonly SemaphoreSlim _batchCheckSemaphore = new(1, 1);

    private string _defaultBranch = "";
    private string[] _branches = [];
    private bool _disposed;

    private GitProcessClient(string bareRepoDir, ILogger logger)
    {
        _bareRepoDir = bareRepoDir;
        _logger = logger;
    }

    private static readonly Version MinGitVersion = new(2, 22);

    public static async Task<GitProcessClient> CloneAsync(
        string cloneUrl,
        ILogger logger,
        string remotePath = "",
        string branch = "",
        CancellationToken ct = default)
    {
        await EnsureGitVersionAsync(logger, ct);

        var repoDir = Path.Combine(Path.GetTempPath(), $"wingitfs_bare_{Guid.NewGuid():N}");
        var client = new GitProcessClient(repoDir, logger);

        // 1. Resolve branch before cloning so we can use --single-branch
        if (string.IsNullOrEmpty(branch))
        {
            logger.LogInformation("Detecting default branch...");
            var lsRemote = await RunGitOneOffAsync(
                $"ls-remote --symref \"{cloneUrl}\" HEAD",
                Path.GetTempPath(), logger, ct);
            if (lsRemote.Success)
                branch = ParseDefaultBranchFromLsRemote(lsRemote.Output);
            if (string.IsNullOrEmpty(branch))
                branch = "main";
            logger.LogInformation("Default branch: {Branch}", branch);
        }
        client._defaultBranch = branch;

        // 2. Shallow blobless single-branch clone
        logger.LogInformation("Starting blobless bare clone (branch: {Branch})...", branch);
        var r = await RunGitOneOffAsync(
            $"clone --filter=blob:none --bare --depth 1 --single-branch --branch \"{branch}\" -- \"{cloneUrl}\" \"{repoDir}\"",
            Path.GetTempPath(), logger, ct, timeoutSeconds: 600);
        if (!r.Success)
            throw new InvalidOperationException($"git clone failed: {r.Error}");
        logger.LogInformation("Blobless clone complete: {Dir}", repoDir);

        // 3. Branch list (single-branch clone only has the one we cloned)
        client._branches = [branch];

        // 4. Build in-memory directory tree (scoped to remotePath if specified)
        var treeScope = (remotePath ?? "").Replace('\\', '/').Trim('/');
        await client.BuildDirectoryTreeAsync(branch, treeScope, ct);

        // 5. Start persistent batch processes
        client.EnsureBatchProcess();
        client.EnsureBatchCheckProcess();

        return client;
    }

    /// <summary>
    /// Parses "git ls-remote --symref &lt;url&gt; HEAD" output to find the default branch.
    /// Output format: "ref: refs/heads/main\tHEAD\n&lt;sha&gt;\tHEAD\n"
    /// </summary>
    private static string ParseDefaultBranchFromLsRemote(string output)
    {
        const string marker = "ref: refs/heads/";
        foreach (var line in output.Split('\n'))
        {
            if (!line.StartsWith(marker, StringComparison.Ordinal)) continue;
            var tabIdx = line.IndexOf('\t');
            return tabIdx > marker.Length
                ? line[marker.Length..tabIdx]
                : line[marker.Length..].Trim();
        }
        return "";
    }

    // -----------------------------------------------------------------
    //  IGitClient
    // -----------------------------------------------------------------

    public Task ValidateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDefaultBranchAsync(CancellationToken ct = default) =>
        Task.FromResult(_defaultBranch);

    public Task<IReadOnlyList<GitItem>> ListItemsAsync(
        VirtualPathMapper.VersionType versionType,
        string version,
        string scopePath,
        CancellationToken ct)
    {
        var path = NormalizePath(scopePath);

        if (_dirTree.TryGetValue(path, out var items))
            return Task.FromResult<IReadOnlyList<GitItem>>(items);

        return Task.FromResult<IReadOnlyList<GitItem>>([]);
    }

    /// <summary>
    /// Returns the blob size via the persistent --batch-check process (header only,
    /// no content downloaded). Triggers a promisor fetch on first access.
    /// </summary>
    public async Task<long> GetFileSizeAsync(
        VirtualPathMapper.VersionType versionType,
        string version,
        string path,
        CancellationToken ct)
    {
        var objectSpec = $"{version}:{NormalizePath(path)}";
        var size = await BatchCheckSizeAsync(objectSpec);
        if (size is null)
            throw new FileNotFoundException($"Cannot get size for: {path}");
        return size.Value;
    }

    /// <summary>
    /// Returns file content via the persistent --batch process (full blob read).
    /// </summary>
    public async Task<byte[]?> GetFileBytesAsync(
        VirtualPathMapper.VersionType versionType,
        string version,
        string path,
        CancellationToken ct)
    {
        var objectSpec = $"{version}:{NormalizePath(path)}";
        var blob = await BatchGetBlobAsync(objectSpec);
        return blob?.Content;
    }

    public Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(_branches);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _batchSemaphore.Dispose();
        _batchCheckSemaphore.Dispose();
        KillBatchProcess();
        KillBatchCheckProcess();

        if (!Directory.Exists(_bareRepoDir)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_bareRepoDir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_bareRepoDir, recursive: true);
            _logger.LogInformation("Deleted bare repo: {Dir}", _bareRepoDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete bare repo at {Dir}", _bareRepoDir);
        }
    }

    // -----------------------------------------------------------------
    //  In-memory directory tree (built once at startup)
    // -----------------------------------------------------------------

    private async Task BuildDirectoryTreeAsync(string branch, string scopePath, CancellationToken ct)
    {
        var args = string.IsNullOrEmpty(scopePath)
            ? $"ls-tree -r -t \"{branch}\""
            : $"ls-tree -r -t \"{branch}\" -- \"{scopePath}/\"";

        _logger.LogInformation("Building directory tree for '{Branch}' (scope: {Scope})...",
            branch, string.IsNullOrEmpty(scopePath) ? "(entire repo)" : scopePath);

        var r = await RunGitOneOffAsync(args, _bareRepoDir, _logger, ct, timeoutSeconds: 120);
        if (!r.Success)
            throw new InvalidOperationException($"ls-tree failed: {r.Error}");

        foreach (var line in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tabIdx = line.IndexOf('\t');
            if (tabIdx < 0) continue;

            var meta = line[..tabIdx].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (meta.Length < 3) continue;

            var type = meta[1];
            var fullPath = line[(tabIdx + 1)..].TrimEnd('\r', '\n', ' ');
            var isFolder = type == "tree";

            var lastSlash = fullPath.LastIndexOf('/');
            var parentDir = lastSlash >= 0 ? fullPath[..lastSlash] : "";
            var name = lastSlash >= 0 ? fullPath[(lastSlash + 1)..] : fullPath;

            if (!_dirTree.TryGetValue(parentDir, out var list))
            {
                list = [];
                _dirTree[parentDir] = list;
            }

            list.Add(new GitItem(name, "/" + fullPath, isFolder, Size: 0));
        }

        _logger.LogInformation("Directory tree ready: {Count} directories indexed", _dirTree.Count);
    }

    // -----------------------------------------------------------------
    //  Persistent cat-file --batch process
    // -----------------------------------------------------------------

    private readonly record struct BlobResult(long Size, byte[] Content);

    private async Task<BlobResult?> BatchGetBlobAsync(string objectSpec)
    {
        await _batchSemaphore.WaitAsync();
        try
        {
            EnsureBatchProcess();

            // Write query
            var query = Encoding.ASCII.GetBytes(objectSpec + "\n");
            await _batchIn!.WriteAsync(query);
            await _batchIn.FlushAsync();

            // Read header: "<sha> <type> <size>\n" or "<spec> missing\n"
            var header = await ReadLineAsync(_batchOut!);
            _logger.LogDebug("batch: {Header}", header);

            if (header.EndsWith(" missing", StringComparison.Ordinal))
            {
                _logger.LogWarning("Object not found: {Spec}", objectSpec);
                return null;
            }

            // Parse size from the last space-delimited token
            var lastSpace = header.LastIndexOf(' ');
            if (lastSpace < 0) return null;
            var size = long.Parse(header[(lastSpace + 1)..]);

            // Read exactly <size> bytes of content
            var content = new byte[size];
            var offset = 0;
            while (offset < size)
            {
                var n = await _batchOut!.ReadAsync(
                    content.AsMemory(offset, (int)Math.Min(size - offset, int.MaxValue)));
                if (n == 0)
                    throw new EndOfStreamException("Unexpected EOF from cat-file --batch");
                offset += n;
            }

            // Consume the trailing LF that separates entries
            var lf = new byte[1];
            await _batchOut!.ReadAsync(lf);

            return new BlobResult(size, content);
        }
        finally
        {
            _batchSemaphore.Release();
        }
    }

    /// <summary>Reads one line from a raw stream (up to and consuming the LF).</summary>
    private static async Task<string> ReadLineAsync(Stream stream)
    {
        var buf = new byte[1];
        var line = new List<byte>(128);
        while (true)
        {
            var n = await stream.ReadAsync(buf);
            if (n == 0) throw new EndOfStreamException();
            if (buf[0] == (byte)'\n') break;
            line.Add(buf[0]);
        }
        return Encoding.ASCII.GetString(line.ToArray());
    }

    private void EnsureBatchProcess()
    {
        if (_batchProcess is not null && !_batchProcess.HasExited)
            return;

        _logger.LogInformation("Starting persistent cat-file --batch process...");
        KillBatchProcess();

        _batchProcess = new Process
        {
            StartInfo = CreateStartInfo("cat-file --batch", _bareRepoDir),
        };
        _batchProcess.Start();
        _batchIn = _batchProcess.StandardInput.BaseStream;
        _batchOut = _batchProcess.StandardOutput.BaseStream;

        // Drain stderr in background to prevent pipe-buffer deadlock during promisor fetches
        _ = Task.Run(async () =>
        {
            try { await _batchProcess.StandardError.ReadToEndAsync(); }
            catch { /* process exited */ }
        });
    }

    private void KillBatchProcess()
    {
        if (_batchProcess is null) return;
        try
        {
            if (!_batchProcess.HasExited)
                _batchProcess.Kill(entireProcessTree: true);
        }
        catch { /* already exited */ }
        _batchProcess.Dispose();
        _batchProcess = null;
        _batchIn = null;
        _batchOut = null;
    }

    // -----------------------------------------------------------------
    //  Persistent cat-file --batch-check process (size-only queries)
    // -----------------------------------------------------------------

    private async Task<long?> BatchCheckSizeAsync(string objectSpec)
    {
        await _batchCheckSemaphore.WaitAsync();
        try
        {
            EnsureBatchCheckProcess();

            var query = Encoding.ASCII.GetBytes(objectSpec + "\n");
            await _batchCheckIn!.WriteAsync(query);
            await _batchCheckIn.FlushAsync();

            // Response: "<sha> <type> <size>\n" or "<spec> missing\n"
            var header = await ReadLineAsync(_batchCheckOut!);
            _logger.LogDebug("batch-check: {Header}", header);

            if (header.EndsWith(" missing", StringComparison.Ordinal))
            {
                _logger.LogWarning("Object not found: {Spec}", objectSpec);
                return null;
            }

            var lastSpace = header.LastIndexOf(' ');
            if (lastSpace < 0) return null;
            return long.Parse(header[(lastSpace + 1)..]);
        }
        finally
        {
            _batchCheckSemaphore.Release();
        }
    }

    private void EnsureBatchCheckProcess()
    {
        if (_batchCheckProcess is not null && !_batchCheckProcess.HasExited)
            return;

        _logger.LogInformation("Starting persistent cat-file --batch-check process...");
        KillBatchCheckProcess();

        _batchCheckProcess = new Process
        {
            StartInfo = CreateStartInfo("cat-file --batch-check", _bareRepoDir),
        };
        _batchCheckProcess.Start();
        _batchCheckIn = _batchCheckProcess.StandardInput.BaseStream;
        _batchCheckOut = _batchCheckProcess.StandardOutput.BaseStream;

        _ = Task.Run(async () =>
        {
            try { await _batchCheckProcess.StandardError.ReadToEndAsync(); }
            catch { /* process exited */ }
        });
    }

    private void KillBatchCheckProcess()
    {
        if (_batchCheckProcess is null) return;
        try
        {
            if (!_batchCheckProcess.HasExited)
                _batchCheckProcess.Kill(entireProcessTree: true);
        }
        catch { /* already exited */ }
        _batchCheckProcess.Dispose();
        _batchCheckProcess = null;
        _batchCheckIn = null;
        _batchCheckOut = null;
    }

    // -----------------------------------------------------------------
    //  One-off git process execution (clone, symbolic-ref, ls-tree, etc.)
    // -----------------------------------------------------------------

    private readonly record struct GitResult(bool Success, string Output, string Error);

    private static async Task<GitResult> RunGitOneOffAsync(
        string arguments,
        string workingDirectory,
        ILogger logger,
        CancellationToken ct,
        int timeoutSeconds = 120)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = CreateStartInfo(arguments, workingDirectory),
            };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stderr.AppendLine(e.Data);
                    if (IsProgressLine(e.Data))
                        logger.LogInformation("git: {Line}", e.Data);
                }
            };

            logger.LogDebug("Exec: git {Args}", arguments);
            process.Start();
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new GitResult(false, "",
                    timeout.IsCancellationRequested ? "Timed out" : "Cancelled");
            }

            if (process.ExitCode != 0)
            {
                logger.LogDebug("git exit {Code}: {Err}", process.ExitCode, stderr);
                return new GitResult(false, stdout.ToString(), stderr.ToString());
            }

            return new GitResult(true, stdout.ToString(), stderr.ToString());
        }
        catch (Exception ex)
        {
            return new GitResult(false, "", $"git execution error: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------
    //  Shared helpers
    // -----------------------------------------------------------------

    private static async Task EnsureGitVersionAsync(ILogger logger, CancellationToken ct)
    {
        var r = await RunGitOneOffAsync("--version", Directory.GetCurrentDirectory(), logger, ct, timeoutSeconds: 10);
        if (!r.Success)
            throw new InvalidOperationException(
                "Could not run 'git --version'. Ensure git is installed and on the PATH.");

        // Expected output: "git version 2.44.0.windows.1" or "git version 2.39.3 (Apple Git-146)"
        var raw = r.Output.Trim();
        var versionStr = raw.Replace("git version ", "");
        var dotParts = versionStr.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (dotParts.Length >= 2
            && int.TryParse(dotParts[0], out var major)
            && int.TryParse(dotParts[1], out var minor))
        {
            var installed = new Version(major, minor);
            logger.LogInformation("Git version: {Raw} (parsed {Major}.{Minor})", raw, major, minor);

            if (installed < MinGitVersion)
                throw new InvalidOperationException(
                    $"Git {MinGitVersion} or later is required (found {installed}). " +
                    "Partial clone with promisor remotes needs Git 2.22+.");
            return;
        }

        logger.LogWarning("Could not parse git version from: {Output}. Proceeding anyway.", raw);
    }

    private static string NormalizePath(string path) =>
        (path ?? "").Replace('\\', '/').Trim('/');

    private static ProcessStartInfo CreateStartInfo(string arguments, string workingDirectory) =>
        new()
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment =
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
            },
        };

    private static bool IsProgressLine(string line) =>
        line.Contains("Cloning") || line.Contains("remote:") ||
        line.Contains("Receiving") || line.Contains("Resolving") ||
        line.Contains("Filtering");
}
