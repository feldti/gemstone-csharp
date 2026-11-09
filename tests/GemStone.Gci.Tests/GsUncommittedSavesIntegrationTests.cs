using GemStone.Gci.Mapping;

namespace GemStone.Gci.Tests;

/// <summary>
/// Saved but uncommitted changes and aborts: the snapshots must follow the server back. Commits
/// a class and a test object under a unique key in UserGlobals and removes both again.
/// </summary>
[Collection("Stone")]
public sealed class GsUncommittedSavesIntegrationTests : IDisposable
{
    private readonly GciSession? _worker;
    private readonly GciSession? _other;
    private readonly string _key = "GemStoneGciTest_" + Guid.NewGuid().ToString("N");

    public GsUncommittedSavesIntegrationTests()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GS_TEST_STONE"))) return;
        _worker = StoneEnv.Login();
        _other = StoneEnv.Login();
        _worker.Execute($$"""
            | c | c := Object subclass: 'GsTxTestItem' instVarNames: #('name' 'tags' 'child') classVars: #()
                classInstVars: #() poolDictionaries: #() inDictionary: UserGlobals.
            UserGlobals at: #{{_key}} put: (c new instVarNamed: 'name' put: 'Dora';
                instVarNamed: 'tags' put: (OrderedCollection with: 'a' with: 'b'); yourself).
            true
            """);
        _worker.Commit();
        _other.Abort();
    }

    public void Dispose()
    {
        if (_worker is { IsLoggedIn: true })
        {
            _worker.Abort();
            _worker.Execute($"UserGlobals removeKey: #{_key} ifAbsent: [nil]; removeKey: #GsTxTestItem ifAbsent: [nil]. true");
            _worker.Commit();
        }
        _worker?.Dispose();
        _other?.Dispose();
    }

    private GciSession W => _worker!;
    private string Item => $"(UserGlobals at: #{_key})";

    /// <summary>What another session sees after the worker committed (or not).</summary>
    private T Committed<T>(string source)
    {
        _other!.Abort();
        return _other.Evaluate<T>(source);
    }

    [StoneFact]
    public void AnAbortTakesBackUncommittedSaves()
    {
        using var ctx = new GsObjectContext(W);
        var item = ctx.Load<TxItem>(Item);
        item.Name = "Emil";
        item.Tags.Add("c");
        Assert.Equal(2, ctx.SaveChanges());

        W.Abort(); // the server forgets the saved changes …
        Assert.Equal("Dora", W.Evaluate<string>($"{Item} instVarNamed: 'name'"));
        // … and so does the context: the object is changed again, not silently overwritten …
        Assert.Throws<GsUnsavedChangesException>(() => ctx.Load<TxItem>(Item));
        // … and the next save writes everything again.
        Assert.Equal(2, ctx.SaveChanges());
        W.Commit();
        Assert.Equal("Emil", Committed<string>($"{Item} instVarNamed: 'name'"));
        Assert.Equal(3, Committed<long>($"({Item} instVarNamed: 'tags') size"));
        Assert.Equal(0, ctx.SaveChanges());
    }

    [StoneFact]
    public void ACommitMakesSavesFinal()
    {
        using var ctx = new GsObjectContext(W);
        var item = ctx.Load<TxItem>(Item);
        item.Name = "Emil";
        ctx.SaveChanges();
        W.Commit();
        W.Abort();
        Assert.Equal(0, ctx.SaveChanges());
        Assert.Same(item, ctx.Load<TxItem>(Item));
    }

    [StoneFact]
    public void NewObjectsSurviveAnAbort()
    {
        using var ctx = new GsObjectContext(W);
        var item = ctx.Load<TxItem>(Item);
        var child = new TxItem { Name = "Kind" };
        item.Child = child;
        ctx.SaveChanges();
        Assert.True(ctx.TryGetOop(child, out var before));

        W.Abort(); // the reference is gone, the new (temporary) object keeps its state
        Assert.True(W.Evaluate<bool>($"({Item} instVarNamed: 'child') isNil"));
        Assert.Equal(1, ctx.SaveChanges()); // only the reference is written again
        Assert.True(ctx.TryGetOop(child, out var after));
        Assert.Equal(before, after);
        W.Commit();
        Assert.Equal("Kind", Committed<string>($"({Item} instVarNamed: 'child') instVarNamed: 'name'"));
    }

    [StoneFact]
    public void AReloadBetweenSaveAndAbort()
    {
        using var ctx = new GsObjectContext(W);
        var item = ctx.Load<TxItem>(Item);
        item.Name = "Emil";
        ctx.SaveChanges();
        Assert.Same(item, ctx.Load<TxItem>(Item)); // new snapshot, taken from the uncommitted state
        W.Abort();
        Assert.Equal(1, ctx.SaveChanges());
        Assert.Equal("Emil", W.Evaluate<string>($"{Item} instVarNamed: 'name'"));
    }

    [StoneFact]
    public void AfterAConflictTheChangesCanBeSavedAgain()
    {
        using var ctx = new GsObjectContext(W);
        var item = ctx.Load<TxItem>(Item);
        item.Name = "Emil";
        ctx.SaveChanges();

        _other!.Execute($"{Item} instVarNamed: 'name' put: 'Fritz'. true");
        _other.Commit();
        Assert.Throws<GciTransactionConflictException>(W.Commit);

        W.Abort();
        Assert.Equal(1, ctx.SaveChanges());
        W.Commit();
        Assert.Equal("Emil", Committed<string>($"{Item} instVarNamed: 'name'"));
    }

    [StoneFact]
    public void SaveChangesAndCommitRetriesAConflict()
    {
        using var ctx = new GsObjectContext(W);
        var item = ctx.Load<TxItem>(Item);
        item.Name = "Emil";
        item.Tags.Remove("a");

        // Another session commits a change to the same objects after the worker's view began.
        _other!.Execute($"{Item} instVarNamed: 'name' put: 'Fritz'. ({Item} instVarNamed: 'tags') add: 'x'. true");
        _other.Commit();

        Assert.Equal(2, ctx.SaveChangesAndCommit()); // conflict, abort, written again, committed
        Assert.Equal("Emil", Committed<string>($"{Item} instVarNamed: 'name'"));
        // The collection was patched, not replaced: the other session's element survives.
        Assert.Equal("b x", Committed<string>($"({Item} instVarNamed: 'tags') inject: '' into: [:s :e | s isEmpty ifTrue: [e] ifFalse: [s, ' ', e]]"));
        Assert.Equal(0, ctx.SaveChanges());
    }
}

[GsClass("GsTxTestItem")]
public sealed class TxItem
{
    [GsInstVar("name")] public string? Name { get; set; }
    [GsInstVar("tags")] public List<string> Tags { get; set; } = [];
    [GsInstVar("child")] public TxItem? Child { get; set; }
}
