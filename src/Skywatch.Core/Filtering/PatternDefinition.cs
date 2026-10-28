namespace Skywatch.Core.Filtering;

/// <summary>A named, configurable regular expression that defines a match (spec F5).</summary>
public sealed record PatternDefinition(string Name, string Regex, bool CaseSensitive, bool Enabled = true);
