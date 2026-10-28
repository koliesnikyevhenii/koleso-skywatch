namespace Skywatch.Core.Normalization;

/// <summary>Both variants get the same cleanup; <see cref="Lower"/> is additionally lowercased (spec F4).</summary>
public sealed record NormalizedText(string Lower, string CasePreserved);
