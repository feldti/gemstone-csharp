using GemStone.Gci.Mapping;

namespace GemStone.Gci.Tests;

/// <summary>
/// The change feed and the replica that follows it. Commits the class GciChangeFeed, a feed, a
/// test class and a test object under unique keys in UserGlobals and removes them again.
/// </summary>
[Collection("Stone")]
public sealed class GciChangeFeedIntegrationTests : IDisposable
{
    private readonly GciSession? _worker;   // consumer side (replica work session)
    private readonly GciSession? _writer;   // the importer
    private readonly GciSession? _listen;   // the replica's listener session
    private readonly string _feedName = "GemStoneGciTest_Feed" + Guid.NewGuid().ToString("N");
    private readonly string _key = "GemStoneGciTest_" + Guid.NewGuid().ToString("N");

    public GciChangeFeedIntegrationTests()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GS_TEST_STONE"))) return;
        _worker = StoneEnv.Login();
        _writer = StoneEnv.Login();
        _listen = StoneEnv.Login();
        GciChangeFeed.Install(_worker, _feedName);
        _worker.Execute($$"""
            | c | c := Object subclass: 'GsFeedTestItem' instVarNames: #('name' 'children') classVars: #()
                classInstVars: #() poolDictionaries: #() inDictionary: UserGlobals.
            UserGlobals at: #{{_key}} put: (c new instVarNamed: 'name' put: 'Dora';
                instVarNamed: 'children' put: (RcIdentityBag with: (c new instVarNamed: 'name' put: 'a'; yourself)); yourself).
            true
            """);
        _worker.Commit();
        _writer.Abort();
    }

    public void Dispose()
    {
        if (_worker is { IsLoggedIn: true })
        {
            _worker.Abort();
            _worker.Execute($"UserGlobals removeKey: #{_key} ifAbsent: [nil]; removeKey: #{_feedName} ifAbsent: [nil]; " +
                            "removeKey: #GsFeedTestItem ifAbsent: [nil]; removeKey: #GciChangeFeed ifAbsent: [nil]. true");
            _worker.Commit();
        }
        _worker?.Dispose();
        _writer?.Dispose();
        _listen?.Dispose();
    }

    private GciSession W => _worker!;
    private string Item => $"(UserGlobals at: #{_key})";

    private GciChangeFeed Feed(GciSession session)
    {
        session.Abort();
        return new GciChangeFeed(session, _feedName);
    }

    [StoneFact]
    public void PublishedObjectsReachEveryConsumer()
    {
        var feed = Feed(W);
        feed.Register("one", W.GemSessionId);
        feed.Register("two", W.GemSessionId);
        W.Commit();
        Assert.Equal(["one", "two"], feed.Consumers().OrderBy(k => k, StringComparer.Ordinal));

        var writerFeed = Feed(_writer!);
        var item = _writer!.Execute(Item);
        writerFeed.Publish(item);
        writerFeed.Publish(item, Oop.Nil);                 // twice in one transaction: taken once
        _writer.Commit();

        W.Abort();
        Assert.Equal(2, feed.Pending("one"));
        Assert.Equal([item], feed.Take("one"));
        Assert.Equal([item], feed.Take("two").Where(o => !o.IsNil));
        W.Commit();
        W.Abort();
        Assert.Empty(feed.Take("one"));
        Assert.Equal(0, feed.Pending("two"));
        _writer.Release(item);
    }

    [StoneFact]
    public void AnAbortedTakeKeepsTheObjects()
    {
        var feed = Feed(W);
        feed.Register("one", W.GemSessionId);
        W.Commit();
        var item = W.Execute(Item);
        feed.Publish(item);
        W.Commit();

        Assert.Equal([item], feed.Take("one"));
        W.Abort();                                         // e.g. the refresh failed
        Assert.Equal([item], feed.Take("one"));
        W.Commit();
        W.Release(item);
    }

    [StoneFact]
    public void ParallelWritersDoNotConflict()
    {
        var feed = Feed(W);
        feed.Register("one", W.GemSessionId);
        W.Commit();

        // Both writers publish in overlapping transactions; both commits must succeed.
        var a = Feed(W);
        var b = Feed(_writer!);
        var x = W.Execute(Item);
        var y = _writer!.Execute($"{Item} instVarNamed: 'children'");
        a.Publish(x);
        b.Publish(y);
        Assert.True(W.TryCommit());
        Assert.True(_writer.TryCommit());

        W.Abort();
        Assert.Equal(new HashSet<Oop> { x, y }, feed.Take("one").ToHashSet());
        W.Commit();
        W.Release(x);
        _writer.Release(y);
    }

    [StoneFact]
    public void UnregisteredConsumersGetNothing()
    {
        var feed = Feed(W);
        feed.Register("one", W.GemSessionId);
        W.Commit();
        feed.Unregister("one");
        W.Commit();
        Assert.Empty(feed.Consumers());
        var item = W.Execute(Item);
        feed.Publish(item);
        W.Commit();
        Assert.Empty(feed.Take("one"));
        W.Release(item);
    }

    [StoneFact]
    public async Task TheReplicaFollowsASmalltalkWriter()
    {
        using var ctx = new GsObjectContext(W);
        using var listener = new GciSignalListener(_listen!);
        using var replica = new GsReplica<FeedItem>(ctx, Feed(W), "replica", listener, Item) { PollInterval = TimeSpan.FromMinutes(5) };
        var changed = new TaskCompletionSource<GsReplicaChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        replica.Changed += (_, e) => changed.TrySetResult(e);
        await Task.Run(replica.Start);                     // no xUnit SynchronizationContext for the listener
        Assert.Equal(("Dora", 1), replica.Read(r => (r.Name, r.Children.Count)));

        // An importer written in Smalltalk: change, publish, commit, ring.
        _writer!.Abort();
        _writer.Execute($$"""
            | item feed |
            item := {{Item}}.
            feed := UserGlobals at: #{{_feedName}}.
            item instVarNamed: 'name' put: 'Emil'.
            (item instVarNamed: 'children') add: (GsFeedTestItem new instVarNamed: 'name' put: 'b'; yourself).
            feed publish: {item. item instVarNamed: 'children'}.
            System commitTransaction ifFalse: [nil error: 'commit failed'].
            feed ring.
            true
            """);

        var e = await Within(changed.Task);                // the doorbell, not the timer (5 minutes)
        Assert.Contains(e.Entities, o => o is FeedItem { Name: "Emil" });
        Assert.Equal(("Emil", 2), replica.Read(r => (r.Name, r.Children.Count)));
        Assert.Equal(["a", "b"], replica.Read(r => r.Children.Select(c => c.Name!).OrderBy(n => n, StringComparer.Ordinal).ToList()));
    }

    [StoneFact]
    public async Task WithoutTheDoorbellTheTimerCatchesUp()
    {
        using var ctx = new GsObjectContext(W);
        using var listener = new GciSignalListener(_listen!);
        using var replica = new GsReplica<FeedItem>(ctx, Feed(W), "replica", listener, Item) { PollInterval = TimeSpan.FromMilliseconds(200) };
        var changed = new TaskCompletionSource<GsReplicaChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        replica.Changed += (_, e) => changed.TrySetResult(e);
        await Task.Run(replica.Start);

        var writerFeed = Feed(_writer!);
        _writer!.Execute($"{Item} instVarNamed: 'name' put: 'Frieda'. true");
        var item = _writer.Execute(Item);
        writerFeed.Publish(item);
        _writer.Commit();                                  // no Ring: the signal got lost, say
        _writer.Release(item);

        await Within(changed.Task);
        Assert.Equal("Frieda", replica.Read(r => r.Name));
    }

    private static async Task<T> Within<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) != task)
            throw new TimeoutException("The replica did not update within 10 seconds.");
        return await task;
    }

    [GsClass("GsFeedTestItem")]
    public sealed class FeedItem
    {
        [GsInstVar("name")] public string? Name { get; set; }
        [GsInstVar("children"), GsCollection(GsCollectionKind.IdentityBag)] public List<FeedItem> Children { get; set; } = [];
    }
}
