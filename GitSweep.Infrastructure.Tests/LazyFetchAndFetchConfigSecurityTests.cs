using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

/// <summary>
/// OBS-1 / OBS-2 / OBS-3 (Task 3 security gate) and the Task 11 gate fixes:
/// a hostile repository must not turn any GitSweep command into local code
/// execution or branch overwrite through git's lazy fetch (partial clones)
/// or through a <c>[remote "&lt;url&gt;"]</c> config section that git still
/// honors when fetching by URL. Each test carries a positive control that
/// proves the hostile config really fires with the same setup — run through
/// hardened-equivalent git WITHOUT <c>GIT_NO_LAZY_FETCH</c> — so what the
/// "GitSweep never runs/applies it" assertions prove is the partial-clone
/// guard and the fetch argv, not the environment variable.
/// </summary>
public sealed class LazyFetchAndFetchConfigSecurityTests : IAsyncLifetime
{
    // A well-formed object id that exists nowhere: reading it in a partial
    // clone makes git try to fetch it from the promisor (the lazy fetch).
    private const string AbsentSha = "0123456789abcdef0123456789abcdef01234567";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));
    private readonly GitRepositoryService _sut = new();

    // Copy of GitRepositoryService.HardenedGitOptions: the positive controls
    // run "plain hardened-equivalent git" (no GIT_NO_LAZY_FETCH, no
    // partial-clone refusal) to prove the hardening alone does NOT stop the
    // lazy fetch / the config section from firing.
    private static readonly string[] HardenedOptions =
    [
        "--no-pager",
        "-c", "core.fsmonitor=false",
        "-c", "core.pager=cat",
        "-c", "commit.gpgsign=false",
        "-c", "gc.auto=0",
        "-c", "maintenance.auto=false",
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

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    // ---- OBS-1: partial clone lazy fetch runs remote.<promisor>.uploadpack ----

    [Theory]
    [InlineData("promisor=true")]
    [InlineData("promisor=yes")]
    [InlineData("promisor=on")]
    [InlineData("promisor=1")]
    [InlineData("promisor=TRUE")]
    [InlineData("promisor (valueless)")]
    [InlineData("extensions.partialClone=origin")]
    [InlineData("partialclonefilter only")]
    public async Task Every_entry_point_refuses_a_partial_clone_and_the_uploadpack_never_runs(string marker)
    {
        var (work, markerFile) = await BuildHostilePartialCloneAsync(marker);

        // Positive control: with exactly this promisor marker, hardened-
        // equivalent git WITHOUT GIT_NO_LAZY_FETCH running the squash/rebase
        // detection plumbing lazily fetches the missing blob and runs the
        // hostile remote.origin.uploadpack. (The script exits 1, so the fetch
        // and cherry fail: allowFailure.)
        await RunGitAsync(work, Concat(HardenedOptions, "cherry", "--end-of-options", "refs/heads/main", "refs/heads/feature"), allowFailure: true);
        Assert.True(File.Exists(markerFile), $"positive control ({marker}): `git cherry` did not run the lazy-fetch uploadpack script");
        File.Delete(markerFile);

        // Still recognized as a repository (rev-parse --git-dir is object-free
        // and exempt), so the CLI reports the refusal, not "not a repo".
        Assert.True(await _sut.IsGitRepositoryAsync(work));

        // Every other entry point runs git through the guarded executor.
        await AssertRefusedAsync(() => _sut.GetLocalBranchesAsync(work, "main"));
        await AssertRefusedAsync(() => _sut.GetBranchRiskSignalsAsync(work, "main", "feature"));
        await AssertRefusedAsync(() => _sut.IsSquashOrRebaseMergedAsync(work, "main", "feature"));
        await AssertRefusedAsync(() => _sut.FetchPruneAsync(work));
        await AssertRefusedAsync(() => _sut.DeleteBranchAsync(work, "feature"));
        await AssertRefusedAsync(() => _sut.BranchExistsAsync(work, "feature"));
        await AssertRefusedAsync(() => _sut.GetBranchTipShaAsync(work, "feature"));
        await AssertRefusedAsync(() => _sut.CreateBranchAsync(work, "restored", AbsentSha));
        await AssertRefusedAsync(() => _sut.GetCurrentBranchAsync(work));
        await AssertRefusedAsync(() => _sut.GetWorktreeBranchesAsync(work));
        await AssertRefusedAsync(() => _sut.GetDefaultBranchNameAsync(work));
        await AssertRefusedAsync(() => _sut.GetRepositoryRootAsync(work));
        await AssertRefusedAsync(() => _sut.GetGitCommonDirectoryAsync(work));

        Assert.False(File.Exists(markerFile), $"{marker}: a GitSweep entry point ran the lazy-fetch uploadpack script");
        Assert.True(await RefExistsAsync(work, "refs/heads/feature"), "the refused delete must leave the branch alone");
        Assert.False(await RefExistsAsync(work, "refs/heads/restored"), "the refused restore must not create a branch");
    }

    [Fact]
    public async Task Restore_branch_creation_in_a_partial_clone_is_refused_before_git_branch_runs()
    {
        // V1: restore/undo recreate a trashed branch with `git branch -- <name>
        // <sha>`. A trash entry lives inside the (untrusted) repository, so its
        // SHA can be absent; in a partial clone git then lazily fetches it and
        // runs the repo's uploadpack.
        var (work, markerFile) = await BuildHostilePartialCloneAsync("promisor=true");

        // Positive control: hardened-equivalent `git branch -- <name> <absent
        // sha>` WITHOUT GIT_NO_LAZY_FETCH runs the uploadpack script.
        await RunGitAsync(work, Concat(HardenedOptions, "branch", "--", "restored", AbsentSha), allowFailure: true);
        Assert.True(File.Exists(markerFile), "positive control: `git branch <name> <absent sha>` did not lazily fetch");
        File.Delete(markerFile);

        // The restore flow (TrashRestoreHelper): existence check, then create.
        await AssertRefusedAsync(() => _sut.BranchExistsAsync(work, "restored"));
        await AssertRefusedAsync(() => _sut.CreateBranchAsync(work, "restored", AbsentSha));

        Assert.False(File.Exists(markerFile), "restore ran the lazy-fetch uploadpack script");
        Assert.False(await RefExistsAsync(work, "refs/heads/restored"));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("0")]
    [InlineData("FALSE")]
    public async Task Explicitly_disabled_promisor_alone_is_not_refused(string value)
    {
        // git_config_bool reads these as false: no promisor, no lazy fetch, so
        // an ordinary repository carrying one must keep working.
        var (repo, _, _, _) = await BuildFileOriginAsync("disabled-" + value.ToLowerInvariant());
        await RunGitAsync(repo, "config", "remote.origin.promisor", value);

        var branches = await _sut.GetLocalBranchesAsync(repo, "main");
        var fetch = await _sut.FetchPruneAsync(repo);

        Assert.NotNull(branches);
        Assert.True(fetch.Success, fetch.ErrorMessage);
    }

    [Fact]
    public async Task Full_clone_is_not_refused_as_a_partial_clone()
    {
        var repo = Path.Combine(_root, "full");
        Directory.CreateDirectory(repo);
        await InitRepoAsync(repo);
        await CommitFileAsync(repo, "README.md", "initial", "initial commit");
        await RunGitAsync(repo, "checkout", "-b", "feature/active");
        await CommitFileAsync(repo, "a.txt", "active", "active work");
        await RunGitAsync(repo, "checkout", "main");

        var branches = await _sut.GetLocalBranchesAsync(repo, "main");
        Assert.Contains(branches, b => b.Name == "feature/active");
        Assert.True(await _sut.BranchExistsAsync(repo, "feature/active"));
    }

    [Fact]
    public async Task Unreadable_config_is_refused_as_undetermined_not_as_not_partial()
    {
        // If git cannot read the configuration, nothing proves the repository
        // is NOT a partial clone: refuse, with the distinct message.
        var repo = Path.Combine(_root, "broken-config");
        Directory.CreateDirectory(repo);
        await InitRepoAsync(repo);
        await File.AppendAllTextAsync(Path.Combine(repo, ".git", "config"), "[remote \"origin\"\n");

        var ex = await Assert.ThrowsAsync<PartialCloneNotSupportedException>(() => _sut.GetCurrentBranchAsync(repo));

        Assert.StartsWith("Could not determine whether the repository at", ex.Message);
        Assert.DoesNotContain("is a partial clone (it fetches", ex.Message);
    }

    // ---- classifier (git config -z --get-regexp output) ----

    [Theory]
    // --get-regexp exit 1: no partial-clone key at all.
    [InlineData(1, "", nameof(GitRepositoryService.PartialCloneConfigStatus.NotPartialClone))]
    // Every spelling git_config_bool accepts as true, and the valueless key.
    [InlineData(0, "remote.origin.promisor\ntrue\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    [InlineData(0, "remote.origin.promisor\nyes\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    [InlineData(0, "remote.origin.promisor\non\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    [InlineData(0, "remote.origin.promisor\n1\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    [InlineData(0, "remote.origin.promisor\nTRUE\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    [InlineData(0, "remote.origin.promisor\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    // Anything that is not an explicit false spelling fails closed.
    [InlineData(0, "remote.origin.promisor\n\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    [InlineData(0, "remote.origin.promisor\n2\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    [InlineData(0, "remote.origin.promisor\nmaybe\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    // Explicit false spellings (case-insensitive, trimmed) do not count.
    [InlineData(0, "remote.origin.promisor\nfalse\0", nameof(GitRepositoryService.PartialCloneConfigStatus.NotPartialClone))]
    [InlineData(0, "remote.origin.promisor\nNo\0", nameof(GitRepositoryService.PartialCloneConfigStatus.NotPartialClone))]
    [InlineData(0, "remote.origin.promisor\n OFF \0", nameof(GitRepositoryService.PartialCloneConfigStatus.NotPartialClone))]
    [InlineData(0, "remote.origin.promisor\n0\0", nameof(GitRepositoryService.PartialCloneConfigStatus.NotPartialClone))]
    [InlineData(0, "remote.a.promisor\nfalse\0remote.b.promisor\nno\0", nameof(GitRepositoryService.PartialCloneConfigStatus.NotPartialClone))]
    // One disabled remote does not hide another enabled one.
    [InlineData(0, "remote.a.promisor\nfalse\0remote.b.promisor\nyes\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    // extensions.partialClone and partialclonefilter count with any value.
    [InlineData(0, "extensions.partialclone\norigin\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    [InlineData(0, "extensions.partialclone\n\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    [InlineData(0, "extensions.partialclone\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    [InlineData(0, "remote.origin.partialclonefilter\nblob:none\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    [InlineData(0, "remote.origin.promisor\nfalse\0remote.origin.partialclonefilter\nblob:none\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    [InlineData(0, "remote.origin.promisor\nfalse\0extensions.partialclone\norigin\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    // A remote whose name contains dots or newlines-free odd characters.
    [InlineData(0, "remote.my.remote.promisor\nyes\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    // OBS-R1: single-level keys (`[remote] promisor`, no subsection) register
    // a promisor remote with an empty name; same rules as remote.<n>.*.
    [InlineData(0, "remote.promisor\ntrue\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    [InlineData(0, "remote.promisor\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    [InlineData(0, "remote.partialclonefilter\nblob:none\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    [InlineData(0, "remote.promisor\nfalse\0", nameof(GitRepositoryService.PartialCloneConfigStatus.NotPartialClone))]
    [InlineData(0, "remote..promisor\nyes\0", nameof(GitRepositoryService.PartialCloneConfigStatus.PartialClone))]
    // Indeterminate: exit 0 without a record, or any other exit code.
    [InlineData(0, "", nameof(GitRepositoryService.PartialCloneConfigStatus.Undetermined))]
    [InlineData(3, "", nameof(GitRepositoryService.PartialCloneConfigStatus.Undetermined))]
    [InlineData(128, "", nameof(GitRepositoryService.PartialCloneConfigStatus.Undetermined))]
    [InlineData(-2, "", nameof(GitRepositoryService.PartialCloneConfigStatus.Undetermined))]
    public void ClassifyPartialCloneConfig_fails_closed(
        int exitCode, string output, string expected)
    {
        Assert.Equal(expected, GitRepositoryService.ClassifyPartialCloneConfig(exitCode, output).ToString());
    }

    // ---- OBS-R1 (Task 11 re-gate): single-level [remote] promisor keys ----

    [Theory]
    [InlineData("promisor", "true")]
    [InlineData("partialclonefilter", "blob:none")]
    public async Task Single_level_remote_promisor_key_is_refused_as_a_partial_clone(string key, string value)
    {
        // `[remote] promisor = true` (key remote.<key>, no subsection) is not a
        // remote.<name>.<key> key, yet git registers a promisor remote with an
        // empty name for it and lazily fetches missing objects from it.
        var repo = Path.Combine(_root, "single-level-" + key);
        Directory.CreateDirectory(repo);
        await InitRepoAsync(repo);
        await CommitFileAsync(repo, "README.md", "initial", "initial commit");

        // Negative control: without the key git does not attempt a lazy fetch.
        Assert.DoesNotContain("--filter=blob:none", await TraceAbsentObjectReadAsync(repo));

        await RunGitAsync(repo, "config", "remote." + key, value);
        Assert.Contains("[remote]\n", (await File.ReadAllTextAsync(Path.Combine(repo, ".git", "config"))).ReplaceLineEndings("\n"));

        // Positive control: with the key, reading an absent object makes git
        // start a lazy fetch (`git fetch '' ... --filter=blob:none --stdin`).
        Assert.Contains("--filter=blob:none", await TraceAbsentObjectReadAsync(repo));

        await AssertRefusedAsync(() => _sut.GetCurrentBranchAsync(repo));
        await AssertRefusedAsync(() => _sut.GetLocalBranchesAsync(repo, "main"));
        await AssertRefusedAsync(() => _sut.CreateBranchAsync(repo, "restored", AbsentSha));
        Assert.False(await RefExistsAsync(repo, "refs/heads/restored"));
    }

    // ---- OBS-R2 (Task 11 re-gate): a fetch may turn the repo into a promisor ----

    [Fact]
    public async Task Successful_fetch_forgets_the_not_partial_clone_verdict()
    {
        var (repo, _, _, _) = await BuildFileOriginAsync("memo-success");

        // Memoises "not a partial clone" for this repository.
        Assert.NotNull(await _sut.GetLocalBranchesAsync(repo, "main"));

        var fetch = await _sut.FetchPruneAsync(repo);
        Assert.True(fetch.Success, fetch.ErrorMessage);

        // What a server-driven `promisor.storeFields` write during that fetch
        // would leave behind in the repository config.
        await RunGitAsync(repo, "config", "remote.origin.partialclonefilter", "blob:none");

        await AssertRefusedAsync(() => _sut.GetCurrentBranchAsync(repo));
        await AssertRefusedAsync(() => _sut.GetLocalBranchesAsync(repo, "main"));
    }

    [Fact]
    public async Task Failed_fetch_forgets_the_not_partial_clone_verdict()
    {
        var (repo, _, _, _) = await BuildFileOriginAsync("memo-failure");
        await RunGitAsync(repo, "remote", "set-url", "origin", FileUrl(Path.Combine(_root, "no-such-origin.git")));

        Assert.NotNull(await _sut.GetLocalBranchesAsync(repo, "main"));

        var fetch = await _sut.FetchPruneAsync(repo);
        Assert.False(fetch.Success);

        await RunGitAsync(repo, "config", "remote.origin.promisor", "true");

        await AssertRefusedAsync(() => _sut.GetCurrentBranchAsync(repo));
    }

    [Fact]
    public async Task Without_a_fetch_the_not_partial_clone_verdict_stays_memoised()
    {
        // Control for the two tests above: the verdict IS memoised per
        // repository, so their refusal after the fetch comes from the fetch
        // dropping it, not from detection re-running on every command.
        var (repo, _, _, _) = await BuildFileOriginAsync("memo-control");
        Assert.NotNull(await _sut.GetLocalBranchesAsync(repo, "main"));

        await RunGitAsync(repo, "config", "remote.origin.partialclonefilter", "blob:none");

        Assert.Null(await Record.ExceptionAsync(() => _sut.GetCurrentBranchAsync(repo)));
        await AssertRefusedAsync(() => new GitRepositoryService().GetCurrentBranchAsync(repo));
    }

    [Theory]
    [InlineData("promisor.acceptFromServer", "all", "None")]
    [InlineData("sideband.allowControlCharacters", "true", "false")]
    public async Task Hardened_options_override_hostile_repository_config(string key, string hostile, string pinned)
    {
        var repo = Path.Combine(_root, "pinned-" + key.Replace('.', '-'));
        Directory.CreateDirectory(repo);
        await InitRepoAsync(repo);
        await RunGitAsync(repo, "config", key, hostile);

        // Control: plain git in this repository sees the hostile value.
        Assert.Equal(hostile, (await RunGitAsync(repo, "config", "--get", key)).Trim());

        // Every GitSweep git invocation sees the pinned value instead
        // (command-line -c beats repository config). An unknown key on older
        // git is harmless: the command still succeeds.
        var hardened = await GitRepositoryService.RunGitProcessAsync(
            repo, ["config", "--get", key], TimeSpan.FromMinutes(1), 1024 * 1024, 1024 * 1024, CancellationToken.None);

        Assert.Equal(0, hardened.ExitCode);
        Assert.Equal(pinned, hardened.Output.Trim());
    }

    [PromisorRemoteCapabilityFact]
    public async Task Fetch_never_accepts_a_promisor_remote_advertised_by_the_server()
    {
        var (repo, url, _, _) = await BuildFileOriginAsync("advertise");
        var upstream = Path.Combine(_root, "advertise-up.git");

        // Hostile server: advertises a promisor remote named like the client's
        // origin (the name a `promisor.storeFields` write would target).
        await RunGitAsync(upstream, "config", "promisor.advertise", "true");
        await RunGitAsync(upstream, "config", "remote.origin.url", url);
        await RunGitAsync(upstream, "config", "remote.origin.promisor", "true");
        await RunGitAsync(upstream, "config", "remote.origin.partialCloneFilter", "blob:none");
        // Hostile client repository config: accept whatever is advertised.
        await RunGitAsync(repo, "config", "promisor.acceptFromServer", "all");

        string[] fetch = ["fetch", "--prune", "--no-tags", "--recurse-submodules=no", "--upload-pack=git-upload-pack", "--refmap=", url, "+refs/heads/*:refs/remotes/origin/*"];
        var packetTrace = new Dictionary<string, string> { ["GIT_TRACE_PACKET"] = "1" };

        // Positive control: plain git accepts the advertised promisor remote.
        var control = await RunGitTraceAsync(repo, packetTrace, fetch);
        Assert.Contains("fetch> promisor-remote=origin", control);

        // GitSweep's executor (its pins, plus the packet trace for the test).
        var hardened = await GitRepositoryService.RunGitProcessAsync(
            repo, fetch, TimeSpan.FromMinutes(2), 1024 * 1024, 1024 * 1024, CancellationToken.None, packetTrace);

        Assert.True(hardened.ExitCode == 0, hardened.Error);
        Assert.Contains("fetch< promisor-remote=", hardened.Error);
        Assert.DoesNotContain("fetch> promisor-remote=", hardened.Error);
    }

    // ---- OBS-2: fetch-by-URL honors [remote "<url>"] uploadpack ----

    [Fact]
    public async Task FetchPrune_does_not_run_uploadpack_from_remote_url_config_section()
    {
        var (repo, url, markerFile, scriptPath) = await BuildFileOriginAsync("obs2");

        // Hostile: a config section named after the origin URL carries an
        // uploadpack program. git applies it when fetching that URL by string.
        await RunGitAsync(repo, "config", $"remote.{url}.uploadpack", scriptPath);

        // Positive control: the previous (unfixed) fetch argv runs the script.
        await RunGitAsync(repo, Concat(HardenedOptions,
            "fetch", "--prune", "--no-tags", "--recurse-submodules=no", url, "+refs/heads/*:refs/remotes/origin/*"),
            allowFailure: true);
        Assert.True(File.Exists(markerFile), "positive control: the unfixed fetch argv did not run the remote.<url>.uploadpack script");
        File.Delete(markerFile);

        var fetch = await _sut.FetchPruneAsync(repo);

        Assert.True(fetch.Success, fetch.ErrorMessage);
        Assert.False(File.Exists(markerFile), "FetchPruneAsync ran the remote.<url>.uploadpack script");
    }

    // ---- OBS-3: fetch-by-URL honors [remote "<url>"] fetch (refmap) ----

    [Fact]
    public async Task FetchPrune_does_not_apply_remote_url_config_refspec_to_local_branches()
    {
        var (repo, url, _, _) = await BuildFileOriginAsync("obs3");

        // feature/victim exists on origin; give it a local-only commit so an
        // opportunistic force-update would destroy work reachable only here.
        await RunGitAsync(repo, "checkout", "-b", "feature/victim");
        await CommitFileAsync(repo, "v.txt", "remote side", "remote work");
        await RunGitAsync(repo, "push", "origin", "feature/victim");
        await CommitFileAsync(repo, "v.txt", "local only", "local only work");
        var victimTip = (await RunGitAsync(repo, "rev-parse", "refs/heads/feature/victim")).Trim();
        // Check out a branch the remote does NOT have, so the hostile refmap's
        // main->main and feature/victim->feature/victim updates do not hit the
        // checked-out branch (git refuses to fetch into a checked-out branch).
        await RunGitAsync(repo, "checkout", "-b", "feature/holding");

        // Hostile: a config section named after the origin URL maps every
        // remote head onto a LOCAL branch (the opportunistic refmap).
        await RunGitAsync(repo, "config", $"remote.{url}.fetch", "+refs/heads/*:refs/heads/*");

        // Positive control: the previous (unfixed) fetch argv force-updates
        // the local branch to the remote tip, losing the local-only commit.
        await RunGitAsync(repo, Concat(HardenedOptions,
            "fetch", "--prune", "--no-tags", "--recurse-submodules=no", url, "+refs/heads/*:refs/remotes/origin/*"));
        Assert.NotEqual(victimTip, (await RunGitAsync(repo, "rev-parse", "refs/heads/feature/victim")).Trim());

        // Restore the local-only tip and run the real (fixed) fetch.
        await RunGitAsync(repo, "update-ref", "refs/heads/feature/victim", victimTip);
        var fetch = await _sut.FetchPruneAsync(repo);

        Assert.True(fetch.Success, fetch.ErrorMessage);
        Assert.Equal(victimTip, (await RunGitAsync(repo, "rev-parse", "refs/heads/feature/victim")).Trim());
    }

    // ---- helpers ----

    private static async Task AssertRefusedAsync(Func<Task> call)
    {
        var ex = await Assert.ThrowsAsync<PartialCloneNotSupportedException>(call);
        Assert.Contains("is a partial clone", ex.Message);
    }

    // Builds a blob:none partial clone of a local promisor (`up`, with
    // uploadpack.allowFilter) whose origin.uploadpack points at a marker-
    // writing script, then leaves exactly ONE partial-clone marker in the
    // config (the clone's own promisor/partialclonefilter keys are removed
    // first), so each case proves that marker alone is detected. main and
    // feature change the same file, so comparing them needs a missing blob.
    private async Task<(string Work, string Marker)> BuildHostilePartialCloneAsync(string marker)
    {
        var up = Path.Combine(_root, "up");
        Directory.CreateDirectory(up);
        await InitRepoAsync(up);
        await RunGitAsync(up, "config", "uploadpack.allowFilter", "true");
        await CommitFileAsync(up, "f.txt", "base", "base");
        await RunGitAsync(up, "checkout", "-b", "feature");
        await CommitFileAsync(up, "f.txt", "feature", "feature");
        await RunGitAsync(up, "checkout", "main");
        await CommitFileAsync(up, "f.txt", "main change", "main change");

        var work = Path.Combine(_root, "work");
        await RunGitAsync(_root, Concat(HardenedOptions, "clone", "--filter=blob:none", FileUrl(up), work));
        await InitIdentityAsync(work);
        await RunGitAsync(work, "branch", "feature", "origin/feature");

        await RunGitAsync(work, ["config", "--unset-all", "remote.origin.promisor"], allowFailure: true);
        await RunGitAsync(work, ["config", "--unset-all", "remote.origin.partialclonefilter"], allowFailure: true);
        await RunGitAsync(work, ["config", "--unset-all", "extensions.partialClone"], allowFailure: true);
        switch (marker)
        {
            case "promisor (valueless)":
                // `git config` cannot write a key without '='; git merges a
                // repeated section header into the same remote.
                await File.AppendAllTextAsync(Path.Combine(work, ".git", "config"), "[remote \"origin\"]\n\tpromisor\n");
                break;
            case "extensions.partialClone=origin":
                await RunGitAsync(work, "config", "extensions.partialClone", "origin");
                break;
            case "partialclonefilter only":
                await RunGitAsync(work, "config", "remote.origin.partialclonefilter", "blob:none");
                break;
            default:
                Assert.StartsWith("promisor=", marker);
                await RunGitAsync(work, "config", "remote.origin.promisor", marker["promisor=".Length..]);
                break;
        }

        var markerFile = Path.Combine(_root, "obs1-marker.txt");
        var scriptPath = await WriteMarkerScriptAsync("pwn-uploadpack.sh", markerFile);
        await RunGitAsync(work, "config", "remote.origin.uploadpack", scriptPath);
        return (work, markerFile);
    }

    // Builds a repo with a file:// origin that really has a 'main' branch, so
    // the fixed FetchPruneAsync succeeds. Returns (repo, origin url, marker
    // path for a planted script, planted script path).
    private async Task<(string Repo, string Url, string Marker, string Script)> BuildFileOriginAsync(string name)
    {
        var up = Path.Combine(_root, name + "-up.git");
        await RunGitAsync(_root, "init", "--bare", up);
        var seed = Path.Combine(_root, name + "-seed");
        Directory.CreateDirectory(seed);
        await InitRepoAsync(seed);
        await CommitFileAsync(seed, "README.md", "initial", "initial commit");
        await RunGitAsync(seed, "remote", "add", "origin", up);
        await RunGitAsync(seed, "push", "-u", "origin", "main");

        var url = FileUrl(up);
        var repo = Path.Combine(_root, name + "-repo");
        await RunGitAsync(_root, Concat(HardenedOptions, "clone", url, repo));
        await InitIdentityAsync(repo);

        var marker = Path.Combine(_root, name + "-marker.txt");
        var script = await WriteMarkerScriptAsync(name + "-pwn.sh", marker);
        return (repo, url, marker, script);
    }

    // Returns the script path with forward slashes: git runs an uploadpack
    // value through its bundled `sh`, which treats '\' as an escape, so a
    // Windows backslash path would become "command not found".
    private async Task<string> WriteMarkerScriptAsync(string fileName, string marker)
    {
        var scriptPath = Path.Combine(_root, fileName);
        await TestScripts.WriteExecutableAsync(scriptPath,
            $"#!/bin/sh\necho owned >> {marker.Replace('\\', '/')}\nexit 1\n");
        return scriptPath.Replace('\\', '/');
    }

    private static async Task<bool> RefExistsAsync(string repo, string refName)
    {
        // show-ref reads refs only (no objects), so it cannot lazy-fetch.
        var (exitCode, _) = await RunGitWithExitCodeAsync(repo, ["show-ref", "--verify", "--quiet", refName]);
        return exitCode == 0;
    }

    private static string FileUrl(string path) => "file://" + path.Replace('\\', '/');

    private static async Task InitRepoAsync(string path)
    {
        try { await RunGitAsync(path, "init", "-b", "main"); }
        catch (InvalidOperationException) { await RunGitAsync(path, "init"); await RunGitAsync(path, "checkout", "-b", "main"); }
        await InitIdentityAsync(path);
    }

    private static async Task InitIdentityAsync(string path)
    {
        await RunGitAsync(path, "config", "user.email", "tests@example.com");
        await RunGitAsync(path, "config", "user.name", "GitSweep Tests");
    }

    private static async Task CommitFileAsync(string repo, string fileName, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, fileName), content);
        await RunGitAsync(repo, "add", ".");
        await RunGitAsync(repo, "commit", "-m", message, "--no-gpg-sign");
    }

    private static string[] Concat(string[] options, params string[] rest)
    {
        var all = new List<string>(options);
        all.AddRange(rest);
        return all.ToArray();
    }

    private static Task<string> RunGitAsync(string workingDirectory, params string[] arguments) =>
        RunGitAsync(workingDirectory, arguments, allowFailure: false);

    private static async Task<string> RunGitAsync(string workingDirectory, string[] arguments, bool allowFailure)
    {
        var (exitCode, output) = await RunGitWithExitCodeAsync(workingDirectory, arguments);
        if (exitCode != 0 && !allowFailure)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {output}");
        return output;
    }

    // Test-side `git cat-file -t <absent sha>` with GIT_TRACE: the trace shows
    // whether git starts a lazy fetch (a promisor remote is registered).
    private static Task<string> TraceAbsentObjectReadAsync(string repo) =>
        RunGitTraceAsync(repo, new Dictionary<string, string> { ["GIT_TRACE"] = "1" }, ["cat-file", "-t", AbsentSha]);

    // Test-side git with extra environment (trace variables); returns stderr
    // followed by stdout whatever the exit code.
    private static async Task<string> RunGitTraceAsync(string workingDirectory, IReadOnlyDictionary<string, string> environment, string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GitExecutableLocator.GetRequiredPath(),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment.Remove("GIT_NO_LAZY_FETCH");
        foreach (var (key, value) in environment) startInfo.Environment[key] = value;
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return await stderrTask + await stdoutTask;
    }

    // Test-side git: same absolute git as production, but the environment is
    // the test process's own minus GIT_NO_LAZY_FETCH, so a positive control
    // shows what git does WITHOUT the variable (the guard is what is tested).
    private static async Task<(int ExitCode, string Output)> RunGitWithExitCodeAsync(string workingDirectory, string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GitExecutableLocator.GetRequiredPath(),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment.Remove("GIT_NO_LAZY_FETCH");
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, process.ExitCode == 0 ? stdout : stderr + stdout);
    }
}
