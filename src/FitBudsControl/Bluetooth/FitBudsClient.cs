namespace FitBudsControl.Bluetooth;

/// <summary>
/// Serializes protocol requests while every received frame is also published as an
/// observation. This mirrors the Python protocol-lab design: request/response matching
/// and asynchronous device-originated reports share one receive stream but neither
/// path hides packets from the other.
/// </summary>
public sealed class FitBudsClient : IAsyncDisposable
{
    private const int MaxPacketHistory = 4000;

    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _pendingLock = new();
    private readonly object _historyLock = new();
    private readonly Queue<ObservedPacket> _packetHistory = new();
    private RfcommSocketConnection? _connection;
    private PendingRequest? _pending;
    private long _rxSequence;
    private int _timeoutMs = 450;

    public event Action<Exception?>? Disconnected;
    public event Action<Packet>? PacketObserved;
    public event Action<ProtocolTrafficEntry>? TrafficObserved;

    public bool IsConnected => _connection?.IsConnected == true;
    public string? ConnectedMac { get; private set; }
    public int ConnectedChannel { get; private set; }

    public async Task ConnectAsync(
        string mac,
        int channel,
        int connectTimeoutMs,
        int requestTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var connection = new RfcommSocketConnection();
        connection.PacketReceived += OnPacketReceived;
        connection.FrameReceived += OnFrameReceived;
        connection.Disconnected += OnDisconnected;

        try
        {
            await connection.ConnectAsync(mac, channel, connectTimeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            connection.PacketReceived -= OnPacketReceived;
            connection.FrameReceived -= OnFrameReceived;
            connection.Disconnected -= OnDisconnected;
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        lock (_historyLock)
        {
            _packetHistory.Clear();
            _rxSequence = 0;
        }

        _connection = connection;
        _timeoutMs = Math.Clamp(requestTimeoutMs, 100, 5000);
        ConnectedMac = mac;
        ConnectedChannel = channel;
    }

    public async Task DisconnectAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null)
        {
            return;
        }

        connection.PacketReceived -= OnPacketReceived;
        connection.FrameReceived -= OnFrameReceived;
        connection.Disconnected -= OnDisconnected;

        lock (_pendingLock)
        {
            _pending?.Completion.TrySetException(new IOException("连接已断开"));
            _pending = null;
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        ConnectedMac = null;
        ConnectedChannel = 0;
    }

    /// <summary>Returns the current receive sequence for later HistorySince calls.</summary>
    public long MarkHistory()
    {
        lock (_historyLock)
        {
            return _rxSequence;
        }
    }

    /// <summary>
    /// Returns retained packets received after <paramref name="mark"/>. This is mainly
    /// useful for diagnostics and mirrors the Python test program's packet_history.
    /// </summary>
    public IReadOnlyList<Packet> HistorySince(long mark, IReadOnlyCollection<ushort>? categories = null)
    {
        HashSet<ushort>? wanted = categories is null ? null : new HashSet<ushort>(categories);
        lock (_historyLock)
        {
            return _packetHistory
                .Where(item => item.Sequence > mark && (wanted is null || wanted.Contains(item.Packet.Category)))
                .Select(item => item.Packet)
                .ToArray();
        }
    }

    public async Task<Packet?> RequestAsync(
        ushort category,
        ReadOnlyMemory<byte> payload,
        Func<Packet, bool>? matcher = null,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        var connection = _connection ?? throw new IOException("耳机尚未连接");

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingRequest(category, matcher, completion);

            lock (_pendingLock)
            {
                _pending = pending;
            }

            try
            {
                var raw = FitBudsFrameCodec.BuildPacket(category, payload.Span);
                PublishTraffic(new ProtocolTrafficEntry(DateTimeOffset.Now, ProtocolTrafficDirection.ToDevice, raw));
                await connection.SendAsync(raw, cancellationToken).ConfigureAwait(false);

                var delay = Task.Delay(timeoutMs ?? _timeoutMs, cancellationToken);
                var completed = await Task.WhenAny(completion.Task, delay).ConfigureAwait(false);
                if (completed == completion.Task)
                {
                    return await completion.Task.ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
            finally
            {
                lock (_pendingLock)
                {
                    if (ReferenceEquals(_pending, pending))
                    {
                        _pending = null;
                    }
                }
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public Task<Packet?> RequestAsync(
        ushort category,
        CancellationToken cancellationToken = default)
        => RequestAsync(category, ReadOnlyMemory<byte>.Empty, cancellationToken: cancellationToken);

    /// <summary>
    /// Sends a command that is not expected to have a response with the same category.
    /// Some FitBuds commands (for example 0xC400 sound-quality selection) are confirmed
    /// by a separate status query/report instead of a direct command ACK.
    /// </summary>
    public async Task SendAsync(
        ushort category,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        var connection = _connection ?? throw new IOException("耳机尚未连接");

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var raw = FitBudsFrameCodec.BuildPacket(category, payload.Span);
            PublishTraffic(new ProtocolTrafficEntry(DateTimeOffset.Now, ProtocolTrafficDirection.ToDevice, raw));
            await connection.SendAsync(raw, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public async Task<Packet?> QueryRetryAsync(
        ushort category,
        ReadOnlyMemory<byte> payload,
        int attempts = 3,
        Func<Packet, bool>? matcher = null,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var packet = await RequestAsync(category, payload, matcher, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (packet is not null)
            {
                return packet;
            }

            if (attempt + 1 < attempts)
            {
                await Task.Delay(100 + (60 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    public Task<Packet?> QueryRetryAsync(
        ushort category,
        int attempts = 3,
        Func<Packet, bool>? matcher = null,
        CancellationToken cancellationToken = default)
        => QueryRetryAsync(category, ReadOnlyMemory<byte>.Empty, attempts, matcher, cancellationToken);

    private void OnFrameReceived(byte[] raw)
    {
        PublishTraffic(new ProtocolTrafficEntry(DateTimeOffset.Now, ProtocolTrafficDirection.FromDevice, raw.ToArray()));
    }

    private void OnPacketReceived(Packet packet)
    {
        lock (_historyLock)
        {
            _rxSequence++;
            _packetHistory.Enqueue(new ObservedPacket(_rxSequence, packet));
            while (_packetHistory.Count > MaxPacketHistory)
            {
                _packetHistory.Dequeue();
            }
        }

        // Publish every valid frame even when it is also the response to a request.
        // A bad UI subscriber must never be allowed to terminate the RFCOMM receive loop.
        try
        {
            PacketObserved?.Invoke(packet);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PacketObserved subscriber failed: {ex}");
        }

        lock (_pendingLock)
        {
            var pending = _pending;
            if (pending is null || packet.Category != pending.Category)
            {
                return;
            }

            if (pending.Matcher is not null && !pending.Matcher(packet))
            {
                return;
            }

            pending.Completion.TrySetResult(packet);
        }
    }


    private void PublishTraffic(ProtocolTrafficEntry entry)
    {
        try
        {
            TrafficObserved?.Invoke(entry);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TrafficObserved subscriber failed: {ex}");
        }
    }

    private void OnDisconnected(Exception? exception)
    {
        lock (_pendingLock)
        {
            _pending?.Completion.TrySetException(exception ?? new IOException("连接已断开"));
            _pending = null;
        }
        Disconnected?.Invoke(exception);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _requestGate.Dispose();
    }

    private sealed record PendingRequest(
        ushort Category,
        Func<Packet, bool>? Matcher,
        TaskCompletionSource<Packet> Completion);

    private sealed record ObservedPacket(long Sequence, Packet Packet);
}
