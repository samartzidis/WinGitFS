# WinGitFS

A Windows application that virtualizes Git repositories (Azure DevOps and GitHub) as a local file system using Windows Projected File System (ProjFS).

## Overview

WinGitFS creates a read-only virtual file system that maps a remote Git repository branch to a local folder. Files and directories are fetched on-demand from the Git API, allowing you to browse and access repository contents without cloning.

## Features

- **Azure DevOps & GitHub support** - Works with both hosting providers
- **On-demand file access** - Files are downloaded only when accessed
- **Authentication** - Supports PAT tokens and Azure AD SSO (ADO)
- **Branch selection** - Map any branch or use the default
- **Path filtering** - Virtualize a specific subdirectory
- **Caching** - Configurable directory and file content caching

## Requirements

- Windows 10/11 (64-bit)
- .NET 8.0 Runtime

## Usage

```bash
WinGitFS.exe <repository-url> [options]
```

### Examples

```bash
# Azure DevOps with SSO
WinGitFS.exe https://dev.azure.com/org/Project/_git/Repo --sso

# GitHub with PAT
WinGitFS.exe https://github.com/owner/repo --pat <token>

# Specific branch and path
WinGitFS.exe https://dev.azure.com/org/Project/_git/Repo?path=/Apps/MyApp&version=GBmain --sso

# Custom local path
WinGitFS.exe https://github.com/owner/repo --local-path C:\MyGitFS
```

### Options

- `--local-path` - Local folder path (creates temp folder if not specified)
- `--pat` - Personal Access Token for authentication
- `--sso` - Use Azure AD SSO authentication (Azure DevOps only)
- `--branch` - Branch to map (defaults to repository's default branch)
- `--dirCacheTtlSeconds` - Directory cache TTL in seconds (default: 30)
- `--fileCacheSizeMb` - File content cache size in MB (default: 256)

## Building

```bash
dotnet build WinGitFS.sln
dotnet publish WinGitFS/WinGitFS.csproj -p:PublishProfile=FolderProfile
```

## License

See LICENSE file.
