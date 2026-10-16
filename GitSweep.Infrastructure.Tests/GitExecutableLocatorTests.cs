using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

/// <summary>
/// SEC-001: git must be launched from a trusted absolute path, never by bare
/// name. On Windows a bare name makes CreateProcess search the current
/// directory first, so a planted <c>git.exe</c> inside a hostile clone would
/// run instead of the real git. These tests exercise the pure resolver with
/// the platform parameterized and a fake file system probe, so they behave
/// the same on Windows and Linux CI and never touch the real current
/// directory (tests run in parallel).
/// </summary>
public sealed class GitExecutableLocatorTests
{
    // Directorio absoluto inventado para el sistema operativo que ejecuta la
    // prueba. No contiene ':' para que tambien se pueda unir con ':' (modo
    // no-Windows) aun cuando el host sea Windows; en Windows se usa una ruta
    // UNC porque una ruta con letra de unidad contiene ':'.
    private static string Abs(string name) =>
        OperatingSystem.IsWindows()
            ? @"\\gitsweep-test\share\" + name
            : "/gitsweep-test/" + name;

    private static string Join(bool isWindows, params string[] entries) =>
        string.Join(isWindows ? ';' : ':', entries);

    private static string Candidate(string directory, bool isWindows) =>
        Path.Combine(directory, isWindows ? "git.exe" : "git");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_SkipsRelativeAndEmptyEntries_EvenWhenAGitBinaryExistsThere(bool isWindows)
    {
        var probed = new List<string>();
        var path = Join(isWindows, ".", "tools", @".\bin", @"..\up", @"\rooted", "C:drive-relative", "", "   ", "\t");

        var result = GitExecutableLocator.Resolve(path, isWindows, candidate =>
        {
            probed.Add(candidate);
            return true; // aunque "exista" un git en cualquier sitio, no debe aceptarse
        });

        Assert.Null(result);
        Assert.Empty(probed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_NeverProbesNonFullyQualifiedCandidates(bool isWindows)
    {
        var probed = new List<string>();
        var good = Abs("good");
        var path = Join(isWindows, ".", "tools", good, @".\bin");

        var result = GitExecutableLocator.Resolve(path, isWindows, candidate =>
        {
            probed.Add(candidate);
            return false;
        });

        Assert.Null(result);
        Assert.NotEmpty(probed);
        Assert.All(probed, candidate => Assert.True(Path.IsPathFullyQualified(candidate), candidate));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_SkipsRelativeEntryAndReturnsLaterAbsoluteEntry(bool isWindows)
    {
        var dir = Abs("trusted");
        var expected = Candidate(dir, isWindows);
        var path = Join(isWindows, ".", "tools", dir);

        var result = GitExecutableLocator.Resolve(path, isWindows, candidate => candidate == expected);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_FirstAbsoluteEntryContainingTheCandidateWins(bool isWindows)
    {
        var first = Abs("first");
        var second = Abs("second");
        var third = Abs("third");
        var path = Join(isWindows, first, second, third);

        var bothSecondAndThird = GitExecutableLocator.Resolve(path, isWindows, candidate =>
            candidate == Candidate(second, isWindows) || candidate == Candidate(third, isWindows));
        var allThree = GitExecutableLocator.Resolve(path, isWindows, _ => true);

        Assert.Equal(Candidate(second, isWindows), bothSecondAndThird);
        Assert.Equal(Candidate(first, isWindows), allThree);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_ReturnsFullyQualifiedPath(bool isWindows)
    {
        var dir = Abs("bin");

        var result = GitExecutableLocator.Resolve(dir, isWindows, _ => true);

        Assert.NotNull(result);
        Assert.True(Path.IsPathFullyQualified(result));
    }

    [Fact]
    public void Resolve_Windows_NeverReturnsCmdOrBatShims()
    {
        var dir = Abs("shims");
        var probed = new List<string>();
        var path = Join(true, dir);

        var result = GitExecutableLocator.Resolve(path, isWindows: true, candidate =>
        {
            probed.Add(candidate);
            var name = Path.GetFileName(candidate);
            return name is "git.cmd" or "git.bat" or "git.com" or "git";
        });

        Assert.Null(result);
        // Solo se busca git.exe: un .cmd/.bat lo interpretaria cmd.exe.
        Assert.Equal(new[] { Candidate(dir, true) }, probed);
    }

    [Fact]
    public void Resolve_Windows_ReturnsGitExeEvenWhenShimsAlsoExist()
    {
        var shimDir = Abs("shim");
        var realDir = Abs("real");
        var path = Join(true, shimDir, realDir);

        var result = GitExecutableLocator.Resolve(path, isWindows: true, candidate =>
        {
            var name = Path.GetFileName(candidate);
            return name is "git.cmd" or "git.bat"
                || candidate == Candidate(realDir, true);
        });

        Assert.Equal(Candidate(realDir, true), result);
        Assert.Equal("git.exe", Path.GetFileName(result));
    }

    [Fact]
    public void Resolve_Windows_HandlesQuotedAndPaddedEntries()
    {
        var quoted = Abs("quoted dir");
        var padded = Abs("padded");

        var quotedResult = GitExecutableLocator.Resolve(
            $"\"{quoted}\"", isWindows: true, candidate => candidate == Candidate(quoted, true));
        var paddedResult = GitExecutableLocator.Resolve(
            $"  \"{padded}\"  ", isWindows: true, candidate => candidate == Candidate(padded, true));

        Assert.Equal(Candidate(quoted, true), quotedResult);
        Assert.Equal(Candidate(padded, true), paddedResult);
    }

    [Fact]
    public void Resolve_Windows_QuotedRelativeEntryIsStillRejected()
    {
        var result = GitExecutableLocator.Resolve("\".\";\"tools\"", isWindows: true, _ => true);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_NonWindows_UsesColonSeparatorAndExtensionlessGit()
    {
        var first = Abs("first");
        var second = Abs("second");
        var path = $"{first}:{second}";

        var result = GitExecutableLocator.Resolve(path, isWindows: false, candidate => candidate == Candidate(second, false));

        Assert.Equal(Candidate(second, false), result);
        Assert.Equal("git", Path.GetFileName(result));
    }

    [Fact]
    public void Resolve_NonWindows_DoesNotAcceptGitExe()
    {
        var dir = Abs("bin");

        var result = GitExecutableLocator.Resolve(
            dir, isWindows: false, candidate => Path.GetFileName(candidate) == "git.exe");

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_NonWindows_DoesNotSplitOnSemicolon()
    {
        var first = Abs("first");
        var second = Abs("second");
        // Con ';' como unico separador, en no-Windows todo es una sola entrada
        // (y por tanto un unico directorio), no dos.
        var path = $"{first};{second}";
        var probed = new List<string>();

        _ = GitExecutableLocator.Resolve(path, isWindows: false, candidate =>
        {
            probed.Add(candidate);
            return false;
        });

        Assert.Equal(new[] { Path.Combine(path, "git") }, probed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_NoMatch_ReturnsNull(bool isWindows)
    {
        var path = Join(isWindows, Abs("a"), Abs("b"));

        Assert.Null(GitExecutableLocator.Resolve(path, isWindows, _ => false));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void Resolve_MissingPathVariable_ReturnsNull(string path, bool isWindows)
    {
        Assert.Null(GitExecutableLocator.Resolve(path, isWindows, _ => true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_MalformedEntries_DoNotThrow(bool isWindows)
    {
        var good = Abs("good");
        var path = Join(isWindows, "\0", "<>|?*", "\"", "\"\"", good);

        var result = GitExecutableLocator.Resolve(path, isWindows, candidate => candidate == Candidate(good, isWindows));

        Assert.Equal(Candidate(good, isWindows), result);
    }

    [Fact]
    public void ProcessWideResolution_OnThisMachine_IsFullyQualified()
    {
        var resolved = GitExecutableLocator.ResolvedPath;

        Assert.False(resolved is null, "git must be resolvable on PATH on the test machine.");
        Assert.True(Path.IsPathFullyQualified(resolved), resolved);
        Assert.True(File.Exists(resolved), resolved);
        Assert.Equal(
            OperatingSystem.IsWindows() ? "git.exe" : "git",
            Path.GetFileName(resolved));
    }

    [Fact]
    public void ProcessWideResolution_IsCached()
    {
        Assert.Equal(GitExecutableLocator.ResolvedPath, GitExecutableLocator.ResolvedPath);
        Assert.Equal(GitExecutableLocator.ResolvedPath, GitExecutableLocator.GetRequiredPath());
    }

    [Fact]
    public void Require_WhenGitWasNotFound_FailsClosedWithActionableMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GitExecutableLocator.Require(null));

        Assert.Equal(
            "Git executable was not found on PATH. Install Git and make sure it is on PATH.",
            ex.Message);
    }

    [Fact]
    public void Require_WhenGitWasFound_ReturnsThePathUnchanged()
    {
        var path = Candidate(Abs("bin"), OperatingSystem.IsWindows());

        Assert.Equal(path, GitExecutableLocator.Require(path));
    }

    // OBS-02: the git child's PATH is rebuilt from the same entry filter that
    // Resolve uses, so git can never resolve a helper program (ssh, sh, a
    // remote helper) against its working directory, i.e. inside the
    // possibly-hostile repository.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FilterPathVariable_KeepsOnlyFullyQualifiedEntriesInOrder(bool isWindows)
    {
        var first = Abs("first");
        var second = Abs("second");
        var path = Join(isWindows, ".", first, "", "tools", @".\bin", "   ", @"..\up", second, "C:drive-relative", @"\rooted", "\t");

        var filtered = GitExecutableLocator.FilterPathVariable(path, isWindows);

        Assert.Equal(Join(isWindows, first, second), filtered);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FilterPathVariable_WhenNoEntryIsFullyQualified_ReturnsEmpty(bool isWindows)
    {
        var path = Join(isWindows, ".", "tools", "", @".\bin");

        Assert.Equal(string.Empty, GitExecutableLocator.FilterPathVariable(path, isWindows));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void FilterPathVariable_MissingPathVariable_ReturnsEmpty(string path, bool isWindows)
    {
        Assert.Equal(string.Empty, GitExecutableLocator.FilterPathVariable(path, isWindows));
    }

    [Fact]
    public void FilterPathVariable_Windows_UnquotesAbsoluteEntriesAndDropsQuotedRelativeOnes()
    {
        var quoted = Abs("quoted dir");
        var path = $"\"{quoted}\";\".\";\"tools\"";

        Assert.Equal(quoted, GitExecutableLocator.FilterPathVariable(path, isWindows: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FullyQualifiedEntries_IsTheFilterResolveProbes(bool isWindows)
    {
        // Resolve and the child PATH share one filter: every directory
        // Resolve probes is exactly one of FullyQualifiedEntries, in order.
        var path = Join(isWindows, ".", Abs("a"), "tools", Abs("b"), "");
        var probedDirectories = new List<string>();

        _ = GitExecutableLocator.Resolve(path, isWindows, candidate =>
        {
            probedDirectories.Add(Path.GetDirectoryName(candidate)!);
            return false;
        });

        Assert.Equal(GitExecutableLocator.FullyQualifiedEntries(path, isWindows), probedDirectories);
        Assert.Equal(new[] { Abs("a"), Abs("b") }, GitExecutableLocator.FullyQualifiedEntries(path, isWindows));
    }

    [Fact]
    public void Resolve_IsDeterministicForTheSameInput()
    {
        var path = Join(false, ".", Abs("a"), Abs("b"));
        Func<string, bool> exists = candidate => candidate == Candidate(Abs("b"), false);

        var results = Enumerable.Range(0, 3)
            .Select(_ => GitExecutableLocator.Resolve(path, isWindows: false, exists))
            .Distinct()
            .ToList();

        Assert.Single(results);
    }
}
