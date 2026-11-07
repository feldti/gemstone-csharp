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
    public async Task<bool> TryCommitAsync() =>
        await RunNonblocking(GciNative.GciNbCommit, ResultKind.Bool, CancellationToken.None) != 0;

    /// <summary><see cref="Commit"/> asynchronously.</summary>
    /// <exception cref="GciTransactionConflictException">Concurrency conflict; abort before retrying.</exception>
    public async Task CommitAsync()
    {
        if (!await TryCommitAsync())
            throw new GciTransactionConflictException(TryGetConflictReport());
    }

    /// <summary><see cref="Abort"/> asynchronously.</summary>
    public Task AbortAsync() => RunNonblocking(GciNative.GciNbAbort, ResultKind.Void, CancellationToken.None);

    /// <summary><see cref="Begin"/> asynchronously.</summary>
    public Task BeginAsync() => RunNonblocking(GciNative.GciNbBegin, ResultKind.Void, CancellationToken.None);

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
                oop = new Oop(GciNative.GciNewUtf8String_(p, (nuint)bytes.Length, utf8OrUnicode: false));
            ThrowIfError();
            return oop;
        }
    }

    /// <summary>
    /// Starts a nonblocking call and polls until it completes, releasing the GCI lock between polls.
    /// Returns the raw result (OOP or BoolType); errors are thrown like their blocking counterparts.
    /// </summary>
    private async Task<long> RunNonblocking(Action start, ResultKind kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (Sync)
        {
            Activate();
            start();
            _nbInProgress = true;
        }

        var breakSent = false;
        var delay = TimeSpan.FromMilliseconds(1);
        while (true)
        {
            if (TryFinish(kind, breakSent, out var result))
                return result;

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

            await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
            if (delay < MaxPollDelay) delay = TimeSpan.FromTicks(delay.Ticks * 2);
        }
    }

    /// <summary>One poll under the lock; true when the call has completed (result or exception).</summary>
    private unsafe bool TryFinish(ResultKind kind, bool cancelled, out long result)
    {
        lock (Sync)
        {
            SwitchTo(); // throws ObjectDisposedException if the session was disposed meanwhile
            long value = 0;
            var status = GciNative.GciNbEndPoll(&value, 0);
            if (status is GciNative.NbProgress.NotReady or GciNative.NbProgress.Progressed)
            {
                result = 0;
                return false;
            }

            _nbInProgress = false;
            try
            {
                if (status == GciNative.NbProgress.HasError || kind == ResultKind.Void ||
                    (kind == ResultKind.Oop && new Oop((ulong)value).IsNil) ||
                    (kind == ResultKind.Bool && value == 0))
                    ThrowIfError(); // nil / FALSE is also a valid result: only throws if GciErr has one
            }
            catch (GciException ex) when (cancelled)
            {
                throw new OperationCanceledException("The GemStone call was interrupted by a soft break.", ex);
            }

            result = value;
            return true;
        }
    }

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
