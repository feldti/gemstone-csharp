namespace GemStone.Gci.Tests;

/// <summary>
/// Writes to <c>UserGlobals</c> of the test user under a unique key and removes it afterwards.
/// A second session in the same process (<see cref="_other"/>) provides concurrent commits.
/// </summary>
[Collection("Stone")]
public class GciTransactionIntegrationTests : IDisposable
{
    private readonly string _key = "GemStoneGciTest_" + Guid.NewGuid().ToString("N")[..12];
    private readonly GciSession _s = StoneEnv.Login();
    private readonly GciSession _other = StoneEnv.Login();

    private string At => $"UserGlobals at: #{_key} ifAbsent: [nil]";
    private string Put(object value) => $"UserGlobals at: #{_key} put: {value}";

    public void Dispose()
    {
        try
        {
            _other.Dispose();
            if (_s.IsLoggedIn)
            {
                _s.TransactionMode = GciTransactionMode.AutoBegin;
                _s.Abort();
                _s.Execute($"UserGlobals removeKey: #{_key} ifAbsent: [nil]");
                _s.Commit();
            }
        }
        finally
        {
            _s.Dispose();
        }
    }

    [StoneFact]
    public void CommitPersistsAndAbortDiscards()
    {
        _s.Abort();
        _s.Execute(Put(1));
        Assert.True(_s.NeedsCommit);
        _s.Commit();
        Assert.False(_s.NeedsCommit);

        _s.Execute(Put(2));
        Assert.Equal(2L, _s.Evaluate(At));
        _s.Abort();
        Assert.Equal(1L, _s.Evaluate(At)); // back to the committed state
    }

    [StoneFact]
    public void ChangesBecomeVisibleToOtherSessionsOnlyAfterCommitAndAbort()
    {
        _other.Abort();
        _s.Execute(Put(42));
        Assert.Null(_other.Evaluate(At)); // uncommitted: invisible

        _s.Commit();
        Assert.Null(_other.Evaluate(At)); // other still sees its old snapshot
        _other.Abort();
        Assert.Equal(42L, _other.Evaluate(At));
    }

    [StoneFact]
    public void ConcurrentWriteIsAConflict()
    {
        _s.Abort();                       // our snapshot: before the other session's commit
        _other.Abort();
        _other.Execute(Put(1));
        _other.Commit();
        _s.Execute(Put(2));               // write the same dictionary

        Assert.False(_s.TryCommit());

        _s.Execute(Put(3));
        var ex = Assert.Throws<GciTransactionConflictException>(() => _s.Commit());
        Assert.False(string.IsNullOrEmpty(ex.ConflictReport));

        _s.Abort();
        Assert.Equal(1L, _s.Evaluate(At)); // the other session's value won
    }

    [StoneFact]
    public void RunInTransactionRetriesAfterConflict()
    {
        _s.Abort();
        _other.Abort();
        var attempts = 0;
        var result = _s.RunInTransaction(s =>
        {
            attempts++;
            if (attempts == 1) // a concurrent commit makes the first attempt conflict
            {
                _other.Execute(Put(100));
                _other.Commit();
            }
            var current = s.Evaluate<long?>(At) ?? 0;
            s.Execute(Put(current + 1));
            return current + 1;
        });

        Assert.Equal(2, attempts);
        Assert.Equal(101L, result);
        _other.Abort();
        Assert.Equal(101L, _other.Evaluate(At));
    }

    [StoneFact]
    public void RunInTransactionAbortsOnException()
    {
        _s.Abort();
        Assert.Throws<InvalidOperationException>(() => _s.RunInTransaction(s =>
        {
            s.Execute(Put(5));
            throw new InvalidOperationException("boom");
        }));
        Assert.False(_s.NeedsCommit);
        Assert.Null(_s.Evaluate(At));
    }

    [StoneFact]
    public void ManualBeginModeControlsTransactions()
    {
        Assert.Equal(GciTransactionMode.AutoBegin, _s.TransactionMode);

        _s.TransactionMode = GciTransactionMode.ManualBegin;
        Assert.Equal(GciTransactionMode.ManualBegin, _s.TransactionMode);
        Assert.Equal(GciTransactionMode.AutoBegin, _other.TransactionMode); // per session
        _s.Abort();
        Assert.False(_s.InTransaction);

        _s.Begin();
        Assert.True(_s.InTransaction);
        _s.Execute(Put(7));
        _s.Commit();
        Assert.False(_s.InTransaction); // manualBegin: commit ends the transaction

        _s.Begin();
        Assert.Equal(7L, _s.Evaluate(At));
    }
}
