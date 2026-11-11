using PotatoPleaseMP.Core;
using PotatoPleaseMP.Core.Net;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Testing;
using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Tests;

/// <summary>Fake game for the host session: versions, a gate, and a save produced on capture.</summary>
public sealed class FakeHostWorld : IHostWorld
{
    public VersionInfo Version { get; set; } = TestRig.Versions;
    public byte Difficulty { get; set; } = 2;
    public QueueReason? Gate { get; set; }
    public bool IsShiftOver { get; set; }
    public Func<SnapshotReason, byte[]> SaveFactory { get; set; } = _ => new byte[] { 1, 2, 3, 4 };
    public bool FailCapture { get; set; }
    public int Captures { get; private set; }
    public List<SnapshotReason> CaptureReasons { get; } = new();
    public byte[]? LastSave { get; private set; }

    public QueueReason? JoinGate() => Gate;

    public HostSnapshot CaptureSnapshot(SnapshotReason reason)
    {
        if (FailCapture) throw new InvalidOperationException("capture failed (test)");
        Captures++;
        CaptureReasons.Add(reason);
        LastSave = SaveFactory(reason);
        return new HostSnapshot { SaveUtcTicks = 638000000000000000UL + (ulong)Captures, SaveVersion = Version.SaveVersion, Save = Task.FromResult(LastSave) };
    }
}

/// <summary>
/// A host session plus bots on one <see cref="LoopbackHub"/> with a manual clock. <see cref="Step"/> advances time and
/// pumps everyone; <see cref="RunUntil"/> steps until a condition holds.
/// </summary>
public sealed class TestRig : IDisposable
{
    public static readonly VersionInfo Versions = new(Envelope.ProtocolVersion, "0.1.0", "1.0.1", 26422447, 44, 0xABCD1234);
    public static readonly PeerId HostId = new(0x0110000100001234UL); // looks like a SteamID64 (universe 1)

    public LoopbackHub Hub { get; } = new();
    public FakeHostWorld World { get; } = new();
    public P2PTransport HostTransport { get; }
    public HostSession Host { get; }
    public List<BotClient> Bots { get; } = new();
    public List<Action<double>> Extra { get; } = new();
    public double Now => Hub.Now;

    public TestRig(Action<HostConfig>? configure = null, bool start = true, Action<LoopbackHub>? hub = null)
    {
        hub?.Invoke(Hub);
        HostTransport = Hub.CreateTransport(HostId);
        var cfg = new HostConfig { HostName = "Host", HostSteamId = HostId.Value, PrepareBlobsOnWorker = false };
        configure?.Invoke(cfg);
        Host = new HostSession(HostTransport, World, cfg, now: 0);
        if (start) Host.Start(0);
    }

    public BotClient AddBot(string name, Action<BotOptions>? options = null, Action<BotClient>? beforeStart = null, bool start = true)
    {
        int index = Enumerable.Range(1, BotIds.MaxIndex).First(i => Bots.All(b => b.Index != i));
        var opt = new BotOptions { Name = name, Version = Versions, LoadSeconds = 0.5, Spawn = new Vec3(index, 0, 0) };
        options?.Invoke(opt);
        var bot = new BotClient(Hub, index, HostId, opt);
        beforeStart?.Invoke(bot);
        Bots.Add(bot);
        if (start) bot.Start(Now);
        return bot;
    }

    public void Step(double dt = 1 / 60.0)
    {
        Hub.Advance(dt);
        double now = Now;
        Host.Update(now);
        foreach (var b in Bots) b.Update(now);
        foreach (var a in Extra) a(now);
        P2PTransport.PumpDeferredCloses();
    }

    public void Run(double seconds, double dt = 1 / 60.0)
    {
        double end = Now + seconds;
        while (Now < end - 1e-9) Step(dt);
    }

    public bool RunUntil(Func<bool> condition, double maxSeconds, double dt = 1 / 60.0)
    {
        double end = Now + maxSeconds;
        while (Now < end)
        {
            if (condition()) return true;
            Step(dt);
        }
        return condition();
    }

    public BotClient JoinBot(string name, Action<BotOptions>? options = null, Action<BotClient>? beforeStart = null)
    {
        var b = AddBot(name, options, beforeStart);
        Assert.True(RunUntil(() => b.State is BotState.Ready or BotState.Ended, 30), $"{name} did not finish joining");
        Assert.Equal(BotState.Ready, b.State);
        return b;
    }

    public void Dispose()
    {
        foreach (var b in Bots) b.Dispose();
        Host.Dispose();
    }
}

/// <summary>Deterministic test data.</summary>
public static class TestData
{
    public static byte[] Random(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    /// <summary>Half random, half repetitive text: compresses, but not trivially.</summary>
    public static byte[] SaveLike(int n, int seed)
    {
        var rng = new Random(seed);
        var b = new byte[n];
        for (int i = 0; i < n; i++)
            b[i] = (i / 4096 % 2 == 0) ? (byte)rng.Next(256) : (byte)("{\"potato\":12,\"peeled\":true}"[i % 27]);
        return b;
    }
}
