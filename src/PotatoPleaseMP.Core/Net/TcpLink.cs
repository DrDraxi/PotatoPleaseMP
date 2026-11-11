using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace PotatoPleaseMP.Core.Net;

/// <summary>
/// Direct-IP <see cref="IP2PLink"/> over TCP, for a second game copy on the same PC or a LAN/VPN friend without Steam
/// lobbies. TCP gives the ordered reliable stream P2PTransport expects; "unreliable" messages ride the same stream, which
/// is fine at LAN latency. Peer ids are universe-0 (<see cref="UdpIds"/> range), exchanged in an 8-byte preamble.
/// Frames: <c>u8 channel, u32 length, bytes</c>. Socket I/O runs on background threads; <see cref="Receive"/> drains queues.
/// </summary>
public sealed class TcpLink : IP2PLink, IDisposable
{
    sealed class Conn
    {
        public TcpClient Client = null!;
        public NetworkStream Stream = null!;
        public ulong Peer;
        public volatile bool Dead;
        public readonly object WriteLock = new();
    }

    readonly ConcurrentQueue<(int Ch, ulong From, byte[] Data)> _inbox = new();
    readonly ConcurrentDictionary<ulong, Conn> _conns = new();
    readonly List<(int Ch, ulong From, byte[] Data)> _held = new();
    TcpListener? _listener;
    volatile bool _disposed;

    public ulong LocalId { get; }
    public int Port { get; private set; }
    public string? LastError { get; private set; }

    public TcpLink(ulong localId) => LocalId = localId;

    /// <summary>Host side: accept peers on <paramref name="port"/> (loopback only unless <paramref name="any"/>).</summary>
    public void Listen(int port, bool any)
    {
        _listener = new TcpListener(any ? IPAddress.Any : IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(AcceptLoop) { IsBackground = true, Name = "PPMP tcp accept" }.Start();
    }

    /// <summary>Client side: connect to the host and learn its id from its preamble (blocks up to the timeout).</summary>
    public ulong ConnectTo(string host, int port, int timeoutMs = 5000)
    {
        var c = new TcpClient { NoDelay = true };
        if (!c.ConnectAsync(host, port).Wait(timeoutMs)) throw new IOException($"No answer from {host}:{port}.");
        var conn = Start(c);
        return conn.Peer;
    }

    void AcceptLoop()
    {
        while (!_disposed && _listener != null)
        {
            try
            {
                var c = _listener.AcceptTcpClient();
                c.NoDelay = true;
                new Thread(() => { try { Start(c); } catch (Exception e) { LastError = e.Message; c.Close(); } }) { IsBackground = true }.Start();
            }
            catch (Exception e) { if (!_disposed) LastError = e.Message; }
        }
    }

    Conn Start(TcpClient c)
    {
        var s = c.GetStream();
        s.Write(BitConverter.GetBytes(LocalId));
        var idBuf = new byte[8];
        ReadExactly(s, idBuf);
        var conn = new Conn { Client = c, Stream = s, Peer = BitConverter.ToUInt64(idBuf) };
        if (_conns.TryRemove(conn.Peer, out var old)) Kill(old);
        _conns[conn.Peer] = conn;
        new Thread(() => ReadLoop(conn)) { IsBackground = true, Name = "PPMP tcp read" }.Start();
        return conn;
    }

    void ReadLoop(Conn c)
    {
        var head = new byte[5];
        try
        {
            while (!c.Dead && !_disposed)
            {
                ReadExactly(c.Stream, head);
                int len = BitConverter.ToInt32(head, 1);
                if (len < 0 || len > 64 << 20) throw new IOException("bad frame length");
                var data = new byte[len];
                ReadExactly(c.Stream, data);
                _inbox.Enqueue((head[0], c.Peer, data));
            }
        }
        catch (Exception e) { if (!_disposed) LastError = e.Message; }
        Kill(c);
    }

    static void ReadExactly(Stream s, byte[] buf)
    {
        int n = 0;
        while (n < buf.Length)
        {
            int r = s.Read(buf, n, buf.Length - n);
            if (r <= 0) throw new IOException("connection closed");
            n += r;
        }
    }

    static void Kill(Conn c)
    {
        c.Dead = true;
        try { c.Client.Close(); } catch { }
    }

    public LinkSend Send(ulong to, ReadOnlySpan<byte> data, int channel, LinkFlags flags)
    {
        if (!_conns.TryGetValue(to, out var c) || c.Dead) return LinkSend.Failed;
        var frame = new byte[5 + data.Length];
        frame[0] = (byte)channel;
        BitConverter.TryWriteBytes(frame.AsSpan(1), data.Length);
        data.CopyTo(frame.AsSpan(5));
        try
        {
            lock (c.WriteLock) c.Stream.Write(frame, 0, frame.Length);
            return LinkSend.Ok;
        }
        catch (Exception e) { LastError = e.Message; Kill(c); return LinkSend.Failed; }
    }

    public void Receive(int channel, List<(ulong From, byte[] Data)> into)
    {
        // One shared queue: pull everything, hand out this channel, keep the rest for the next call in order.
        while (_inbox.TryDequeue(out var m)) _held.Add(m);
        for (int i = 0; i < _held.Count; i++)
        {
            if (_held[i].Ch != channel) continue;
            into.Add((_held[i].From, _held[i].Data));
            _held.RemoveAt(i--);
        }
    }

    public bool Accept(ulong peer) => _conns.TryGetValue(peer, out var c) && !c.Dead;

    public void Close(ulong peer)
    {
        if (_conns.TryRemove(peer, out var c)) Kill(c);
    }

    public LinkStatus Status(ulong peer) => new()
    {
        State = _conns.TryGetValue(peer, out var c) && !c.Dead ? LinkState.Connected : LinkState.ClosedByPeer,
        PingMs = 1,
    };

    public void Dispose()
    {
        _disposed = true;
        try { _listener?.Stop(); } catch { }
        foreach (var c in _conns.Values) Kill(c);
        _conns.Clear();
    }
}
