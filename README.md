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
- Git available on the command line (found through `PATH`, see [Safety](#safety))
- A full clone of the repository: partial clones (`--filter=...`) are refused

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

# Refresh origin (git fetch --prune), then preview merged branches whose upstream was deleted on the remote.
gitsweep clean --fetch --gone-only --dry-run

# Same detection without touching the network: use the remote-tracking refs the repository already has.
gitsweep clean --gone-only

# Automation: prune-fetch origin, then delete every safe gone branch without prompting.
gitsweep clean --fetch --gone-only --all --yes
```

## Options

| Option | Description |
| --- | --- |
| `-p, --path` | Repository path. Defaults to the current directory. |
| `-a, --age` | Months after which a branch is stale. Defaults to `6`. |
| `--dry-run` | Show candidates without deleting branches. |
| `--merged-only` | Only include branches merged into the target branch. |
| `--stale-only` | Only include stale branches. |
| `--fetch` | Prune-fetch `origin` before scanning so branches whose upstream was deleted on the remote are detected as gone. Updates remote-tracking refs only; also runs with `--dry-run`. See [Gone branches](#gone-branches). |
| `--gone-only` | Only include safe gone branches: upstream gone and really merged into the target. Cannot be combined with `--merged-only` or `--stale-only`. |
| `--all` | Select every listed candidate without opening the picker. |
| `-y, --yes` | Skip final confirmation. Must be used with `--all`. |

## Gone branches

A branch is **gone** when it has a configured upstream whose remote-tracking ref no longer exists (Git reports it as `[gone]`). That typically happens when the branch was deleted on the remote and a `git fetch --prune` then removed the tracking ref. A **safe gone** branch is gone *and* really merged into the target branch (an ancestor, or a squash/rebase merge). Without a mode option, safe gone branches are listed together with merged and stale ones; `--gone-only` lists only safe gone branches. A gone branch with unmerged commits is never selected by `--gone-only` and is never marked **segura**. In the candidate table the reason reads `gone`, `gone (merged)` or `gone (squash-merged)`, and the risk table shows `gone` in its Upstream column.

By default GitSweep uses the remote-tracking refs the repository already has. `--fetch` refreshes them first by running (plus the fixed hardening options described under [Safety](#safety)):

```bash
git fetch --prune --no-prune-tags --no-tags --recurse-submodules=no --upload-pack=git-upload-pack --refmap= <origin-url> "+refs/heads/*:refs/remotes/origin/*"
```

- Only `origin` is fetched, and by its URL (from `git remote get-url origin`), never by remote name. The explicit refspec means only `refs/remotes/origin/*` is updated or pruned: local branches and tags are never created, moved or deleted, even if the repository's own `remote.origin.fetch`, `fetch.pruneTags` (or a `[remote "<url>"]` section) says otherwise. `--refmap=`, `--no-prune-tags` and the pinned `--upload-pack` neutralize those settings.
- The fetch fails closed when `origin` is missing or its URL starts with `-`, and when the repository's configuration sets `http.<url>.saveCookies` to true (that would let the remote write a cookie file at a path the repository chooses). It has a 5-minute deadline. Git itself never prompts: credential helpers, askpass and proxy commands are disabled, and only the standard `https`, `http`, `ssh`, `git`, `file` and local-path transports are allowed, whatever the repository's `protocol.*` settings say. A remote that needs HTTPS credentials from a helper therefore fails. SSH uses plain `ssh` with your `~/.ssh` configuration; `ssh` itself may still ask on the terminal for a passphrase or a host-key confirmation, bounded by the deadline. In that case run `git fetch --prune` yourself, but only in a repository whose configuration you trust (GitSweep's hardening does not apply to a fetch you run yourself, and a plain `git fetch --prune` honors every setting listed above), and use `clean` without `--fetch`.
- If the fetch fails for any reason, `clean` stops with exit code 1 (`Fetch with prune failed; refusing to detect gone branches from stale remote state: ...`) and scans and deletes nothing. Credentials embedded in the remote URL are redacted from that message.
- `--fetch` also runs with `--dry-run`: it only updates remote-tracking refs.

With `--gone-only`, GitSweep re-scans immediately before deleting and again before each branch. A branch that stopped being a safe gone candidate in the meantime (upstream restored, new unique commits, merge state changed) is skipped and reported as a failure instead of being deleted.

## Branch protection (`.gitsweep.json`)

Protected branches are filtered out before any analysis: they are never listed as candidates and never deleted. These are always protected:

- `main` and `master` (case-insensitive). They are built in; `.gitsweep.json` can add protection but can never remove it.
- The current branch and any branch checked out in a worktree. The scan skips them and the delete step refuses them again, so no `.gitsweep.json` setting can make them deletable. The target branch (the one merges are measured against) is never a candidate either.
- Any branch matching a pattern in `protectedBranches`.

To add your own patterns, create `.gitsweep.json` in the root of the working tree (the directory printed by `git rev-parse --show-toplevel`). All keys are optional and `{}` is a valid file:

```json
{
  "protectedBranches": ["develop", "release/*", "hotfix/*", "keep-?"],
  "excludeCurrentBranch": true,
  "excludeWorktreeBranches": true
}
```

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `protectedBranches` | array of strings | `[]` | Extra glob patterns for branches that are never deleted. At most 100 patterns. `main` and `master` are protected on top of these. |
| `excludeCurrentBranch` | boolean | `true` | Treat the current branch as protected. GitSweep never deletes it even when this is `false`: the scan skips it and the delete step refuses it. |
| `excludeWorktreeBranches` | boolean | `true` | Treat branches checked out in a worktree as protected. GitSweep never deletes them even when this is `false`: the scan skips them and the delete step refuses them. |

Key names are matched case-insensitively; use the camelCase spelling shown. Without a `.gitsweep.json` file GitSweep uses the defaults above (no extra patterns, both flags `true`).

**Glob syntax.** Patterns are matched case-insensitively against the whole short branch name (for example `feature/login`, not `refs/heads/feature/login`).

| Syntax | Meaning |
| --- | --- |
| `*` | Any sequence of characters, including `/` and the empty sequence. `release/*` matches `release/1.0` and `release/1.0/rc1`; a lone `*` protects every branch. |
| `?` | Exactly one character. |
| anything else | Matched literally. |

There are no character classes, escapes or negation. A pattern must be non-empty, at most 255 characters, free of whitespace and control characters, must not contain any of `~ ^ : [ ] \`, and must not contain `..` or `@{`.

**Invalid configuration fails closed.** `clean` (including `--dry-run`) refuses to run, deletes nothing and exits with code 1 (`Error: Invalid .gitsweep.json protection configuration; refusing to clean: ...`) when the file meets one of the cases below. The message names the kind of problem (not valid JSON, unknown property, repeated property, value of the wrong type, invalid pattern) and, where it applies, the line, without quoting the file's content:

- is empty, is `null`, is not valid JSON (comments and trailing commas are not allowed) or has a value of the wrong type;
- contains any key other than the three above, including typos such as `protected_branches` and `$schema`, or repeats a key (spellings that differ only in case count as repeats);
- declares more than 100 patterns or an invalid pattern;
- is larger than 64 KiB, is a symbolic link, or cannot be read.

**In the output.** After the scan summary, protected branches that were found appear in a "Protection (.gitsweep.json)" panel with the columns Branch, Reason (`GlobPattern` for built-in and configured patterns) and Pattern (the pattern that matched, for example `main` or `release/*`). If nothing was excluded, GitSweep prints `Protection: no branches excluded by .gitsweep.json, current-branch or worktree rules.` Protection is checked again right before each deletion; a branch that became protected since the scan is reported as `Branch is protected (<reason>); it was not deleted.`

## How It Works

- `GitSweep.Cli` hosts the Spectre.Console CLI and interactive TUI.
- `GitSweep.Core` owns contracts and models such as `BranchInfo`, `IBranchAnalyzer`, and `IGitRepositoryService`.
- `GitSweep.Infrastructure` runs Git commands and keeps command-line process details out of the UI layer.
- Protected branches (built-in `main`/`master`, `.gitsweep.json` patterns, the current branch and worktree branches) and the target branch are removed first and never become candidates. See [Branch protection](#branch-protection-gitsweepjson).
- Merged branches are detected with `git branch --merged <target>`, plus a local squash/rebase check (`git cherry` against the target) for merges that never make the branch an ancestor.
- Stale branches are branches whose last commit is older than the configured age and are not already classified as merged.
- Gone branches are branches whose upstream no longer exists on the remote; a safe gone branch is also really merged into the target. `--fetch` refreshes that state first. See [Gone branches](#gone-branches).
- Every candidate also gets a risk traffic light (semáforo) in a Spectre table: **segura** only when the branch is really merged into the target (ancestor or squash/rebase) with no unique commits left; **peligrosa** when it has unique commits that are not merged and not fully pushed (no upstream, or commits ahead of its upstream); **revisar** for everything else, including unknown data — GitSweep never marks a branch segura when a count could not be measured. Unique commits are counted by patch equivalence (`git cherry`) against the target, and unpushed commits against the branch upstream.

GitSweep deletes branches only with safe local deletes (`git branch -d`, never `-D`), and a branch is deleted only if two independent checks pass:

1. **Reachability (GitSweep's own check).** Before running `git branch -d`, GitSweep requires the branch tip to be contained in another local branch, remote-tracking branch or tag. GitSweep's own keep-refs and symbolic refs such as `origin/HEAD` do not count. `git branch -d` alone is not enough, because it trusts the branch's configured upstream, which comes from repository config and can point a branch at itself. If no other ref contains the tip, or Git gives no valid answer, GitSweep refuses with `Branch '<name>' has commits that are not reachable from any other branch, remote-tracking branch or tag; it was not deleted.` (or a `Could not verify ...` message) and leaves the branch alone.
2. **Git's merged check.** `git branch -d` applies its own check. If Git refuses (for example because the branch is not fully merged), GitSweep reports Git's message and leaves the branch intact.

The reachability check is repeated after the delete; if it no longer holds, the branch is recreated at its original tip (never with `-f`) and reported as not deleted.

Before any deletion, GitSweep captures the branch tip SHA, pins it with a keep-ref `refs/gitsweep/trash/<id>` (so `git gc` cannot prune its commits) and stores it as a JSON file under `.git/gitsweep/trash` (one file per branch, named after the same GUID `<id>`, grouped by a per-run batch id). If the SHA cannot be captured, the keep-ref cannot be created and verified (for example because `refs/gitsweep` in the git directory is a symlink or junction), or the trash entry cannot be written, the branch is not deleted. `gitsweep undo` restores every branch from the most recent batch, and `gitsweep restore` lists the trash and recreates a chosen branch (or `--all`) at exactly its saved SHA with plain `git branch <name> <sha>^{commit}` (a ref or tag named like the SHA cannot redirect it). Existing branches are never overwritten, and a trash entry (together with its keep-ref) is removed only after GitSweep has checked that the branch exists again and points at the saved SHA; otherwise the restore is reported as failed and both are kept.

- If the branch already exists at the saved commit (for example `git checkout <name>` recreated it from its remote branch), `restore`/`undo` count it as restored and drop the trash entry and its keep-ref.
- If it exists at another commit, nothing is changed and the entry is kept; the message shows the saved SHA, so you can rename the existing branch and restore again, or run `git branch <new-name> "<sha>^{commit}"` (the `^{commit}` suffix, quoted for your shell, stops git resolving the SHA through a tag or ref of the same name).
- A branch whose name differs from the trashed one only in letter case is never overwritten either: the restore is refused, because on a case-insensitive file system (Windows, macOS) Git could not keep the two apart.

Keep-refs are ordinary Git refs. While a branch sits in the trash its keep-ref is listed by `git for-each-ref refs/gitsweep/`, is included by commands that walk every ref (`git log --all`, `git gc`), and would be pushed to the remote by `git push --mirror` (a normal push of branches does not). It disappears on `restore` or `undo`, or when a failed delete is rolled back (removal is best effort; a leftover keep-ref only keeps a commit alive).

**Retention.** GitSweep never expires the trash: every trash entry and its keep-ref stay until you restore the branch, so their commits are never reclaimed by `git gc` and remain visible to `git log --all` and `git push --mirror`. To empty the trash for good (the deleted branches can then no longer be restored, and Git prunes their commits on its next `gc`), run the following in the repository, and only in one whose configuration you trust (these are plain git commands, not GitSweep's hardened ones: `update-ref` fires the repository's reference-transaction hooks):

```bash
rm -f "$(git rev-parse --git-common-dir)"/gitsweep/trash/*.json
git for-each-ref --format='delete %(refname)' refs/gitsweep/trash/ | git update-ref --stdin
```

### Safety

A cloned repository can carry hostile configuration, ref names, remotes or a `.gitsweep.json`. GitSweep overrides every repository setting it knows can make one of the Git commands it runs execute a program, write outside `refs/remotes/origin/*` during `--fetch`, or fake the history its checks rely on (the list is below). That is defense in depth, not a sandbox: Git has many settings and new versions add more, so treat a repository whose `.git` directory you do not trust with care, and prefer `clean` without `--fetch` there. Besides the checks above:

- **Partial clones are refused.** `clean` (including `--dry-run` and `--fetch`), `restore` and `undo` stop with exit code 1 when the repository is a partial clone (`--filter=...`, a promisor remote, `extensions.partialClone`) or when GitSweep cannot tell. In a partial clone, reading an object that is not present makes Git fetch it and run the program configured in `remote.<name>.uploadpack`. Use a full clone.
- **Unknown, duplicate or invalid `.gitsweep.json` content makes `clean` refuse to run** (see [Branch protection](#branch-protection-gitsweepjson)). The file can only add protection.
- **Git is resolved from `PATH` as an absolute path.** Only fully qualified `PATH` entries are considered, never the current directory, so a `git.exe` planted in a cloned repository is not run (on Windows only `git.exe` is accepted). GitSweep stops if Git is not found, and the Git child process gets the same filtered `PATH`. The npm launcher does the same for `dotnet`: it looks in `DOTNET_ROOT` first, then in fully qualified `PATH` entries, and never falls back to the bare name.
- **Git runs hardened, with deadlines and output caps.** No shell is involved (arguments are passed as a list). The hook directory (`core.hooksPath`) is pointed at an empty location, so no hook there runs. Hooks defined in configuration (`hook.<name>.command`, introduced in git 2.54) are not governed by `core.hooksPath`, so GitSweep refuses to run whenever the effective configuration (the repository, its includes, and your global and system config) binds such a hook to an event its own commands could fire (for example `reference-transaction`, or any event not on a conservative allow-list of events GitSweep never triggers). This check is deny-by-default and applies on every git version, including those older than 2.54 where the keys are inert; a configured hook for an unrelated event (a commit or checkout hook you keep in your global config) is left alone. Repository-configured programs are overridden: fsmonitor, pager, commit signing, ssh command, askpass and credential helpers (including URL-scoped ones), the `git://` proxy command, the alternate-refs command, auto-gc/maintenance, remote helpers (only the standard transports are allowed, even if the repository allows another one) and the `ext::` transport. A repository's bundle URI is never fetched. Replace refs (`refs/replace/`) and the grafts file (`info/grafts`) are ignored, so they cannot make a branch look merged or reachable. Git's commit-graph cache (`objects/info/commit-graph`) is still used for speed; a branch that a corrupted cache made look merged would still be pinned by its keep-ref, so `undo` restores it. Inherited `GIT_DIR` and `GIT_CONFIG_*` style variables are removed. Local Git commands have a 2-minute deadline and `git fetch` 5 minutes; stdout is capped at 64 Mi characters and stderr at 1 Mi characters. When a deadline or cap is hit, the Git process tree is killed and the operation fails closed: truncated output is never used as an answer, and a question Git did not answer is never read as "safe to delete".
- **Terminal output is sanitized.** Text that comes from the repository, a remote or the trash (branch, upstream and target names, paths, Git output including `remote:` messages, error messages) is made visible before it is displayed: control characters become `\xNN` (ESC is shown as `\x1B`) and invisible or bidirectional formatting characters become `\uXXXX`. Credentials in remote URLs (`user:token@`), token-like query parameters and `Authorization` headers are redacted from Git error messages.

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
