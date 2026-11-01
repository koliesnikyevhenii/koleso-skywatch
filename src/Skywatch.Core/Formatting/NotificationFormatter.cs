using System.Globalization;
using System.Text;
using Skywatch.Core.Filtering;

namespace Skywatch.Core.Formatting;

/// <summary>
/// What one match notification shows. <see cref="EditedAtUtc"/> is set for edit-triggered notifications;
/// <see cref="Highlights"/> are spans of <see cref="Text"/> to show in bold (AC-F8.14); <see cref="IsFollowUp"/>
/// marks a source's reply to a notified post (AC-F13.3).
/// </summary>
public sealed record NotificationContent(
    IReadOnlyList<string> PatternNames,
    string Text,
    string SourceName,
    DateTime PublishedAtUtc,
    DateTime? EditedAtUtc,
    string? Link,
    IReadOnlyList<TextSpan>? Highlights = null,
    bool IsFollowUp = false);

/// <summary>The message text and the spans of it to send as bold entities (AC-F8.14).</summary>
public sealed record FormattedNotification(string Text, IReadOnlyList<TextSpan> Bold);

/// <summary>AC-F8.2: the plain-text match message (no parse mode). Strings are Ukrainian.</summary>
public static class NotificationFormatter
{
    /// <summary>Telegram's limit for one text message.</summary>
    public const int MaxLength = 4096;

    /// <summary>C3: the disclaimer, shown as a note under the original text.</summary>
    public const string Disclaimer = "ℹ️ Неперевірене повідомлення з каналу третьої сторони. Не замінює офіційні сигнали тривоги.";

    /// <summary>Ends the first line, so a push preview already shows the message needs care (C3).</summary>
    public const string Warning = "⚠️ Увага";

    /// <summary>Shown instead of the pattern names, which stay in the database and the daily report.</summary>
    public const string Tag = "🏷 КП";

    /// <summary>AC-F13.3: the first line of a follow-up starts with this instead of the tag.</summary>
    public const string FollowUpMarker = "↪️ Продовження";

    private const string _ellipsis = "…";

    /// <param name="sendAttemptAtUtc">AC-F8.5: the delay is measured to this send attempt.</param>
    /// <param name="displayZone">AC-F8.12: the published time is shown in this zone, honouring DST.</param>
    public static string Format(NotificationContent content, DateTime sendAttemptAtUtc, TimeZoneInfo displayZone) =>
        Render(content, sendAttemptAtUtc, displayZone).Text;

    /// <summary>
    /// <see cref="Format"/> plus the bold spans: the highlights moved past the header and cut where the text is
    /// truncated (AC-F8.4, AC-F8.14).
    /// </summary>
    public static FormattedNotification Render(NotificationContent content, DateTime sendAttemptAtUtc, TimeZoneInfo displayZone)
    {
        var header = $"{TagLine(content)}\n\n";

        var published = TimeZoneInfo.ConvertTimeFromUtc(content.PublishedAtUtc, displayZone);
        var footer = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"\n\n{Disclaimer}\n──────────\n")
            .Append(CultureInfo.InvariantCulture, $"📢 {content.SourceName}\n")
            .Append(CultureInfo.InvariantCulture, $"🕐 {published:HH:mm:ss} (опубл.) · затримка {DelaySeconds(content, sendAttemptAtUtc)} с");
        if (content.Link is not null)
        {
            footer.Append(CultureInfo.InvariantCulture, $"\n🔗 {content.Link}");
        }

        var tail = footer.ToString();
        var (body, kept) = Truncate(content.Text, MaxLength - header.Length - tail.Length);

        List<TextSpan> bold = [];
        foreach (var span in content.Highlights ?? [])
        {
            var end = Math.Min(span.End, kept);
            if (span.Start >= 0 && end > span.Start)
            {
                bold.Add(new TextSpan(header.Length + span.Start, end - span.Start));
            }
        }

        return new FormattedNotification(header + body + tail, bold);
    }

    /// <summary>
    /// AC-F8.2. An edit reply (AC-F9.5) whose new text no longer matches has no pattern names: the line then carries
    /// only the edit marker. The warning always ends the line.
    /// </summary>
    private static string TagLine(NotificationContent content)
    {
        // Every pattern looks for the same village, and the readers don't know the pattern names: one short tag.
        List<string> parts = [];
        if (content.IsFollowUp)
        {
            parts.Add(FollowUpMarker);
        }
        else if (content.PatternNames.Count > 0)
        {
            parts.Add(Tag);
        }

        if (content.EditedAtUtc is not null)
        {
            parts.Add("✏️ оновлено");
        }

        parts.Add(Warning);
        return string.Join(" · ", parts);
    }

    /// <summary>AC-F8.5 and AC-F7.3: whole seconds from publication, or from the edit for edit-triggered notifications.</summary>
    public static long DelaySeconds(NotificationContent content, DateTime sendAttemptAtUtc) =>
        (long)Math.Round((sendAttemptAtUtc - (content.EditedAtUtc ?? content.PublishedAtUtc)).TotalSeconds, MidpointRounding.AwayFromZero);

    /// <summary>AC-F8.4: only the original text is cut, never between the halves of a surrogate pair.</summary>
    /// <returns>The text to show and how many characters of the original it keeps.</returns>
    private static (string Text, int Kept) Truncate(string text, int available)
    {
        if (text.Length <= available)
        {
            return (text, text.Length);
        }

        var keep = Math.Max(available - _ellipsis.Length, 0);
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return (string.Concat(text.AsSpan(0, keep), _ellipsis), keep);
    }
}
