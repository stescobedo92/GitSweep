using GitSweep.Core.Models;

namespace GitSweep.Core.Services;

/// <summary>
/// Clasifica una rama en el semáforo segura/revisar/peligrosa a partir de
/// su merge real, sus commits únicos y sus commits sin subir.
/// </summary>
public interface IBranchRiskClassifier
{
    BranchRiskAssessment Classify(BranchInfo branch, BranchRiskSignals? signals);
}
