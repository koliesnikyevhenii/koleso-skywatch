using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Skywatch.Worker.Persistence;

/// <summary>
/// Spec §7: every timestamp is UTC on read. On write a local or unspecified time is rejected, so a value that
/// did not come from <see cref="TimeProvider.GetUtcNow"/> or Telegram's UTC dates cannot be stored by mistake.
/// </summary>
internal sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => EnsureUtc(v),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
{
    private static DateTime EnsureUtc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : throw new InvalidOperationException($"Timestamps must be UTC; got DateTimeKind.{value.Kind}.");
}
