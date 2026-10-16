using System.Text.RegularExpressions;
using Xunit;

namespace GitSweep.Cli.Tests;

/// <summary>
/// P1-T1 / P1-T3 (final-coverage-review.md), end to end through the built CLI
/// (<c>gitsweep-cli.dll</c>): one repository whose <c>.git/config</c> carries
/// every program-running family GitSweep knows about (hook-directory hooks for
/// every event, configuration-defined hooks for a few events GitSweep never
/// fires, fsmonitor, pager, gpg, ssh, git proxy, askpass, credential helper,
/// uploadpack, alternate-refs command, remote helper alias, aliases shadowing
/// builtins, external diff, editor, auto-maintenance) plus a bundle URI, is
/// scanned, previewed, cleaned, undone, cleaned with <c>--fetch</c> and
/// restored. No program may run and no ref may appear under
/// <c>refs/bundles/</c>. A twin repository built the same way proves with plain
/// git that the fixture is live. A separate test adds a configured hook for an
/// event GitSweep does fire and asserts every command refuses (RG-01/RG2-01).
/// </summary>
public sealed class HostileRepositoryCliSweepTests : IAsyncLifetime
{
    private static readonly string[] HookEvents =
    [
        "applypatch-msg", "pre-applypatch", "post-applypatch", "pre-commit", "pre-merge-commit",
        "prepare-commit-msg", "commit-msg", "post-commit", "pre-rebase", "post-checkout", "post-merge",
        "pre-push", "pre-receive", "update", "proc-receive", "post-receive", "post-update",
        "reference-transaction", "push-to-checkout", "pre-auto-gc", "post-rewrite", "sendemail-validate",
        "fsmonitor-watchman", "p4-changelist", "p4-prepare-changelist", "p4-post-changelist", "p4-pre-submit",
        "post-index-change"
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitSweepTests", Guid.NewGuid().ToString("N"));

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

    [Fact]
    public async Task No_repository_configured_program_runs_in_any_command()
    {
        var control = await BuildHostileRepositoryAsync("control");
        await AssertFixtureIsLiveAsync(control);

        var fixture = await BuildHostileRepositoryAsync("sweep");
        var tip = await RealGit.TipAsync(fixture.Work, "feature/merged");
        Assert.NotNull(tip);

        // --all keeps the preview from opening the picker (stdin is not a
        // terminal here).
        await RunStepAsync(fixture, "scan", ["-p", fixture.Work, "--dry-run", "--all"]);
        await RunStepAsync(fixture, "clean --dry-run", ["clean", "-p", fixture.Work, "--dry-run", "--merged-only", "--all"]);
        await RunStepAsync(fixture, "clean", ["clean", "-p", fixture.Work, "--merged-only", "--all", "--yes"]);
        Assert.Null(await RealGit.TipAsync(fixture.Work, "feature/merged"));
        await RunStepAsync(fixture, "undo", ["undo", "-p", fixture.Work, "--yes"]);
        Assert.Equal(tip, await RealGit.TipAsync(fixture.Work, "feature/merged"));

        await PushNewOriginCommitAsync(fixture, "sweep");
        await RunStepAsync(fixture, "clean --fetch", ["clean", "-p", fixture.Work, "--fetch", "--merged-only", "--all", "--yes"]);
        Assert.Null(await RealGit.TipAsync(fixture.Work, "feature/merged"));
        await RunStepAsync(fixture, "restore", ["restore", "-p", fixture.Work, "--all", "--yes"]);
        Assert.Equal(tip, await RealGit.TipAsync(fixture.Work, "feature/merged"));

        Assert.Empty(RealGit.TrashFiles(fixture.Work));
        Assert.Empty(await RealGit.KeepRefsAsync(fixture.Work));
        Assert.Equal(string.Empty, (await RealGit.RunAsync(fixture.Work, "for-each-ref", "--format=%(refname)", "refs/bundles/")).Trim());
    }

    private static async Task RunStepAsync(Fixture fixture, string step, string[] arguments)
    {
        var (exitCode, output) = await CliProcess.RunAsync(arguments);
        Assert.True(exitCode == 0, $"{step}: exit {exitCode}\n{output}");
        Assert.False(File.Exists(fixture.Log), $"{step}: a repository-configured program ran:\n{(File.Exists(fixture.Log) ? await File.ReadAllTextAsync(fixture.Log) : string.Empty)}");
    }

    // Plain git in the twin repository runs the planted programs.
    private async Task AssertFixtureIsLiveAsync(Fixture control)
    {
        await RealGit.RunAsync(control.Work, "branch", "probe");
        await RealGit.RunAsync(control.Work, "status");
        await RealGit.RunRawAsync(control.Work, ["ls-remote", "origin"]);
        await PushNewOriginCommitAsync(control, "control");
        await RealGit.RunAsync(control.Work, "fetch", "--upload-pack=git-upload-pack", "origin");

        var log = File.Exists(control.Log) ? await File.ReadAllTextAsync(control.Log) : string.Empty;
        foreach (var expected in new[] { "hookdir reference-transaction", "hookdir pre-auto-gc", "fsmonitor", "uploadpack", "alternate-refs" })
        {
            Assert.True(log.Contains(expected, StringComparison.Ordinal), $"positive control: '{expected}' did not run with plain git:\n{log}");
        }

        Assert.Contains("refs/bundles/", await RealGit.RunAsync(control.Work, "for-each-ref", "--format=%(refname)"));
    }

    // RG-01 / RR-1: a configuration-defined hook for a ref event GitSweep
    // fires (the collision shape that bypasses the event-level pins on
    // git >= 2.55 and is unstoppable on 2.54.x) makes every GitSweep command
    // refuse, through the built CLI, with nothing run and nothing deleted.
    // Version-independent: the refusal comes from reading the config listing,
    // which works on git 2.43 too (there the hook could not run anyway).
    [Fact]
    public async Task Configured_hook_for_a_ref_event_makes_every_command_refuse()
    {
        var fixture = await BuildHostileRepositoryAsync("refuse", dangerousConfigHook: true);
        var tip = await RealGit.TipAsync(fixture.Work, "feature/merged");
        Assert.NotNull(tip);

        // M-2(c): on git >= 2.54 the dangerous collision hook really runs under
        // the pre-wave hardening, so the "log never written" assertions below
        // are not vacuous. (The config listing refusal also holds on 2.43,
        // where the hook could not run.)
        if (await RealGit.ConfigHooksSupportedAsync(fixture.Work))
        {
            await RealGit.RunAsync(fixture.Work, "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
                "-c", "hook.reference-transaction.enabled=false", "branch", "cfghook-probe");
            Assert.True(File.Exists(fixture.Log) && (await File.ReadAllTextAsync(fixture.Log)).Contains("config-hook reference-transaction", StringComparison.Ordinal),
                "positive control: the collision config hook did not run under the pre-wave hardening");
            await RealGit.RunAsync(fixture.Work, "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
                "-c", "hook.gitsweep-evil.enabled=false", "-c", "hook.reference-transaction.enabled=false", "branch", "-D", "cfghook-probe");
            File.Delete(fixture.Log);
        }

        foreach (var (step, arguments) in new (string, string[])[]
        {
            ("scan", ["-p", fixture.Work, "--dry-run", "--all"]),
            ("clean --dry-run", ["clean", "-p", fixture.Work, "--dry-run", "--merged-only", "--all"]),
            ("clean", ["clean", "-p", fixture.Work, "--merged-only", "--all", "--yes"]),
            ("clean --fetch", ["clean", "-p", fixture.Work, "--fetch", "--merged-only", "--all", "--yes"]),
            ("undo", ["undo", "-p", fixture.Work, "--yes"]),
            ("restore", ["restore", "-p", fixture.Work, "--all", "--yes"]),
        })
        {
            var (exitCode, output) = await CliProcess.RunAsync(arguments);
            Assert.True(exitCode != 0, $"{step}: expected a non-zero exit, got {exitCode}\n{output}");
            var flat = Regex.Replace(output, "\u001b\\[[0-9;]*m|\r|\n", string.Empty);
            Assert.Contains("reference-transaction", flat, StringComparison.Ordinal);
            Assert.Contains("refuses to run", flat, StringComparison.Ordinal);
            Assert.False(File.Exists(fixture.Log), $"{step}: a program ran:\n{(File.Exists(fixture.Log) ? await File.ReadAllTextAsync(fixture.Log) : string.Empty)}");
        }

        Assert.Equal(tip, await RealGit.TipAsync(fixture.Work, "feature/merged"));
        Assert.Empty(RealGit.TrashFiles(fixture.Work));
        Assert.Empty(await RealGit.KeepRefsAsync(fixture.Work));
    }

    private sealed record Fixture(string Origin, string Seed, string Work, string Log);

    private async Task<Fixture> BuildHostileRepositoryAsync(string name, bool dangerousConfigHook = false)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var origin = Path.Combine(directory, "origin.git");
        await RealGit.RunAsync(directory, "init", "--bare", "-b", "main", origin);
        var seed = await RealGit.InitRepoAsync(Path.Combine(directory, "seed"));
        await RealGit.CommitFileAsync(seed, "README.md", "initial", "initial commit");
        await RealGit.RunAsync(seed, "branch", "feature/merged");
        await RealGit.CommitFileAsync(seed, "main.txt", "main", "main work");
        await RealGit.RunAsync(seed, "push", origin, "main", "feature/merged");
        await RealGit.RunAsync(seed, "checkout", "-b", "bundled");
        await RealGit.CommitFileAsync(seed, "bundled.txt", "bundled", "bundled work");
        var bundle = Path.Combine(directory, "hostile.bundle");
        await RealGit.RunAsync(seed, "bundle", "create", bundle, "bundled");
        await RealGit.RunAsync(seed, "checkout", "main");

        var work = Path.Combine(directory, "work");
        await RealGit.RunAsync(directory, "clone", origin, work);
        await RealGit.RunAsync(work, "config", "user.email", "tests@example.com");
        await RealGit.RunAsync(work, "config", "user.name", "GitSweep Tests");
        await RealGit.RunAsync(work, "branch", "feature/merged", "origin/feature/merged");
        var fixture = new Fixture(origin, seed, work, Path.Combine(directory, "programs-that-ran.log"));

        // At least two packs, so that auto-maintenance's gc has work to do.
        for (var round = 0; round < 2; round++)
        {
            await PushNewOriginCommitAsync(fixture, "pack" + round);
            await RealGit.RunAsync(work, "fetch", "origin");
            await RealGit.RunAsync(work, "repack", "-q");
        }

        // Every program writes "<label> <args>" to the shared log.
        var scripts = Path.Combine(directory, "scripts");
        var hooks = Path.Combine(directory, "hooks");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(hooks);
        var hook = await WriteScriptAsync(scripts, "config-hook", fixture.Log, drainStdin: true, exitCode: 0);
        // Hook-directory hooks for every event (disabled by core.hooksPath).
        foreach (var hookEvent in HookEvents)
        {
            await WriteScriptAsync(hooks, hookEvent, fixture.Log, drainStdin: true, exitCode: 0, label: "hookdir " + hookEvent);
        }

        // Configuration-defined hooks only for events GitSweep never fires, so
        // the sweep proves such hooks are allowed and still never run. A
        // dangerous event as a config hook is covered by the refusal test.
        foreach (var hookEvent in new[] { "pre-commit", "commit-msg", "post-merge", "pre-push", "post-checkout" })
        {
            await Config(work, $"hook.gitsweep-{hookEvent}.event", hookEvent);
            await Config(work, $"hook.gitsweep-{hookEvent}.command", $"{hook} {hookEvent}");
        }

        if (dangerousConfigHook)
        {
            // RR-1 collision shape: a friendly-named hook for a ref event GitSweep
            // fires, plus a hook.<event>.command key that makes git 2.55 read the
            // event-level pin as a friendly-name switch. GitSweep must refuse.
            await Config(work, "hook.gitsweep-evil.event", "reference-transaction");
            await Config(work, "hook.gitsweep-evil.command", $"{hook} reference-transaction");
            await Config(work, "hook.reference-transaction.command", "true");
        }

        await Config(work, "core.hooksPath", ForwardSlashes(hooks));
        await Config(work, "core.fsmonitor", await WriteScriptAsync(scripts, "fsmonitor", fixture.Log));
        var pager = await WriteScriptAsync(scripts, "pager", fixture.Log, exitCode: 0);
        await Config(work, "core.pager", pager);
        await Config(work, "pager.branch", pager);
        await Config(work, "gpg.program", await WriteScriptAsync(scripts, "gpg", fixture.Log));
        await Config(work, "commit.gpgsign", "true");
        await Config(work, "core.sshCommand", await WriteScriptAsync(scripts, "ssh", fixture.Log));
        await Config(work, "core.gitProxy", await WriteScriptAsync(scripts, "git-proxy", fixture.Log));
        await Config(work, "core.askPass", await WriteScriptAsync(scripts, "askpass", fixture.Log));
        await Config(work, "credential.helper", "!" + await WriteScriptAsync(scripts, "credential-helper", fixture.Log));
        var uploadPack = await WriteScriptAsync(scripts, "uploadpack", fixture.Log);
        await Config(work, "remote.origin.uploadpack", uploadPack);
        var originUrl = (await RealGit.RunAsync(work, "remote", "get-url", "origin")).Trim();
        await Config(work, $"remote.{originUrl}.uploadpack", uploadPack);
        await File.WriteAllTextAsync(Path.Combine(work, ".git", "objects", "info", "alternates"),
            ForwardSlashes(Path.Combine(origin, "objects")) + "\n");
        await Config(work, "core.alternateRefsCommand", await WriteScriptAsync(scripts, "alternate-refs", fixture.Log, exitCode: 0));
        await Config(work, "fetch.bundleURI", ForwardSlashes(bundle));
        await Config(work, "protocol.foo.allow", "always");
        await Config(work, "alias.remote-foo", "!" + await WriteScriptAsync(scripts, "remote-helper", fixture.Log));
        await Config(work, "alias.branch", "!" + await WriteScriptAsync(scripts, "alias-branch", fixture.Log));
        await Config(work, "alias.fetch", "!" + await WriteScriptAsync(scripts, "alias-fetch", fixture.Log));
        await Config(work, "diff.external", await WriteScriptAsync(scripts, "external-diff", fixture.Log));
        await Config(work, "core.editor", await WriteScriptAsync(scripts, "editor", fixture.Log));
        await Config(work, "maintenance.strategy", "gc");
        await Config(work, "gc.auto", "1");
        await Config(work, "gc.autoPackLimit", "1");
        await Config(work, "gc.autoDetach", "false");
        await Config(work, "maintenance.autoDetach", "false");
        return fixture;
    }

    private static async Task PushNewOriginCommitAsync(Fixture fixture, string label)
    {
        await RealGit.CommitFileAsync(fixture.Seed, $"{label}-{Guid.NewGuid():N}.txt", label, label);
        await RealGit.RunAsync(fixture.Seed, "push", fixture.Origin, "main");
    }

    private static Task Config(string repo, string key, string value) =>
        RealGit.RunAsync(repo, "config", key, value);

    // Hook scripts drain stdin (git feeds some hooks on stdin); program
    // scripts must not, or a remote helper waiting for EOF would deadlock
    // with git. Paths use forward slashes: git runs most of these through
    // its bundled `sh`, which reads '\' as an escape.
    private static async Task<string> WriteScriptAsync(
        string directory, string name, string log, bool drainStdin = false, int exitCode = 1, string? label = null)
    {
        var path = Path.Combine(directory, name);
        var content =
            "#!/bin/sh\n" +
            $"echo \"{label ?? name} $*\" >> '{ForwardSlashes(log)}'\n" +
            (drainStdin ? "cat >/dev/null 2>&1\n" : string.Empty) +
            $"exit {exitCode}\n";
        await File.WriteAllTextAsync(path, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return ForwardSlashes(path);
    }

    private static string ForwardSlashes(string path) => path.Replace('\\', '/');
}
