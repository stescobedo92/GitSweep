using GitSweep.Core.Models;
using GitSweep.Core.Services;

namespace GitSweep.Infrastructure.Services;

/// <summary>
/// Semáforo de riesgo fail-closed:
/// segura solo con merge real (ancestro o squash/rebase) y cero commits
/// únicos; peligrosa con commits únicos no mergeados y sin subir;
/// cualquier dato desconocido o intermedio queda en revisar.
/// </summary>
public class BranchRiskClassifier : IBranchRiskClassifier
{
    public BranchRiskAssessment Classify(BranchInfo branch, BranchRiskSignals? signals)
    {
        if (branch is null)
            throw new ArgumentNullException(nameof(branch));

        signals ??= BranchRiskSignals.Unknown;

        // El merge puede venir ya calculado en BranchInfo (GetLocalBranches)
        // o en las señales recién medidas; basta una fuente que lo confirme.
        var isReallyMerged = branch.IsEffectivelyMerged || signals.IsReallyMerged;
        var isUpstreamGone = branch.IsUpstreamGone || signals.IsUpstreamGone;

        if (signals.UniqueCommitCount is null || signals.UnpushedCommitCount is null)
        {
            return new BranchRiskAssessment(branch, BranchRiskLevel.Review, signals,
                "Risk data is unknown; review before deleting.");
        }

        var unique = signals.UniqueCommitCount.Value;
        var unpushed = signals.UnpushedCommitCount.Value;

        if (isReallyMerged && unique == 0)
        {
            return new BranchRiskAssessment(branch, BranchRiskLevel.Safe, signals,
                isUpstreamGone
                    ? "Upstream is gone and the branch is really merged with no unique commits."
                    : "Really merged into the target with no unique commits.");
        }

        if (isUpstreamGone && unique > 0)
        {
            // Gone por sí solo nunca es seguro: si quedan commits únicos no
            // mergeados, ese trabajo solo existe en local (peligrosa cuando
            // además no está subido, revisar en caso contrario).
            return unpushed > 0 || !signals.HasUpstream
                ? new BranchRiskAssessment(branch, BranchRiskLevel.Dangerous, signals,
                    "Upstream is gone but the branch has unique commits that are not merged and not fully pushed.")
                : new BranchRiskAssessment(branch, BranchRiskLevel.Review, signals,
                    "Upstream is gone but the branch has unique commits that are not merged; review before deleting.");
        }

        if (isReallyMerged)
        {
            return new BranchRiskAssessment(branch, BranchRiskLevel.Review, signals,
                "Marked as merged but still has unique commits; review before deleting.");
        }

        if (unique > 0 && (!signals.HasUpstream || unpushed > 0))
        {
            return new BranchRiskAssessment(branch, BranchRiskLevel.Dangerous, signals,
                "Unique commits are not merged and not fully pushed.");
        }

        if (unique > 0)
        {
            return new BranchRiskAssessment(branch, BranchRiskLevel.Review, signals,
                "Unique commits are pushed but not merged into the target.");
        }

        return new BranchRiskAssessment(branch, BranchRiskLevel.Review, signals,
            "No unique commits, but no real merge was confirmed.");
    }
}
