using PotatoPleaseMP.Core.Net;

namespace PotatoPleaseMP.Net.Steam;

/// <summary><see cref="IP2PLink"/> over ISteamNetworkingMessages (spec 17 App. C). Main thread only.</summary>
internal sealed unsafe class SteamLink : IP2PLink
{
    readonly IntPtr[] _batch = new IntPtr[64];
    public ulong LocalId => SteamNative.LocalSteamId;
    public long Strays;

    public LinkSend Send(ulong to, ReadOnlySpan<byte> data, int channel, LinkFlags f)
    {
        int flags = ((f & LinkFlags.Reliable) != 0 ? SteamMessages.SendReliable : SteamMessages.SendUnreliable) | SteamMessages.SendNoNagle
                    | ((f & LinkFlags.RestartBroken) != 0 ? SteamMessages.SendAutoRestartBrokenSession : 0)
                    | ((f & LinkFlags.Urgent) != 0 ? SteamMessages.SendUseCurrentThread : 0);
        int r = SteamMessages.Send(to, data, flags, channel);
        return r == 1 ? LinkSend.Ok : r == 25 ? LinkSend.Busy : LinkSend.Failed; // k_EResultOK / LimitExceeded
    }

    public void Receive(int channel, List<(ulong From, byte[] Data)> into)
    {
        int n;
        do
        {
            fixed (IntPtr* b = _batch) n = SteamMessages.Receive(channel, b, _batch.Length);
            for (int i = 0; i < n; i++)
            {
                var m = (SteamNetworkingMessage*)_batch[i];
                try
                {
                    if (m->Peer.Type != 16 || m->Size <= 0) { Strays++; continue; }
                    var bytes = new byte[m->Size];
                    new ReadOnlySpan<byte>(m->Data, m->Size).CopyTo(bytes);
                    into.Add((m->Peer.SteamId64, bytes));
                }
                finally { SteamMessages.Release(_batch[i]); }
            }
        } while (n == _batch.Length);
    }

    public bool Accept(ulong peer) => SteamMessages.Accept(peer);
    public void Close(ulong peer) => SteamMessages.Close(peer);

    public LinkStatus Status(ulong peer)
    {
        int state = SteamMessages.Status(peer, out var st);
        return new LinkStatus
        {
            State = (LinkState)state, PingMs = st.Ping, QualityLocal = st.QualityLocal, QualityRemote = st.QualityRemote,
            OutBytesPerSec = st.OutBytesPerSec, InBytesPerSec = st.InBytesPerSec, SendRate = st.SendRateBytesPerSecond,
            PendingReliable = st.PendingReliable, PendingUnreliable = st.PendingUnreliable, UnackedReliable = st.SentUnackedReliable,
        };
    }
}
