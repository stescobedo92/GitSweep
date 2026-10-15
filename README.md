# GitSweep

[![Build](https://github.com/stescobedo92/GitSweep/actions/workflows/ci.yml/badge.svg)](https://github.com/stescobedo92/GitSweep/actions/workflows/ci.yml)
[![Release](https://github.com/stescobedo92/GitSweep/actions/workflows/release.yml/badge.svg)](https://github.com/stescobedo92/GitSweep/actions/workflows/release.yml)
[![NuGet Version](https://img.shields.io/nuget/v/GitSweep.Cli.svg)](https://www.nuget.org/packages/GitSweep.Cli)
[![NuGet Downloads](https://img.shields.io/nuget/dt/GitSweep.Cli.svg)](https://www.nuget.org/packages/GitSweep.Cli)
[![License](https://img.shields.io/github/license/stescobedo92/GitSweep.svg)](LICENSE.txt)

![GitSweep icon](assets/binarycoffee.png)

GitSweep is an interactive .NET CLI for finding and cleaning local Git branches that are already merged or stale. It uses Spectre.Console for a colorful TUI with scan summaries, candidate tables, confirmation prompts, progress bars, and readable error messages.

## Requirements

- .NET 10 SDK for local development
- Git available on the command line

## Install

```bash
dotnet tool install --global GitSweep.Cli
```

Or install the npm wrapper:

```bash
npm install --global @stescobedo9205/gitsweep-cli
```

Update an existing install:

```bash
dotnet tool update --global GitSweep.Cli
npm update --global @stescobedo9205/gitsweep-cli
```

## Quick Start

```bash
# Analyze the current repository, then choose branches interactively.
gitsweep

# Same command through the explicit subcommand.
gitsweep clean

# Preview candidates without deleting anything.
gitsweep clean --dry-run
```

## Usage Examples

```bash
# Analyze another repository and consider branches stale after 3 months.
gitsweep clean -p C:/code/my-repo -a 3

# Show only branches already merged into the default branch.
gitsweep clean -p C:/code/my-repo --merged-only

# Show only stale branches older than 12 months.
gitsweep clean -p C:/code/my-repo --stale-only --age 12

# Automation-friendly cleanup: select all merged branches and skip confirmation.
gitsweep clean --merged-only --all --yes

# Preview what an automation run would delete.
gitsweep clean --stale-only --age 6 --all --dry-run

# Undo the most recent clean run (restores its whole deletion batch).
gitsweep undo

# List trashed branches and restore one (or all) at its saved SHA.
gitsweep restore --branch feature/login
gitsweep restore --all --yes

# Use a relative path and default stale age.
gitsweep clean -p ../other-repo
```

## Options

| Option | Description |
| --- | --- |
| `-p, --path` | Repository path. Defaults to the current directory. |
| `-a, --age` | Months after which a branch is stale. Defaults to `6`. |
| `--dry-run` | Show candidates without deleting branches. |
| `--merged-only` | Only include branches merged into the target branch. |
| `--stale-only` | Only include stale branches. |
| `--all` | Select every listed candidate without opening the picker. |
| `-y, --yes` | Skip final confirmation. Must be used with `--all`. |

## How It Works

- `GitSweep.Cli` hosts the Spectre.Console CLI and interactive TUI.
- `GitSweep.Core` owns contracts and models such as `BranchInfo`, `IBranchAnalyzer`, and `IGitRepositoryService`.
- `GitSweep.Infrastructure` runs Git commands and keeps command-line process details out of the UI layer.
- Merged branches are detected with `git branch --merged <target>`.
- Stale branches are branches whose last commit is older than the configured age and are not already classified as merged.
- Every candidate also gets a risk traffic light (semáforo) in a Spectre table: **segura** only when the branch is really merged into the target (ancestor or squash/rebase) with no unique commits left; **peligrosa** when it has unique commits that are not merged and not fully pushed (no upstream, or commits ahead of its upstream); **revisar** for everything else, including unknown data — GitSweep never marks a branch segura when a count could not be measured. Unique commits are counted by patch equivalence (`git cherry`) against the target, and unpushed commits against the branch upstream.

GitSweep deletes branches with safe local deletes (`git branch -d`). If Git refuses because a branch is not fully merged, GitSweep reports the failure and leaves the branch intact.

Before any deletion, GitSweep captures the branch tip SHA and stores it as a JSON file under `.git/gitsweep/trash` (one GUID-named file per branch, grouped by a per-run batch id). If the SHA cannot be captured or the trash entry cannot be written, the branch is not deleted. `gitsweep undo` restores every branch from the most recent batch, and `gitsweep restore` lists the trash and recreates a chosen branch (or `--all`) at exactly its saved SHA with plain `git branch` — existing branches are never overwritten, and a trash entry is removed only after its branch exists again.

## Development

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project GitSweep.Cli -- clean --dry-run
```

Pack the CLI tool locally:

```bash
dotnet pack GitSweep.Cli/GitSweep.Cli.csproj -c Release -p:Version=0.0.0-local
```

## Releases

Releases are automated with Release Please and Conventional Commits:

- `fix:` creates a patch release.
- `feat:` creates a minor release.
- `feat!:` or `BREAKING CHANGE:` creates a major release.

When changes land on `master`, GitHub Actions calculates the next SemVer, packs `GitSweep.Cli` as a .NET tool, publishes it to nuget.org, publishes `@stescobedo9205/gitsweep-cli` to npm, and creates the GitHub release. The workflow currently uses the `NUGET_ORG_NEW_API_KEY` secret for both registry publishes.
