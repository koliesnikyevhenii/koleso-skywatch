using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skywatch.Worker.Options;
using TL;
using WTelegram;

namespace Skywatch.Worker.Telegram;

/// <summary>WTelegramClient <c>UpdateManager</c> behind <see cref="ITelegramUpdateSource"/> (AC-F3.1–F3.4, F2.5).</summary>
internal sealed partial class WTelegramUpdateSource : ITelegramUpdateSource, IDisposable
{
    private const int _capacity = 1000;
    private const int _backPressureThreshold = _capacity * 8 / 10;
    private static readonly TimeSpan _backPressureWarningInterval = TimeSpan.FromMinutes(1);

    private readonly TelegramOptions _telegram;
    private readonly HashSet<long> _allowlist;
    private readonly IReadOnlyList<SourceOptions> _sources;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Action<string> _saveState;
    private readonly Func<CancellationToken, Task> _reconnect;
    private readonly Channel<SourceEvent> _events = System.Threading.Channels.Channel.CreateBounded<SourceEvent>(
        new BoundedChannelOptions(_capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _warningLock = new();
    private DateTimeOffset? _lastBackPressureWarning;
    private int _reconnecting;
    private Client? _client;
    private UpdateManager? _manager;

    public WTelegramUpdateSource(IOptions<TelegramOptions> telegram, IOptions<MonitoringOptions> monitoring, TimeProvider time, ILogger<WTelegramUpdateSource> logger)
        : this(telegram.Value, monitoring.Value, time, logger, saveState: null)
    {
    }

    /// <param name="saveState">Writes the update state to the given path; <see langword="null"/> uses the connected <c>UpdateManager</c>.</param>
    /// <param name="reconnect">One reconnect attempt; <see langword="null"/> recreates the client (AC-F3.11).</param>
    internal WTelegramUpdateSource(
        TelegramOptions telegram,
        MonitoringOptions monitoring,
        TimeProvider time,
        ILogger<WTelegramUpdateSource> logger,
        Action<string>? saveState,
        Func<CancellationToken, Task>? reconnect = null)
    {
        _telegram = telegram;
        _sources = monitoring.Sources;
        _allowlist = [.. monitoring.Sources.Where(s => s.Enabled).Select(s => s.ChatId)];
        _time = time;
        _logger = logger;
        _saveState = saveState ?? SaveManagerState;
        _reconnect = reconnect ?? RecreateClientAsync;
    }

    public async Task<IReadOnlyList<ResolvedSource>> ResolveSourcesAsync(CancellationToken ct)
    {
        var client = CreateClient();
        _client = client;
        _manager = CreateUpdateManager(client);

        Messages_Chats chats;
        try
        {
            // AC-F3.10: a long FLOOD_WAIT is waited out here instead of exiting into a restart loop (R23).
            chats = await TelegramRetry.HonourFloodWaitAsync(
                async () =>
                {
                    await client.LoginUserIfNeeded(reloginOnFailedResume: false).WaitAsync(ct);
                    return await client.Messages_GetAllChats().WaitAsync(ct);
                },
                _time,
                _logger,
                ct);
        }
        catch (RpcException ex) when (ex.Code == 401)
        {
            throw new TelegramSessionRejectedException(ex.Message, ex);
        }

        // The manager needs the access hashes of the sources to catch up on them (AC-F3.3).
        foreach (var (id, chat) in chats.chats)
        {
            _manager.Chats[id] = chat;
        }

        return TlMapper.ResolveSources(_sources, chats.chats.Values);
    }

    public async IAsyncEnumerable<SourceEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Cancellation stops intake only; whatever is already queued is still handed to the consumer.
        using var registration = ct.Register(StopIntake);
        await foreach (var sourceEvent in _events.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return sourceEvent;
        }
    }

    public Task<bool> TrySaveStateAsync(CancellationToken ct)
    {
        // Saving with events still queued would mark them as handled; after a crash they would be lost.
        if (_events.Reader.Count > 0)
        {
            return Task.FromResult(false);
        }

        _saveState(_telegram.UpdateStatePath);
        return Task.FromResult(true);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _client?.Dispose();
        _stopping.Dispose();
    }

    /// <summary>The <c>UpdateManager</c> handler: maps, filters by allowlist and enqueues, nothing else (AC-F3.4).</summary>
    internal async Task HandleUpdateAsync(Update update)
    {
        // AC-F2.5: chats that are not enabled sources go no further.
        if (TlMapper.ToIncomingMessage(update, _time.GetUtcNow().UtcDateTime) is { } message && _allowlist.Contains(message.ChatId))
        {
            await EnqueueAsync(message);
        }
    }

    private async Task EnqueueAsync(SourceEvent sourceEvent)
    {
        try
        {
            await _events.Writer.WriteAsync(sourceEvent);
        }
        catch (ChannelClosedException)
        {
            // Shutting down: intake has stopped, the consumer is draining.
            return;
        }

        WarnIfFallingBehind();
    }

    private void WarnIfFallingBehind()
    {
        var size = _events.Reader.Count;
        if (size <= _backPressureThreshold)
        {
            return;
        }

        var now = _time.GetUtcNow();
        lock (_warningLock)
        {
            if (_lastBackPressureWarning is { } last && now - last < _backPressureWarningInterval)
            {
                return;
            }

            _lastBackPressureWarning = now;
        }

        LogFallingBehind(_logger, size, _capacity);
    }

    private void StopIntake()
    {
        // Disconnect first, so no update reaches the manager's state that is not also in the channel.
        _stopping.Cancel();
        _client?.Dispose();
        _events.Writer.TryComplete();
    }

    private Client CreateClient()
    {
        // Run mode never prompts (AC-F1.3): a request for login data means the session is not usable.
        var client = new Client(what => what is "phone_number" or "verification_code" or "password"
            ? throw new TelegramSessionRejectedException("not authorized")
            : _telegram.WTelegramConfig(what));
        client.OnOther += OnOtherAsync;
        return client;
    }

    private UpdateManager CreateUpdateManager(Client client)
    {
        try
        {
            // No state file yet → start from now, without history (AC-F3.2); with one → catch up (AC-F3.3).
            return client.WithUpdateManager(HandleUpdateAsync, _telegram.UpdateStatePath);
        }
        catch (Exception ex) when (File.Exists(_telegram.UpdateStatePath))
        {
            // R14: a corrupt state file must not stop the service.
            LogStateUnreadable(_logger, _telegram.UpdateStatePath, ex.GetType().Name);
            return client.WithUpdateManager(HandleUpdateAsync, (IDictionary<long, UpdateManager.MBoxState>?)null);
        }
    }

    private void SaveManagerState(string path)
    {
        if (_manager is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _manager.SaveState(path);
    }

    private async Task OnOtherAsync(IObject other)
    {
        if (other is ReactorError && !_stopping.IsCancellationRequested)
        {
            await OnReactorErrorAsync();
        }
    }

    /// <summary>
    /// AC-F3.11: the client's own auto-reconnects are exhausted. Retry with backoff until it works, honouring
    /// FLOOD_WAIT (AC-F3.10); a rejected session ends the attempts (AC-F1.8). The manager catches up afterwards.
    /// </summary>
    internal async Task OnReactorErrorAsync()
    {
        // The new client may report its own reactor error while this loop still runs: one loop at a time.
        if (Interlocked.Exchange(ref _reconnecting, 1) == 1)
        {
            return;
        }

        try
        {
            await EnqueueAsync(new ConnectionStateChanged(ConnectionState.Disconnected, _time.GetUtcNow().UtcDateTime));
            var backoff = TelegramRetry.FirstReconnectDelay;
            var delay = backoff;
            while (!_stopping.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(delay, _time, _stopping.Token);
                    await _reconnect(_stopping.Token);
                    await EnqueueAsync(new ConnectionStateChanged(ConnectionState.Reconnected, _time.GetUtcNow().UtcDateTime));
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (TelegramRetry.IsSessionRejected(ex))
                {
                    await EnqueueAsync(new SessionRejected(ex.Message, _time.GetUtcNow().UtcDateTime));
                    return;
                }
                catch (Exception ex) when (TelegramRetry.FloodWait(ex) is { } wait)
                {
                    TelegramRetry.LogFloodWait(_logger, wait.TotalSeconds);
                    delay = wait;
                }
                catch (Exception ex)
                {
                    backoff = TelegramRetry.NextBackoff(backoff);
                    delay = backoff;
                    LogReconnectFailed(_logger, ex.GetType().Name, delay.TotalSeconds);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _reconnecting, 0);
        }
    }

    /// <summary>
    /// The WTelegramClient way after a <c>ReactorError</c>: dispose the client and create a new one. The new
    /// <c>UpdateManager</c> starts from the old one's state and peers, so missed updates are still caught up.
    /// </summary>
    private async Task RecreateClientAsync(CancellationToken ct)
    {
        var previous = _manager;
        _client?.Dispose();
        var client = CreateClient();
        var manager = client.WithUpdateManager(HandleUpdateAsync, previous?.State);
        if (previous is not null)
        {
            foreach (var (id, chat) in previous.Chats)
            {
                manager.Chats[id] = chat;
            }

            foreach (var (id, user) in previous.Users)
            {
                manager.Users[id] = user;
            }
        }

        _client = client;
        _manager = manager;
        await client.LoginUserIfNeeded(reloginOnFailedResume: false).WaitAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update channel holds {Size}/{Capacity} events; processing is falling behind.")]
    private static partial void LogFallingBehind(ILogger logger, int size, int capacity);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update state file '{Path}' could not be read ({Error}); starting without catch-up.")]
    private static partial void LogStateUnreadable(ILogger logger, string path, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconnect to Telegram failed ({Error}); retrying in {DelaySeconds} s.")]
    private static partial void LogReconnectFailed(ILogger logger, string error, double delaySeconds);
}
