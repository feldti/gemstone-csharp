namespace GemStone.Gci.Mapping;

/// <summary>
/// A read-only copy of an object graph in C# that follows the stone: it loads the graph once
/// (without change tracking) and then refreshes what writers publish to a <see cref="GciChangeFeed"/>.
/// Readers and updates are synchronized with a reader/writer lock.
/// </summary>
/// <remarks>
/// <para>
/// The replica needs two sessions: the work session of the <paramref name="context"/> (it loads,
/// aborts, takes from the feed and commits the take) and a listener session that does nothing else
/// (its <see cref="GciSignalListener"/> receives the feed's doorbell). The feed object must use the
/// work session.
/// </para>
/// <para>
/// An update: abort (see what the writers committed), take the published objects, refresh them
/// in one load, report them, and commit the take in the background (the next update waits for
/// it), so a change is visible without waiting for the stone's log write. If anything fails, it
/// aborts, and the objects stay in the queue; after a crash they come again (refreshed twice,
/// which is harmless). While that commit runs, the work session is busy: the replica owns it. Updates run when the doorbell rings and, because signals
/// are not persistent, every <see cref="PollInterval"/>.
/// </para>
/// <para>
/// Read the graph only inside <see cref="Read{TResult}"/> (or <see cref="Read(Action{T})"/>), which
/// holds the read lock; an update waits until all readers are done, and readers wait while an
/// update changes the objects.
/// </para>
/// </remarks>
/// <typeparam name="T">Mapped class of the root.</typeparam>
/// <param name="context">Context that holds the replica; its session is the work session. Owned by the caller.</param>
/// <param name="feed">Change feed on the same session.</param>
/// <param name="consumer">Key of this replica in the feed, stable across restarts (one per running replica).</param>
/// <param name="listener">Listener on a separate session; the replica handles its <see cref="GciSignalListener.SignalsReceived"/>.</param>
/// <param name="rootSource">Smalltalk expression that answers the root (as for <see cref="GsObjectContext.Load{T}(string, GsLoadOptions?)"/>).</param>
public sealed class GsReplica<T>(GsObjectContext context, GciChangeFeed feed, string consumer, GciSignalListener listener, string rootSource)
    : IDisposable where T : class
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly SemaphoreSlim _updating = new(1, 1);
    private readonly GsLoadOptions _options = new() { TrackChanges = false };
    private Timer? _timer;
    private T? _root;
    private bool _disposed;
    private Task<bool>? _pendingCommit;                     // commit of the last take, still running

    /// <summary>Fallback interval for updates without a doorbell (signals are not persistent). Set before <see cref="Start"/>.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Raised after an update changed objects, outside the locks, on the thread that ran the update.</summary>
    public event EventHandler<GsReplicaChangedEventArgs>? Changed;

    /// <summary>Raised when an update failed; the published objects stay in the queue.</summary>
    public event EventHandler<GsReplicaErrorEventArgs>? UpdateFailed;

    /// <summary>
    /// Registers the consumer, loads the graph and starts listening. Changes committed after the
    /// registration are refreshed by the next update (some objects may be refreshed twice).
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(feed.Session, context.Session))
            throw new InvalidOperationException("The change feed must use the context's session.");
        var session = context.Session;
        feed.Register(consumer, listener.SessionId);
        session.Commit();                                   // the queue collects from here on
        _lock.EnterWriteLock();
        try
        {
            _root = context.Load<T>(rootSource, _options);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        listener.SignalsReceived += OnSignals;
        listener.Start();
        _timer = new Timer(_ => SafeUpdate(), null, PollInterval, PollInterval);
        SafeUpdate();                                       // what was committed during the load
    }

    /// <summary>Runs <paramref name="read"/> on the root under the read lock.</summary>
    public TResult Read<TResult>(Func<T, TResult> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _lock.EnterReadLock();
        try
        {
            return read(_root ?? throw new InvalidOperationException("Call Start first."));
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Runs <paramref name="read"/> on the root under the read lock.</summary>
    public void Read(Action<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        Read(root => { read(root); return 0; });
    }

    /// <summary>
    /// Takes what was published for this replica and refreshes it now; answers the refreshed
    /// objects. Normally the doorbell and the timer call it.
    /// </summary>
    public IReadOnlyList<object> Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = context.Session;
        IReadOnlyList<object> refreshed;
        Oop[] oops;
        _updating.Wait();
        try
        {
            EndTransaction(session);
            oops = feed.Take(consumer);
            if (oops.Length == 0)
                return [];
            _lock.EnterWriteLock();
            try
            {
                refreshed = context.Refresh(oops, _options);
            }
            catch
            {
                session.Abort();                            // the taken objects go back into the queue
                throw;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
            // Make the take permanent in the background: the commit waits for the stone's log write
            // (several milliseconds), which would otherwise delay every change. The next update
            // waits for it. Task.Run: no SynchronizationContext, so the blocking wait cannot deadlock.
            _pendingCommit = Task.Run(session.TryCommitAsync);
        }
        finally
        {
            _updating.Release();
        }
        if (refreshed.Count > 0) Changed?.Invoke(this, new GsReplicaChangedEventArgs(refreshed, oops));
        return refreshed;
    }

    /// <summary>Waits for the commit of the last take (if any), then aborts for the latest view.</summary>
    private void EndTransaction(GciSession session)
    {
        if (_pendingCommit is { } commit)
        {
            _pendingCommit = null;
            try
            {
                commit.GetAwaiter().GetResult();            // false or an error: those objects come again
            }
            catch (GciException)
            {
            }
        }
        session.Abort();                                    // the commit's view is already old
    }

    private void OnSignals(object? sender, GciSignalsEventArgs e)
    {
        if (e.Signals.Messages.Any(m => m.Value == GciChangeFeed.SignalValue)) SafeUpdate();
    }

    private void SafeUpdate()
    {
        if (_disposed) return;
        try
        {
            Update();
        }
        catch (Exception ex) when (ex is GciException or InvalidOperationException or GsMappingException)
        {
            UpdateFailed?.Invoke(this, new GsReplicaErrorEventArgs(ex));
        }
    }

    /// <summary>Stops listening and updating. The consumer stays registered; see <see cref="GciChangeFeed.Unregister"/>.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        listener.SignalsReceived -= OnSignals;
        listener.Stop();
        _timer?.Dispose();
        _updating.Wait();                                   // let a running update finish
        try
        {
            if (_pendingCommit is not null && context.Session.IsLoggedIn) EndTransaction(context.Session);
        }
        catch (GciException)
        {
            // Not committed: those objects are delivered again to the next replica with this key.
        }
        finally
        {
            _updating.Release();
        }
    }
}

/// <summary>Objects an update of a <see cref="GsReplica{T}"/> refreshed.</summary>
public sealed class GsReplicaChangedEventArgs(IReadOnlyList<object> entities, IReadOnlyList<Oop> published) : EventArgs
{
    /// <summary>The refreshed C# objects (owners of reported collections included).</summary>
    public IReadOnlyList<object> Entities { get; } = entities;

    /// <summary>What the writers published (including objects the replica does not hold).</summary>
    public IReadOnlyList<Oop> Published { get; } = published;
}

/// <summary>A failed update of a <see cref="GsReplica{T}"/>.</summary>
public sealed class GsReplicaErrorEventArgs(Exception error) : EventArgs
{
    /// <summary>What went wrong.</summary>
    public Exception Error { get; } = error;
}
