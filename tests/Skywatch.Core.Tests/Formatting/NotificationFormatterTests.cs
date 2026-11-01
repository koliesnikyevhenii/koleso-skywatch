using System.Text;
using Skywatch.Core.Filtering;
using Skywatch.Core.Formatting;

namespace Skywatch.Core.Tests.Formatting;

// Spec F8.2–F8.5, F8.12, F7.3 (age part). Fake ids only.
public class NotificationFormatterTests
{
    private const string _text = "Шахед курсом на Краснопавлівку";

    // Summer time (EEST, UTC+3): the day before the 25 Oct 2026 change.
    private static readonly DateTime _summerUtc = new(2026, 10, 24, 9, 15, 30, DateTimeKind.Utc);

    private static readonly TimeZoneInfo _kyiv = FindKyiv();

    private static TimeZoneInfo FindKyiv()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Kiev");
        }
    }

    private static NotificationContent Content(
        string text = _text,
        string? link = "https://t.me/test_channel/42",
        DateTime? publishedAtUtc = null,
        DateTime? editedAtUtc = null,
        params string[] patterns) =>
        new(patterns.Length == 0 ? ["Settlement"] : patterns, text, "Test channel", publishedAtUtc ?? _summerUtc, editedAtUtc, link);

    private static string Format(NotificationContent content, double delaySeconds = 2) =>
        NotificationFormatter.Format(content, (content.EditedAtUtc ?? content.PublishedAtUtc).AddSeconds(delaySeconds), _kyiv);

    // AC-F8.2 + F8.3: exact output for a public channel; several patterns still show one tag.
    [Fact]
    public void AC_F8_2_public_channel_message_has_the_exact_format()
    {
        var content = Content(link: SourceLinkBuilder.Build(-1000000000042, "test_channel", 42), patterns: ["Settlement", "Abbreviation"]);

        Format(content).ShouldBe(
            """
            🏷 КП · ⚠️ Увага

            Шахед курсом на Краснопавлівку

            ℹ️ Неперевірене повідомлення з каналу третьої сторони. Не замінює офіційні сигнали тривоги.
            ──────────
            📢 Test channel
            🕐 12:15:30 (опубл.) · затримка 2 с
            🔗 https://t.me/test_channel/42
            """.ReplaceLineEndings("\n"));
    }

    // AC-F8.3: a private channel links through t.me/c/{internalId}.
    [Fact]
    public void AC_F8_3_private_channel_message_links_by_internal_id() =>
        Format(Content(link: SourceLinkBuilder.Build(-1000000000042, null, 7))).ShouldEndWith("\n🔗 https://t.me/c/42/7");

    // AC-F8.3: a basic group has no link line; the message ends with the time line.
    [Fact]
    public void AC_F8_3_basic_group_message_has_no_link_line()
    {
        var formatted = Format(Content(link: SourceLinkBuilder.Build(-4242, null, 7)));

        formatted.ShouldNotContain("🔗");
        formatted.ShouldEndWith("🕐 12:15:30 (опубл.) · затримка 2 с");
    }

    // AC-F8.2: edit-triggered notifications carry the edit marker after the tags; AC-F7.3 / F8.5: the delay is
    // measured from the edit, while the shown time stays the publication time.
    [Fact]
    public void AC_F8_2_edit_marker_and_AC_F7_3_delay_from_the_edit()
    {
        var content = Content(editedAtUtc: _summerUtc.AddMinutes(10));

        var formatted = Format(content, delaySeconds: 4);

        formatted.ShouldStartWith("🏷 КП · ✏️ оновлено · ⚠️ Увага\n");
        formatted.ShouldContain("🕐 12:15:30 (опубл.) · затримка 4 с");
    }

    // AC-F8.12: Kyiv time honours DST across the 25 Oct 2026 change (03:00 EEST → 03:00 EET at 01:00 UTC).
    [Theory]
    [InlineData("2026-10-24T09:15:30Z", "12:15:30")] // summer time, UTC+3
    [InlineData("2026-10-26T09:15:30Z", "11:15:30")] // winter time, UTC+2
    [InlineData("2026-10-25T00:59:59Z", "03:59:59")] // last second of summer time
    [InlineData("2026-10-25T01:00:00Z", "03:00:00")] // first second of winter time
    public void AC_F8_12_published_time_is_shown_in_kyiv_time_honouring_dst(string publishedUtc, string shown)
    {
        var published = DateTime.Parse(publishedUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);

        Format(Content(publishedAtUtc: published)).ShouldContain($"🕐 {shown} (опубл.)");
    }

    // AC-F8.5: the delay is rounded to whole seconds.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2.4, 2)]
    [InlineData(2.5, 3)]
    [InlineData(2.6, 3)]
    [InlineData(61.49, 61)]
    public void AC_F8_5_delay_is_rounded_to_whole_seconds(double delaySeconds, int shown) =>
        Format(Content(), delaySeconds).ShouldContain($"· затримка {shown} с");

    // AC-F8.4: a 5000-character text is truncated with "…" so the whole message fits in 4096; header and footer
    // stay intact.
    [Fact]
    public void AC_F8_4_long_text_is_truncated_with_ellipsis_and_header_and_footer_stay_intact()
    {
        var formatted = Format(Content(text: new string('а', 5000)));

        formatted.Length.ShouldBe(NotificationFormatter.MaxLength);
        formatted.ShouldStartWith("🏷 КП · ⚠️ Увага\n\nааа");
        formatted.ShouldContain("а…\n\n" + NotificationFormatter.Disclaimer + "\n──────────\n📢 Test channel\n");
        formatted.ShouldEndWith("\n🔗 https://t.me/test_channel/42");
    }

    // AC-F8.4: a text that fits exactly is not truncated.
    [Fact]
    public void AC_F8_4_text_that_fits_exactly_is_not_truncated()
    {
        var overhead = Format(Content(text: "")).Length;

        var formatted = Format(Content(text: new string('а', NotificationFormatter.MaxLength - overhead)));

        formatted.Length.ShouldBe(NotificationFormatter.MaxLength);
        formatted.ShouldNotContain("…");
    }

    // AC-F8.4 (plan P4a): an emoji at or around the cut point never leaves half a surrogate pair.
    [Theory]
    [InlineData(-3)]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void AC_F8_4_emoji_at_the_cut_point_leaves_a_valid_string(int offset)
    {
        var available = NotificationFormatter.MaxLength - Format(Content(text: "")).Length;
        var emojiAt = available - 1 + offset; // the cut keeps available − 1 chars, then appends "…"
        var text = new string('а', emojiAt) + "🚀" + new string('б', 200);

        var formatted = Format(Content(text: text));

        formatted.Length.ShouldBeLessThanOrEqualTo(NotificationFormatter.MaxLength);
        Should.NotThrow(() => new UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(formatted));
        formatted.ShouldContain("…\n\n" + NotificationFormatter.Disclaimer + "\n──────────\n");
    }

    // Plan P5 reply variant (AC-F9.5): the edit removed every mention, so there are no pattern names; the tag line
    // carries only the edit marker and the warning.
    [Fact]
    public void AC_F9_5_edit_reply_without_matching_patterns_shows_only_the_edit_marker()
    {
        var content = new NotificationContent([], "Відбій", "Test channel", _summerUtc, _summerUtc.AddMinutes(1), Link: null);

        Format(content).Split('\n')[0].ShouldBe("✏️ оновлено · ⚠️ Увага");
    }

    // C3: the disclaimer is part of every message, under the original text; the first line warns in push previews.
    [Fact]
    public void C3_every_message_carries_the_disclaimer_under_the_text_and_a_warning_on_the_first_line()
    {
        var formatted = Format(Content(text: "Шахед курсом на Краснопавлівку"));

        formatted.Split('\n')[0].ShouldEndWith(NotificationFormatter.Warning);
        formatted.ShouldContain("Шахед курсом на Краснопавлівку\n\n" + NotificationFormatter.Disclaimer + "\n──────────\n");
    }

    // AC-F8.14: highlights of the original text become bold spans of the message, moved past the first line.
    [Fact]
    public void AC_F8_14_highlight_is_a_bold_span_on_the_same_word_in_the_message()
    {
        const string text = "Шахед курсом на Краснопавлівку";
        var content = Content(text: text) with { Highlights = [new(text.IndexOf("Краснопавлівку", StringComparison.Ordinal), "Краснопавлівку".Length)] };

        var rendered = NotificationFormatter.Render(content, _summerUtc.AddSeconds(2), _kyiv);

        var bold = rendered.Bold.ShouldHaveSingleItem();
        rendered.Text.Substring(bold.Start, bold.Length).ShouldBe("Краснопавлівку");
        rendered.Text.ShouldBe(Format(content));
    }

    // AC-F8.14 + AC-F8.4: a highlight is cut where the text is truncated; one past the cut is dropped.
    [Fact]
    public void AC_F8_14_highlights_are_cut_at_the_truncation_point()
    {
        var available = NotificationFormatter.MaxLength - Format(Content(text: "")).Length;
        var keep = available - 1; // the cut keeps available − 1 chars, then appends "…"
        var text = new string('а', keep - 3) + "Краснопавлівку" + new string('б', 100) + "Краснопавлівку";
        var content = Content(text: text) with { Highlights = [new(keep - 3, 14), new(keep + 111, 14)] };

        var rendered = NotificationFormatter.Render(content, _summerUtc.AddSeconds(2), _kyiv);

        var bold = rendered.Bold.ShouldHaveSingleItem();
        rendered.Text.Substring(bold.Start, bold.Length).ShouldBe("Кра");
        rendered.Text.Length.ShouldBe(NotificationFormatter.MaxLength);
    }

    // AC-F8.14: no highlights, no bold spans.
    [Fact]
    public void AC_F8_14_without_highlights_there_is_no_bold() =>
        NotificationFormatter.Render(Content(), _summerUtc.AddSeconds(2), _kyiv).Bold.ShouldBeEmpty();

    // AC-F13.3: a follow-up starts with its own marker and keeps the disclaimer and the footer.
    [Fact]
    public void AC_F13_3_follow_up_has_the_follow_up_first_line_and_the_disclaimer()
    {
        var content = new NotificationContent([], "Не фіксується", "Test channel", _summerUtc, null, "https://t.me/test_channel/43", IsFollowUp: true);

        Format(content).ShouldBe(
            """
            ↪️ Продовження · ⚠️ Увага

            Не фіксується

            ℹ️ Неперевірене повідомлення з каналу третьої сторони. Не замінює офіційні сигнали тривоги.
            ──────────
            📢 Test channel
            🕐 12:15:30 (опубл.) · затримка 2 с
            🔗 https://t.me/test_channel/43
            """.ReplaceLineEndings("\n"));
    }
}
