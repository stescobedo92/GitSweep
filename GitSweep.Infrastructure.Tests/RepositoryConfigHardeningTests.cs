#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitSweep.Core.Models;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

/// <summary>
/// Final fix wave (final-coverage-review.md P1/P1b, final-review.md I5): a
/// hostile repository's own configuration must not make any GitSweep command
/// run a program, write outside the remote-tracking refs during
/// <c>clean --fetch</c>, or fake the history its merge and reachability
/// checks rely on. Every test carries a positive control run by test-side git
/// with the same setup: <see cref="PreviousHardenedOptions"/> (what GitSweep
/// pinned before this wave) when the test proves a NEW pin, plain git when it
/// proves hardening that already existed. Test-side git runs with no system
/// or global configuration (<see cref="IsolatedEnvironment"/>), so neither
/// the machine's credential manager nor a user setting can change a control.
/// </summary>
public sealed class RepositoryConfigHardeningTests : IAsyncLifetime
{
    private static readonly string NullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    // GitRepositoryService.HardenedGitOptions as it was before this fix wave.
    private static readonly string[] PreviousHardenedOptions =
    [
        "--no-pager",
        "-c", "core.fsmonitor=false",
        "-c", "core.pager=cat",
        "-c", "commit.gpgsign=false",
        "-c", "gc.auto=0",
        "-c", "maintenance.auto=false",
        "-c", $"core.hooksPath={NullDevice}",
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
        "-c", "credential.helper=",
        "-c", "sideband.allowControlCharacters=false",
        "-c", "promisor.acceptFromServer=None"
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));
    private readonly GitRepositoryService _sut = new();
    private string _emptyGlobalConfig = "";

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _emptyGlobalConfig = Path.Combine(_root, "empty-global.gitconfig");
        File.WriteAllText(_emptyGlobalConfig, string.Empty);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    // The fetch argv GitSweep used before this wave (no --no-prune-tags).
    private static string[] PreviousFetch(string url) =>
        ["fetch", "--prune", "--no-tags", "--recurse-submodules=no", "--upload-pack=git-upload-pack", "--refmap=", url, "+refs/heads/*:refs/remotes/origin/*"];

    // ---- P1-F1 / RG-01 / RR-1: configured hooks for an event GitSweep can fire ----

    private const string SomeSha = "0123456789abcdef0123456789abcdef01234567";

    // The event-level pins do not hold (git 2.54.x has no such key; git 2.55
    // reads it as a friendly-name switch when the config also defines a
    // hook.<event>.<var> key, RR-1), and the key spelling can carry an empty
    // friendly name (RG2-01: [hook] event / [hook ""] event), so GitSweep
    // refuses, up front and on every git version, any repository whose config
    // binds a hook to an event its own commands can fire. Deny by default: an
    // unknown event too.
    [Theory]
    [InlineData("friendly", "reference-transaction")]
    [InlineData("collision", "reference-transaction")] // RR-1: event pin bypassed
    [InlineData("friendly", "pre-auto-gc")]
    [InlineData("friendly", "post-index-change")]
    [InlineData("friendly", "gitsweep-made-up-event")] // unknown/future event
    [InlineData("no-subsection", "reference-transaction")]   // RG2-01: [hook] event
    [InlineData("empty-subsection", "reference-transaction")] // RG2-01: [hook ""] event
    public async Task Configured_hook_for_an_event_gitsweep_can_fire_is_refused(string shape, string hookEvent)
    {
        var tag = shape + "-" + hookEvent;
        var fixture = await BuildFileOriginAsync("cfghook-refuse-" + tag);
        var marker = Path.Combine(_root, $"cfghook-{tag}-marker.txt");
        var hook = await WriteScriptAsync($"cfghook-{tag}.sh", marker, "config-hook", drainStdin: true, exitCode: 0);
        await WriteConfiguredHookAsync(fixture.Repo, shape, hookEvent, $"{hook} {hookEvent}");

        // Positive control where git actually runs configured hooks (2.54+)
        // and the event fires on a ref update: under the pre-wave hardening the
        // hook runs despite core.hooksPath. Each shape is exercised with the
        // hardening that leaves it live on BOTH 2.54.0 and 2.55:
        //  - collision: with the current event-level pin present, proving the
        //    pin is bypassed (RR-1);
        //  - friendly / empty-subsection: with the pre-wave options (no pin);
        //  - no-subsection: only on git < 2.55 (2.55's `if (!name)` guard drops
        //    [hook] event, so it cannot run there; the refusal still applies).
        if (TestConfigHooks.IsSupported && hookEvent == "reference-transaction" && PositiveControlRuns(shape))
        {
            var pcOptions = shape == "collision"
                ? new[] { "-c", "hook.reference-transaction.enabled=false" }
                : [];
            await PreviousHardenedGit(fixture.Repo, [.. pcOptions, "branch", "gitsweep-probe"]);
            Assert.Contains("config-hook reference-transaction", await ReadMarkerAsync(marker));
            await Git(fixture.Repo, "branch", "-D", "gitsweep-probe");
            File.Delete(marker);
        }

        var sanitized = SanitizedEventName(hookEvent);
        await AssertHookRefusedAsync(() => _sut.GetLocalBranchesAsync(fixture.Repo, "main"), sanitized);
        await AssertHookRefusedAsync(() => _sut.DeleteBranchAsync(fixture.Repo, "feature/done"), sanitized);
        await AssertHookRefusedAsync(() => _sut.FetchPruneAsync(fixture.Repo), sanitized);
        await AssertHookRefusedAsync(() => _sut.CreateBranchAsync(fixture.Repo, "restored", SomeSha), sanitized);
        await AssertHookRefusedAsync(() => _sut.GetCurrentBranchAsync(fixture.Repo), sanitized);

        Assert.False(File.Exists(marker), "the configured hook ran instead of GitSweep refusing");
        // feature/done is untouched and restored was never created (test-side
        // git bypasses the guard).
        Assert.NotEqual(string.Empty, (await Git(fixture.Repo, "rev-parse", "--verify", "refs/heads/feature/done")).Trim());
        var (restoredExists, _, _) = await RunGitAsync(fixture.Repo, ["show-ref", "--verify", "--quiet", "refs/heads/restored"]);
        Assert.NotEqual(0, restoredExists);
    }

    // The no-subsection form ([hook] event) binds only on git < 2.55 (2.55
    // drops it with an `if (!name)` guard); every other shape runs on 2.54.0
    // and 2.55 under the chosen hardening.
    private static bool PositiveControlRuns(string shape) =>
        shape != "no-subsection" || (TestGitVersion.Current is { } v && v < new Version(2, 55));

    // Writes a hook bound to <hookEvent> with <command> in the shape under
    // test. git config cannot write the empty-name spellings, so those are
    // appended to .git/config directly, the way a hostile archive ships them.
    private async Task WriteConfiguredHookAsync(string repo, string shape, string hookEvent, string command)
    {
        switch (shape)
        {
            case "no-subsection":
                await File.AppendAllTextAsync(Path.Combine(repo, ".git", "config"),
                    $"[hook]\n\tevent = {hookEvent}\n\tcommand = {command}\n");
                break;
            case "empty-subsection":
                await File.AppendAllTextAsync(Path.Combine(repo, ".git", "config"),
                    $"[hook \"\"]\n\tevent = {hookEvent}\n\tcommand = {command}\n");
                break;
            default:
                await Git(repo, "config", "hook.gitsweep-evil.event", hookEvent);
                await Git(repo, "config", "hook.gitsweep-evil.command", command);
                if (shape == "collision")
                {
                    await Git(repo, "config", $"hook.{hookEvent}.command", "true");
                }

                break;
        }
    }

    // M-2(b): pre-auto-gc is fired by auto-maintenance after a fetch, not by a
    // ref update, so its hazard is proven on the fetch path. GitSweep refuses
    // the repository before fetching.
    [Fact]
    public async Task Configured_pre_auto_gc_hook_would_run_on_a_fetch_and_is_refused()
    {
        var fixture = await BuildFileOriginAsync("cfghook-preautogc");
        var marker = Path.Combine(_root, "cfghook-preautogc-marker.txt");
        var hook = await WriteScriptAsync("cfghook-preautogc.sh", marker, "config-hook", drainStdin: true, exitCode: 0);
        await Git(fixture.Repo, "config", "hook.gitsweep-autogc.event", "pre-auto-gc");
        await Git(fixture.Repo, "config", "hook.gitsweep-autogc.command", $"{hook} pre-auto-gc");

        if (TestConfigHooks.IsSupported)
        {
            // With only core.hooksPath pinned (the pre-existing hook defense)
            // but auto-gc left on, a fetch runs the configured pre-auto-gc hook
            // through auto-maintenance: core.hooksPath does not govern it. (The
            // pre-wave options also pin gc.auto=0, so they are not used here.)
            await EnableAutoGcAsync(fixture);
            await PushNewOriginCommitAsync(fixture, "control");
            await Git(fixture.Repo, "-c", $"core.hooksPath={NullDevice}", "fetch", "origin");
            Assert.Contains("config-hook pre-auto-gc", await ReadMarkerAsync(marker));
            File.Delete(marker);
        }

        await AssertHookRefusedAsync(() => _sut.FetchPruneAsync(fixture.Repo), "pre-auto-gc");
        Assert.False(File.Exists(marker), "the configured pre-auto-gc hook ran instead of GitSweep refusing");
    }

    // M-3: fail-closed branches of the classifier, like ClassifyPartialCloneConfig_fails_closed.
    [Theory]
    [InlineData(1, "", false)]                                                   // no hook-event key
    [InlineData(128, "", true)]                                                  // unreadable
    [InlineData(0, "", true)]                                                    // exit 0 promised a record
    [InlineData(0, "hook.gitsweep-x.event\npre-commit\0", false)]                // allow-listed
    [InlineData(0, "hook.gitsweep-x.event\nreference-transaction\0", true)]      // friendly name
    [InlineData(0, "hook.event\nreference-transaction\0", true)]                 // RG2-01 no-subsection
    [InlineData(0, "hook..event\nreference-transaction\0", true)]               // RG2-01 empty-subsection
    [InlineData(0, "hook.gitsweep-x.event\0", true)]                             // value-less key
    [InlineData(0, "hook.gitsweep-x.event\n\0", true)]                           // empty value
    [InlineData(0, "hook.a.event\npre-commit\0hook.b.event\nreference-transaction\0", true)] // one of several
    public void ClassifyConfiguredHookEvents_fails_closed(int exitCode, string output, bool refused)
    {
        var result = GitRepositoryService.ClassifyConfiguredHookEvents(exitCode, output);
        Assert.Equal(refused, result is not null);
    }

    [Fact]
    public void ClassifyConfiguredHookEvents_sanitizes_the_event_name_and_caps_the_list()
    {
        // A control character and a bidi override in the event value become '?'
        // and never reach the message raw.
        var hostile = GitRepositoryService.ClassifyConfiguredHookEvents(0, "hook.x.event\nref\u0007‮tx\0");
        Assert.NotNull(hostile);
        Assert.Contains("ref??tx", hostile);
        Assert.DoesNotContain('\u0007', hostile);
        Assert.DoesNotContain('‮', hostile);

        // Seven distinct disallowed events: the message names five and summarizes the rest.
        var many = string.Concat(Enumerable.Range(0, 7).Select(i => $"hook.h{i}.event\nbad-event-{i}\0"));
        var capped = GitRepositoryService.ClassifyConfiguredHookEvents(0, many);
        Assert.NotNull(capped);
        Assert.Contains("and 2 more", capped);
    }

    [Fact]
    public async Task Configured_hook_for_a_harmless_event_is_allowed_and_clean_proceeds()
    {
        // pre-commit is an event GitSweep never fires, so a configured hook
        // for it is allowed (and never runs). Works on every git: the config
        // listing is read the same way on 2.43.
        var fixture = await BuildFileOriginAsync("cfghook-allow");
        var marker = Path.Combine(_root, "cfghook-allow-marker.txt");
        var hook = await WriteScriptAsync("cfghook-allow.sh", marker, "config-hook", drainStdin: true, exitCode: 0);
        await Git(fixture.Repo, "config", "hook.gitsweep-precommit.event", "pre-commit");
        await Git(fixture.Repo, "config", "hook.gitsweep-precommit.command", $"{hook} pre-commit");

        var branch = Assert.Single(await _sut.GetLocalBranchesAsync(fixture.Repo, "main"), b => b.Name == "feature/done");
        Assert.True(branch.IsMerged);
        var (deleted, message) = await _sut.DeleteBranchAsync(fixture.Repo, "feature/done");

        Assert.True(deleted, message);
        Assert.False(File.Exists(marker), "the pre-commit configured hook ran");
    }

    private static async Task AssertHookRefusedAsync(Func<Task> call, string sanitizedEvent)
    {
        var ex = await Assert.ThrowsAsync<RepositoryHookNotSupportedException>(call);
        Assert.Contains("refuses to run", ex.Message);
        Assert.Contains(sanitizedEvent, ex.Message);
    }

    // Mirrors GitRepositoryService.SanitizeHookEventName for the assertion.
    private static string SanitizedEventName(string value) =>
        new string(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '?').ToArray());

    // ---- P1-T2: hook-directory hooks and auto-maintenance on the fetch and keep-ref paths ----

    [Fact]
    public async Task Hook_directory_hooks_and_auto_maintenance_never_run_on_the_fetch_or_keep_ref_updates()
    {
        var fixture = await BuildFileOriginAsync("hookdir");
        var marker = Path.Combine(_root, "hookdir-marker.txt");
        var hooks = Path.Combine(_root, "hookdir-hooks");
        Directory.CreateDirectory(hooks);
        foreach (var hookName in new[] { "reference-transaction", "pre-auto-gc" })
        {
            await TestScripts.WriteExecutableAsync(Path.Combine(hooks, hookName), MarkerScript(marker, "hookdir " + hookName, drainStdin: true, exitCode: 0));
        }

        await Git(fixture.Repo, "config", "core.hooksPath", hooks.Replace('\\', '/'));
        await EnableAutoGcAsync(fixture);

        await RunEveryRefUpdatingEntryPointAsync(fixture);
        Assert.False(File.Exists(marker), "a hook-directory hook ran during a GitSweep command:\n" + await ReadMarkerAsync(marker));

        // Positive control: with this configuration a plain fetch runs the
        // repository's reference-transaction hook for its ref updates and,
        // through auto-maintenance, its pre-auto-gc hook.
        await PushNewOriginCommitAsync(fixture, "control");
        await Git(fixture.Repo, "fetch", "origin");
        var control = await ReadMarkerAsync(marker);
        Assert.Contains("hookdir reference-transaction", control);
        Assert.Contains("hookdir pre-auto-gc", control);
    }

    // ---- P1b-01: protocol.<x>.allow outranks the pinned protocol.allow=never ----

    [Theory]
    [InlineData("scheme")]
    [InlineData("insteadOf")]
    [InlineData("remote-url-alias")]
    public Task Remote_helper_enabled_by_repository_protocol_config_never_runs(string variant) =>
        AssertRepositoryEnabledRemoteHelperNeverRunsAsync(variant);

    // git 2.43 stops a fetch by URL whose only section is `vcs` ("has no
    // configured URL") before any helper runs, so the control needs 2.55.
    [UrlScopedVcsFact]
    public Task Remote_helper_named_by_a_remote_url_vcs_section_never_runs() =>
        AssertRepositoryEnabledRemoteHelperNeverRunsAsync("vcs");

    private async Task AssertRepositoryEnabledRemoteHelperNeverRunsAsync(string variant)
    {
        var repo = await InitRepoAsync("helper-" + variant.ToLowerInvariant());
        var marker = Path.Combine(_root, "helper-" + variant + "-marker.txt");
        var script = await WriteScriptAsync("helper-" + variant + ".sh", marker, "remote-helper", drainStdin: false, exitCode: 1);
        // No git-remote-foo exists, so git expands the repository's alias:
        // a shell command chosen by .git/config alone.
        await Git(repo, "config", "protocol.foo.allow", "always");
        await Git(repo, "config", "alias.remote-foo", "!" + script);
        var origin = $"https://127.0.0.1:1/{variant}.git";
        switch (variant)
        {
            case "scheme":
                origin = "foo::bar";
                break;
            case "vcs":
                await Git(repo, "config", $"remote.{origin}.vcs", "foo");
                break;
            case "insteadOf":
                await Git(repo, "config", "url.foo::bar.insteadOf", origin);
                break;
            default:
                await Git(repo, "config", $"remote.{origin}.url", "foo::bar");
                break;
        }

        await Git(repo, "remote", "add", "origin", origin);
        var fetchUrl = (await Git(repo, "remote", "get-url", "origin")).Trim();

        // Positive control: the previous hardening's fetch runs the helper.
        await PreviousHardenedGit(repo, allowFailure: true, PreviousFetch(fetchUrl));
        Assert.Contains("remote-helper", await ReadMarkerAsync(marker));
        File.Delete(marker);

        var fetch = await _sut.FetchPruneAsync(repo);

        Assert.False(fetch.Success);
        Assert.False(File.Exists(marker), $"{variant}: the repository-enabled remote helper ran");
    }

    // ---- P1b-02: core.gitProxy is first-match-wins ----

    [Fact]
    public async Task Repository_gitProxy_never_runs_for_a_git_protocol_origin()
    {
        var repo = await InitRepoAsync("gitproxy");
        var marker = Path.Combine(_root, "gitproxy-marker.txt");
        // git runs core.gitProxy directly (no shell) with host and port.
        var script = await WriteScriptAsync("gitproxy.sh", marker, "git-proxy", drainStdin: false, exitCode: 1);
        await Git(repo, "config", "core.gitProxy", script);
        // Port 1 on loopback: nothing listens, a direct connection is refused.
        await Git(repo, "remote", "add", "origin", "git://127.0.0.1:1/proxy.git");

        // Positive control: the previous pin `-c core.gitProxy=` comes after
        // the repository value, which git uses because it was found first.
        await PreviousHardenedGit(repo, allowFailure: true, PreviousFetch("git://127.0.0.1:1/proxy.git"));
        Assert.Contains("git-proxy 127.0.0.1 1", await ReadMarkerAsync(marker));
        File.Delete(marker);

        var fetch = await _sut.FetchPruneAsync(repo);

        Assert.False(fetch.Success);
        Assert.False(File.Exists(marker), "the repository's core.gitProxy program ran");
    }

    // ---- P1b-03 / DOC-V1: core.alternateRefsCommand with objects/info/alternates ----

    [Fact]
    public async Task Repository_alternateRefsCommand_never_runs_during_the_fetch()
    {
        var fixture = await BuildFileOriginAsync("altrefs");
        var marker = Path.Combine(_root, "altrefs-marker.txt");
        var script = await WriteScriptAsync("altrefs.sh", marker, "alternate-refs", drainStdin: false, exitCode: 0);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Repo, ".git", "objects", "info", "alternates"),
            Path.Combine(fixture.Origin, "objects").Replace('\\', '/') + "\n");
        await Git(fixture.Repo, "config", "core.alternateRefsCommand", script);

        // Positive control: the previous hardening's fetch lists the
        // alternate's refs by running the configured command through the shell.
        await PreviousHardenedGit(fixture.Repo, PreviousFetch(fixture.Url));
        Assert.Contains("alternate-refs", await ReadMarkerAsync(marker));
        File.Delete(marker);

        var fetch = await _sut.FetchPruneAsync(fixture.Repo);

        Assert.True(fetch.Success, fetch.ErrorMessage);
        Assert.False(File.Exists(marker), "the repository's core.alternateRefsCommand ran during the fetch");
    }

    // ---- P1b-05: fetch.bundleURI ----

    [Fact]
    public async Task Repository_bundle_uri_is_never_fetched()
    {
        var fixture = await BuildFileOriginAsync("bundle");
        await Git(fixture.Seed, "checkout", "-b", "bundled");
        await CommitFileAsync(fixture.Seed, "bundled.txt", "bundled", "bundled work");
        var bundle = Path.Combine(_root, "bundle.bundle");
        await Git(fixture.Seed, "bundle", "create", bundle, "bundled");
        await Git(fixture.Seed, "checkout", "main");
        await Git(fixture.Repo, "config", "fetch.bundleURI", bundle.Replace('\\', '/'));

        // Positive control: the previous hardening's fetch first copies the
        // repository-chosen bundle and writes its refs under refs/bundles/.
        await PreviousHardenedGit(fixture.Repo, PreviousFetch(fixture.Url));
        Assert.Contains("refs/bundles/", await Git(fixture.Repo, "for-each-ref", "--format=%(refname)"));
        foreach (var refName in (await Git(fixture.Repo, "for-each-ref", "--format=%(refname)", "refs/bundles/")).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            await Git(fixture.Repo, "update-ref", "-d", refName.Trim());
        }

        var fetch = await _sut.FetchPruneAsync(fixture.Repo);

        Assert.True(fetch.Success, fetch.ErrorMessage);
        Assert.Equal(string.Empty, (await Git(fixture.Repo, "for-each-ref", "--format=%(refname)", "refs/bundles/")).Trim());
        var (tokenExit, _, _) = await RunGitAsync(fixture.Repo, ["config", "--get", "fetch.bundleCreationToken"]);
        Assert.Equal(1, tokenExit);
    }

    // ---- P1b-06: fetch.pruneTags / remote.<url>.pruneTags ----

    [Fact]
    public async Task Repository_pruneTags_never_prunes_local_tags_during_the_fetch()
    {
        var fixture = await BuildFileOriginAsync("prunetags");
        await Git(fixture.Repo, "config", "fetch.pruneTags", "true");
        await Git(fixture.Repo, "config", $"remote.{fixture.Url}.pruneTags", "true");
        await Git(fixture.Repo, "tag", "local-only-tag", "HEAD");

        // Positive control: with this configuration a plain prune-fetch of
        // origin deletes the local tag the remote does not have.
        await Git(fixture.Repo, "fetch", "--prune", "origin");
        Assert.DoesNotContain("local-only-tag", await Git(fixture.Repo, "tag", "--list"));
        await Git(fixture.Repo, "tag", "local-only-tag", "HEAD");

        var fetch = await _sut.FetchPruneAsync(fixture.Repo);

        Assert.True(fetch.Success, fetch.ErrorMessage);
        Assert.Contains("local-only-tag", await Git(fixture.Repo, "tag", "--list"));
    }

    // ---- P1b-04: http.<url>.cookieFile + http.<url>.saveCookies ----

    [Fact]
    public async Task Repository_url_scoped_saveCookies_refuses_the_fetch_and_no_cookie_file_is_written()
    {
        // The remote answers the dumb-HTTP ref advertisement (no refs) with a
        // cookie; everything else is 404.
        await using var server = new TestHttpServer(request =>
            request.StartsWith("GET /cookies.git/info/refs", StringComparison.Ordinal)
                ? TestHttpServer.Response(200, "OK", "Set-Cookie: gitsweep=planted-by-remote; Path=/\r\n")
                : TestHttpServer.Response(404, "Not Found"));
        var repo = await InitRepoAsync("cookies");
        var url = server.Url + "/cookies.git";
        var jar = Path.Combine(_root, "outside-the-repository.txt");
        await Git(repo, "remote", "add", "origin", url);
        await Git(repo, "config", $"http.{server.Url}/.cookieFile", jar.Replace('\\', '/'));
        await Git(repo, "config", $"http.{server.Url}/.saveCookies", "true");

        // Positive control: the URL-scoped keys win over any generic setting,
        // and the fetch makes curl write the server's cookie to the
        // repository-chosen path.
        await PreviousHardenedGit(repo, "-c", "http.saveCookies=false", allowFailure: true, PreviousFetch(url));
        Assert.True(File.Exists(jar), "positive control: git did not write the cookie file");
        Assert.Contains("planted-by-remote", await File.ReadAllTextAsync(jar));
        File.Delete(jar);
        var requestsBefore = server.Requests.Count;

        var fetch = await _sut.FetchPruneAsync(repo);

        Assert.False(fetch.Success);
        Assert.Contains("http.<url>.saveCookies", fetch.ErrorMessage);
        Assert.False(File.Exists(jar), "the fetch wrote the repository-chosen cookie file");
        Assert.Equal(requestsBefore, server.Requests.Count);
    }

    // RR-5: a URL-scoped http.<url>.saveCookies set to false does not refuse
    // the fetch (the only reliable signal is "true"); a working origin fetch
    // then succeeds.
    [Fact]
    public async Task Repository_url_scoped_saveCookies_false_does_not_refuse_the_fetch()
    {
        var fixture = await BuildFileOriginAsync("cookies-false");
        await Git(fixture.Repo, "config", "http.http://example.invalid/.saveCookies", "false");

        var fetch = await _sut.FetchPruneAsync(fixture.Repo);

        Assert.True(fetch.Success, fetch.ErrorMessage);
    }

    // RR-5: a non-boolean http.<url>.saveCookies cannot be classified, so the
    // fetch refuses (fail closed) with the "could not check" message rather
    // than proceeding. The fixture's origin is reachable, so only the refusal
    // can keep the fetch from succeeding.
    [Fact]
    public async Task Repository_url_scoped_saveCookies_non_boolean_refuses_the_fetch()
    {
        var fixture = await BuildFileOriginAsync("cookies-nonbool");
        await Git(fixture.Repo, "config", "http.http://example.invalid/.saveCookies", "maybe");

        var fetch = await _sut.FetchPruneAsync(fixture.Repo);

        Assert.False(fetch.Success);
        // The "Could not check" wording, not the "sets ... to true" one: a
        // non-boolean value must be classified as unverifiable, not as true.
        Assert.Contains("Could not check", fetch.ErrorMessage);
        Assert.Contains("http.<url>.saveCookies", fetch.ErrorMessage);
    }

    // ---- P1-T4: credential helpers and askpass on an authentication challenge ----

    [Fact]
    public async Task Repository_credential_helpers_and_askpass_never_run_on_an_authentication_challenge()
    {
        await using var server = new TestHttpServer(_ =>
            TestHttpServer.Response(401, "Unauthorized", "WWW-Authenticate: Basic realm=\"gitsweep\"\r\n"));
        var repo = await InitRepoAsync("credentials");
        var helperMarker = Path.Combine(_root, "credential-helper-marker.txt");
        var urlHelperMarker = Path.Combine(_root, "credential-url-helper-marker.txt");
        var askPassMarker = Path.Combine(_root, "askpass-marker.txt");
        var helper = await WriteScriptAsync("credential-helper.sh", helperMarker, "credential.helper", drainStdin: true, exitCode: 0);
        var urlHelper = await WriteScriptAsync("credential-url-helper.sh", urlHelperMarker, "credential.<url>.helper", drainStdin: true, exitCode: 0);
        var askPass = await WriteScriptAsync("askpass.sh", askPassMarker, "core.askPass", drainStdin: false, exitCode: 1);
        await Git(repo, "config", "credential.helper", "!" + helper);
        await Git(repo, "config", $"credential.{server.Url}.helper", urlHelper);
        await Git(repo, "config", "core.askPass", askPass);
        await Git(repo, "remote", "add", "origin", server.Url + "/private.git");

        // Positive control: plain git answers the challenge by running both
        // configured helpers and then the configured askpass program.
        await RunGitAsync(repo, ["ls-remote", "origin"]);
        Assert.Contains("credential.helper get", await ReadMarkerAsync(helperMarker));
        Assert.Contains("credential.<url>.helper get", await ReadMarkerAsync(urlHelperMarker));
        Assert.Contains("core.askPass", await ReadMarkerAsync(askPassMarker));
        File.Delete(helperMarker);
        File.Delete(urlHelperMarker);
        File.Delete(askPassMarker);

        var fetch = await _sut.FetchPruneAsync(repo);

        Assert.False(fetch.Success);
        Assert.False(File.Exists(helperMarker), "the repository's credential.helper ran");
        Assert.False(File.Exists(urlHelperMarker), "the repository's credential.<url>.helper ran");
        Assert.False(File.Exists(askPassMarker), "the repository's core.askPass ran");
    }

    // ---- P1-T3: core.fsmonitor across every entry point ----

    [Fact]
    public async Task Repository_fsmonitor_never_runs_from_any_entry_point()
    {
        var fixture = await BuildFileOriginAsync("fsmonitor");
        var marker = Path.Combine(_root, "fsmonitor-marker.txt");
        var script = await WriteScriptAsync("fsmonitor.sh", marker, "fsmonitor", drainStdin: false, exitCode: 1);
        await Git(fixture.Repo, "config", "core.fsmonitor", script);

        // Positive control: a command that refreshes the index runs it.
        await Git(fixture.Repo, "status");
        Assert.Contains("fsmonitor", await ReadMarkerAsync(marker));
        File.Delete(marker);

        Assert.True(await _sut.IsGitRepositoryAsync(fixture.Repo));
        Assert.Equal("main", await _sut.GetDefaultBranchNameAsync(fixture.Repo));
        Assert.Contains(await _sut.GetLocalBranchesAsync(fixture.Repo, "main"), b => b.Name == "feature/done");
        _ = await _sut.GetBranchRiskSignalsAsync(fixture.Repo, "main", "feature/done");
        _ = await _sut.IsSquashOrRebaseMergedAsync(fixture.Repo, "main", "feature/done");
        Assert.Equal("main", await _sut.GetCurrentBranchAsync(fixture.Repo));
        _ = await _sut.GetWorktreeBranchesAsync(fixture.Repo);
        Assert.NotNull(await _sut.GetRepositoryRootAsync(fixture.Repo));
        await RunEveryRefUpdatingEntryPointAsync(fixture);

        Assert.False(File.Exists(marker), "the repository's core.fsmonitor program ran during a GitSweep command");
    }

    // ---- I5: replace refs and grafts fake ancestry ----

    [Theory]
    [InlineData("replace")]
    [InlineData("grafts")]
    public async Task Forged_history_cannot_fake_a_merge_or_the_reachability_of_unique_commits(string vector)
    {
        var repo = await InitRepoAsync("history-" + vector);
        await CommitFileAsync(repo, "README.md", "base", "base", "2020-01-01T00:00:00Z");
        var baseCommit = (await Git(repo, "rev-parse", "HEAD")).Trim();
        await Git(repo, "checkout", "-b", "feature/unique");
        await CommitFileAsync(repo, "unique.txt", "unique work", "unique work", "2020-01-02T00:00:00Z");
        var unique = (await Git(repo, "rev-parse", "HEAD")).Trim();
        await Git(repo, "checkout", "main");
        await CommitFileAsync(repo, "main.txt", "main work", "main work", "2020-01-03T00:00:00Z");
        var mainTip = (await Git(repo, "rev-parse", "HEAD")).Trim();

        // Each vector makes git believe the unique commit is a second parent
        // of main's tip, without touching any commit object.
        switch (vector)
        {
            case "replace":
                var tree = (await Git(repo, "rev-parse", "main^{tree}")).Trim();
                var fake = (await Git(repo, "commit-tree", tree, "-p", baseCommit, "-p", unique, "-m", "main work")).Trim();
                await Git(repo, "update-ref", $"refs/replace/{mainTip}", fake);
                break;
            default:
                await File.WriteAllTextAsync(Path.Combine(repo, ".git", "info", "grafts"), $"{mainTip} {baseCommit} {unique}\n");
                break;
        }

        // Positive control: with the previous hardening git now reports the
        // branch as merged and its tip as contained in main.
        var (ancestorExit, _, _) = await RunGitAsync(repo, [.. PreviousHardenedOptions, "merge-base", "--is-ancestor", unique, "main"]);
        Assert.Equal(0, ancestorExit);
        Assert.Contains("refs/heads/main", await PreviousHardenedGit(repo, "for-each-ref", "--contains", unique, "--format=%(refname)", "refs/heads/"));
        Assert.Contains("feature/unique", await PreviousHardenedGit(repo, "branch", "--merged", "refs/heads/main"));

        var branch = Assert.Single(await _sut.GetLocalBranchesAsync(repo, "main"), b => b.Name == "feature/unique");
        var signals = await _sut.GetBranchRiskSignalsAsync(repo, "main", "feature/unique");
        var (deleted, message) = await _sut.DeleteBranchAsync(repo, "feature/unique");

        Assert.False(branch.IsMerged, $"{vector}: the scan reported the branch as merged");
        Assert.Equal(BranchMergeKind.NotMerged, branch.MergeKind);
        Assert.False(signals.IsReallyMerged, $"{vector}: the risk signals reported the branch as merged");
        Assert.Equal(1, signals.UniqueCommitCount);
        Assert.False(deleted, $"{vector}: the branch with a unique commit was deleted");
        Assert.Contains("not reachable from any other branch", message);
        Assert.Equal(unique, await _sut.GetBranchTipShaAsync(repo, "feature/unique"));
    }

    // ---- shared flows ----

    // Every GitSweep entry point that updates a ref (each one fires
    // reference-transaction): the fetch's remote-tracking updates, the
    // keep-ref creation, `git branch -d`, the branch recreation and the
    // keep-ref deletion. Leaves the branch restored at its tip.
    private async Task RunEveryRefUpdatingEntryPointAsync(OriginFixture fixture)
    {
        await PushNewOriginCommitAsync(fixture, "gitsweep");
        var fetch = await _sut.FetchPruneAsync(fixture.Repo);
        Assert.True(fetch.Success, fetch.ErrorMessage);

        var tip = await _sut.GetBranchTipShaAsync(fixture.Repo, "feature/done");
        Assert.NotNull(tip);
        var trash = new TrashService(_sut);
        var entry = await trash.SaveAsync(fixture.Repo, "feature/done", tip!, Guid.NewGuid());
        var (deleted, deleteMessage) = await _sut.DeleteBranchAsync(fixture.Repo, "feature/done");
        Assert.True(deleted, deleteMessage);
        var (created, createMessage) = await _sut.CreateBranchAsync(fixture.Repo, "feature/done", tip!);
        Assert.True(created, createMessage);
        Assert.True(await trash.RemoveAsync(fixture.Repo, entry.Id));
        Assert.Equal(tip, await _sut.GetBranchTipShaAsync(fixture.Repo, "feature/done"));
        Assert.Equal(string.Empty, (await Git(fixture.Repo, "for-each-ref", "--format=%(refname)", "refs/gitsweep/")).Trim());
    }

    // Repository config that makes a plain fetch run auto-maintenance's gc
    // task synchronously (git 2.55 defaults to another strategy), plus two
    // packs so that `gc --auto` has work to do (gc.autoPackLimit=1).
    private async Task EnableAutoGcAsync(OriginFixture fixture)
    {
        await Git(fixture.Repo, "config", "maintenance.strategy", "gc");
        await Git(fixture.Repo, "config", "gc.auto", "1");
        await Git(fixture.Repo, "config", "gc.autoPackLimit", "1");
        await Git(fixture.Repo, "config", "gc.autoDetach", "false");
        await Git(fixture.Repo, "config", "maintenance.autoDetach", "false");
        for (var round = 0; round < 2; round++)
        {
            await PushNewOriginCommitAsync(fixture, "pack" + round);
            await Git(fixture.Repo, "-c", "gc.auto=0", "-c", "maintenance.auto=false", "-c", "core.hooksPath=" + NullDevice,
                "-c", "hook.reference-transaction.enabled=false", "fetch", "origin");
            await Git(fixture.Repo, "-c", "gc.auto=0", "repack", "-q");
        }

        Assert.True(Directory.GetFiles(Path.Combine(fixture.Repo, ".git", "objects", "pack"), "*.pack").Length >= 2);
    }

    private sealed record OriginFixture(string Origin, string Url, string Seed, string Repo);

    // A bare origin (HEAD -> main) with main and feature/done (an ancestor of
    // main), a seed clone used to push new commits, and the repository under
    // test: a file:// clone with a local feature/done, main checked out.
    private async Task<OriginFixture> BuildFileOriginAsync(string name)
    {
        var origin = Path.Combine(_root, name + "-origin.git");
        await Git(_root, "init", "--bare", "-b", "main", origin);
        var seed = await InitRepoAsync(name + "-seed");
        await CommitFileAsync(seed, "README.md", "initial", "initial commit");
        await Git(seed, "branch", "feature/done");
        await CommitFileAsync(seed, "main.txt", "main", "main work");
        await Git(seed, "remote", "add", "origin", origin);
        await Git(seed, "push", "-u", "origin", "main", "feature/done");

        var url = "file://" + origin.Replace('\\', '/');
        var repo = Path.Combine(_root, name + "-repo");
        await Git(_root, "clone", url, repo);
        await SetIdentityAsync(repo);
        await Git(repo, "branch", "feature/done", "origin/feature/done");
        return new OriginFixture(origin, url, seed, repo);
    }

    private async Task PushNewOriginCommitAsync(OriginFixture fixture, string label)
    {
        await CommitFileAsync(fixture.Seed, label + "-" + Guid.NewGuid().ToString("N") + ".txt", label, label);
        await Git(fixture.Seed, "push", "origin", "main");
    }

    private async Task<string> InitRepoAsync(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        await Git(path, "init", "-b", "main");
        await SetIdentityAsync(path);
        return path;
    }

    private async Task SetIdentityAsync(string repo)
    {
        await Git(repo, "config", "user.email", "tests@example.com");
        await Git(repo, "config", "user.name", "GitSweep Tests");
    }

    private async Task CommitFileAsync(string repo, string fileName, string content, string message, string? date = null)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, fileName), content);
        await Git(repo, "add", ".");
        var environment = date is null
            ? null
            : new Dictionary<string, string> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date };
        var (exitCode, stdout, stderr) = await RunGitAsync(repo, ["commit", "-m", message, "--no-gpg-sign"], environment);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git commit failed: {stderr}{stdout}");
        }
    }

    // A marker script: appends "<label> <arguments>" to the marker file. Hook
    // scripts drain stdin (git feeds some hooks on stdin); program scripts
    // must not, because a remote helper or proxy that waits for EOF on stdin
    // would deadlock with git. Returns the path with forward slashes: git
    // runs most of these through its bundled `sh`, which treats '\' as an
    // escape character.
    private async Task<string> WriteScriptAsync(string fileName, string marker, string label, bool drainStdin, int exitCode)
    {
        var path = Path.Combine(_root, fileName);
        await TestScripts.WriteExecutableAsync(path, MarkerScript(marker, label, drainStdin, exitCode));
        return path.Replace('\\', '/');
    }

    private static string MarkerScript(string marker, string label, bool drainStdin, int exitCode) =>
        "#!/bin/sh\n" +
        $"echo \"{label} $*\" >> '{marker.Replace('\\', '/')}'\n" +
        (drainStdin ? "cat >/dev/null 2>&1\n" : string.Empty) +
        $"exit {exitCode}\n";

    private static async Task<string> ReadMarkerAsync(string marker) =>
        File.Exists(marker) ? await File.ReadAllTextAsync(marker) : string.Empty;

    // ---- test-side git ----

    private Task<string> Git(string workingDirectory, params string[] arguments) =>
        GitChecked(workingDirectory, arguments, allowFailure: false);

    private Task<string> PreviousHardenedGit(string workingDirectory, params string[] arguments) =>
        GitChecked(workingDirectory, [.. PreviousHardenedOptions, .. arguments], allowFailure: false);

    private Task<string> PreviousHardenedGit(string workingDirectory, bool allowFailure, string[] arguments) =>
        GitChecked(workingDirectory, [.. PreviousHardenedOptions, .. arguments], allowFailure);

    private Task<string> PreviousHardenedGit(string workingDirectory, string extraOption, string extraValue, bool allowFailure, string[] arguments) =>
        GitChecked(workingDirectory, [.. PreviousHardenedOptions, extraOption, extraValue, .. arguments], allowFailure);

    private async Task<string> GitChecked(string workingDirectory, string[] arguments, bool allowFailure)
    {
        var (exitCode, stdout, stderr) = await RunGitAsync(workingDirectory, arguments);
        if (exitCode != 0 && !allowFailure)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({exitCode}): {stderr}{stdout}");
        }

        return stdout;
    }

    // Test-side git: the same absolute git as GitSweep, no system or global
    // configuration, never a terminal prompt, and none of the variables
    // GitSweep now pins, so a control shows what git does without them.
    private async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string workingDirectory, string[] arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GitExecutableLocator.GetRequiredPath(),
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var (key, value) in IsolatedEnvironment()) startInfo.Environment[key] = value;
        foreach (var name in new[] { "GIT_ALLOW_PROTOCOL", "GIT_PROXY_COMMAND", "GIT_GRAFT_FILE", "GIT_ASKPASS", "SSH_ASKPASS", "GIT_DIR", "GIT_WORK_TREE" })
        {
            startInfo.Environment.Remove(name);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment) startInfo.Environment[key] = value;
        }

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private Dictionary<string, string> IsolatedEnvironment() => new()
    {
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_CONFIG_GLOBAL"] = _emptyGlobalConfig,
        ["GIT_TERMINAL_PROMPT"] = "0"
    };
}
