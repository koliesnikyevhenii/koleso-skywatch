using Microsoft.EntityFrameworkCore;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Persistence;

// Plan P3a contract: the initial migration creates the full spec §7 schema.
public sealed class SchemaTests : IDisposable
{
    private readonly SqliteTestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<List<string>> QueryAsync(string sql)
    {
        await using var db = _db.CreateDbContext();
        return await db.Database.SqlQueryRaw<string>(sql).ToListAsync(TestContext.Current.CancellationToken);
    }

    private Task<List<string>> ColumnsAsync(string table) => QueryAsync($"SELECT name AS Value FROM pragma_table_info('{table}')");

    /// <summary>Each index on the table as "unique?:col1,col2".</summary>
    private Task<List<string>> IndexesAsync(string table) => QueryAsync(
        $"""
        SELECT (CASE il."unique" WHEN 1 THEN 'unique:' ELSE ':' END) || group_concat(ii.name, ',') AS Value
        FROM pragma_index_list('{table}') il, pragma_index_info(il.name) ii
        WHERE il.origin = 'c'
        GROUP BY il.name
        """);

    public static TheoryData<string, string[]> Tables() => new()
    {
        { "Sources", ["Id", "TelegramChatId", "Name", "Type", "Username", "NoForwards", "IsEnabled", "CreatedAtUtc"] },
        {
            "Messages",
            [
                "Id", "SourceId", "TelegramMessageId", "Text", "PublishedAtUtc", "ReceivedAtUtc", "EditedAtUtc", "ContentHash",
                "OriginChatId", "OriginMessageId", "IsMatched", "MatchedPatterns", "IsDuplicate", "DuplicateOfMessageId", "DuplicateReason",
            ]
        },
        {
            "Notifications",
            [
                "Id", "MessageId", "Kind", "Status", "Attempts", "CreatedAtUtc", "SentAtUtc", "BotMessageId", "Error", "PossibleDuplicate",
                "StaleReportedAtUtc", "Label", "LabeledByUserId", "LabeledAtUtc",
            ]
        },
        { "ServiceEvents", ["Id", "Kind", "OccurredAtUtc", "Detail"] },
    };

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task Spec7_table_has_exactly_the_specified_columns(string table, string[] columns) =>
        (await ColumnsAsync(table)).ShouldBe(columns, ignoreOrder: true);

    [Fact]
    public async Task Spec7_messages_indexes()
    {
        var indexes = await IndexesAsync("Messages");

        indexes.ShouldContain("unique:SourceId,TelegramMessageId");
        indexes.ShouldContain(":ContentHash,ReceivedAtUtc");
        indexes.ShouldContain(":OriginChatId,OriginMessageId,ReceivedAtUtc");
        indexes.ShouldContain(":ReceivedAtUtc");
    }

    [Fact]
    public async Task Spec7_sources_telegram_chat_id_is_unique() =>
        (await IndexesAsync("Sources")).ShouldContain("unique:TelegramChatId");

    // Plan P3a: DB-level foreign keys, because ExecuteDelete bypasses the change tracker.
    [Fact]
    public async Task P3a_foreign_keys_cascade_notifications_and_set_null_duplicate_references()
    {
        var actions = await QueryAsync(
            """
            SELECT 'Notifications.' || "from" || ':' || on_delete AS Value FROM pragma_foreign_key_list('Notifications')
            UNION ALL
            SELECT 'Messages.' || "from" || ':' || on_delete AS Value FROM pragma_foreign_key_list('Messages')
            """);

        actions.ShouldContain("Notifications.MessageId:CASCADE");
        actions.ShouldContain("Messages.DuplicateOfMessageId:SET NULL");
    }

    // Assumption confirmed in P3a start: enums are stored by name, so the experiment data reads well in plain SQL.
    [Fact]
    public async Task P3a_enums_are_stored_by_name()
    {
        await using (var db = _db.CreateDbContext())
        {
            var source = new Source { TelegramChatId = -1000000000042, Name = "Test source", Type = SourceType.Supergroup, IsEnabled = true, CreatedAtUtc = DateTime.UnixEpoch };
            var message = new Message { Source = source, TelegramMessageId = 1, ContentHash = "h", PublishedAtUtc = DateTime.UnixEpoch, ReceivedAtUtc = DateTime.UnixEpoch, IsDuplicate = true, DuplicateReason = DuplicateReason.SameText };
            db.Notifications.Add(new Notification { Message = message, Kind = NotificationKind.EditReply, Status = NotificationStatus.SkippedStale, Label = NotificationLabel.Useful, CreatedAtUtc = DateTime.UnixEpoch });
            db.ServiceEvents.Add(new ServiceEvent { Kind = ServiceEventKind.Disconnected, OccurredAtUtc = DateTime.UnixEpoch });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await QueryAsync("SELECT Type AS Value FROM Sources")).ShouldBe(["Supergroup"]);
        (await QueryAsync("SELECT DuplicateReason AS Value FROM Messages")).ShouldBe(["SameText"]);
        (await QueryAsync("SELECT Kind || ',' || Status || ',' || Label AS Value FROM Notifications")).ShouldBe(["EditReply,SkippedStale,Useful"]);
        (await QueryAsync("SELECT Kind AS Value FROM ServiceEvents")).ShouldBe(["Disconnected"]);
    }
}
