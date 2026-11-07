using GemStone.Gci.Native;

namespace GemStone.Gci;

/// <summary>Transaction mode of a session (<c>System transactionMode</c>).</summary>
public enum GciTransactionMode
{
    /// <summary><c>#autoBegin</c> (default): a new transaction starts after every commit or abort.</summary>
    AutoBegin,

    /// <summary><c>#manualBegin</c>: outside a transaction until <see cref="GciSession.Begin"/>.</summary>
    ManualBegin,

    /// <summary><c>#transactionless</c>: never in a transaction, for read-mostly sessions.</summary>
    Transactionless,
}

/// <summary>Commit failed because of concurrency conflicts with another session.</summary>
/// <remarks>
/// The session keeps its uncommitted changes; GemStone expects an <see cref="GciSession.Abort"/>
/// before the work is retried.
/// </remarks>
public sealed class GciTransactionConflictException(string? conflictReport)
    : Exception("Commit failed due to transaction conflicts." +
                (string.IsNullOrEmpty(conflictReport) ? "" : " " + conflictReport))
{
    /// <summary><c>System transactionConflicts printString</c>, or null if it could not be fetched.</summary>
    public string? ConflictReport { get; } = conflictReport;
}

public sealed partial class GciSession
{
    /// <summary>Commits the current transaction (GciCommit).</summary>
    /// <exception cref="GciTransactionConflictException">Concurrency conflict; call <see cref="Abort"/> before retrying.</exception>
    /// <exception cref="GciException">The commit raised an error.</exception>
    public void Commit()
    {
        lock (Sync)
        {
            if (TryCommit()) return;
            throw new GciTransactionConflictException(TryGetConflictReport());
        }
    }

    /// <summary>Commits the current transaction; false on a concurrency conflict.</summary>
    /// <exception cref="GciException">The commit raised an error (other than a conflict).</exception>
    public bool TryCommit()
    {
        lock (Sync)
        {
            Activate();
            if (GciNative.GciCommit()) return true;
            ThrowIfError(); // FALSE with an error report = real error
            return false;   // FALSE without = transaction conflict (GciCommit docs)
        }
    }

    /// <summary>Discards all uncommitted changes and moves to the latest committed state (GciAbort).</summary>
    public void Abort()
    {
        lock (Sync)
        {
            Activate();
            GciNative.GciAbort();
            ThrowIfError();
        }
    }

    /// <summary>Begins a transaction (GciBegin); a transaction in progress is aborted first.</summary>
    public void Begin()
    {
        lock (Sync)
        {
            Activate();
            GciNative.GciBegin();
            ThrowIfError();
        }
    }

    /// <summary>Whether the session is currently in a transaction (<c>System inTransaction</c>).</summary>
    public bool InTransaction => Evaluate<bool>("System inTransaction");

    /// <summary>Whether there are uncommitted changes (<c>System needsCommit</c>).</summary>
    public bool NeedsCommit => Evaluate<bool>("System needsCommit");

    /// <summary>Transaction mode (<c>System transactionMode</c> / <c>transactionMode:</c>).</summary>
    public GciTransactionMode TransactionMode
    {
        get => Evaluate<string>("System transactionMode") switch
        {
            "autoBegin" => GciTransactionMode.AutoBegin,
            "manualBegin" => GciTransactionMode.ManualBegin,
            "transactionless" => GciTransactionMode.Transactionless,
            var other => throw new InvalidOperationException($"Unknown transaction mode #{other}."),
        };
        set => Execute("System transactionMode: #" + value switch
        {
            GciTransactionMode.AutoBegin => "autoBegin",
            GciTransactionMode.ManualBegin => "manualBegin",
            GciTransactionMode.Transactionless => "transactionless",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        });
    }

    /// <summary>
    /// Runs <paramref name="work"/> and commits; on a conflict aborts and retries, up to
    /// <paramref name="maxAttempts"/> times. Any other exception aborts and is rethrown.
    /// </summary>
    /// <remarks>
    /// The work must be repeatable: after an abort it sees the newly committed state of other sessions.
    /// In <see cref="GciTransactionMode.ManualBegin"/> mode a transaction is begun before every attempt.
    /// </remarks>
    public T RunInTransaction<T>(Func<GciSession, T> work, int maxAttempts = 3)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        lock (Sync)
        {
            var manual = TransactionMode == GciTransactionMode.ManualBegin;
            for (var attempt = 1; ; attempt++)
            {
                if (manual) Begin();
                T result;
                try
                {
                    result = work(this);
                }
                catch
                {
                    if (IsLoggedIn) Abort();
                    throw;
                }
                if (TryCommit()) return result;

                var report = TryGetConflictReport();
                Abort();
                if (attempt >= maxAttempts) throw new GciTransactionConflictException(report);
            }
        }
    }

    /// <inheritdoc cref="RunInTransaction{T}(Func{GciSession, T}, int)"/>
    public void RunInTransaction(Action<GciSession> work, int maxAttempts = 3)
    {
        ArgumentNullException.ThrowIfNull(work);
        RunInTransaction<object?>(s => { work(s); return null; }, maxAttempts);
    }

    private string? TryGetConflictReport()
    {
        try { return Evaluate<string>("System transactionConflicts printString"); }
        catch (GciException) { return null; }
    }
}
