using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FitBudsControl.Bluetooth;

/// <summary>
/// Classic Bluetooth RFCOMM transport implemented with Winsock so the app can
/// connect to the exact RFCOMM channel that the confirmed Python program uses.
/// </summary>
public sealed class RfcommSocketConnection : IAsyncDisposable
{
    private const int AfBth = 32;
    private const int SockStream = 1;
    private const int BthprotoRfcomm = 3;
    private const int SdBoth = 2;
    private const int SocketError = -1;
    private const int WsaEWouldBlock = 10035;
    private const int WsaEInProgress = 10036;
    private const int WsaEAlready = 10037;
    private const int SolSocket = 0xFFFF;
    private const int SoError = 0x1007;
    private const int FionBio = unchecked((int)0x8004667E);
    private const int ConnectSelectSliceMs = 100;
    private static readonly nint InvalidSocket = new(-1);

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly StreamParser _parser = new();
    private readonly object _socketGate = new();

    private nint _socket = InvalidSocket;
    private Task? _receiveTask;
    private bool _winsockStarted;
    private int _disposed;

    public event Action<Packet>? PacketReceived;
    public event Action<byte[]>? FrameReceived;
    public event Action<Exception?>? Disconnected;

    public RfcommSocketConnection()
    {
        _parser.FrameObserved += OnFrameObserved;
    }

    public bool IsConnected
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            lock (_socketGate)
            {
                return _socket != InvalidSocket;
            }
        }
    }

    public async Task ConnectAsync(
        string mac,
        int channel,
        int connectTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (channel is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), "连接通道设置不正确");
        }

        if (IsConnected)
        {
            throw new InvalidOperationException("当前已有连接");
        }

        connectTimeoutMs = Math.Clamp(connectTimeoutMs, 500, 10000);
        var btAddress = BluetoothAddress.Parse(mac);

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWinsock();

            var socket = Native.socket(AfBth, SockStream, BthprotoRfcomm);
            if (socket == InvalidSocket)
            {
                ThrowWinsock("连接失败");
            }

            try
            {
                var address = new SockAddrBth
                {
                    AddressFamily = (ushort)AfBth,
                    BluetoothAddress = btAddress,
                    ServiceClassId = Guid.Empty,
                    Port = (uint)channel,
                };

                ConnectWithTimeout(socket, ref address, connectTimeoutMs, cancellationToken);

                lock (_socketGate)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        throw new ObjectDisposedException(nameof(RfcommSocketConnection));
                    }

                    _socket = socket;
                    socket = InvalidSocket; // Ownership transferred to this instance.
                }
            }
            finally
            {
                if (socket != InvalidSocket)
                {
                    Native.closesocket(socket);
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        _receiveTask = Task.Run(ReceiveLoop);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (bytes.IsEmpty)
        {
            return;
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var socket = GetSocketOrThrow();
            var data = bytes.ToArray();
            var offset = 0;

            while (offset < data.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = data.Length - offset;
                var chunk = offset == 0 ? data : data.AsSpan(offset).ToArray();

                var sent = Native.send(socket, chunk, remaining, 0);
                if (sent == SocketError)
                {
                    ThrowWinsock("连接已断开");
                }
                if (sent == 0)
                {
                    throw new IOException("连接已断开");
                }

                offset += sent;
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void OnFrameObserved(byte[] raw)
    {
        try
        {
            FrameReceived?.Invoke(raw);
        }
        catch
        {
            // Diagnostics must never interrupt the receive loop.
        }
    }

    private void ReceiveLoop()
    {
        Exception? failure = null;
        var buffer = new byte[4096];
        nint socket;

        try
        {
            socket = GetSocketOrThrow();
        }
        catch (Exception ex)
        {
            if (!_stop.IsCancellationRequested)
            {
                Disconnected?.Invoke(ex);
            }
            return;
        }

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var received = Native.recv(socket, buffer, buffer.Length, 0);
                if (received == 0)
                {
                    throw new IOException("连接已断开");
                }
                if (received == SocketError)
                {
                    var error = Native.WSAGetLastError();
                    if (_stop.IsCancellationRequested)
                    {
                        break;
                    }
                    throw new Win32Exception(error, "连接已断开");
                }

                foreach (var packet in _parser.Feed(buffer.AsSpan(0, received)))
                {
                    try
                    {
                        PacketReceived?.Invoke(packet);
                    }
                    catch
                    {
                        // A UI/event consumer must not terminate the receive loop.
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!_stop.IsCancellationRequested)
            {
                failure = ex;
            }
        }
        finally
        {
            // If the peer disappears, mark the connection closed immediately so
            // callers do not keep treating a stale SOCKET handle as connected.
            CloseSocketIfCurrent(socket);

            if (!_stop.IsCancellationRequested)
            {
                Disconnected?.Invoke(failure);
            }
        }
    }

    private nint GetSocketOrThrow()
    {
        lock (_socketGate)
        {
            if (_socket == InvalidSocket)
            {
                throw new IOException("耳机尚未连接");
            }

            return _socket;
        }
    }

    private void CloseSocketIfCurrent(nint socket)
    {
        var shouldClose = false;
        lock (_socketGate)
        {
            if (_socket == socket && socket != InvalidSocket)
            {
                _socket = InvalidSocket;
                shouldClose = true;
            }
        }

        if (shouldClose)
        {
            Native.shutdown(socket, SdBoth);
            Native.closesocket(socket);
        }
    }

    private nint TakeSocket()
    {
        lock (_socketGate)
        {
            var socket = _socket;
            _socket = InvalidSocket;
            return socket;
        }
    }

    private static void ConnectWithTimeout(
        nint socket,
        ref SockAddrBth address,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        uint nonBlocking = 1;
        if (Native.ioctlsocket(socket, FionBio, ref nonBlocking) == SocketError)
        {
            ThrowWinsock("连接失败");
        }

        var result = Native.connect(socket, ref address, Marshal.SizeOf<SockAddrBth>());
        if (result == SocketError)
        {
            var error = Native.WSAGetLastError();
            if (error is not (WsaEWouldBlock or WsaEInProgress or WsaEAlready))
            {
                throw new Win32Exception(error, "连接失败");
            }

            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var remainingMs = timeoutMs - (int)stopwatch.ElapsedMilliseconds;
                if (remainingMs <= 0)
                {
                    throw new TimeoutException("连接超时");
                }

                var waitMs = Math.Min(remainingMs, ConnectSelectSliceMs);
                var writeSet = new FdSetOne(socket);
                var exceptSet = new FdSetOne(socket);
                var timeout = TimeVal.FromMilliseconds(waitMs);

                var selected = Native.select(0, 0, ref writeSet, ref exceptSet, ref timeout);
                if (selected == SocketError)
                {
                    ThrowWinsock("连接失败");
                }

                if (selected == 0)
                {
                    continue;
                }

                var socketError = GetSocketError(socket);
                if (socketError != 0)
                {
                    throw new Win32Exception(socketError, "连接失败");
                }

                if (writeSet.Count > 0)
                {
                    break;
                }

                if (exceptSet.Count > 0)
                {
                    throw new IOException("连接失败");
                }
            }
        }

        // WSA select/connect was deliberately non-blocking only during establishment.
        // Restore blocking mode before the existing send/recv loops take ownership.
        nonBlocking = 0;
        if (Native.ioctlsocket(socket, FionBio, ref nonBlocking) == SocketError)
        {
            ThrowWinsock("连接失败");
        }
    }

    private static int GetSocketError(nint socket)
    {
        var optionLength = sizeof(int);
        if (Native.getsockopt(socket, SolSocket, SoError, out var socketError, ref optionLength) == SocketError)
        {
            ThrowWinsock("连接失败");
        }

        return socketError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FdSetOne
    {
        public FdSetOne(nint socket)
        {
            Count = 1;
            Sockets = new nint[64];
            Sockets[0] = socket;
        }

        public uint Count;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public nint[] Sockets;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeVal
    {
        public int Seconds;
        public int Microseconds;

        public static TimeVal FromMilliseconds(int milliseconds)
            => new()
            {
                Seconds = milliseconds / 1000,
                Microseconds = (milliseconds % 1000) * 1000,
            };
    }

    private void EnsureWinsock()
    {
        if (_winsockStarted)
        {
            return;
        }

        // WSADATA is smaller than 512 bytes on supported Windows architectures;
        // allocating an oversized native buffer keeps this interop simple while
        // still letting Winsock write the complete structure.
        var data = Marshal.AllocHGlobal(512);
        try
        {
            var result = Native.WSAStartup(0x0202, data);
            if (result != 0)
            {
                throw new Win32Exception(result, "连接功能无法启动");
            }
            _winsockStarted = true;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private static void ThrowWinsock(string message)
    {
        var error = Native.WSAGetLastError();
        throw new Win32Exception(error, message);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(RfcommSocketConnection));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();

        var socket = TakeSocket();
        if (socket != InvalidSocket)
        {
            Native.shutdown(socket, SdBoth);
            Native.closesocket(socket);
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch
            {
                // Dispose is best effort.
            }
        }

        if (_winsockStarted)
        {
            Native.WSACleanup();
            _winsockStarted = false;
        }

        _sendGate.Dispose();
        _stop.Dispose();
    }

    // ws2bth.h defines SOCKADDR_BTH while pshpack1.h is active.
    // The native layout is therefore exactly 30 bytes:
    // USHORT(2) + BTH_ADDR(8) + GUID(16) + ULONG(4), with no padding.
    // Using the CLR default packing inserts 6 bytes after AddressFamily,
    // causing Winsock to read the wrong Bluetooth address and return 10049.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SockAddrBth
    {
        public ushort AddressFamily;
        public ulong BluetoothAddress;
        public Guid ServiceClassId;
        public uint Port;
    }

    internal static int NativeSockAddrBthSize => Marshal.SizeOf<SockAddrBth>();

    private static class Native
    {
        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern int WSAStartup(ushort versionRequested, nint wsaData);

        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern int WSACleanup();

        // IMPORTANT: Do not set SetLastError=true here. On modern .NET,
        // the runtime clears the thread last-error before invoking a P/Invoke
        // marked SetLastError=true. WSAGetLastError() exists specifically to
        // read that value, so marking this method would erase the Winsock
        // error and make every failure appear as WSA 0.
        [DllImport("ws2_32.dll")]
        internal static extern int WSAGetLastError();

        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern nint socket(int af, int type, int protocol);

        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern int connect(nint socket, ref SockAddrBth name, int nameLength);

        [DllImport("ws2_32.dll")]
        internal static extern int ioctlsocket(nint socket, int command, ref uint argument);

        [DllImport("ws2_32.dll")]
        internal static extern int select(
            int ignoredNfds,
            nint readSet,
            [In, Out] ref FdSetOne writeSet,
            [In, Out] ref FdSetOne exceptSet,
            ref TimeVal timeout);

        [DllImport("ws2_32.dll")]
        internal static extern int getsockopt(
            nint socket,
            int level,
            int optionName,
            out int optionValue,
            ref int optionLength);

        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern int send(nint socket, byte[] buffer, int length, int flags);

        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern int recv(nint socket, byte[] buffer, int length, int flags);

        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern int shutdown(nint socket, int how);

        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern int closesocket(nint socket);
    }
}
