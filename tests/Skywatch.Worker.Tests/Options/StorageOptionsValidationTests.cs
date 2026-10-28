using Microsoft.Extensions.Options;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Options;

// Spec §6: every options class is validated on start. A zero retention would clear text or delete rows immediately.
public sealed class StorageOptionsValidationTests
{
    [Theory]
    [InlineData("Storage:RetentionDays", "0")]
    [InlineData("Storage:UnmatchedRetentionDays", "0")]
    [InlineData("Storage:MetadataRetentionDays", "-1")]
    [InlineData("Storage:BusyTimeoutSeconds", "0")]
    [InlineData("Storage:RetentionBatchSize", "0")]
    [InlineData("Storage:DatabasePath", " ")]
    public async Task Spec6_invalid_storage_option_fails_run_mode_startup(string key, string value)
    {
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.TelegramSettings(session.Path);
        settings[key] = value;
        using var host = TestHost.Build([], settings);

        var ex = await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(key);
    }
}
