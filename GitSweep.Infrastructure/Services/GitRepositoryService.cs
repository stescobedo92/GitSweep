using GitSweep.Core.Models;
using GitSweep.Core.Services;
using System.Diagnostics;

namespace GitSweep.Infrastructure.Services;

public class GitRepositoryService : IGitRepositoryService
{
    private const string SyntheticCommitMessage = "GitSweep squash-merge detection";

    // Identity used only so `git commit-tree` works in repositories without a
    // configured user. It never appears in refs, logs or output: the synthetic
    // commit is left dangling for git's garbage collector.
    private static readonly IReadOnlyDictionary<string, string> SyntheticCommitIdentity =
        new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "GitSweep",
            ["GIT_AUTHOR_EMAIL"] = "gitsweep@localhost",
            ["GIT_COMMITTER_NAME"] = "GitSweep",
            ["GIT_COMMITTER_EMAIL"] = "gitsweep@localhost"
        };

    // Hooks directory that can never contain an executable hook: the null
    // device on Windows, /dev/null elsewhere. Pointing core.hooksPath at a
    // file (or nonexistent path) makes git look for "<path>/<hook>", which
    // never exists, so no hook is ever run.
    private static readonly string NullHooksPath =
        OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    // Configuration forced on every git invocation. Command-line `-c` values
    // override repository config, so a hostile cloned repository cannot turn
    // a read-only scan into code execution or an interactive hang through:
    //  - core.fsmonitor: a repo-configured fsmonitor helper program,
    //  - core.pager: a configured pager program (output is piped anyway),
    //  - commit.gpgsign: a repo-configured signing program invoked while
    //    creating the synthetic squash-detection commit,
    //  - gc.auto / maintenance.auto: auto-gc and auto-maintenance run the
    //    repo's `pre-auto-gc` hook, i.e. code shipped inside a cloned repo,
    //  - core.hooksPath: ref updates (branch create/delete in clean,
    //    restore and undo) fire the repo's `reference-transaction` hook,
    //    i.e. code shipped inside a cloned repo, so hooks are pointed at a
    //    location that can never hold an executable hook,
    //  - transport helpers reachable through `fetch --prune`: a cloned repo
    //    can name an arbitrary local program in `core.sshCommand`,
    //    `core.gitProxy`, `core.askPass` or `credential.helper`, the `ext::`
    //    transport runs a shell command by design, and each of those is
    //    executed locally as soon as git talks to the remote. SSH is pinned
    //    to the plain `ssh` binary (the user's ~/.ssh config still applies),
    //    the helper slots are emptied so no config-chosen program runs, and
    //    `ext` is refused outright; with GIT_TERMINAL_PROMPT=0 a fetch that
    //    would need interactive credentials then fails closed instead of
    //    executing code or prompting. The protocol allowlist below refuses
    //    every transport that is not a standard one: without it, a remote
    //    URL such as `foo::bar` (or any `<scheme>::`/`scheme://` URL whose
    //    scheme is not built in) makes git execute a `git-remote-<scheme>`
    //    program found on PATH, so repository config alone could pick a
    //    local program to run. Only https/http/ssh/git/file/local remain.
    private static readonly string[] HardenedGitOptions =
    [
        "--no-pager",
        "-c", "core.fsmonitor=false",
        "-c", "core.pager=cat",
        "-c", "commit.gpgsign=false",
        "-c", "gc.auto=0",
        "-c", "maintenance.auto=false",
        "-c", $"core.hooksPath={NullHooksPath}",
        "-c", "protocol.allow=never",
        "-c", "protocol.https.allow=always",
        "-c", "protocol.http.allow=always",
        "-c", "protocol.ssh.allow=always",
        "-c", "protocol.git.allow=always",
        "-c", "protocol.file.allow=always",
        "-c", "protocol.local.allow=always",
        "-c", "protocol.ext.allow=never",
        "-c", "core.sshCommand=ssh",
        "-c", "core.gitProxy=",
        "-c", "core.askPass=",
        "-c", "credential.helper="
    ];

    // Ambient git environment variables removed from every child process so
    // results depend only on the repository path the user selected, never on
    // a GIT_DIR/GIT_WORK_TREE inherited from the caller's environment (which
    // could silently redirect even branch deletion to another repository).
    // GIT_CONFIG_* entries are removed by prefix in ExecuteGitCommandAsync:
    // they inject configuration at command-line precedence (GIT_CONFIG_COUNT/
    // KEY/VALUE, GIT_CONFIG_PARAMETERS) or redirect the global/system config
    // files (GIT_CONFIG_GLOBAL/SYSTEM), either of which could smuggle in a
    // helper program such as core.fsmonitor or a per-command pager.* that
    // `-c core.pager` does not cover. Transport/askpass/diff helpers are
    // removed for the same reason even though no command here performs
    // transport or an external diff: fail closed if one ever does.
    private static readonly string[] RemovedGitEnvironmentVariables =
    [
        "GIT_DIR",
        "GIT_COMMON_DIR",
        "GIT_WORK_TREE",
        "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_NAMESPACE",
        "GIT_QUARANTINE_PATH",
        "GIT_SSH",
        "GIT_SSH_COMMAND",
        "GIT_ASKPASS",
        "SSH_ASKPASS",
        "GIT_EXTERNAL_DIFF",
        "GIT_ALLOW_PROTOCOL",
        "GIT_PROTOCOL_FROM_USER"
    ];

    public async Task<bool> IsGitRepositoryAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        var result = await ExecuteGitCommandAsync(path, ["rev-parse", "--git-dir"], ct);
        return result.ExitCode == 0;
    }

    public async Task<string> GetDefaultBranchNameAsync(string path, CancellationToken ct = default)
    {
        // First, try to get the remote default branch
        var remoteResult = await ExecuteGitCommandAsync(path, ["symbolic-ref", "refs/remotes/origin/HEAD"], ct);
        if (remoteResult.ExitCode == 0)
        {
            return remoteResult.Output.Replace("refs/remotes/origin/", string.Empty).Trim();
        }

        // Fallback to local HEAD
        var localResult = await ExecuteGitCommandAsync(path, ["symbolic-ref", "--short", "HEAD"], ct);
        return localResult.ExitCode == 0 ? localResult.Output.Trim() : "main";
    }

    public async Task<List<BranchInfo>> GetLocalBranchesAsync(string path, string targetBranch, CancellationToken ct = default)
    {
        if (!IsValidBranchName(targetBranch))
        {
            throw new ArgumentException("Target branch name is invalid.", nameof(targetBranch));
        }

        var branches = new List<BranchInfo>();

        // Nunca candidatas: la rama actual ni las que están en worktrees
        // (git rechazaría borrarlas, pero excluirlas aquí evita ofrecerlas).
        var currentBranch = await GetCurrentBranchAsync(path, ct);
        var worktreeBranches = await GetWorktreeBranchesAsync(path, ct);
        var neverCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(currentBranch)) neverCandidates.Add(currentBranch);
        foreach (var worktreeBranch in worktreeBranches) neverCandidates.Add(worktreeBranch);

        // Get merged branches. The target is qualified as refs/heads/<name>:
        // a bare name would resolve a same-named tag before the branch
        // (gitrevisions order), which could misreport what is merged.
        var mergedResult = await ExecuteGitCommandAsync(path, ["branch", "--merged", $"refs/heads/{targetBranch}"], ct);
        var mergedBranchNames = ParseBranchList(mergedResult.Output);

        // Get all local branches with their last commit date.
        // %(refname:strip=2) always yields the plain branch name; the more
        // common %(refname:short) would return "heads/<name>" when a tag
        // shares the name, which would then fail to resolve as a branch.
        // %(upstream:track) is "[gone]" when the upstream is configured but
        // its remote-tracking ref is gone (after `git fetch --prune` removed
        // it); %(upstream:short) is empty when there is no upstream at all.
        // Fields are TAB-separated: a branch or upstream name may legally
        // contain '|' (git ref format allows it), and a printable delimiter
        // inside a name would let one field forge another (for example a
        // fake "[gone]" track). Ref names can never contain control
        // characters, so TAB cannot be smuggled in through a name.
        var allBranchesResult = await ExecuteGitCommandAsync(
            path,
            ["for-each-ref", "--sort=-committerdate", "--format=%(refname:strip=2)\t%(committerdate:iso8601)\t%(upstream:short)\t%(upstream:track)", "refs/heads/"],
            ct
        );

        foreach (var line in allBranchesResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 2) continue;

            var name = parts[0];
            if (!IsValidBranchName(name)) continue;

            var date = DateTime.TryParse(parts[1], out var commitDate) ? commitDate : (DateTime?)null;
            var upstreamName = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;
            var upstreamTrack = parts.Length > 3 ? parts[3] : string.Empty;
            var isUpstreamGone = upstreamName is not null && IsGoneTrack(upstreamTrack);

            // Skip the target branch itself
            if (name.Equals(targetBranch, StringComparison.OrdinalIgnoreCase)) continue;
            if (neverCandidates.Contains(name)) continue;

            if (mergedBranchNames.Contains(name))
            {
                branches.Add(new BranchInfo(name, date, true, BranchMergeKind.Ancestor, upstreamName, isUpstreamGone));
                continue;
            }

            // `git branch --merged` misses squash/rebase merges because the
            // branch tip never becomes an ancestor of the target. Detect
            // those locally (merge-base + commit-tree / cherry).
            var isSquashOrRebaseMerged = await IsSquashOrRebaseMergedAsync(path, targetBranch, name, ct);
            branches.Add(new BranchInfo(
                name,
                date,
                false,
                isSquashOrRebaseMerged ? BranchMergeKind.SquashOrRebase : BranchMergeKind.NotMerged,
                upstreamName,
                isUpstreamGone));
        }

        return branches;
    }

    public async Task<(bool Success, string ErrorMessage)> DeleteBranchAsync(string path, string branchName, CancellationToken ct = default)
    {
        if (!IsValidBranchName(branchName))
        {
            return (false, $"Invalid branch name: '{branchName}'.");
        }

        // Defensa en profundidad: nunca borrar la rama actual ni una rama en
        // un worktree, aunque el llamador no haya filtrado las candidatas.
        var currentBranch = await GetCurrentBranchAsync(path, ct);
        if (!string.IsNullOrWhiteSpace(currentBranch) &&
            string.Equals(currentBranch, branchName, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Branch '{branchName}' is the current branch and cannot be deleted.");
        }

        var worktreeBranches = await GetWorktreeBranchesAsync(path, ct);
        if (worktreeBranches.Any(b => string.Equals(b, branchName, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, $"Branch '{branchName}' is checked out in a worktree and cannot be deleted.");
        }

        // Safe delete only: never force-delete with -D. A branch git considers
        // unmerged is refused here and reported to the caller instead.
        var result = await ExecuteGitCommandAsync(path, ["branch", "-d", "--", branchName], ct);
        return (result.ExitCode == 0, GitOutputRedactor.Redact(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error));
    }

    public async Task<(bool Success, string ErrorMessage)> FetchPruneAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
        {
            return (false, "Repository path does not exist.");
        }

        ct.ThrowIfCancellationRequested();

        // Resolve the origin URL and fetch from the URL itself, never from
        // the remote name: `git fetch origin` also applies the repository's
        // own `remote.origin.fetch` refspecs, and a hostile cloned repo can
        // set that refspec to `+refs/heads/*:refs/heads/*`, which makes a
        // plain `fetch --prune` force-update and even prune (delete) LOCAL
        // branches. Fetching the resolved URL with one explicit refspec
        // ignores every configured refspec, so only remote-tracking refs
        // under refs/remotes/origin/* are updated or pruned: a local branch
        // is never created, moved or deleted, and no local ref is ever
        // force-updated (--no-tags also keeps fetch from touching local
        // tags, and submodules are never recursed into). A URL starting
        // with '-' is refused so it can never be parsed as a git option
        // (for example a forged `--upload-pack=<program>`). Without an
        // `origin` remote the fetch fails closed: gone detection is defined
        // against origin (see GetDefaultBranchNameAsync), and silently
        // succeeding would let callers trust stale remote state.
        // GIT_TERMINAL_PROMPT=0 (set by the shared executor) makes the fetch
        // fail closed instead of prompting for credentials, and transport
        // helpers and protocols are pinned by the shared executor so repo
        // config cannot substitute a program of its own. The reported
        // message is redacted before it leaves this method: git errors
        // quote the remote URL, which may embed credentials, and callers
        // print the message verbatim.
        var remoteUrlResult = await ExecuteGitCommandAsync(path, ["remote", "get-url", "origin"], ct);
        var remoteUrl = remoteUrlResult.Output.Trim();
        if (remoteUrlResult.ExitCode != 0 || remoteUrl.Length == 0 || remoteUrl.StartsWith('-'))
        {
            return (false, "Remote 'origin' is not configured; cannot fetch and prune.");
        }

        var result = await ExecuteGitCommandAsync(
            path,
            ["fetch", "--prune", "--no-tags", "--recurse-submodules=no", remoteUrl, "+refs/heads/*:refs/remotes/origin/*"],
            ct);
        var message = string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim();
        return (result.ExitCode == 0, GitOutputRedactor.Redact(message));
    }

    public async Task<bool> IsSquashOrRebaseMergedAsync(string path, string targetBranch, string branchName, CancellationToken ct = default)
    {
        if (!IsValidBranchName(targetBranch) || !IsValidBranchName(branchName))
        {
            return false;
        }

        if (string.Equals(targetBranch, branchName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        ct.ThrowIfCancellationRequested();

        // Always resolve operands as local branches. A bare name follows
        // gitrevisions order (refs/tags/<name> before refs/heads/<name>), so
        // a tag sharing the branch name would shadow the branch and could
        // fabricate a merge for a branch whose changes are not in the target.
        // IsValidBranchName rejects "..", so the refs/heads/ prefix cannot be
        // escaped. `--end-of-options` keeps the operand from ever being
        // parsed as an option (defense in depth behind the name validation).
        var targetRef = $"refs/heads/{targetBranch}";
        var branchRef = $"refs/heads/{branchName}";

        // Rebase / cherry-pick case: every commit on the branch already has a
        // patch-equivalent commit on the target. `git cherry` marks those
        // commits with '-', and commits still missing from the target with '+'.
        var cherryResult = await ExecuteGitCommandAsync(path, ["cherry", "--end-of-options", targetRef, branchRef], ct);
        if (cherryResult.ExitCode == 0)
        {
            var cherryLines = NonEmptyLines(cherryResult.Output);
            if (cherryLines.Count > 0 && cherryLines.All(line => line.StartsWith('-')))
            {
                return true;
            }
        }

        // Squash case: a squash merge collapses the whole branch into a single
        // commit on the target, so per-commit patch comparison cannot see it.
        // Rebuild the branch's cumulative change as one synthetic commit on
        // top of the merge-base and ask `git cherry` whether an equivalent
        // patch already exists on the target.
        var mergeBaseResult = await ExecuteGitCommandAsync(path, ["merge-base", "--end-of-options", targetRef, branchRef], ct);
        if (mergeBaseResult.ExitCode != 0)
        {
            return false;
        }

        var mergeBase = mergeBaseResult.Output.Trim();
        if (!IsHexSha(mergeBase))
        {
            return false;
        }

        var branchTreeResult = await ExecuteGitCommandAsync(path, ["rev-parse", $"{branchRef}^{{tree}}"], ct);
        var mergeBaseTreeResult = await ExecuteGitCommandAsync(path, ["rev-parse", $"{mergeBase}^{{tree}}"], ct);
        if (branchTreeResult.ExitCode != 0 || mergeBaseTreeResult.ExitCode != 0)
        {
            return false;
        }

        var branchTree = branchTreeResult.Output.Trim();
        var mergeBaseTree = mergeBaseTreeResult.Output.Trim();
        if (!IsHexSha(branchTree) || !IsHexSha(mergeBaseTree))
        {
            return false;
        }

        // No net change relative to the merge-base: be conservative and do
        // not claim a squash/rebase merge. This also covers the ancestor
        // case, which `git branch --merged` already reports.
        if (string.Equals(branchTree, mergeBaseTree, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Write the synthetic commit into a throwaway quarantine object
        // directory (with the repository's objects as alternate) instead of
        // the scanned repository: detection must stay read-only, so a scan
        // never mutates the user's repository or writes into a repo that the
        // caller may only have read access to. Fail closed if the quarantine
        // cannot be set up.
        var commonDirResult = await ExecuteGitCommandAsync(
            path, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct);
        if (commonDirResult.ExitCode != 0)
        {
            return false;
        }

        var repositoryObjects = Path.Combine(commonDirResult.Output.Trim(), "objects");
        if (!Directory.Exists(repositoryObjects))
        {
            return false;
        }

        var quarantineDirectory = Path.Combine(
            Path.GetTempPath(), "GitSweep", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(quarantineDirectory);
            var quarantineEnvironment = new Dictionary<string, string>(SyntheticCommitIdentity)
            {
                ["GIT_OBJECT_DIRECTORY"] = quarantineDirectory,
                ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = repositoryObjects
            };

            var syntheticResult = await ExecuteGitCommandAsync(
                path,
                ["commit-tree", branchTree, "-p", mergeBase, "-m", SyntheticCommitMessage],
                ct,
                quarantineEnvironment);
            if (syntheticResult.ExitCode != 0)
            {
                return false;
            }

            var syntheticCommit = syntheticResult.Output.Trim();
            if (!IsHexSha(syntheticCommit))
            {
                return false;
            }

            var squashCherryResult = await ExecuteGitCommandAsync(
                path, ["cherry", "--end-of-options", targetRef, syntheticCommit], ct, quarantineEnvironment);
            if (squashCherryResult.ExitCode != 0)
            {
                return false;
            }

            // A single synthetic commit: '-' means its patch already exists on
            // the target (squash-merged); '+' means it does not.
            return squashCherryResult.Output.TrimStart().StartsWith('-');
        }
        finally
        {
            try
            {
                if (Directory.Exists(quarantineDirectory))
                {
                    Directory.Delete(quarantineDirectory, recursive: true);
                }
            }
            catch (Exception)
            {
                // Best effort only; the directory lives under the OS temp root.
            }
        }
    }

    public async Task<BranchRiskSignals> GetBranchRiskSignalsAsync(
        string path, string targetBranch, string branchName, CancellationToken ct = default)
    {
        if (!IsValidBranchName(targetBranch) || !IsValidBranchName(branchName) ||
            string.Equals(targetBranch, branchName, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(path))
        {
            return BranchRiskSignals.Unknown;
        }

        ct.ThrowIfCancellationRequested();
        var targetRef = $"refs/heads/{targetBranch}";
        var branchRef = $"refs/heads/{branchName}";

        var ancestorResult = await ExecuteGitCommandAsync(
            path, ["merge-base", "--is-ancestor", "--end-of-options", branchRef, targetRef], ct);
        var isReallyMerged = ancestorResult.ExitCode == 0 ||
            await IsSquashOrRebaseMergedAsync(path, targetBranch, branchName, ct);

        // Commits únicos por parche: 'git cherry' marca con '+' los que no
        // tienen equivalente en el destino. Con merge real el parche
        // acumulado ya está en el destino, así que el conteo es 0.
        int? uniqueCommitCount;
        if (isReallyMerged)
        {
            uniqueCommitCount = 0;
        }
        else
        {
            var cherryResult = await ExecuteGitCommandAsync(
                path, ["cherry", "--end-of-options", targetRef, branchRef], ct);
            uniqueCommitCount = cherryResult.ExitCode == 0
                ? NonEmptyLines(cherryResult.Output).Count(line => line.StartsWith('+'))
                : null;
        }

        // Sin subir: contra el upstream si existe; sin upstream, todo lo que
        // no está en el destino solo existe en local.
        var ancestryUnique = await CountCommitsAsync(path, $"{targetRef}..{branchRef}", ct);
        // Sin --end-of-options: `git rev-parse` lo repetiría en la salida.
        // El nombre ya está validado (nunca empieza por '-') y va como un
        // único argumento de ArgumentList, sin shell.
        var upstreamResult = await ExecuteGitCommandAsync(
            path, ["rev-parse", "--abbrev-ref", $"{branchName}@{{upstream}}"], ct);
        var upstream = upstreamResult.ExitCode == 0 ? upstreamResult.Output.Trim() : string.Empty;

        bool hasUpstream;
        int? unpushedCommitCount;
        if (upstream.Length > 0 && !upstream.StartsWith('-'))
        {
            hasUpstream = true;
            unpushedCommitCount = await CountCommitsAsync(path, $"{upstream}..{branchRef}", ct);
        }
        else
        {
            hasUpstream = false;
            unpushedCommitCount = ancestryUnique;
        }

        // Gone: upstream configured but its remote-tracking ref is missing
        // (for-each-ref reports %(upstream:track) as "[gone]"). When gone,
        // `rev-parse <branch>@{upstream}` fails above, so HasUpstream is
        // false and unpushed falls back to "not in target": fail-closed.
        var trackResult = await ExecuteGitCommandAsync(
            path, ["for-each-ref", "--format=%(upstream:short)\t%(upstream:track)", branchRef], ct);
        var isUpstreamGone = false;
        if (trackResult.ExitCode == 0)
        {
            // TAB-separated for the same reason as GetLocalBranchesAsync:
            // neither an upstream name nor the track field can contain a
            // control character, so no field can forge the other.
            var trackParts = trackResult.Output.TrimEnd('\r', '\n').Split('\t');
            isUpstreamGone = trackParts.Length == 2 && trackParts[0].Length > 0 &&
                IsGoneTrack(trackParts[1]);
        }

        return new BranchRiskSignals(uniqueCommitCount, unpushedCommitCount, hasUpstream, isReallyMerged, isUpstreamGone);
    }

    private async Task<int?> CountCommitsAsync(string path, string range, CancellationToken ct)
    {
        var result = await ExecuteGitCommandAsync(path, ["rev-list", "--count", "--end-of-options", range], ct);
        return result.ExitCode == 0 && int.TryParse(result.Output.Trim(), out var count) && count >= 0
            ? count
            : null;
    }

    public async Task<string?> GetGitCommonDirectoryAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        var result = await ExecuteGitCommandAsync(
            path, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var gitDir = result.Output.Trim();
        if (gitDir.Length == 0)
        {
            return null;
        }

        // --path-format=absolute should already be absolute; normalize anyway
        // so callers can safely build contained paths from it.
        return Path.GetFullPath(gitDir);
    }

    public async Task<string?> GetBranchTipShaAsync(string path, string branchName, CancellationToken ct = default)
    {
        if (!IsValidBranchName(branchName) || !Directory.Exists(path))
        {
            return null;
        }

        var result = await ExecuteGitCommandAsync(
            path, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branchName}^{{commit}}"], ct);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var sha = result.Output.Trim();
        return IsHexSha(sha) ? sha : null;
    }

    public async Task<bool> BranchExistsAsync(string path, string branchName, CancellationToken ct = default)
    {
        return await GetBranchTipShaAsync(path, branchName, ct) is not null;
    }

    public async Task<(bool Success, string ErrorMessage)> CreateBranchAsync(
        string path, string branchName, string commitSha, CancellationToken ct = default)
    {
        if (!IsValidBranchName(branchName))
        {
            return (false, $"Invalid branch name: '{branchName}'.");
        }

        if (!IsHexSha(commitSha))
        {
            return (false, "Invalid commit SHA.");
        }

        // Never overwrite: plain `git branch <name> <sha>` fails when the
        // branch already exists (no -f, no -D anywhere in GitSweep). The SHA
        // operand starts with a hex digit so it can never parse as an option;
        // `--` keeps the branch name from parsing as one either.
        var result = await ExecuteGitCommandAsync(path, ["branch", "--", branchName, commitSha], ct);
        return (result.ExitCode == 0, GitOutputRedactor.Redact(string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim()));
    }

    public async Task<string?> GetCurrentBranchAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path)) return null;
        var result = await ExecuteGitCommandAsync(path, ["branch", "--show-current"], ct);
        if (result.ExitCode != 0) return null;
        var name = result.Output.Trim();
        return IsValidBranchName(name) ? name : null;
    }

    public async Task<IReadOnlyList<string>> GetWorktreeBranchesAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path)) return [];
        var result = await ExecuteGitCommandAsync(path, ["worktree", "list", "--porcelain"], ct);
        if (result.ExitCode != 0) return [];
        var branches = new List<string>();
        foreach (var rawLine in result.Output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith("branch ", StringComparison.Ordinal)) continue;
            var refName = line["branch ".Length..].Trim();
            const string prefix = "refs/heads/";
            if (!refName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var name = refName[prefix.Length..];
            if (IsValidBranchName(name) && !branches.Contains(name, StringComparer.OrdinalIgnoreCase))
                branches.Add(name);
        }
        return branches;
    }

    public async Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path)) return null;
        var result = await ExecuteGitCommandAsync(path, ["rev-parse", "--show-toplevel"], ct);
        if (result.ExitCode != 0) return null;
        var root = result.Output.Trim();
        return root.Length == 0 ? null : Path.GetFullPath(root);
    }

    /// <summary>
    /// Validates a branch name before it is passed to git. Besides the usual
    /// ref-format rules, a leading '-' is rejected so a crafted name can never
    /// be interpreted as a git option. All git invocations also use
    /// <see cref="ProcessStartInfo.ArgumentList"/> (no shell), so a name is
    /// always a single argument.
    /// </summary>
    internal static bool IsValidBranchName(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName) || branchName.Length > 255)
        {
            return false;
        }

        if (branchName[0] is '-' or '.' or '/' ||
            branchName[^1] is '.' or '/' ||
            branchName is "@" ||
            branchName.Contains("..", StringComparison.Ordinal) ||
            branchName.Contains("//", StringComparison.Ordinal) ||
            branchName.Contains("@{", StringComparison.Ordinal) ||
            branchName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var c in branchName)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\')
            {
                return false;
            }
        }

        foreach (var component in branchName.Split('/'))
        {
            if (component.Length == 0 ||
                component[0] == '.' ||
                component.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    // %(upstream:track) reports exactly "[gone]" for a deleted upstream;
    // anything else ("[ahead 1]", "[behind 2]", empty) is not gone. An
    // exact match keeps a value that merely contains the word (or a forged
    // field) from being read as a deleted upstream.
    private static bool IsGoneTrack(string track) =>
        track.Trim().Equals("[gone]", StringComparison.OrdinalIgnoreCase);

    private static List<string> NonEmptyLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool IsHexSha(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    /// <summary>Shared SHA validation for trash storage: full 40/64-char hex only.</summary>
    internal static bool IsValidCommitSha(string? value) =>
        value is not null && IsHexSha(value);

    private static HashSet<string> ParseBranchList(string output)
    {
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimStart('*', '+', ' '))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Task<(int ExitCode, string Output, string Error)> ExecuteGitCommandAsync(
        string workingDirectory, string[] arguments, CancellationToken ct) =>
        ExecuteGitCommandAsync(workingDirectory, arguments, ct, null);

    private static async Task<(int ExitCode, string Output, string Error)> ExecuteGitCommandAsync(
        string workingDirectory,
        string[] arguments,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environmentVariables)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // No inherited terminal: a child that asks for credentials
                // or confirmation (ssh, credential helpers) gets EOF at
                // once and fails closed instead of prompting or hanging.
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var option in HardenedGitOptions)
        {
            process.StartInfo.ArgumentList.Add(option);
        }

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        foreach (var variable in RemovedGitEnvironmentVariables)
        {
            process.StartInfo.Environment.Remove(variable);
        }

        // Indexed/prefixed families (GIT_CONFIG_COUNT, GIT_CONFIG_KEY_<n>,
        // GIT_CONFIG_VALUE_<n>, GIT_CONFIG_PARAMETERS, GIT_CONFIG_GLOBAL,
        // GIT_CONFIG_SYSTEM, GIT_TRACE*) cannot be listed exhaustively.
        foreach (var variable in process.StartInfo.Environment.Keys
                     .Where(key => key.StartsWith("GIT_CONFIG", StringComparison.OrdinalIgnoreCase) ||
                                   key.StartsWith("GIT_TRACE", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            process.StartInfo.Environment.Remove(variable);
        }

        // Never block on an interactive credential/terminal prompt: all git
        // operations here are local plumbing and must fail closed instead.
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GIT_PAGER"] = "cat";

        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Do not leave a git process behind on cancellation: it could
            // keep holding repository locks or finish a write afterwards.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Best effort only; the process may have already exited.
            }

            throw;
        }

        var output = await outputTask;
        var error = await errorTask;

        return (process.ExitCode, output, error);
    }
}
