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
    public async Task AWaitReturnsAsSoonAsSomethingArrives()
    {
        var obj = CommitTestObject();
        using var listener = new GciSignalListener(_listenerSession!);
        listener.Watch(obj);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.True((await listener.WaitAsync(TimeSpan.FromMilliseconds(300))).IsEmpty); // nothing: times out
        Assert.InRange(sw.ElapsedMilliseconds, 250, 5000);

        // The GCI is free while the listener waits: the worker commits meanwhile.
        var wait = listener.WaitAsync(TimeSpan.FromSeconds(20));
        Thread.Sleep(200);
        sw.Restart();
        W.Execute($"(UserGlobals at: #{_key}) at: 1 put: 5. true");
        W.Commit();
        Assert.Equal([obj], (await Within(wait)).ChangedObjects);
        Assert.InRange(sw.ElapsedMilliseconds, 0, 5000); // not the 20 s timeout

        wait = listener.WaitAsync(TimeSpan.FromSeconds(20));
        Thread.Sleep(200);
        W.SendSignal(listener.SessionId, 3, "wake up");
        var m = Assert.Single((await Within(wait)).Messages);
        Assert.Equal((3L, "wake up"), (m.Value, m.Message));
    }

    [StoneFact]
    public async Task NoSignalIsLostWhileWaiting()
    {
        using var listener = new GciSignalListener(_listenerSession!);
        var received = new List<GciSessionSignal>();
        var all = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SignalsReceived += (_, e) =>
        {
            lock (received)
            {
                received.AddRange(e.Signals.Messages);
                if (received.Count >= 40) all.TrySetResult(true);
            }
        };
        listener.Start();
        // Bursts and pauses: signals arrive while waiting, while reading and between two waits.
        for (var round = 0; round < 8; round++)
        {
            for (var i = 1; i <= 5; i++) W.SendSignal(listener.SessionId, round * 10 + i, "s");
            Thread.Sleep(round * 15);
        }
        await Within(all.Task);
        listener.Stop();
        lock (received)
            Assert.Equal(Enumerable.Range(0, 8).SelectMany(r => Enumerable.Range(r * 10 + 1, 5)).Select(i => (long)i), received.Select(s => s.Value));
    }

    [StoneFact]
    public async Task WatchAndPollInterruptAWaitInTheBackground()
    {
        var obj = CommitTestObject();
        using var listener = new GciSignalListener(_listenerSession!) { MaxWait = TimeSpan.FromSeconds(30) };
        var got = new TaskCompletionSource<GciSignals>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SignalsReceived += (_, e) => got.TrySetResult(e.Signals);
        listener.Start();
        Thread.Sleep(300); // the background wait is in progress

        var sw = System.Diagnostics.Stopwatch.StartNew();
        listener.Watch(obj); // interrupts it, does not wait 30 s
        Assert.True(listener.Poll().IsEmpty);
        Assert.InRange(sw.ElapsedMilliseconds, 0, 5000);

        W.Execute($"(UserGlobals at: #{_key}) at: 1 put: 6. true");
        W.Commit();
        Assert.Equal([obj], (await Within(got.Task)).ChangedObjects);

        sw.Restart();
        listener.Stop();
        Assert.InRange(sw.ElapsedMilliseconds, 0, 5000);
        Assert.True(listener.Poll().IsEmpty); // usable afterwards
    }

    /// <summary>
    /// A signal and a commit notification both pending while the listener is idle: the next call
    /// fails with one error, and reading that error must not run into the other.
    /// </summary>
    [StoneTheory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ASignalAndACommitArrivingTogether(bool signalFirst, bool waiting)
    {
        var obj = CommitTestObject();
        using var listener = new GciSignalListener(_listenerSession!);
        listener.Watch(obj);
        for (var round = 1; round <= 6; round++)
        {
            // Rounds 1-3: both arrive while the listener is idle; rounds 4-6 (waiting only): during a wait.
            var during = waiting && round > 3 ? listener.WaitAsync(TimeSpan.FromSeconds(10)) : null;
            if (during is not null) Thread.Sleep(100);
            if (signalFirst) W.SendSignal(listener.SessionId, round, $"round {round}");
            W.Execute($"(UserGlobals at: #{_key}) at: 1 put: {round}. true");
            W.Commit();
            if (!signalFirst) W.SendSignal(listener.SessionId, round, $"round {round}");
            Thread.Sleep(200);
            var messages = new List<GciSessionSignal>();
            var changed = new List<Oop>();
            if (during is not null)
            {
                var first = await Within(during);
                messages.AddRange(first.Messages);
                changed.AddRange(first.ChangedObjects);
            }
            if (messages.Count == 0 || changed.Count == 0)
            {
                var s = waiting && during is null ? await listener.WaitAsync(TimeSpan.FromSeconds(5))
                    : PollUntil(listener, x => x.Messages.Count + messages.Count > 0 && x.ChangedObjects.Count + changed.Count > 0);
                messages.AddRange(s.Messages);
                changed.AddRange(s.ChangedObjects);
            }
            Assert.Equal($"round {round}", Assert.Single(messages).Message);
            Assert.Equal([obj], changed);
        }
    }

    /// <summary>
    /// The sender runs on its gem in parallel (ExecuteAsync), so signals arrive at any point of the
    /// listener's Smalltalk: during the abort, while reading the queues, while building the answer.
    /// </summary>
    [StoneTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoSignalIsLostUnderLoad(bool waiting)
    {
        const int count = 300;
        using var listener = new GciSignalListener(_listenerSession!);
        var sender = W.ExecuteAsync($"1 to: {count} do: [:i | System sendSignal: i to: {listener.SessionId} withMessage: 's'. (Delay forMilliseconds: 1) wait]. true");
        var received = new List<long>();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (received.Count < count && DateTime.UtcNow < until)
        {
            var s = waiting ? await listener.WaitAsync(TimeSpan.FromMilliseconds(500)) : listener.Poll();
            received.AddRange(s.Messages.Select(m => m.Value));
        }
        await sender;
        Assert.Equal(Enumerable.Range(1, count).Select(i => (long)i), received);
    }

    /// <summary>In 3.7 the deprecated signalFromGemStoneSession fails for them (nil GsSession).</summary>
    [StoneTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignalsOfASenderThatHasLoggedOut(bool waiting)
    {
        using var listener = new GciSignalListener(_listenerSession!);
        long serial;
        using (var gone = StoneEnv.Login())
        {
            serial = gone.Evaluate<long>("GsSession currentSession serialNumber");
            gone.SendSignal(listener.SessionId, 1, "bye");
        }
        W.SendSignal(listener.SessionId, 2, "still here");
        Thread.Sleep(300);
        var s = waiting ? await listener.WaitAsync(TimeSpan.FromSeconds(5)) : listener.Poll();
        Assert.Equal(2, s.Messages.Count);
        // The session number of a sender that is gone: null with 3.7, possibly still the old one with 3.6.
        Assert.Equal((serial, "bye"), (s.Messages[0].SenderSerial, s.Messages[0].Message));
        Assert.Equal((W.GemSessionId, "still here"), (s.Messages[1].SenderSessionId, s.Messages[1].Message));
    }

    [StoneFact]
    public async Task StopInsideTheEventHandlerDoesNotHang()
    {
        var listener = new GciSignalListener(_listenerSession!);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SignalsReceived += (_, _) =>
        {
            listener.Stop(); // runs on the loop itself (no synchronization context)
            listener.Dispose();
            stopped.TrySetResult(true);
        };
        await Task.Run(listener.Start); // no synchronization context there (xUnit has one): the handler runs on the loop
        var sw = System.Diagnostics.Stopwatch.StartNew();
        W.SendSignal(listener.SessionId, 1, "stop"); // may arrive before the first wait starts: no MaxWait delay then
        Assert.True(await Within(stopped.Task));
        Assert.InRange(sw.ElapsedMilliseconds, 0, 2000);
        Assert.Equal(7, await Within(Task.Run(() => { Thread.Sleep(100); return _listenerSession!.Evaluate<long>("3 + 4"); })));
    }

    [StoneFact]
    public async Task ACancelledWaitKeepsWhatArrived()
    {
        using var listener = new GciSignalListener(_listenerSession!);
        using var cts = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.WaitAsync(TimeSpan.FromSeconds(30), cts.Token));
        W.SendSignal(listener.SessionId, 1, "after");
        Assert.Equal("after", Assert.Single(PollUntil(listener, s => s.Messages.Count > 0).Messages).Message);
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
            Assert.Equal([address], ctx.Refresh(changed));
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
