// Load benchmark: a large object graph (about 820,000 domain objects) loaded into C# in one go,
// e.g. as a read-only replica that a change feed keeps up to date.
//
//   dotnet run -c Release --project samples/ReplicaBench -- setup        creates and commits the data
//   dotnet run -c Release --project samples/ReplicaBench [rounds]        loads it (tracked and untracked)
//   dotnet run -c Release --project samples/ReplicaBench -- refresh      an untracked replica follows another session's commits
//   dotnet run -c Release --project samples/ReplicaBench -- cleanup      removes data and classes
//
// Connection: GS_STONE, GS_GEM_SERVICE, GS_USER (DataCurator), GS_PASSWORD (swordfish).
//
// Model: root -> 20,000 customers -> 10 orders each -> 3 positions each -> one of 5,000 shared articles.
// It has the features that make real models expensive to load:
// - every object has a revision (SmallInteger) and a string key;
// - doubles (SmallDouble and nil), timestamps (DateAndTime, converted on the server);
// - RC collections (orders are an RcIdentityBag, as written by parallel importers);
// - ways back (order -> customer, position -> order) and cross references (related customers),
//   mapped as Oop so the load stops there.

using System.Diagnostics;
using System.Reflection;
using GemStone.Gci;
using GemStone.Gci.Generated;
using GemStone.Gci.Mapping;

const int Customers = 20_000, OrdersPerCustomer = 10, PositionsPerOrder = 3, Articles = 5_000, Chunk = 1_000;
const string RootName = "GemStoneGciBench_Root";
string[][] classes =
[
    ["BenchEntity", "Object", "rev key"],
    ["BenchRoot", "BenchEntity", "customers articles"],
    ["BenchCustomer", "BenchEntity", "id name city orders related"],
    ["BenchOrder", "BenchEntity", "customer number placed shipped cancelled total discount positions"],
    ["BenchPosition", "BenchEntity", "order article quantity price"],
    ["BenchArticle", "BenchEntity", "number name price"],
];

var net = new GciNetParameters(Env("GS_STONE", "gs64stone"), GemService: Env("GS_GEM_SERVICE", "gemnetobject"));
using var session = GciSession.Login(net, Env("GS_USER", "DataCurator"), Env("GS_PASSWORD", "swordfish"));

switch (args.FirstOrDefault())
{
    case "setup": Cleanup(quiet: true); Setup(); break;
    case "cleanup": Cleanup(quiet: false); break;
    case "refresh":
        using (var importer = GciSession.Login(net, Env("GS_USER", "DataCurator"), Env("GS_PASSWORD", "swordfish")))
            RefreshCheck(importer);
        break;
    default:
        var rounds = args.Length > 0 && int.TryParse(args[0], out var r) ? r : 2;
        for (var i = 0; i < rounds; i++)
        {
            Measure("tracked  ", new GsLoadOptions());
            Measure("untracked", new GsLoadOptions { TrackChanges = false });
        }
        break;
}

void Setup()
{
    var sw = Stopwatch.StartNew();
    foreach (var c in classes)
    {
        var ivars = string.Join(" ", c[2].Split(' ').Select(v => "#" + v));
        session.Execute($"""
            ({c[1]} subclass: '{c[0]}' instVarNames: #({ivars})
                classVars: #() classInstVars: #() poolDictionaries: #() inDictionary: UserGlobals)
                compileAccessingMethodsFor: #({ivars}).
            nil
            """);
    }
    session.Execute($$"""
        | arts root |
        arts := (1 to: {{Articles}}) collect: [:i |
            BenchArticle new rev: 1; key: 'A', i printString; number: i; name: 'Article ', i printString;
                price: (i \\ 500) / 4.0; yourself].
        root := BenchRoot new rev: 1; key: 'R'; customers: OrderedCollection new; articles: arts; yourself.
        UserGlobals at: #{{RootName}} put: root.
        nil
        """);
    session.Commit();
    for (var from = 1; from <= Customers; from += Chunk)
    {
        session.Execute($$"""
            | root arts cities customers |
            root := UserGlobals at: #{{RootName}}.
            arts := root articles.
            customers := root customers.
            cities := #(#Berlin #Hamburg #Muenchen #Koeln #Frankfurt).
            {{from}} to: {{from + Chunk - 1}} do: [:i | | c |
                c := BenchCustomer new rev: 1; key: 'C', i printString; id: i; name: 'Customer ', i printString;
                    city: (cities at: i \\ 5 + 1); orders: RcIdentityBag new; related: OrderedCollection new; yourself.
                customers isEmpty ifFalse: [c related add: customers last. customers last related add: c].
                1 to: {{OrdersPerCustomer}} do: [:j | | o |
                    o := BenchOrder new rev: 1; key: 'O', i printString, '-', j printString; customer: c;
                        number: i * 100 + j; placed: DateAndTime now; shipped: (j odd ifTrue: [DateAndTime now]);
                        total: (i * j \\ 100000) / 8.0; discount: (j = 3 ifTrue: [0.05]);
                        positions: OrderedCollection new; yourself.
                    1 to: {{PositionsPerOrder}} do: [:k |
                        o positions add: (BenchPosition new rev: 1; key: o key, '-', k printString; order: o;
                            article: (arts at: (i * 7 + (j * 13) + k) \\ {{Articles}} + 1);
                            quantity: k; price: (i + k \\ 1000) / 4.0; yourself)].
                    c orders add: o].
                customers add: c].
            nil
            """);
        session.Commit();
        Console.Write($"\r{from + Chunk - 1} customers");
    }
    Console.WriteLine($"\rcreated {Customers} customers in {sw.Elapsed.TotalSeconds:F1} s");
}

void Cleanup(bool quiet)
{
    var names = string.Join(" ", classes.Select(c => c[0]).Reverse());
    session.Execute($"UserGlobals removeKey: #{RootName} ifAbsent: []. #({names}) do: [:n | UserGlobals removeKey: n ifAbsent: []]. nil");
    session.Commit();
    if (!quiet) Console.WriteLine("removed");
}

void Measure(string label, GsLoadOptions options)
{
    using var ctx = new GsObjectContext(session, GsGeneratedMappings.Default);
    session.Abort();
    GC.Collect();
    var memBefore = GC.GetTotalMemory(true);
    var calls = Calls();
    var cpu = Process.GetCurrentProcess().TotalProcessorTime;
    var sw = Stopwatch.StartNew();
    var root = ctx.Load<BenchRoot>(RootName, options);
    sw.Stop();
    cpu = Process.GetCurrentProcess().TotalProcessorTime - cpu;
    var memAfter = GC.GetTotalMemory(true);

    // Walk the graph like the application would.
    var walk = Stopwatch.StartNew();
    long objects = 1 + root.Articles.Count;
    double sum = 0;
    foreach (var c in root.Customers)
    {
        objects++;
        foreach (var o in c.Orders)
        {
            objects++;
            sum += o.Total + (o.Shipped is null ? 0 : 1);
            foreach (var p in o.Positions) { objects++; sum += p.Quantity * p.Price + p.Article!.Price; }
        }
    }
    walk.Stop();
    Console.WriteLine($"{label}: {objects:N0} objects in {sw.Elapsed.TotalSeconds:F2} s (client CPU {cpu.TotalSeconds:F2} s), " +
                      $"{Calls() - calls:N0} GCI calls, {(memAfter - memBefore) / 1e6:F0} MB managed, " +
                      $"walk {walk.Elapsed.TotalMilliseconds:F0} ms (checksum {sum})");
}

// An untracked replica follows what another session commits: a changed position and a new order
// in an RcIdentityBag. A change feed would report these OOPs (the position and the bag).
void RefreshCheck(GciSession importer)
{
    foreach (var track in new[] { false, true })
    {
        var options = new GsLoadOptions { TrackChanges = track };
        using var ctx = new GsObjectContext(session, GsGeneratedMappings.Default);
        session.Abort();
        var customer = ctx.Load<BenchRoot>(RootName, options).Customers[0];
        var position = customer.Orders[0].Positions[0];
        var before = (position.Quantity, customer.Orders.Count);

        importer.Abort();
        var bag = importer.Execute($$"""
            | c p |
            p := Object _objectForOop: {{position.Oop.Value}}.
            p quantity: p quantity + 1000; rev: p rev + 1.
            c := Object _objectForOop: {{customer.Oop.Value}}.
            c orders add: (BenchOrder new rev: 1; key: 'new'; customer: c; number: 0; placed: DateAndTime now;
                total: 0.0; positions: OrderedCollection new; yourself).
            c orders
            """);
        importer.Commit();
        try
        {
            session.Abort();
            var refreshed = ctx.Refresh([position.Oop, bag], options);
            Console.WriteLine($"TrackChanges={track}: quantity {before.Quantity} -> {position.Quantity}, " +
                              $"orders {before.Count} -> {customer.Orders.Count}, " +
                              $"refreshed {string.Join(", ", refreshed.Select(o => o.GetType().Name))}");
        }
        finally
        {
            importer.Execute($$"""
                | c p |
                p := Object _objectForOop: {{position.Oop.Value}}.
                p quantity: p quantity - 1000.
                c := Object _objectForOop: {{customer.Oop.Value}}.
                c orders removeAll: (c orders select: [:o | o key = 'new']).
                nil
                """);
            importer.Commit();
            importer.Release(bag);
        }
    }
}

// GciSession.CallCount is internal; read it by reflection for the measurement.
long Calls() => (long)typeof(GciSession).GetProperty("CallCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;

static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

// ---- mapping; ways back and cross references stay Oop ----

public abstract class BenchEntity
{
    [GsOop] public Oop Oop { get; private set; }
    [GsInstVar("rev")] public long Rev { get; set; }
    [GsInstVar("key")] public string? Key { get; set; }
}

[GsClass("BenchRoot")]
public class BenchRoot : BenchEntity
{
    [GsInstVar("customers"), GsCollection(GsCollectionKind.Ordered)] public List<BenchCustomer> Customers { get; set; } = [];
    [GsInstVar("articles"), GsCollection(GsCollectionKind.Array)] public List<BenchArticle> Articles { get; set; } = [];
}

[GsClass("BenchCustomer")]
public class BenchCustomer : BenchEntity
{
    [GsInstVar("id")] public long Id { get; set; }
    [GsInstVar("name")] public string? Name { get; set; }
    [GsInstVar("city")] public string? City { get; set; }
    [GsInstVar("orders"), GsCollection(GsCollectionKind.IdentityBag)] public List<BenchOrder> Orders { get; set; } = [];
    [GsInstVar("related"), GsCollection(GsCollectionKind.Ordered)] public List<Oop> Related { get; set; } = [];
}

[GsClass("BenchOrder")]
public class BenchOrder : BenchEntity
{
    [GsInstVar("customer")] public Oop Customer { get; set; }
    [GsInstVar("number")] public long Number { get; set; }
    [GsInstVar("placed")] public DateTimeOffset Placed { get; set; }
    [GsInstVar("shipped")] public DateTimeOffset? Shipped { get; set; }
    [GsInstVar("cancelled")] public DateTimeOffset? Cancelled { get; set; }
    [GsInstVar("total")] public double Total { get; set; }
    [GsInstVar("discount")] public double? Discount { get; set; }
    [GsInstVar("positions"), GsCollection(GsCollectionKind.Ordered)] public List<BenchPosition> Positions { get; set; } = [];
}

[GsClass("BenchPosition")]
public class BenchPosition : BenchEntity
{
    [GsInstVar("order")] public Oop Order { get; set; }
    [GsInstVar("article")] public BenchArticle? Article { get; set; }
    [GsInstVar("quantity")] public long Quantity { get; set; }
    [GsInstVar("price")] public double Price { get; set; }
}

[GsClass("BenchArticle")]
public class BenchArticle : BenchEntity
{
    [GsInstVar("number")] public long Number { get; set; }
    [GsInstVar("name")] public string? Name { get; set; }
    [GsInstVar("price")] public double Price { get; set; }
}
