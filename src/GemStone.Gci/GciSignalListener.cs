using GemStone.Gci.Native;

namespace GemStone.Gci;

/// <summary>
/// Receives notifications from the server on a session of its own: changes other sessions commit
/// to watched objects (the notify set), and signals other sessions send with
/// <see cref="GciSession.SendSignal"/>.
/// </summary>
/// <remarks>
/// <para>
/// The server cannot call a client. The listener either waits on the server
/// (<see cref="WaitAsync"/>, <see cref="Start()"/>): one call that returns as soon as something
/// arrives, typically within a millisecond of the commit or signal. Or it asks (<see cref="Poll"/>,
/// <see cref="Start(TimeSpan)"/>): one round trip that reads everything that arrived.
/// </para>
/// <para>
/// Use a session that does nothing else: every poll and every wait aborts it (so it never holds
/// an old view of the repository for long), and a wait occupies it.
/// </para>
/// <para>
/// A notification is a hint, not the data: to see a change, the session that works with the
/// objects must abort (or commit) and load them again.
/// </para>
/// </remarks>
public sealed class GciSignalListener : IDisposable
{
    /// <summary>
    /// <c>[:ms | …]</c>: aborts (fresh view, releases the old commit record), empties both queues
    /// and, if nothing was there and <c>ms</c> &gt; 0, waits up to <c>ms</c> milliseconds for a
    /// signal, then empties the queues again. Answers <c>{changed objects. {serial. id. value. text} …}</c>.
    /// </summary>
    /// <remarks>
    /// Signals always go into two queues (<c>_signalFromGemStoneSession</c>, <c>signaledObjects</c>).
    /// Not <c>signalFromGemStoneSession</c> (deprecated since 3.0): it turns the sender's serial
    /// number into a <c>GsSession</c>, which is nil in 3.7 once the sender has logged out.
    /// The sender's session number is looked up from the serial: nil once it has logged out
    /// (3.7 answers 0 then; 3.6 may still answer the old number for a while).
    /// Only while waiting does the listener also let the VM raise them (<c>enableSignalling</c>),
    /// to wake the semaphore; the handler records a session signal (the VM took it out of the
    /// queue for that), wakes the semaphore and resumes. The VM switches raising off after each
    /// one; the <c>ensure:</c> switches it off after the wait in any case.
    /// <para>
    /// Raising stays off otherwise because a signal raised while the session is idle comes back
    /// as error 6008/6010 from the next GCI call, and that is not reliable: a session signal
    /// followed by a commit notification gives only the 6008 error, and the session signal is
    /// gone, neither in an error nor in the queue (3.6.5 and 3.7.6). With raising off, both
    /// stay in their queues. A soft break (used to interrupt the wait) is caught: what was read
    /// so far is still answered.
    /// </para>
    /// </remarks>
    private const string ListenSource = """
        [:ms | | objs sigs add drain wake |
         objs := IdentitySet new. sigs := OrderedCollection new. wake := Semaphore new.
         add := [:serial :int :msg | sigs add: {serial.
           [(GsSession sessionIdOfSerial: serial) ifNotNil: [:id | id = 0 ifFalse: [id]]] on: Error do: [:x | x return: nil].
           int. msg}].
         drain := [| s |
           objs addAll: System signaledObjects.
           [(s := System _signalFromGemStoneSession) notNil]
             whileTrue: [add value: (s at: 1) value: (s at: 2) value: (s at: 3)]].
         [[System abortTransaction.
           drain value.
           (ms > 0 and: [objs isEmpty and: [sigs isEmpty]]) ifTrue: [
             [InterSessionSignal enableSignalling. ObjectsCommittedNotification enableSignalling.
              wake waitForMilliseconds: ms]
               ensure: [InterSessionSignal disableSignalling. ObjectsCommittedNotification disableSignalling]].
           drain value]
            on: Break do: [:e | e return: nil].
          {objs asArray. sigs asArray}]
           on: InterSessionSignal, ObjectsCommittedNotification
           do: [:e | (e isKindOf: InterSessionSignal)
                 ifTrue: [add value: e sendingSessionSerial value: e sentInt value: e sentMessage].
               wake signal.
               e resume: nil]]
        """;

    private readonly Oop _listenBlock;
    private readonly object _loopLock = new();

    /// <summary>One user of the session at a time: a wait, a poll or a change of the notify set.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Interrupts the wait in progress (a soft break the wait block catches).</summary>
    private CancellationTokenSource? _interruptWait;
    private int _waitingForGate;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    /// <summary>Prepares <paramref name="session"/> for receiving signals.</summary>
    /// <param name="session">A session used only by this listener; the caller still owns (and disposes) it.</param>
    public GciSignalListener(GciSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        SessionId = session.GemSessionId;
        // Raising stays off except while waiting (see ListenSource). Signals sent before this are
        // still captured if they surface as an error.
        var none = new List<GciSessionSignal>();
        session.CallCapturingSignals(() => GciNative.GciExecuteStr(
            "System disableSignaledObjectsError; disableSignaledGemStoneSessionError. true", Oop.Nil.Value), none);
        _listenBlock = session.CallCapturingSignals(() => GciNative.GciExecuteStr(ListenSource, Oop.Nil.Value), none);
        _pending.AddRange(none);
    }

    /// <summary>The listener's session.</summary>
    public GciSession Session { get; }

    /// <summary>The address other sessions send signals to (<see cref="GciSession.SendSignal"/>).</summary>
    public long SessionId { get; }

    /// <summary>
    /// The longest a single wait of <see cref="Start()"/> lasts before it aborts and waits again
    /// (default 5 seconds). The listener's view of the repository is at most this old.
    /// </summary>
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Signals caught outside a poll (e.g. while watching objects), delivered with the next poll.</summary>
    private readonly List<GciSessionSignal> _pending = [];

    /// <summary>
    /// Reports changes other sessions commit to these objects (<c>System addAllToNotifySet:</c>).
    /// Aborts the listener session first, so that it sees recently committed objects. Interrupts a
    /// wait in progress, which then goes on.
    /// </summary>
    /// <param name="objects">Committed (persistent) objects; their OOPs are the same in every session.</param>
    public void Watch(params Oop[] objects) => NotifySet("addAllToNotifySet:", objects);

    /// <summary>Stops reporting changes to these objects.</summary>
    public void Unwatch(params Oop[] objects) => NotifySet("removeAllFromNotifySet:", objects);

    /// <summary>Stops reporting changes to any object.</summary>
    public void UnwatchAll() => Exclusive(() =>
        Release(Session.CallCapturingSignals(() => GciNative.GciExecuteStr("System clearNotifySet. true", Oop.Nil.Value), _pending)));

    private unsafe void NotifySet(string selector, Oop[] objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (objects.Length == 0) return;
        Exclusive(() =>
        {
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
        });
    }

    /// <summary>Runs <paramref name="action"/> with the session, interrupting a wait in progress.</summary>
    private void Exclusive(Action action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_loopLock)
        {
            _waitingForGate++;
            _interruptWait?.Cancel();
        }
        try
        {
            _gate.Wait();
        }
        finally
        {
            lock (_loopLock) _waitingForGate--;
        }
        try
        {
            action();
        }
        finally
        {
            _gate.Release();
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
        GciSignals signals = null!;
        Exclusive(() =>
        {
            lock (GciSession.Sync)
            {
                var received = TakePending();
                var result = Session.CallCapturingSignals(() =>
                {
                    var ms = Oop.FromSmallInteger(0).Value;
                    return GciNative.GciPerform(_listenBlock.Value, "value:", &ms, 1);
                }, received);
                signals = Decode(result, received);
            }
        });
        return signals;
    }

    /// <summary>
    /// Waits on the server until something arrives (then returns it, typically within a millisecond of the
    /// commit or signal) or <paramref name="timeout"/> has passed (then returns an empty result).
    /// Aborts the listener session first. The GCI is free meanwhile: other sessions keep working.
    /// </summary>
    /// <param name="timeout">At most this long; whole milliseconds.</param>
    /// <param name="cancellationToken">Ends the wait (with <see cref="OperationCanceledException"/>);
    /// anything that arrived until then comes with the next poll or wait.</param>
    /// <exception cref="InvalidOperationException">Another wait or a background loop is running.</exception>
    public async Task<GciSignals> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_loopLock)
            if (_cts is not null) throw new InvalidOperationException("The listener is already running in the background.");
        return await WaitCoreAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GciSignals> WaitCoreAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var ms = (long)Math.Ceiling(timeout.TotalMilliseconds);
        if (ms < 1) throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be at least one millisecond.");
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        var received = TakePending();
        try
        {
            using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_loopLock)
            {
                if (_waitingForGate > 0) ms = 0; // someone needs the session: just look, don't wait
                _interruptWait = interrupt;
            }
            Oop result;
            try
            {
                // Something already in hand (kept from before, or a signal that surfaced as an error
                // when the call started): just read the queues, don't wait.
                result = await Session.PerformCapturingSignalsAsync(_listenBlock, "value:",
                    () => [Oop.FromSmallInteger(received.Count > 0 || HasPendingObjects ? 0 : ms)],
                    received, interrupt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Interrupted before the block's handler was in place: nothing was read yet.
                return new GciSignals([], received);
            }
            finally
            {
                lock (_loopLock) _interruptWait = null;
            }
            GciSignals signals;
            lock (GciSession.Sync) signals = Decode(result, received);
            if (!cancellationToken.IsCancellationRequested) return signals;
            Keep(signals); // the wait block caught the soft break and answered what it had read
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) when (received.Count > 0 && !_kept)
        {
            lock (_loopLock) _pending.InsertRange(0, received);
            throw;
        }
        finally
        {
            _kept = false;
            _gate.Release();
        }
    }

    private bool _kept; // under _gate: Keep already saved what was received

    private List<GciSessionSignal> TakePending()
    {
        lock (_loopLock)
        {
            var received = new List<GciSessionSignal>(_pending);
            _pending.Clear();
            return received;
        }
    }

    /// <summary>Changed objects are reported again by the server only if changed again: keep them for the next call.</summary>
    private void Keep(GciSignals signals)
    {
        _kept = true;
        lock (_loopLock)
        {
            _pending.InsertRange(0, signals.Messages);
            _pendingObjects.InsertRange(0, signals.ChangedObjects);
        }
    }

    private readonly List<Oop> _pendingObjects = [];

    private bool HasPendingObjects
    {
        get { lock (_loopLock) return _pendingObjects.Count > 0; }
    }

    /// <summary>Reads the listen block's answer and releases it. Under <see cref="GciSession.Sync"/>.</summary>
    private GciSignals Decode(Oop result, List<GciSessionSignal> signals)
    {
        try
        {
            var t = Session.Traverse(result, 4); // result → {objs. sigs} → signal → text
            var parts = t[result].Indexed.ToArray();
            List<Oop> changed;
            lock (_loopLock)
            {
                changed = [.. _pendingObjects];
                _pendingObjects.Clear();
            }
            foreach (var o in t[parts[0]].Indexed)
                if (!changed.Contains(o)) changed.Add(o);
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

    /// <summary>
    /// Raised by <see cref="Start()"/> / <see cref="Start(TimeSpan)"/> when something arrived; on
    /// the <see cref="SynchronizationContext"/> of the thread that called Start (the UI thread in
    /// WinForms/WPF), otherwise on a thread-pool thread.
    /// </summary>
    public event EventHandler<GciSignalsEventArgs>? SignalsReceived;

    /// <summary>
    /// Raised when a background wait or poll fails. After a fatal error (the session is lost)
    /// the listener stops; other errors are reported and it goes on.
    /// </summary>
    public event EventHandler<GciSignalListenerErrorEventArgs>? PollFailed;

    /// <summary>
    /// Waits for notifications in the background (<see cref="WaitAsync"/>, at most <see cref="MaxWait"/>
    /// at a time) until <see cref="Stop"/> or <see cref="Dispose"/>, and raises
    /// <see cref="SignalsReceived"/> as soon as something arrives. The recommended way: low latency,
    /// one round trip per <see cref="MaxWait"/> while nothing happens.
    /// </summary>
    public void Start() => StartLoop(null);

    /// <summary>
    /// Polls every <paramref name="interval"/> in the background (<see cref="Poll"/>) until
    /// <see cref="Stop"/> or <see cref="Dispose"/>. Latency up to the interval, one round trip per interval;
    /// <see cref="Start()"/> is usually the better choice.
    /// </summary>
    public void Start(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.FromMilliseconds(1));
        StartLoop(interval);
    }

    private void StartLoop(TimeSpan? interval)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_loopLock)
        {
            if (_cts is not null) throw new InvalidOperationException("The listener is already running in the background.");
            _cts = new CancellationTokenSource();
            var context = SynchronizationContext.Current;
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(interval, context, token));
        }
    }

    /// <summary>
    /// Stops the background loop and waits for a running wait or poll to finish. Called from a
    /// <see cref="SignalsReceived"/> handler that runs on the loop itself (no synchronization
    /// context), it only ends the loop: waiting there would wait for itself.
    /// </summary>
    public void Stop()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_loopLock)
        {
            if (_cts is null) return;
            cts = _cts;
            cts.Cancel();
            loop = _loop;
            _cts = null;
            _loop = null;
        }
        if (_raising == this) return; // the loop ends after the handler; it still uses the token
        try { loop?.Wait(); }
        catch (AggregateException) { } // cancellation
        cts.Dispose();
    }

    /// <summary>The listener whose event handler runs on this thread, called directly from the loop.</summary>
    [ThreadStatic] private static GciSignalListener? _raising;

    private async Task LoopAsync(TimeSpan? interval, SynchronizationContext? context, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            GciSignals signals;
            try
            {
                if (interval is { } i)
                {
                    await Task.Delay(i, token).ConfigureAwait(false);
                    signals = Poll();
                }
                else
                {
                    if (Volatile.Read(ref _waitingForGate) > 0) await Task.Delay(1, token).ConfigureAwait(false); // let Watch etc. go first
                    signals = await WaitCoreAsync(MaxWait, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is GciException or InvalidOperationException or ObjectDisposedException)
            {
                var lost = e is ObjectDisposedException || (e is GciException { IsFatal: true }) || !Session.IsLoggedIn;
                Raise(context, () => PollFailed?.Invoke(this, new GciSignalListenerErrorEventArgs(e, stopped: lost)));
                if (lost) return;
                try { await Task.Delay(interval ?? TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            if (!signals.IsEmpty)
                Raise(context, () => SignalsReceived?.Invoke(this, new GciSignalsEventArgs(signals)));
        }
    }

    private void Raise(SynchronizationContext? context, Action raise)
    {
        if (context is not null)
        {
            context.Post(_ => raise(), null);
            return;
        }
        var outer = _raising;
        _raising = this;
        try
        {
            raise();
        }
        finally
        {
            _raising = outer;
        }
    }

    /// <summary>Stops the background loop and releases the listener's objects; the session stays logged in (with signals still enabled).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        try
        {
            if (Session.IsLoggedIn) Session.Release(_listenBlock);
        }
        catch (Exception e) when (e is GciException or InvalidOperationException)
        {
            // the session is broken or still busy; the gem drops its export set at logout
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

    /// <summary>True if the background loop stopped (the session is lost or disposed).</summary>
    public bool Stopped { get; } = stopped;
}
