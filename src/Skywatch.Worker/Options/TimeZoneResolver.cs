namespace Skywatch.Worker.Options;

/// <summary>
/// AC-F8.12: resolves <c>Notifications:DisplayTimeZone</c>. Older tz databases (and ICU on some Windows builds)
/// know only the former name <c>Europe/Kiev</c>, so <c>Europe/Kyiv</c> falls back to it.
/// </summary>
internal static class TimeZoneResolver
{
    public const string Kyiv = "Europe/Kyiv";
    public const string Kiev = "Europe/Kiev";

    /// <returns>The zone, or <see langword="null"/> if neither the id nor its fallback is known.</returns>
    public static TimeZoneInfo? Resolve(string id, Func<string, TimeZoneInfo?> find) =>
        find(id) ?? (id == Kyiv ? find(Kiev) : null);

    /// <summary>The zone or a startup error; validation (<see cref="NotificationOptionsValidator"/>) runs first.</summary>
    public static TimeZoneInfo ResolveOrThrow(string id) =>
        Resolve(id, FindSystemTimeZone) ?? throw new InvalidOperationException($"Notifications:DisplayTimeZone '{id}' is not a time zone known to this system.");

    public static TimeZoneInfo? FindSystemTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }
}
