using System.Globalization;
using System.Text;

namespace Skywatch.Core.Formatting;

/// <summary>What one match notification shows. <see cref="EditedAtUtc"/> is set for edit-triggered notifications.</summary>
public sealed record NotificationContent(
    IReadOnlyList<string> PatternNames,
    string Text,
    string SourceName,
    DateTime PublishedAtUtc,
    DateTime? EditedAtUtc,
    string? Link);

/// <summary>AC-F8.2: the plain-text match message (no parse mode). Strings are Ukrainian.</summary>
public static class NotificationFormatter
{
    /// <summary>Telegram's limit for one text message.</summary>
    public const int MaxLength = 4096;

    public const string Disclaimer = "⚠️ Неперевірено · повідомлення з каналу третьої сторони. Не замінює офіційні сигнали тривоги.";

    private const string _ellipsis = "…";

    /// <param name="sendAttemptAtUtc">AC-F8.5: the delay is measured to this send attempt.</param>
    /// <param name="displayZone">AC-F8.12: the published time is shown in this zone, honouring DST.</param>
    public static string Format(NotificationContent content, DateTime sendAttemptAtUtc, TimeZoneInfo displayZone)
    {
        var tags = string.Join(", ", content.PatternNames);
        var header = $"{Disclaimer}\n🏷 {tags}{(content.EditedAtUtc is null ? "" : " · ✏️ оновлено")}\n\n";

        var published = TimeZoneInfo.ConvertTimeFromUtc(content.PublishedAtUtc, displayZone);
        var footer = new StringBuilder()
            .Append("\n\n──────────\n")
            .Append(CultureInfo.InvariantCulture, $"📢 {content.SourceName}\n")
            .Append(CultureInfo.InvariantCulture, $"🕐 {published:HH:mm:ss} (опубл.) · затримка {DelaySeconds(content, sendAttemptAtUtc)} с");
        if (content.Link is not null)
        {
            footer.Append(CultureInfo.InvariantCulture, $"\n🔗 {content.Link}");
        }

        var tail = footer.ToString();
        return header + Truncate(content.Text, MaxLength - header.Length - tail.Length) + tail;
    }

    /// <summary>AC-F8.5 and AC-F7.3: whole seconds from publication, or from the edit for edit-triggered notifications.</summary>
    public static long DelaySeconds(NotificationContent content, DateTime sendAttemptAtUtc) =>
        (long)Math.Round((sendAttemptAtUtc - (content.EditedAtUtc ?? content.PublishedAtUtc)).TotalSeconds, MidpointRounding.AwayFromZero);

    /// <summary>AC-F8.4: only the original text is cut, never between the halves of a surrogate pair.</summary>
    private static string Truncate(string text, int available)
    {
        if (text.Length <= available)
        {
            return text;
        }

        var keep = Math.Max(available - _ellipsis.Length, 0);
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return string.Concat(text.AsSpan(0, keep), _ellipsis);
    }
}
