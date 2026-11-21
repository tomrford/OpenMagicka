using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace OpenMagicka.Net;

/// <summary>
/// Direct-IP TCP transport (host star topology, up to 4 players) designed for Tailscale, LAN, and loopback play.
/// Uses <c>TCP_NODELAY</c> with binary length-prefixed framing; <see cref="DeliveryChannel.ReliableOrdered"/>
/// messages are queued in order while <see cref="DeliveryChannel.Unreliable"/> messages coalesce to the newest
/// pending frame per peer so snapshots never build latency behind a slow link.
/// </summary>
public sealed class TcpNetTransport : INetTransport
{
    public const int DefaultPort = 27016;
    public const int MaxPayloadBytes = 4 * 1024 * 1024;
    public const int MaxClients = 3;

    public static readonly PeerId HostPeer = new(0);

    private const uint Magic = 0x4F4D4E31; // "OMN1"
    private const int HeaderBytes = 5;     // 1-byte channel + 4-byte length

    private readonly CancellationTokenSource cts = new();
    private readonly ConcurrentQueue<NetMessage> inbox = new();
    private readonly ConcurrentDictionary<PeerId, PeerConnection> peers = new();
    private readonly TcpListener? listener;
    private readonly Lock peerIdLock = new();
    private volatile bool disposed;

    private TcpNetTransport(PeerId localPeer, TcpListener? listener, int port)
    {
        LocalPeer = localPeer;
        this.listener = listener;
        Port = port;
    }

    public PeerId LocalPeer { get; }

    /// <summary>The local port bound (host) or remote port connected to (client).</summary>
    public int Port { get; }

    public bool IsHost => listener is not null;

    public IReadOnlyCollection<PeerId> ConnectedPeers => peers.Keys.ToArray();

    public event Action<PeerId>? PeerConnected;

    public event Action<PeerId>? PeerDisconnected;

    /// <summary>Starts hosting on <paramref name="port"/> (0 picks an ephemeral port). Default binds all interfaces (Tailscale, LAN, loopback).</summary>
    public static TcpNetTransport Host(int port = DefaultPort, IPAddress? bindAddress = null)
    {
        var tcp = new TcpListener(bindAddress ?? IPAddress.Any, port);
        tcp.Start();
        var boundPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        var transport = new TcpNetTransport(HostPeer, tcp, boundPort);
        _ = Task.Run(() => transport.AcceptLoopAsync(transport.cts.Token));
        return transport;
    }

    /// <summary>Connects to a host by IP or hostname (e.g. Tailscale IP <c>100.64.0.1</c>, MagicDNS name, or <c>127.0.0.1</c>).</summary>
    public static TcpNetTransport Connect(string host, int port = DefaultPort, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var client = new TcpClient();
        try
        {
            ConfigureSocket(client);
            using var connectCts = new CancellationTokenSource(effectiveTimeout);
            client.ConnectAsync(host, port, connectCts.Token).AsTask().GetAwaiter().GetResult();

            var stream = client.GetStream();
            var greeting = new byte[8];
            ReadExactly(stream, greeting, connectCts.Token);
            if (BinaryPrimitives.ReadUInt32LittleEndian(greeting.AsSpan(0, 4)) != Magic)
            {
                throw new InvalidDataException("Remote endpoint is not an OpenMagicka host.");
            }
            var assignedId = BinaryPrimitives.ReadInt32LittleEndian(greeting.AsSpan(4, 4));
            if (assignedId <= 0)
            {
                throw new InvalidOperationException("Host rejected the connection (session full).");
            }

            var transport = new TcpNetTransport(new PeerId(assignedId), listener: null, port);
            transport.AttachPeer(HostPeer, client);
            return transport;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Parses <c>host</c> or <c>host:port</c> (including IPv6 <c>[addr]:port</c>) with <see cref="DefaultPort"/> fallback.</summary>
    public static (string Host, int Port) ParseEndpoint(string address, int defaultPort = DefaultPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var trimmed = address.Trim();
        if (trimmed.StartsWith('['))
        {
            var close = trimmed.IndexOf(']');
            if (close < 0)
            {
                throw new ArgumentException($"Invalid endpoint \"{address}\".", nameof(address));
            }
            var host = trimmed[1..close];
            if (close + 1 == trimmed.Length)
            {
                return (host, defaultPort);
            }
            if (trimmed[close + 1] != ':' || !int.TryParse(trimmed[(close + 2)..], out var v6Port) || v6Port is <= 0 or > 65535)
            {
                throw new ArgumentException($"Invalid port in endpoint \"{address}\".", nameof(address));
            }
            return (host, v6Port);
        }

        var colon = trimmed.LastIndexOf(':');
        if (colon > 0 && trimmed.IndexOf(':') == colon)
        {
            var host = trimmed[..colon];
            if (!int.TryParse(trimmed[(colon + 1)..], out var port) || port is <= 0 or > 65535)
            {
                throw new ArgumentException($"Invalid port in endpoint \"{address}\".", nameof(address));
            }
            return (host, port);
        }
        return (trimmed, defaultPort);
    }

    public void Send(PeerId to, DeliveryChannel channel, ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentException($"Payload of {payload.Length} bytes exceeds {MaxPayloadBytes}.", nameof(payload));
        }
        if (!peers.TryGetValue(to, out var peer))
        {
            throw new ArgumentException($"Peer {to.Value} is not connected.", nameof(to));
        }
        peer.Enqueue(channel, payload.ToArray());
    }

    /// <summary>Sends a message to every currently connected peer (ignoring peers that disconnect concurrently).</summary>
    public void Broadcast(DeliveryChannel channel, ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentException($"Payload of {payload.Length} bytes exceeds {MaxPayloadBytes}.", nameof(payload));
        }
        var copy = payload.ToArray();
        foreach (var peer in peers.Values)
        {
            peer.Enqueue(channel, copy);
        }
    }

    public bool TryReceive(out NetMessage message)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return inbox.TryDequeue(out message);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        cts.Cancel();
        try
        {
            listener?.Stop();
        }
        catch (SocketException)
        {
        }
        foreach (var (_, peer) in peers)
        {
            peer.Dispose();
        }
        peers.Clear();
        inbox.Clear();
        cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener is not null)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }

            ConfigureSocket(client);
            var assigned = AllocatePeerId();
            var greeting = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(greeting.AsSpan(0, 4), Magic);
            BinaryPrimitives.WriteInt32LittleEndian(greeting.AsSpan(4, 4), assigned?.Value ?? 0);
            try
            {
                await client.GetStream().WriteAsync(greeting, ct).ConfigureAwait(false);
                await client.GetStream().FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                client.Dispose();
                continue;
            }

            if (assigned is not { } peerId)
            {
                client.Dispose();
                continue;
            }
            AttachPeer(peerId, client);
        }
    }

    private PeerId? AllocatePeerId()
    {
        lock (peerIdLock)
        {
            for (var id = 1; id <= MaxClients; id++)
            {
                var candidate = new PeerId(id);
                if (!peers.ContainsKey(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }
    }

    private void AttachPeer(PeerId peerId, TcpClient client)
    {
        var conn = new PeerConnection(peerId, client, this);
        if (!peers.TryAdd(peerId, conn))
        {
            conn.Dispose();
            return;
        }
        PeerConnected?.Invoke(peerId);
        conn.Start(cts.Token);
    }

    private void OnPeerClosed(PeerId peerId, PeerConnection conn)
    {
        if (peers.TryRemove(new KeyValuePair<PeerId, PeerConnection>(peerId, conn)))
        {
            conn.Dispose();
            if (!disposed)
            {
                PeerDisconnected?.Invoke(peerId);
            }
        }
    }

    private static void ConfigureSocket(TcpClient client)
    {
        client.NoDelay = true;
        client.SendBufferSize = 256 * 1024;
        client.ReceiveBufferSize = 256 * 1024;
    }

    private static void ReadExactly(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.ReadAsync(buffer.AsMemory(total), ct).AsTask().GetAwaiter().GetResult();
            if (read == 0)
            {
                throw new EndOfStreamException("Remote peer closed the connection during handshake.");
            }
            total += read;
        }
    }

    private sealed class PeerConnection(PeerId peerId, TcpClient client, TcpNetTransport owner) : IDisposable
    {
        private readonly ConcurrentQueue<byte[]> reliable = new();
        private readonly SemaphoreSlim sendSignal = new(0);
        private byte[]? latestUnreliable;
        private volatile bool closed;

        public void Enqueue(DeliveryChannel channel, byte[] payload)
        {
            if (closed)
            {
                return;
            }
            if (channel == DeliveryChannel.Unreliable)
            {
                var previous = Interlocked.Exchange(ref latestUnreliable, payload);
                if (previous is null)
                {
                    ReleaseSignal();
                }
            }
            else
            {
                reliable.Enqueue(payload);
                ReleaseSignal();
            }
        }

        public void Start(CancellationToken ct)
        {
            _ = Task.Run(() => ReadLoopAsync(ct));
            _ = Task.Run(() => WriteLoopAsync(ct));
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            var stream = client.GetStream();
            var header = new byte[HeaderBytes];
            try
            {
                while (!closed && !ct.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
                    {
                        break;
                    }
                    var channel = (DeliveryChannel)header[0];
                    if (channel is not (DeliveryChannel.Unreliable or DeliveryChannel.ReliableOrdered))
                    {
                        break;
                    }
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
                    if (length is < 0 or > MaxPayloadBytes)
                    {
                        break;
                    }
                    var payload = length == 0 ? [] : new byte[length];
                    if (length > 0 && !await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
                    {
                        break;
                    }
                    owner.inbox.Enqueue(new NetMessage(peerId, channel, payload));
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                owner.OnPeerClosed(peerId, this);
            }
        }

        private async Task WriteLoopAsync(CancellationToken ct)
        {
            var stream = client.GetStream();
            var header = new byte[HeaderBytes];
            try
            {
                while (!closed && !ct.IsCancellationRequested)
                {
                    await sendSignal.WaitAsync(ct).ConfigureAwait(false);
                    while (!closed && !ct.IsCancellationRequested)
                    {
                        if (reliable.TryDequeue(out var reliablePayload))
                        {
                            await WriteFrameAsync(stream, header, DeliveryChannel.ReliableOrdered, reliablePayload, ct).ConfigureAwait(false);
                            continue;
                        }
                        if (Interlocked.Exchange(ref latestUnreliable, null) is { } unreliablePayload)
                        {
                            await WriteFrameAsync(stream, header, DeliveryChannel.Unreliable, unreliablePayload, ct).ConfigureAwait(false);
                            continue;
                        }
                        break;
                    }
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                owner.OnPeerClosed(peerId, this);
            }
        }

        private static async ValueTask WriteFrameAsync(NetworkStream stream, byte[] header, DeliveryChannel channel, byte[] payload, CancellationToken ct)
        {
            header[0] = (byte)channel;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1, 4), payload.Length);
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            if (payload.Length > 0)
            {
                await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            }
        }

        private static async ValueTask<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return false;
                }
                total += read;
            }
            return true;
        }

        private void ReleaseSignal()
        {
            try
            {
                sendSignal.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            if (closed)
            {
                return;
            }
            closed = true;
            try
            {
                client.Dispose();
            }
            catch (Exception)
            {
            }
            try
            {
                sendSignal.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
