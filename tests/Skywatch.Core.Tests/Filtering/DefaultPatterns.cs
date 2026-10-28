using Skywatch.Core.Filtering;

namespace Skywatch.Core.Tests.Filtering;

/// <summary>The default patterns exactly as in the spec F5 table (the Worker test checks appsettings.json against the same forms).</summary>
internal static class DefaultPatterns
{
    public static readonly PatternDefinition Settlement = new("Settlement", @"\bкрасно[-\s]?павл(?:івк|івц|овк)\w*", CaseSensitive: false);

    public static readonly PatternDefinition Adjective = new("Adjective", @"\bкрасно[-\s]?павл(?:івськ|овск)\w*", CaseSensitive: false);

    public static readonly PatternDefinition Abbreviation = new("Abbreviation", @"\b(?i:на|в|у|над|по|біля|возле|до|через|під|под)\s+К[Пп]\b", CaseSensitive: true);

    public static readonly PatternDefinition Colloquial = new("Colloquial", @"\bкраснопе\b", CaseSensitive: false);

    public static IReadOnlyList<PatternDefinition> All { get; } = [Settlement, Adjective, Abbreviation, Colloquial];
}
