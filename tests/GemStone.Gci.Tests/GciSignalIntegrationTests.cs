using GemStone.Gci.Mapping;

namespace GemStone.Gci.Tests;

/// <summary>
/// Notifications between sessions: a listener session, a worker session that commits changes and
/// sends signals. Commits a test object under a unique key in UserGlobals and removes it again.
/// </summary>
[Collection("Stone")]
public sealed class GciSignalIntegrationTests : IDisposable
{
    private readonly GciSession? _worker;
    private readonly GciSession? _listenerSession;
    private readonly string _key = "GemStoneGciTest_" + Guid.NewGuid().ToString("N");

    public GciSignalIntegrationTests()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GS_TEST_STONE"))) return;
        _worker = StoneEnv.Login();
        _listenerSession = StoneEnv.Login();
    }

    public void Dispose()
    {
        if (_worker is { IsLoggedIn: true })
        {
            _worker.Abort();
            _worker.Execute($"UserGlobals removeKey: #{_key} ifAbsent: [nil]. true");
            _worker.Commit();
        }
        _worker?.Dispose();
        _listenerSession?.Dispose();
    }

    private GciSession W => _worker!;

    private Oop CommitTestObject()
    {
        var oop = W.Execute($"UserGlobals at: #{_key} put: (Array new: 1)");
        W.Commit();
        return oop;
    }

    /// <summary>Polls until something arrived (signals travel through the stone asynchronously).</summary>
    private static GciSignals PollUntil(GciSignalListener listener, Func<GciSignals, bool> done)
    {
        var all = new List<GciSessionSignal>();
        var changed = new List<Oop>();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var s = listener.Poll();
            all.AddRange(s.Messages);
            changed.AddRange(s.ChangedObjects);
            var sofar = new GciSignals(changed, all);
            if (done(sofar) || DateTime.UtcNow > until) return sofar;
            Thread.Sleep(50);
        }
    }

    [StoneFact]
    public void ChangesToWatchedObjectsAreReported()
    {
        var obj = CommitTestObject();
        using var listener = new GciSignalListener(_listenerSession!);
        listener.Watch(obj);
        Assert.True(listener.Poll().IsEmpty);

        W.Execute($"(UserGlobals at: #{_key}) at: 1 put: 42. true");
        W.Commit();
        var signals = PollUntil(listener, s => s.ChangedObjects.Count > 0);
        Assert.Equal([obj], signals.ChangedObjects);
        Assert.Empty(signals.Messages);
        Assert.True(listener.Poll().IsEmpty); // reported once

        // The listener aborted: it sees the committed state.
        Assert.Equal(42, listener.Session.Evaluate<long>($"(UserGlobals at: #{_key}) at: 1"));

        listener.Unwatch(obj);
        W.Execute($"(UserGlobals at: #{_key}) at: 1 put: 43. true");
        W.Commit();
        Thread.Sleep(300);
        Assert.True(listener.Poll().IsEmpty);
    }

    [StoneFact]
    public void NoSignalIsLost()
    {
        using var listener = new GciSignalListener(_listenerSession!);
        var serial = W.Evaluate<long>("GsSession currentSession serialNumber");
        var received = new List<GciSessionSignal>();
        // Several rounds: signals that arrive while the listener is idle surface as GCI errors at
        // the start of the next call, others inside the drain block; both must be delivered.
        for (var round = 0; round < 4; round++)
        {
            for (var i = 1; i <= 5; i++)
                W.SendSignal(listener.SessionId, round * 10 + i, $"Grüße {round * 10 + i}");
            Thread.Sleep(round * 100);
            received.AddRange(PollUntil(listener, s => s.Messages.Count >= 5).Messages);
        }
        Assert.Equal(Enumerable.Range(0, 4).SelectMany(r => Enumerable.Range(r * 10 + 1, 5)).Select(i => (long)i), received.Select(s => s.Value));
        Assert.All(received, s => Assert.Equal($"Grüße {s.Value}", s.Message));
        Assert.All(received, s => Assert.Equal(serial, s.SenderSerial));
    }

    private static async Task<T> Within<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) != task)
            throw new TimeoutException("No signal within 10 seconds.");
        return await task;
    }

    [StoneFact]
    public void SignalMessagesAreLatin1()
    {
        using var listener = new GciSignalListener(_listenerSession!);
        Assert.Throws<ArgumentException>(() => W.SendSignal(listener.SessionId, 1, "5 €"));
        W.SendSignal(listener.SessionId, 2, "Größe ½ ÿ");
        Assert.Equal("Größe ½ ÿ", Assert.Single(PollUntil(listener, s => s.Messages.Count > 0).Messages).Message);
    }

    [StoneFact]
    public async Task BackgroundPollingRaisesAnEvent()
    {
        var obj = CommitTestObject();
        using var listener = new GciSignalListener(_listenerSession!);
        listener.Watch(obj);
        var got = new TaskCompletionSource<GciSignals>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SignalsReceived += (_, e) => got.TrySetResult(e.Signals);
        listener.Start(TimeSpan.FromMilliseconds(50));

        W.SendSignal(listener.SessionId, 7, "hello");
        var first = await Within(got.Task);
        var hello = Assert.Single(first.Messages);
        Assert.Equal((7L, "hello"), (hello.Value, hello.Message));

        // The worker keeps working while the listener polls in the background.
        got = new TaskCompletionSource<GciSignals>(TaskCreationOptions.RunContinuationsAsynchronously);
        W.Execute($"(UserGlobals at: #{_key}) at: 1 put: 1. true");
        W.Commit();
        var second = await Within(got.Task);
        Assert.Equal([obj], second.ChangedObjects);
        listener.Stop();
    }

    [StoneFact]
    public void ReportedObjectsLeadBackToLoadedEntities()
    {
        // A worker that loaded an object refreshes it after the listener reported a change.
        W.Execute($$"""
            | a | a := Object subclass: 'GsSignalTestAddress' instVarNames: #('street' 'city') classVars: #()
                classInstVars: #() poolDictionaries: #() inDictionary: UserGlobals.
            UserGlobals at: #{{_key}} put: (a new instVarNamed: 'city' put: 'Bonn'; yourself). true
            """);
        W.Commit();
        try
        {
            using var ctx = new GsObjectContext(W);
            var address = ctx.Load<SignalTestAddress>($"UserGlobals at: #{_key}");
            Assert.True(ctx.TryGetOop(address, out var oop));
            using var listener = new GciSignalListener(_listenerSession!);
            listener.Watch(oop);

            // Another session changes it.
            using var other = StoneEnv.Login();
            other.Execute($"(UserGlobals at: #{_key}) instVarNamed: 'city' put: 'Köln'. true");
            other.Commit();

            var changed = PollUntil(listener, s => s.ChangedObjects.Count > 0).ChangedObjects;
            Assert.True(ctx.TryGetEntity(Assert.Single(changed), out var entity));
            Assert.Same(address, entity);
            Assert.Equal("Bonn", address.City);
            W.Abort();
            ctx.Load<SignalTestAddress>(changed[0]);
            Assert.Equal("Köln", address.City);
        }
        finally
        {
            W.Abort();
            W.Execute("UserGlobals removeKey: #GsSignalTestAddress ifAbsent: [nil]. true");
            W.Commit();
        }
    }
}

[GsClass("GsSignalTestAddress")]
public sealed class SignalTestAddress
{
    [GsInstVar("street")] public string? Street { get; set; }
    [GsInstVar("city")] public string? City { get; set; }
}
