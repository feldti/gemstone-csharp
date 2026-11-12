// Example: log in, find a root object in GemStone, keep it, build a structure in RAM, hang it
// into a collection of the root and commit.
//
//   dotnet run --project samples/RootExample -- setup     creates the classes and the root, commits
//   dotnet run --project samples/RootExample              adds a warehouse with items, commits
//   dotnet run --project samples/RootExample -- cleanup   removes the root and the classes, commits
//
// Connection from the environment (as in GsRepl): GS_STONE, GS_GEM_SERVICE, GS_USER, GS_PASSWORD.

using GemStone.Gci;
using GemStone.Gci.Generated;
using GemStone.Gci.Mapping;

var net = new GciNetParameters(
    StoneName: Env("GS_STONE", "gs64stone"),
    GemService: Env("GS_GEM_SERVICE", "gemnetobject"));

// 1. Log in. Dispose logs out.
using var session = GciSession.Login(net, Env("GS_USER", "DataCurator"), Env("GS_PASSWORD", "swordfish"));

switch (args.FirstOrDefault())
{
    case "setup": Setup(session); return;
    case "cleanup": Cleanup(session); return;
}

// 2. Find the root: a global in the user's symbol list (here UserGlobals at: #ExampleData).
//    The OOP is in the session's export set, so the gem keeps the object for us; we hold it for
//    the lifetime of the program and release it at the end.
session.Abort();                                         // see the latest committed state
var rootOop = session.ResolveSymbol("ExampleData");
try
{
    // 3. Load the root into C# objects and keep them. The context is the unit of work: it knows
    //    what it loaded and writes only the differences.
    using var ctx = new GsObjectContext(session, GsGeneratedMappings.Default);
    ExampleRoot root = ctx.Load<ExampleRoot>(rootOop);
    Console.WriteLine($"Root loaded: {root.Warehouses.Count} warehouses");

    // 4. Build a structure in RAM: plain C# objects, nothing happens on the server yet.
    var warehouse = new Warehouse
    {
        Name = $"Warehouse {DateTime.Now:HH:mm:ss}",
        Opened = DateOnly.FromDateTime(DateTime.Today),
        Items =
        [
            new Item { Sku = "A-100", Quantity = 12, Price = 9.95m },
            new Item { Sku = "B-200", Quantity = 3, Price = 129.00m },
            new Item { Sku = "C-300", Quantity = 250, Price = 0.45m },
        ],
    };

    // 5. Hang it into the collection of the root. Persistence by reachability: the new objects
    //    become persistent because the (persistent) root references them at commit time.
    root.Warehouses.Add(warehouse);

    // 6. Write and commit. SaveChanges creates the warehouse and its items with `new` on their
    //    GemStone classes and patches the root's Array in place, all in one batch.
    //    SaveChangesAndCommit commits; on a conflict (another session changed the root's Array in
    //    the meantime) it aborts, writes again on top of the new state and retries.
    ctx.SaveChangesAndCommit();

    ctx.TryGetOop(warehouse, out var warehouseOop);
    Console.WriteLine($"Committed '{warehouse.Name}' as {warehouseOop}; " +
        $"server: {session.Evaluate<long>("(ExampleData instVarNamed: 'warehouses') size")} warehouses, " +
        $"{session.Evaluate<long>("(ExampleData instVarNamed: 'warehouses') inject: 0 into: [:n :w | n + (w instVarNamed: 'items') size]")} items");

    // The root and the context stay usable: further changes to `root` or `warehouse` followed by
    // another SaveChanges/Commit write only what changed.
}
finally
{
    session.Release(rootOop);
}

static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;

// Classes and root object on the server. In a real application they exist already.
static void Setup(GciSession session)
{
    session.Execute("""
        Object subclass: #ExampleRoot
            instVarNames: #(warehouses)
            classVars: #() classInstVars: #() poolDictionaries: #()
            inDictionary: UserGlobals options: #().
        Object subclass: #ExampleWarehouse
            instVarNames: #(name opened items)
            classVars: #() classInstVars: #() poolDictionaries: #()
            inDictionary: UserGlobals options: #().
        Object subclass: #ExampleItem
            instVarNames: #(sku quantity price)
            classVars: #() classInstVars: #() poolDictionaries: #()
            inDictionary: UserGlobals options: #().
        """);
    // A separate call: the source above is compiled as a whole, before ExampleRoot exists.
    session.Execute("UserGlobals at: #ExampleData put: (ExampleRoot new instVarAt: 1 put: Array new; yourself)");
    session.Commit();
    Console.WriteLine("Classes and ExampleData created.");
}

static void Cleanup(GciSession session)
{
    session.Execute("""
        #(#ExampleData #ExampleRoot #ExampleWarehouse #ExampleItem)
            do: [:k | UserGlobals removeKey: k ifAbsent: []].
        """);
    session.Commit();
    Console.WriteLine("Removed.");
}

// C# classes for the GemStone classes. Names may differ; the attributes connect them.

[GsClass("ExampleRoot")]
public class ExampleRoot
{
    [GsInstVar("warehouses"), GsCollection(GsCollectionKind.Array)]
    public List<Warehouse> Warehouses { get; set; } = [];
}

[GsClass("ExampleWarehouse")]
public class Warehouse
{
    [GsInstVar("name")] public string? Name { get; set; }
    [GsInstVar("opened")] public DateOnly Opened { get; set; }

    [GsInstVar("items"), GsCollection(GsCollectionKind.Ordered)]
    public List<Item> Items { get; set; } = [];
}

[GsClass("ExampleItem")]
public class Item
{
    [GsInstVar("sku")] public string? Sku { get; set; }
    [GsInstVar("quantity")] public long Quantity { get; set; }
    [GsInstVar("price")] public decimal Price { get; set; }
}
