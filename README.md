# WinGitFS

A Windows application that virtualizes remote Git repositories (Azure DevOps and GitHub) as a local read-only file system using Windows Projected File System (ProjFS).

## Overview

WinGitFS projects a remote Git repository as a local folder. It uses **git.exe** only: a one-time blobless clone fetches tree metadata, an in-memory directory tree is built from a single `git ls-tree` call, and file content is served on demand via a persistent `git cat-file --batch` process. There are no REST API calls and no per-file process spawns - directory listings are instant and file content is streamed from the batch process.

## Features

- **Azure DevOps and GitHub** - Works with both; URL is parsed to build the appropriate clone URL
- **On-demand file content** - Blobs are fetched only when a file is opened (via batch process)
- **Instant directory browsing** - Full tree is built in memory at startup
- **Authentication** - PAT via `--pat` (embedded in clone URL); otherwise Git Credential Manager is used (e.g. SSO for ADO)
- **Branch selection** - `--branch` or branch/path in URL (e.g. GitHub `.../tree/branch/path`, ADO `?version=GBbranch`)
- **Path filtering** - Virtualize a subfolder via URL (e.g. ADO `?path=/Apps/MyApp`, GitHub `.../tree/main/Apps/MyApp`)
- **Caching** - In-memory directory tree and file content cache in the provider

## Requirements

- Windows 10/11 (64-bit)
- .NET 8.0 Runtime
- **git** 2.22 or later in PATH (used for clone, ls-tree, and cat-file --batch; partial clone requires 2.22+)

## Usage

```bash
WinGitFS.exe <repository-url> [path] [options]
```

The local path can be given as the second positional argument or via `--path`. If both are omitted, a temp folder is used.

### Examples

```bash
# Azure DevOps with PAT
WinGitFS.exe https://dev.azure.com/org/Project/_git/Repo --pat <token>

# Azure DevOps with Git Credential Manager (e.g. SSO)
WinGitFS.exe https://dev.azure.com/org/Project/_git/Repo

# GitHub with PAT
WinGitFS.exe https://github.com/owner/repo --pat <token>

# Specific branch and subfolder (ADO: query string)
WinGitFS.exe "https://dev.azure.com/org/Project/_git/Repo?path=/Apps/MyApp&version=GBmain" --pat <token>

# Specific branch and subfolder (GitHub: path in URL)
WinGitFS.exe https://github.com/owner/repo/tree/main/Apps/MyApp --pat <token>

# Custom local path (second positional or --path)
WinGitFS.exe https://github.com/owner/repo C:\MyGitFS
WinGitFS.exe https://github.com/owner/repo --path C:\MyGitFS
```

### Options

- `path` (positional) or `--path` - Local folder to use as the virtualization root (created if needed). If omitted, a temp folder is created and opened in Explorer; it is deleted on exit.
- `--pat` - Personal Access Token; injected into the clone URL for authentication. If omitted, git uses Git Credential Manager (e.g. browser SSO for ADO).
- `--branch` - Branch to map (defaults to the repository default branch).

Press Enter or Ctrl+C in the console to stop; the process exits and any auto-created temp folder is cleaned up.

## Building

```bash
dotnet build WinGitFS.sln
dotnet publish WinGitFS/WinGitFS.csproj -p:PublishProfile=FolderProfile
```

Or use the VS Code build task (Ctrl+Shift+B).

## License

See LICENSE file.
