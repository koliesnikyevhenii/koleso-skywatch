namespace Skywatch.Worker.Persistence.Entities;

/// <summary>Spec §7 ServiceEvents: lifecycle and connection history (AC-F3.7), errors and report runs.</summary>
internal sealed class ServiceEvent
{
    public long Id { get; set; }

    public ServiceEventKind Kind { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public string? Detail { get; set; }
}

internal enum ServiceEventKind
{
    Started,
    Stopped,
    Disconnected,
    Reconnected,
    ProcessingError,
    DailyReportSent,
}
