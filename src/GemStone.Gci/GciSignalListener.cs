using GemStone.Gci.Native;

namespace GemStone.Gci;

/// <summary>
/// Receives notifications from the server on a session of its own: changes other sessions commit
/// to watched objects (the notify set), and signals other sessions send with
/// <see cref="GciSession.SendSignal"/>.
/// </summary>
/// <remarks>
/// <para>
/// The server cannot interrupt a client. The listener asks: <see cref="Poll"/> reads everything
/// that arrived, in one round trip; <see cref="Start"/> does that periodically in the background
/// and raises <see cref="SignalsReceived"/>.
/// </para>
/// <para>
/// Use a session that does nothing else. With signals enabled, any Smalltalk the session runs
/// can fail with GemStone error 6008 or 6010 when a signal arrives; the listener handles that,
/// other code would not. Every poll aborts the listener session, so it never holds an old view of
/// the repository.
/// </para>
/// <para>
/// A notification is a hint, not the data: to see a change, the session that works with the
/// objects must abort (or commit) and load them again.
/// </para>
/// </remarks>
public sealed class GciSignalListener : IDisposable
{
    /// <summary>
    /// Aborts (fresh view, releases the old commit record), then empties both queues. A signal that
    /// arrives while the block runs is raised as a notification inside it and caught there.
    /// Answers <c>{changed objects. {serial. id. value. text} …}</c>.
    /// </summary>
    private const string DrainSource = """
        [| objs sigs add |
         System abortTransaction.
         objs := IdentitySet new. sigs := OrderedCollection new.
         add := [:serial :id :int :msg | sigs add: {serial. id. int. msg}].
         [| s |
          objs addAll: System signaledObjects.
          [(s := System signalFromGemStoneSession) notEmpty]
            whileTrue: [add value: (s at: 1) serialNumber value: nil value: (s at: 2) value: (s at: 3)]]
           on: InterSessionSignal, ObjectsCommittedNotification
           do: [:e | (e isKindOf: InterSessionSignal)
                 ifTrue: [add value: e sendingSessionSerial value: e sendingSessionId value: e sentInt value: e sentMessage].
               e resume: nil].
         objs addAll: System signaledObjects.
         {objs asArray. sigs asArray}]
        """;

    private readonly Oop _drainBlock;
    private readonly object _loopLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    /// <summary>Enables object and session signals on <paramref name="session"/>.</summary>
    /// <param name="session">A session used only by this listener; the caller still owns (and disposes) it.</param>
    public GciSignalListener(GciSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        SessionId = session.GemSessionId; // before enabling: afterwards plain Evaluate may meet a signal
        var none = new List<GciSessionSignal>();
        session.CallCapturingSignals(() => GciNative.GciExecuteStr(
            "System enableSignaledObjectsError; enableSignaledGemStoneSessionError. true", Oop.Nil.Value), none);
        _drainBlock = session.CallCapturingSignals(() => GciNative.GciExecuteStr(DrainSource, Oop.Nil.Value), none);
        _pending.AddRange(none);
    }

    /// <summary>The listener's session.</summary>
    public GciSession Session { get; }

    /// <summary>The address other sessions send signals to (<see cref="GciSession.SendSignal"/>).</summary>
    public long SessionId { get; }

    /// <summary>Signals caught outside a poll (e.g. while watching objects), delivered with the next poll.</summary>
    private readonly List<GciSessionSignal> _pending = [];

    /// <summary>
    /// Reports changes other sessions commit to these objects (<c>System addAllToNotifySet:</c>).
    /// Aborts the listener session first, so that it sees recently committed objects.
    /// </summary>
    /// <param name="objects">Committed (persistent) objects; their OOPs are the same in every session.</param>
    public void Watch(params Oop[] objects) => NotifySet("addAllToNotifySet:", objects);

    /// <summary>Stops reporting changes to these objects.</summary>
    public void Unwatch(params Oop[] objects) => NotifySet("removeAllFromNotifySet:", objects);

    /// <summary>Stops reporting changes to any object.</summary>
    public void UnwatchAll()
    {
        lock (GciSession.Sync)
            Release(Session.CallCapturingSignals(() => GciNative.GciExecuteStr("System clearNotifySet. true", Oop.Nil.Value), _pending));
    }

    private unsafe void NotifySet(string selector, Oop[] objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (objects.Length == 0) return;
        lock (GciSession.Sync)
        {
            // Objects committed after the listener's last abort are not visible to it yet.
            Session.Abort();
            // Creating and filling the Array runs no Smalltalk, so no signal can interfere.
            var array = Session.NewObject(KnownClasses.Array, objects.Length);
            try
            {
                Session.StoreVaryingOops(array, 1, objects);
                var system = Session.ResolveSymbol("System");
                Release(Session.CallCapturingSignals(() =>
                {
                    var arg = array.Value; // a local of the lambda: its address may be taken
                    return GciNative.GciPerform(system.Value, selector, &arg, 1);
                }, _pending));
                Session.Release(system);
            }
            finally
            {
                Session.Release(array);
            }
        }
    }

    private void Release(Oop oop)
    {
        if (!oop.IsSpecial) Session.Release(oop);
    }

    /// <summary>
    /// Everything that arrived since the last poll: changed watched objects and signals of other
    /// sessions. Also aborts the listener session. One round trip (two if something arrived).
    /// </summary>
    public unsafe GciSignals Poll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (GciSession.Sync)
        {
            var signals = new List<GciSessionSignal>(_pending);
            _pending.Clear();
            var result = Session.CallCapturingSignals(() =>
            {
                var dummy = Oop.Nil.Value; // the C API wants a valid pointer even without arguments
                return GciNative.GciPerform(_drainBlock.Value, "value", &dummy, 0);
            }, signals);
            try
            {
                var t = Session.Traverse(result, 4); // result → {objs. sigs} → signal → text
                var parts = t[result].Indexed.ToArray();
                Oop[] changed = [.. t[parts[0]].Indexed];
                foreach (var s in t[parts[1]].Indexed)
                {
                    var f = t[s].Indexed.ToArray();
                    signals.Add(new GciSessionSignal(
                        f[0].IsSmallInteger ? f[0].ToSmallInteger() : 0,
                        f[1].IsSmallInteger ? f[1].ToSmallInteger() : null,
                        f[2].IsSmallInteger ? f[2].ToSmallInteger() : 0,
                        t.ToValue(f[3]) as string ?? ""));
                }
                return new GciSignals(changed, signals);
            }
            finally
            {
                Release(result);
            }
        }
    }

    /// <summary>
    /// Raised by the background polling of <see cref="Start"/> when something arrived; on the
    /// <see cref="SynchronizationContext"/> of the thread that called <see cref="Start"/> (the UI
    /// thread in WinForms/WPF), otherwise on a thread-pool thread.
    /// </summary>
    public event EventHandler<GciSignalsEventArgs>? SignalsReceived;

    /// <summary>
    /// Raised when a background poll fails. After a fatal error (the session is lost) polling
    /// stops; other errors are reported and polling continues.
    /// </summary>
    public event EventHandler<GciSignalListenerErrorEventArgs>? PollFailed;

    /// <summary>Polls every <paramref name="interval"/> in the background until <see cref="Stop"/> or <see cref="Dispose"/>.</summary>
    public void Start(TimeSpan interval)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.FromMilliseconds(1));
        lock (_loopLock)
        {
            if (_cts is not null) throw new InvalidOperationException("The listener is already polling.");
            _cts = new CancellationTokenSource();
            var context = SynchronizationContext.Current;
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(interval, context, token));
        }
    }

    /// <summary>Stops the background polling and waits for a running poll to finish.</summary>
    public void Stop()
    {
        Task? loop;
        lock (_loopLock)
        {
            if (_cts is null) return;
            _cts.Cancel();
            loop = _loop;
            _cts = null;
            _loop = null;
        }
        try { loop?.Wait(); }
        catch (AggregateException) { } // cancellation
    }

    private async Task LoopAsync(TimeSpan interval, SynchronizationContext? context, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            GciSignals signals;
            try
            {
                signals = Poll();
            }
            catch (Exception e) when (e is GciException or InvalidOperationException or ObjectDisposedException)
            {
                var lost = e is ObjectDisposedException || (e is GciException { IsFatal: true }) || !Session.IsLoggedIn;
                Raise(context, () => PollFailed?.Invoke(this, new GciSignalListenerErrorEventArgs(e, stopped: lost)));
                if (lost) return;
                continue;
            }
            if (!signals.IsEmpty)
                Raise(context, () => SignalsReceived?.Invoke(this, new GciSignalsEventArgs(signals)));
        }
    }

    private static void Raise(SynchronizationContext? context, Action raise)
    {
        if (context is null) raise();
        else context.Post(_ => raise(), null);
    }

    /// <summary>Stops polling and releases the listener's objects; the session stays logged in (with signals still enabled).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        try
        {
            if (Session.IsLoggedIn) Session.Release(_drainBlock);
        }
        catch (GciException)
        {
            // the session is broken; nothing left to release
        }
    }
}

/// <summary>Arguments of <see cref="GciSignalListener.SignalsReceived"/>.</summary>
public sealed class GciSignalsEventArgs(GciSignals signals) : EventArgs
{
    public GciSignals Signals { get; } = signals;
}

/// <summary>Arguments of <see cref="GciSignalListener.PollFailed"/>.</summary>
public sealed class GciSignalListenerErrorEventArgs(Exception error, bool stopped) : EventArgs
{
    public Exception Error { get; } = error;

    /// <summary>True if polling stopped (the session is lost or disposed).</summary>
    public bool Stopped { get; } = stopped;
}
