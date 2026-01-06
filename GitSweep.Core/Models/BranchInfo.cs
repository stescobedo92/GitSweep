namespace GitSweep.Core.Models;

public record BranchInfo(string Name, DateTime? LastCommitDate, bool IsMerged);
