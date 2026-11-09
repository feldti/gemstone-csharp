using System.Text;
using GemStone.Gci.Native;

namespace GemStone.Gci;

/// <summary>
/// Asynchronous variants built on the nonblocking GCI (<c>GciNb*</c> + <c>GciNbEndPoll</c>).
/// </summary>
/// <remarks>
/// <para>
/// While a session waits for the server, the process-wide GCI lock is free: other sessions can
/// make calls (sync or async) in the meantime, so N sessions can execute in parallel on their
/// gems. Each session supports one call at a time — synchronous calls on a session with a
/// pending async call throw <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// Completion is detected by polling with a growing delay (1 ms … <see cref="MaxPollDelay"/>);
/// on Windows, timer resolution makes the shortest delays ~15 ms. Converting results to .NET
/// values (<see cref="EvaluateAsync"/>, <see cref="SendAsync"/>) uses short synchronous calls.
/// </para>
/// </remarks>
public sealed partial class GciSession
{
    /// <summary>Upper bound of the polling interval while waiting for a nonblocking call.</summary>
    public static readonly TimeSpan MaxPollDelay = TimeSpan.FromMilliseconds(32);

    private bool _nbInProgress; // guarded by Sync

    /// <summary>
    /// <see cref="Execute"/> without blocking the GCI. Cancelling sends a soft break; the task
    /// then fails with <see cref="OperationCanceledException"/> (the session stays usable).
    /// </summary>
    public async Task<Oop> ExecuteAsync(string source, Oop? symbolList = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var symList = (symbolList ?? Oop.Nil).Value;
        if (Ascii.IsValid(source))
            return new Oop((ulong)await RunNonblocking(() => GciNative.GciNbExecuteStr(source, symList), ResultKind.Oop, cancellationToken));

        // Non-ASCII source must be compiled from a Utf8 object, as in Execute.
        var sourceObj = NewUtf8(source);
        try
        {
            return new Oop((ulong)await RunNonblocking(() => GciNative.GciNbExecute(sourceObj.Value, symList), ResultKind.Oop, cancellationToken));
        }
        finally
        {
            Release(sourceObj);
        }
    }

    /// <summary><see cref="Perform"/> without blocking the GCI; cancellation as for <see cref="ExecuteAsync"/>.</summary>
    public async Task<Oop> PerformAsync(Oop receiver, string selector, Oop[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(args);
        var argsCopy = args.ToArray(); // pinned only during the start call
        return new Oop((ulong)await RunNonblocking(() => StartPerform(receiver, selector, argsCopy), ResultKind.Oop, cancellationToken));
    }

    /// <inheritdoc cref="PerformAsync(Oop, string, Oop[], CancellationToken)"/>
    public Task<Oop> PerformAsync(Oop receiver, string selector, params Oop[] args) =>
        PerformAsync(receiver, selector, args, CancellationToken.None);

    /// <summary><see cref="Evaluate"/> asynchronously.</summary>
    public async Task<object?> EvaluateAsync(string source, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(source, cancellationToken: cancellationToken);
        lock (Sync) return ConvertAndRelease(result);
    }

    /// <summary><see cref="Evaluate{T}"/> asynchronously.</summary>
    public async Task<T> EvaluateAsync<T>(string source, CancellationToken cancellationToken = default) =>
        CastTo<T>(await EvaluateAsync(source, cancellationToken));

    /// <summary><see cref="Send"/> asynchronously.</summary>
    public async Task<object?> SendAsync(Oop receiver, string selector, object?[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        Oop[] argOops;
        lock (Sync) argOops = [.. args.Select(FromValue)];
        try
        {
            var result = await PerformAsync(receiver, selector, argOops, cancellationToken);
            lock (Sync) return ConvertAndRelease(result);
        }
        finally
        {
            Release([.. argOops.Where((_, i) => args[i] is not Oop)]);
        }
    }

    /// <summary><see cref="TryCommit"/> asynchronously; false on a transaction conflict. Not cancellable once started.</summary>
    public async Task<bool> TryCommitAsync()
    {
        if (await RunNonblocking(GciNative.GciNbCommit, ResultKind.Bool, CancellationToken.None) == 0) return false;
        lock (Sync) OnTransactionEnded(committed: true);
        return true;
    }

    /// <summary><see cref="Commit"/> asynchronously.</summary>
    /// <exception cref="GciTransactionConflictException">Concurrency conflict; abort before retrying.</exception>
    public async Task CommitAsync()
    {
        if (!await TryCommitAsync())
            throw new GciTransactionConflictException(TryGetConflictReport());
    }

    /// <summary><see cref="Abort"/> asynchronously.</summary>
    public async Task AbortAsync()
    {
        try
        {
            await RunNonblocking(GciNative.GciNbAbort, ResultKind.Void, CancellationToken.None);
        }
        finally
        {
            lock (Sync) OnTransactionEnded(committed: false);
        }
    }

    /// <summary><see cref="Begin"/> asynchronously.</summary>
    public async Task BeginAsync()
    {
        try
        {
            await RunNonblocking(GciNative.GciNbBegin, ResultKind.Void, CancellationToken.None);
        }
        finally
        {
            lock (Sync) OnTransactionEnded(committed: false);
        }
    }

    // ---- Machinery --------------------------------------------------------------------------

    private enum ResultKind { Oop, Bool, Void }

    private static unsafe void StartPerform(Oop receiver, string selector, Oop[] args)
    {
        ulong dummy = Oop.Nil.Value; // the C API wants a valid pointer even for zero arguments
        fixed (Oop* p = args)
            GciNative.GciNbPerform(receiver.Value, selector, args.Length == 0 ? &dummy : (ulong*)p, args.Length);
    }

    private unsafe Oop NewUtf8(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        lock (Sync)
        {
            Activate();
            Oop oop;
            fixed (byte* p = bytes)
                oop = new Oop(GciNative.GciNewUtf8String_(p, (nuint)bytes.Length, utf8OrUnicode: true)); // Unicode, see Execute
            ThrowIfError();
            return oop;
        }
    }

    /// <summary>
    /// Starts a nonblocking call and polls until it completes, releasing the GCI lock between polls.
    /// Returns the raw result (OOP or BoolType); errors are thrown like their blocking counterparts.
    /// </summary>
    /// <param name="start">Starts the nonblocking call (under the lock, session active).</param>
    /// <param name="kind">How to read the result.</param>
    /// <param name="ct">Sends a soft break.</param>
    /// <param name="signals">If set, signals that make the call fail (6008/6010) are captured here and the call repeated.</param>
    /// <param name="waitOnSocket">
    /// Wait for the session's socket (GciNbGetNotifyHandle + GciPollSocketForRead, outside the
    /// lock) instead of polling with a growing delay: no CPU while waiting, and the result is
    /// picked up as soon as it arrives. For long calls such as the signal listener's wait.
    /// </param>
    private async Task<long> RunNonblocking(Action start, ResultKind kind, CancellationToken ct,
        List<GciSessionSignal>? signals = null, bool waitOnSocket = false)
    {
        var maxDelay = MaxPollDelay;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var handle = -1;
            lock (Sync)
            {
                Activate();
                start();
                _nbInProgress = true;
                if (waitOnSocket) handle = GciNative.GciNbGetNotifyHandle();
            }

            var breakSent = false;
            var delay = TimeSpan.FromMilliseconds(1);
            while (true)
            {
                var state = TryFinish(kind, breakSent, signals, attempt, out var result);
                if (state == Finish.Done) return result;
                if (state == Finish.Retry) break;

                if (!breakSent && ct.IsCancellationRequested && kind == ResultKind.Oop)
                {
                    lock (Sync)
                    {
                        if (_nbInProgress)
                        {
                            SwitchTo();
                            GciNative.GciSoftBreak(); // the call then completes with RT_ERR_SOFT_BREAK
                        }
                    }
                    breakSent = true;
                    continue;
                }

                if (handle >= 0)
                {
                    // Blocks a pool thread for up to SocketWaitSlice, without the lock and without CPU.
                    var h = handle;
                    if (await Task.Run(() => GciNative.GciPollSocketForRead(h, (int)SocketWaitSlice.TotalMilliseconds)).ConfigureAwait(false) < 0)
                        handle = -1; // poll failed: fall back to the timed loop
                    continue;
                }
                await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
                if (delay < maxDelay) delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
            }
        }
    }

    private enum Finish { Pending, Done, Retry }

    /// <summary>
    /// One poll under the lock: <see cref="Finish.Done"/> when the call has completed (result or
    /// exception), <see cref="Finish.Retry"/> when it failed because a signal arrived first and
    /// <paramref name="signals"/> captured it (see <see cref="CallCapturingSignals"/>).
    /// </summary>
    private unsafe Finish TryFinish(ResultKind kind, bool cancelled, List<GciSessionSignal>? signals, int attempt, out long result)
    {
        lock (Sync)
        {
            result = 0;
            SwitchTo(); // throws ObjectDisposedException if the session was disposed meanwhile
            long value = 0;
            var status = GciNative.GciNbEndPoll(&value, 0);
            if (status is GciNative.NbProgress.NotReady or GciNative.NbProgress.Progressed)
                return Finish.Pending;

            _nbInProgress = false;
            if (status == GciNative.NbProgress.HasError || kind == ResultKind.Void ||
                (kind == ResultKind.Oop && new Oop((ulong)value).IsNil) ||
                (kind == ResultKind.Bool && value == 0))
            {
                // nil / FALSE is also a valid result: only an error if GciErr has one
                var error = LastError();
                if (error is not null)
                {
                    if (signals is not null && attempt <= 1000 && TryCaptureSignal(error, signals)) return Finish.Retry;
                    DiscardError(error);
                    if (cancelled) throw new OperationCanceledException("The GemStone call was interrupted by a soft break.", error);
                    throw error;
                }
            }

            result = value;
            return Finish.Done;
        }
    }

    /// <summary>
    /// <see cref="PerformAsync(Oop, string, Oop[], CancellationToken)"/> on a session with signals
    /// enabled: a signal that arrived while the session was idle is captured and the call repeated.
    /// </summary>
    /// <param name="receiver">Receiver of the message.</param>
    /// <param name="selector">Selector of the message.</param>
    /// <param name="args">The arguments, asked again for every attempt (a repeated call may need others).</param>
    /// <param name="signals">Receives the captured signals.</param>
    /// <param name="ct">Sends a soft break.</param>
    internal async Task<Oop> PerformCapturingSignalsAsync(Oop receiver, string selector, Func<Oop[]> args,
        List<GciSessionSignal> signals, CancellationToken ct)
    {
        return new Oop((ulong)await RunNonblocking(() => StartPerform(receiver, selector, args()), ResultKind.Oop, ct,
            signals, waitOnSocket: true).ConfigureAwait(false));
    }

    /// <summary>
    /// How long one wait for the socket lasts before cancellation is checked again (a
    /// cancellation sends a soft break at most this late).
    /// </summary>
    private static readonly TimeSpan SocketWaitSlice = TimeSpan.FromMilliseconds(50);

    /// <summary>Called from Dispose (under the lock): interrupt and drain a pending nonblocking call before logging out.</summary>
    private unsafe void FinishPendingNonblockingCall()
    {
        GciNative.GciSoftBreak();
        long value;
        var deadline = Environment.TickCount64 + 30_000;
        while (GciNative.GciNbEndPoll(&value, 100) is GciNative.NbProgress.NotReady or GciNative.NbProgress.Progressed
               && Environment.TickCount64 < deadline)
        {
        }
        _nbInProgress = false;
        LastError();
    }
}
