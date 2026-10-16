using GitSweep.Core.Models;
using NSubstitute;

namespace GitSweep.Cli.Tests;

/// <summary>
/// Argument matcher for <see cref="Core.Services.IBranchAnalyzer"/> stubs.
/// </summary>
/// <remarks>
/// clean hands the analyzer the unprotected list that
/// <see cref="Core.Services.IBranchProtectionService.FilterProtected"/>
/// builds, which is always a new list. A stub keyed on the list returned by
/// <c>GetLocalBranchesAsync</c> matches by reference and only ever matched
/// through the removed O12 fallback (<c>unprotected = allBranches</c> when
/// nothing was protected). Stubs therefore match the branches by content.
/// </remarks>
internal static class AnalyzerInput
{
    public static IReadOnlyList<BranchInfo> Matching(IReadOnlyList<BranchInfo> expected) =>
        Arg.Is<IReadOnlyList<BranchInfo>>(actual => actual.SequenceEqual(expected));
}
