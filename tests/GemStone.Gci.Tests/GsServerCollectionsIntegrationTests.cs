using GemStone.Gci.Generated;
using GemStone.Gci.Mapping;

namespace GemStone.Gci.Tests.Mapping;

// The server models associations with database-oriented collections (RC collections,
// dictionaries, queues); the C# side only has lists.

[GsClass("GsSrvTestThing")]
public sealed class SrvThing
{
    [GsInstVar("name")] public string? Name { get; set; }
}

/// <summary>Every association as a plain list.</summary>
[GsClass("GsSrvTestHolder")]
public sealed class SrvHolder
{
    [GsInstVar("items")] public List<SrvThing> Items { get; set; } = [];
    [GsInstVar("byName"), GsDictionaryKey("name")] public List<SrvThing> ByName { get; set; } = [];
    [GsInstVar("queue")] public List<SrvThing> Queue { get; set; } = [];
}

/// <summary>The same class, but the dictionary without a key selector: loadable, not changeable.</summary>
[GsClass("GsSrvTestHolder")]
public sealed class SrvHolderWithoutKey
{
    [GsInstVar("byName")] public List<SrvThing> ByName { get; set; } = [];
}

/// <summary>A class without initialize: new objects get collections chosen by the C# side.</summary>
[GsClass("GsSrvTestPlain")]
public sealed class SrvPlain
{
    [GsInstVar("items")] public List<SrvThing> Items { get; set; } = [];
    [GsInstVar("byName"), GsDictionaryKey("name")] public List<SrvThing> ByName { get; set; } = [];
}

/// <summary>Own session; creates the classes (with an initialize that sets up RC collections) and never commits.</summary>
public sealed class ServerCollectionsFixture : IDisposable
{
    public GciSession? Session { get; }

    public ServerCollectionsFixture()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GS_TEST_STONE"))) return;
        Session = StoneEnv.Login();
        Session.Execute("""
            Object subclass: 'GsSrvTestThing' instVarNames: #(#name)
                classVars: #() classInstVars: #() poolDictionaries: #() inDictionary: UserGlobals.
            Object subclass: 'GsSrvTestHolder' instVarNames: #(#items #byName #queue)
                classVars: #() classInstVars: #() poolDictionaries: #() inDictionary: UserGlobals.
            Object subclass: 'GsSrvTestPlain' instVarNames: #(#items #byName)
                classVars: #() classInstVars: #() poolDictionaries: #() inDictionary: UserGlobals.
            nil
            """);
        Session.Execute("""
            (UserGlobals at: #GsSrvTestThing) compileMethod: 'name ^name' dictionaries: System myUserProfile symbolList
                category: 'accessing' environmentId: 0.
            (UserGlobals at: #GsSrvTestHolder) compileMethod: 'initialize
                items := RcIdentityBag new. byName := RcKeyValueDictionary new. queue := RcQueue new'
                dictionaries: System myUserProfile symbolList category: 'initialization' environmentId: 0.
            nil
            """);
    }

    public void Dispose()
    {
        if (Session is { IsLoggedIn: true }) Session.Abort();
        Session?.Dispose();
    }
}

/// <summary>The same tests with the metadata of the source generator instead of reflection.</summary>
[Collection("Stone")]
public sealed class GsServerCollectionsGeneratedIntegrationTests(ServerCollectionsFixture fixture) : GsServerCollectionsIntegrationTests(fixture)
{
    protected override GsObjectContext NewContext() => new(S, GsGeneratedMappings.Default);
}

[Collection("Stone")]
public class GsServerCollectionsIntegrationTests(ServerCollectionsFixture fixture) : IClassFixture<ServerCollectionsFixture>
{
    protected GciSession S => fixture.Session!;

    protected virtual GsObjectContext NewContext() => new(S);

    /// <summary>A holder whose initialize created the RC collections, filled with a, b, c; returns its key.</summary>
    private string NewHolder()
    {
        var key = "GsSrvTest_" + Guid.NewGuid().ToString("N");
        S.Execute($$"""
            | h |
            h := (UserGlobals at: #GsSrvTestHolder) new.
            #('a' 'b' 'c') do: [:n | | t |
                t := (UserGlobals at: #GsSrvTestThing) new instVarNamed: 'name' put: n; yourself.
                (h instVarNamed: 'items') add: t.
                (h instVarNamed: 'byName') at: n put: t.
                (h instVarNamed: 'queue') add: t].
            UserGlobals at: #{{key}} put: h.
            true
            """);
        return key;
    }

    /// <summary>Class name and sorted element names of a collection, e.g. <c>RcIdentityBag a b</c>.</summary>
    private string Describe(string collection) => S.Evaluate<string>($$"""
        | c names s |
        c := {{collection}}.
        names := (c isKindOf: AbstractDictionary)
            ifTrue: [c keys asSortedCollection collect: [:k | k , '=' , ((c at: k) instVarNamed: 'name')]]
            ifFalse: [(c asArray collect: [:t | t instVarNamed: 'name']) asSortedCollection].
        s := WriteStream on: String new.
        s nextPutAll: c class name.
        names do: [:n | s space; nextPutAll: n].
        s contents
        """);

    private static string[] Names(IEnumerable<SrvThing> things) => [.. things.Select(t => t.Name!).Order(StringComparer.Ordinal)];

    [StoneFact]
    public void RcCollectionsDictionariesAndQueuesLoadAsLists()
    {
        var key = NewHolder();
        using var ctx = NewContext();
        var h = ctx.Load<SrvHolder>(key);

        Assert.Equal(["a", "b", "c"], Names(h.Items));
        Assert.Equal(["a", "b", "c"], Names(h.ByName));
        Assert.Equal(["a", "b", "c"], Names(h.Queue));
        Assert.Same(h.Items.Single(t => t.Name == "a"), h.ByName.Single(t => t.Name == "a")); // one object, one instance
        Assert.Equal(3, S.Evaluate<long>($"({key} instVarNamed: 'queue') size"));          // loading does not consume the queue
    }

    [StoneFact]
    public void ChangedListsArePatchedIntoTheServerCollections()
    {
        var key = NewHolder();
        using var ctx = NewContext();
        var h = ctx.Load<SrvHolder>(key);

        var d = new SrvThing { Name = "d" };
        h.Items.Remove(h.Items.Single(t => t.Name == "b"));
        h.Items.Add(d);
        h.ByName.Remove(h.ByName.Single(t => t.Name == "a"));
        h.ByName.Add(d);
        h.Queue.Add(d);
        ctx.SaveChanges();

        Assert.Equal("RcIdentityBag a c d", Describe($"{key} instVarNamed: 'items'"));
        Assert.Equal("RcKeyValueDictionary b=b c=c d=d", Describe($"{key} instVarNamed: 'byName'"));
        Assert.Equal("RcQueue a b c d", Describe($"{key} instVarNamed: 'queue'"));
        Assert.True(S.Evaluate<bool>($"(({key} instVarNamed: 'byName') at: 'd') == (({key} instVarNamed: 'items') detect: [:t | (t instVarNamed: 'name') = 'd'])"));

        // A second save after reloading: nothing left to write.
        using var again = NewContext();
        again.Load<SrvHolder>(key);
        Assert.Equal(0, again.SaveChanges());
    }

    [StoneFact]
    public void NewObjectsGetTheCollectionsOfInitialize()
    {
        var key = "GsSrvTest_" + Guid.NewGuid().ToString("N");
        var a = new SrvThing { Name = "a" };
        var h = new SrvHolder { Items = [a, new SrvThing { Name = "b" }], ByName = [a], Queue = [a] };
        using var ctx = NewContext();
        var oop = ctx.Add(h);
        S.Execute($"UserGlobals at: #{key} put: (Object _objectForOop: {oop.Value}). true");

        Assert.Equal("RcIdentityBag a b", Describe($"{key} instVarNamed: 'items'"));
        Assert.Equal("RcKeyValueDictionary a=a", Describe($"{key} instVarNamed: 'byName'"));
        Assert.Equal("RcQueue a", Describe($"{key} instVarNamed: 'queue'"));

        // The snapshots know the server collections: a later change patches them.
        h.ByName.Add(h.Items[1]);
        h.Items.RemoveAt(0);
        ctx.SaveChanges();
        Assert.Equal("RcIdentityBag b", Describe($"{key} instVarNamed: 'items'"));
        Assert.Equal("RcKeyValueDictionary a=a b=b", Describe($"{key} instVarNamed: 'byName'"));
    }

    [StoneFact]
    public void WithoutInitializeTheCSharpSideChoosesTheCollections()
    {
        var key = "GsSrvTest_" + Guid.NewGuid().ToString("N");
        var p = new SrvPlain { Items = [new SrvThing { Name = "x" }], ByName = [new SrvThing { Name = "y" }] };
        using var ctx = NewContext();
        var oop = ctx.Add(p);
        S.Execute($"UserGlobals at: #{key} put: (Object _objectForOop: {oop.Value}). true");

        Assert.Equal("OrderedCollection x", Describe($"{key} instVarNamed: 'items'"));
        Assert.Equal("KeyValueDictionary y=y", Describe($"{key} instVarNamed: 'byName'"));
    }

    [StoneFact]
    public void ADictionaryWithoutKeySelectorCannotBeChanged()
    {
        var key = NewHolder();
        using var ctx = NewContext();
        var h = ctx.Load<SrvHolderWithoutKey>(key);
        h.ByName.Add(new SrvThing { Name = "z" });

        var e = Assert.Throws<GsMappingException>(() => ctx.SaveChanges());
        Assert.Contains("GsDictionaryKey", e.Message);
        Assert.Equal("RcKeyValueDictionary a=a b=b c=c", Describe($"{key} instVarNamed: 'byName'"));
    }

    [StoneFact]
    public void AQueueCanOnlyBeAppendedTo()
    {
        var key = NewHolder();
        using var ctx = NewContext();
        var h = ctx.Load<SrvHolder>(key);
        h.Queue.RemoveAt(0);

        var e = Assert.Throws<GciException>(() => ctx.SaveChanges());
        Assert.Contains("RcQueue can only be appended to", e.Message);   // GemStone's own error says nothing useful
        Assert.Equal("RcQueue a b c", Describe($"{key} instVarNamed: 'queue'"));
    }
}
