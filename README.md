# GitSweep

Command-line tool to analyze and clean local Git branches that are stale or already merged.

## Requirements
- .NET 10 SDK
- Git available on the command line

## Quick start
```bash
# Analyze the current repository with the default stale age (6 months)
dotnet run --project GitSweep.Cli -- clean

# Analyze another repo path and mark branches stale after 3 months
dotnet run --project GitSweep.Cli -- clean -p ./path/to/repo -a 3
```

## Usage examples
```bash
# List candidate branches without deleting any (cancel at confirmation)
dotnet run --project GitSweep.Cli -- clean -p C:/code/my-repo

# Clean branches in a repo and select none (no deletions)
dotnet run --project GitSweep.Cli -- clean -p C:/code/my-repo --age 12

# Use a relative path and default age
dotnet run --project GitSweep.Cli -- clean -p ../other-repo
```

## How it works
- `IGitRepositoryService` inspects the repository, lists local branches with last commit date, and detects merge status.
- `IBranchAnalyzer` determines which branches are merged and which are stale based on the provided age.
- `CleanCommand` presents candidate branches and lets you choose which ones to delete interactively.

## Tests
Run all unit and integration tests:
```bash
dotnet test
```

Test projects use `xunit`, `Microsoft.NET.Test.Sdk`, and `coverlet.collector` for coverage output.

## Project structure
- `GitSweep.Core`: contracts and models (`BranchInfo`, `IBranchAnalyzer`, `IGitRepositoryService`).
- `GitSweep.Infrastructure`: service implementations (`BranchAnalyzer`, `GitRepositoryService`).
- `GitSweep.Cli`: Spectre.Console CLI for interactive cleanup.
- `tests/GitSweep.Core.Tests`: unit tests for the branch analyzer.
- `tests/GitSweep.Infrastructure.Tests`: integration tests for the Git service (requires `git` available locally).