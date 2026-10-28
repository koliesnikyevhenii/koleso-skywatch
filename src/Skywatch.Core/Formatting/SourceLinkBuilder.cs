using System.Globalization;

namespace Skywatch.Core.Formatting;

/// <summary>AC-F8.3: a link to the original post, from the Bot-API-style marked chat id.</summary>
public static class SourceLinkBuilder
{
    /// <summary>Channels and supergroups have marked ids <c>-100{internalId}</c>; basic groups do not.</summary>
    private const long _channelIdOffset = 1_000_000_000_000;

    /// <returns>
    /// <c>https://t.me/{username}/{messageId}</c> for a public channel or supergroup,
    /// <c>https://t.me/c/{internalId}/{messageId}</c> for a private one, <see langword="null"/> for a basic group.
    /// </returns>
    public static string? Build(long markedChatId, string? username, int messageId)
    {
        if (markedChatId > -_channelIdOffset)
        {
            return null; // basic group: no link line
        }

        return string.IsNullOrWhiteSpace(username)
            ? string.Create(CultureInfo.InvariantCulture, $"https://t.me/c/{-markedChatId - _channelIdOffset}/{messageId}")
            : string.Create(CultureInfo.InvariantCulture, $"https://t.me/{username}/{messageId}");
    }
}
