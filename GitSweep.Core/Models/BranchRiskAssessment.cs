namespace GitSweep.Core.Models;

/// <summary>Resultado del semáforo para una rama: nivel, señales y motivo legible.</summary>
public record BranchRiskAssessment(
    BranchInfo Branch,
    BranchRiskLevel Level,
    BranchRiskSignals Signals,
    string Reason);
