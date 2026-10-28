using Skywatch.Worker.Telegram;

namespace Skywatch.Worker.Persistence.Entities;

/// <summary>Spec §7 Sources. Upserted at startup from the resolved configured sources (AC-F2.2).</summary>
internal sealed class Source
{
    public int Id { get; set; }

    public long TelegramChatId { get; set; }

    public string Name { get; set; } = "";

    public SourceType Type { get; set; }

    public string? Username { get; set; }

    public bool NoForwards { get; set; }

    public bool IsEnabled { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
