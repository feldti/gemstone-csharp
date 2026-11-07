using System.Numerics;
using GemStone.Gci.Mapping;

namespace GemStone.Gci.Tests.Mapping;

// ---- C# classes as a code generator would emit them (instVar names differ on purpose) ----

public enum AccountStatus { Active, Closed, Frozen }

[GsClass("GsMapTestAddress")]
public sealed class Address
{
    [GsInstVar("street")] public string? Street { get; set; }
    [GsInstVar("city")] public string? City { get; set; }
}

[GsClass("GsMapTestAccount")]
public class Account
{
    [GsOop] public Oop Oop { get; private set; }
    [GsInstVar("accName")] public string? Name { get; set; }
    [GsInstVar("amount")] public decimal Balance { get; set; }
    [GsInstVar("postalAddress")] public Address? Address { get; set; }
    [GsInstVar("orderList"), GsCollection(GsCollectionKind.Ordered)] public List<Order> Orders { get; set; } = [];
    [GsInstVar("tags"), GsCollection(GsCollectionKind.Set)] public HashSet<string> Tags { get; set; } = [];
    [GsInstVar("state")] public AccountStatus Status { get; set; }
    [GsInstVar("opened")] public DateOnly? Opened { get; set; }
    [GsInstVar("extra")] public object? Extra { get; set; }
    public string DisplayName => $"{Name} ({Balance})"; // not mapped
}

[GsClass("GsMapTestSpecialAccount")]
public sealed class SpecialAccount : Account
{
    [GsInstVar("bonus")] public long Bonus { get; set; }
}

[GsClass("GsMapTestOrder")]
public sealed class Order
{
    [GsInstVar("number")] public int Number { get; set; }
    [GsInstVar("total")] public double Total { get; set; }
    [GsInstVar("account")] public Account? Account { get; set; }
    [GsInstVar("lines"), GsCollection(GsCollectionKind.Dictionary)] public Dictionary<string, long>? Lines { get; set; }
}

// ---- Deliberately wrong mappings ----

[GsClass("GsMapTestAddress")]
public sealed class AddressWithTypo
{
    [GsInstVar("stret")] public string? Street { get; set; }
    [GsInstVar("zip")] public string? Zip { get; set; }
}

[GsClass("GsMapTestNoSuchClass")]
public sealed class Phantom
{
    [GsInstVar("x")] public int X { get; set; }
}

[GsClass("GsMapTestOrder")]
public sealed class OrderWithWrongKind
{
    [GsInstVar("lines"), GsCollection(GsCollectionKind.Array)] public List<long>? Lines { get; set; }
}

[GsClass("GsMapTestOrder")]
public sealed class OrderWithAddressAsAccount
{
    [GsInstVar("account")] public Address? Account { get; set; }
}

[GsClass("GsMapTestHolder", Dictionary = "UserGlobals")]
public sealed class Holder
{
    [GsInstVar("items")] public List<object?>? Items { get; set; }
    [GsInstVar("idSet")] public HashSet<long>? IdSet { get; set; }
    [GsInstVar("bag")] public List<string>? Bag { get; set; }
    [GsInstVar("symbols")] public Dictionary<string, Address>? BySymbol { get; set; }
    [GsInstVar("code"), GsConverter(typeof(UpperCaseConverter))] public string? Code { get; set; }
    [GsInstVar("big")] public BigInteger Big { get; set; }
    [GsInstVar("when")] public DateTimeOffset When { get; set; }
}

public sealed class UpperCaseConverter : IGsValueConverter
{
    public object? FromGemStone(object? value, Type targetType) => (value as string)?.ToUpperInvariant();
}

/// <summary>Defines the GemStone classes and data in its own session; nothing is committed.</summary>
public sealed class MappingFixture : IDisposable
{
    public GciSession? Session { get; }

    public MappingFixture()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GS_TEST_STONE"))) return;
        Session = StoneEnv.Login();
        Session.Execute("""
            | def addr1 addr2 a1 a2 a3 o1 o2 h big |
            def := [:sup :name :ivs | sup subclass: name instVarNames: ivs classVars: #() classInstVars: #()
                       poolDictionaries: #() inDictionary: UserGlobals].
            def value: Object value: 'GsMapTestAddress' value: #('street' 'city').
            def value: Object value: 'GsMapTestAccount' value: #('accName' 'amount' 'postalAddress' 'orderList' 'tags' 'state' 'opened' 'extra' 'unmapped').
            def value: (UserGlobals at: #GsMapTestAccount) value: 'GsMapTestSpecialAccount' value: #('bonus').
            def value: Object value: 'GsMapTestOrder' value: #('number' 'total' 'account' 'lines').
            def value: Object value: 'GsMapTestHolder' value: #('items' 'idSet' 'bag' 'symbols' 'code' 'big' 'when').

            addr1 := (UserGlobals at: #GsMapTestAddress) new.
            addr1 instVarNamed: 'street' put: 'Hauptstraße 1'; instVarNamed: 'city' put: 'Köln'.
            addr2 := (UserGlobals at: #GsMapTestAddress) new.
            addr2 instVarNamed: 'street' put: 'Elm St'; instVarNamed: 'city' put: 'Portland'.

            a1 := (UserGlobals at: #GsMapTestAccount) new.
            a1 instVarNamed: 'accName' put: 'Alice'; instVarNamed: 'amount' put: 1234.56s2;
               instVarNamed: 'postalAddress' put: addr1; instVarNamed: 'orderList' put: OrderedCollection new;
               instVarNamed: 'tags' put: (Set withAll: #('vip' 'eu')); instVarNamed: 'state' put: #active;
               instVarNamed: 'opened' put: (Date newDay: 15 monthNumber: 3 year: 2021); instVarNamed: 'extra' put: 42;
               instVarNamed: 'unmapped' put: (Array new: 1000).
            a2 := (UserGlobals at: #GsMapTestAccount) new.
            a2 instVarNamed: 'accName' put: 'Bob'; instVarNamed: 'amount' put: 0s2;
               instVarNamed: 'postalAddress' put: addr1; instVarNamed: 'orderList' put: OrderedCollection new;
               instVarNamed: 'tags' put: Set new; instVarNamed: 'state' put: #frozen; instVarNamed: 'extra' put: 'text'.
            a3 := (UserGlobals at: #GsMapTestSpecialAccount) new.
            a3 instVarNamed: 'accName' put: 'Carol'; instVarNamed: 'amount' put: 99999999999999999999.99s2;
               instVarNamed: 'postalAddress' put: addr2; instVarNamed: 'orderList' put: OrderedCollection new;
               instVarNamed: 'tags' put: (Set with: 'gold'); instVarNamed: 'state' put: #closed;
               instVarNamed: 'bonus' put: 500.

            o1 := (UserGlobals at: #GsMapTestOrder) new.
            o1 instVarNamed: 'number' put: 1; instVarNamed: 'total' put: 19.5; instVarNamed: 'account' put: a1;
               instVarNamed: 'lines' put: (KeyValueDictionary new at: 'apple' put: 3; at: 'pear' put: 5; yourself).
            o2 := (UserGlobals at: #GsMapTestOrder) new.
            o2 instVarNamed: 'number' put: 2; instVarNamed: 'total' put: 7.25; instVarNamed: 'account' put: a1.
            (a1 instVarNamed: 'orderList') add: o1; add: o2.

            big := 2 raisedTo: 100.
            h := (UserGlobals at: #GsMapTestHolder) new.
            h instVarNamed: 'items' put: (Array with: 1 with: 'two' with: 3.5 with: nil);
              instVarNamed: 'idSet' put: (IdentitySet withAll: (1 to: 2000));
              instVarNamed: 'bag' put: (Bag new add: 'x'; add: 'x'; add: 'y'; yourself);
              instVarNamed: 'symbols' put: (SymbolDictionary new at: #home put: addr1; at: #work put: addr2; yourself);
              instVarNamed: 'code' put: #abc; instVarNamed: 'big' put: big;
              instVarNamed: 'when' put: (DateAndTime year: 2024 month: 2 day: 29 hour: 12 minute: 30 second: 0 offset: Duration zero).

            UserGlobals at: #GsMapTestAccounts put: (Array with: a1 with: a2 with: a3).
            UserGlobals at: #GsMapTestHolderObj put: h.
            UserGlobals at: #GsMapTestOrderNoLines put: o2.
            UserGlobals at: #GsMapTestMany put: ((1 to: 3000) collect: [:i | | a |
                a := (UserGlobals at: #GsMapTestAccount) new.
                a instVarNamed: 'accName' put: 'acc', i printString; instVarNamed: 'amount' put: (i asScaledDecimal: 2);
                  instVarNamed: 'orderList' put: OrderedCollection new; instVarNamed: 'tags' put: Set new;
                  instVarNamed: 'state' put: #active; instVarNamed: 'postalAddress' put: addr2.
                a]).
            true
            """);
    }

    public void Dispose()
    {
        if (Session is null) return;
        Session.Abort(); // never committed: classes and data disappear
        Session.Dispose();
    }
}

[Collection("Stone")]
public sealed class GsMappingIntegrationTests(MappingFixture fixture) : IClassFixture<MappingFixture>
{
    private GciSession S => fixture.Session!;

    [StoneFact]
    public void LoadsGraphWithRenamedInstVarsSubclassesAndCycles()
    {
        var ctx = new GsObjectContext(S);
        var accounts = ctx.Load<List<Account>>("UserGlobals at: #GsMapTestAccounts");

        Assert.Equal(3, accounts.Count);
        var (alice, bob, carol) = (accounts[0], accounts[1], accounts[2]);

        Assert.Equal("Alice", alice.Name);
        Assert.Equal(1234.56m, alice.Balance);
        Assert.Equal("Hauptstraße 1", alice.Address!.Street);
        Assert.Equal("Köln", alice.Address.City);
        Assert.Equal(AccountStatus.Active, alice.Status);
        Assert.Equal(new DateOnly(2021, 3, 15), alice.Opened);
        Assert.Equal(42L, alice.Extra);
        Assert.Equal(["eu", "vip"], alice.Tags.OrderBy(t => t, StringComparer.Ordinal));
        Assert.NotEqual(default, alice.Oop);

        Assert.Equal(2, alice.Orders.Count);
        Assert.Equal(1, alice.Orders[0].Number);
        Assert.Equal(19.5, alice.Orders[0].Total);
        Assert.Same(alice, alice.Orders[0].Account);   // cycle → same instance
        Assert.Equal(3L, alice.Orders[0].Lines!["apple"]);
        Assert.Equal(5L, alice.Orders[0].Lines!["pear"]);
        Assert.Null(alice.Orders[1].Lines);

        Assert.Same(alice.Address, bob.Address);        // shared reference → same instance
        Assert.Null(bob.Opened);
        Assert.Equal("text", bob.Extra);
        Assert.Equal(AccountStatus.Frozen, bob.Status);
        Assert.Empty(bob.Tags);

        var special = Assert.IsType<SpecialAccount>(carol); // GemStone subclass → C# subclass
        Assert.Equal(500, special.Bonus);
        Assert.Equal(99999999999999999999.99m, special.Balance);
        Assert.Equal(AccountStatus.Closed, special.Status);

        Assert.True(ctx.TryGetOop(alice, out var oop));
        Assert.Equal(alice.Oop, oop);
    }

    [StoneFact]
    public void DepthIsOnlyAPerformanceSetting()
    {
        var shallow = new GsObjectContext(S).Load<List<Account>>("UserGlobals at: #GsMapTestAccounts", new GsLoadOptions { Depth = 1 });
        var deep = new GsObjectContext(S).Load<List<Account>>("UserGlobals at: #GsMapTestAccounts", new GsLoadOptions { Depth = 10 });

        Assert.Equal(deep.Select(a => a.Name), shallow.Select(a => a.Name));
        Assert.Equal("Köln", shallow[0].Address!.City);
        Assert.Same(shallow[0], shallow[0].Orders[1].Account);
        Assert.Equal(5L, shallow[0].Orders[0].Lines!["pear"]);
    }

    [StoneFact]
    public void LoadsCollectionsDictionariesConvertersAndServerConvertedValues()
    {
        var h = new GsObjectContext(S).Load<Holder>("UserGlobals at: #GsMapTestHolderObj");

        Assert.Equal([1L, "two", 3.5, null], h.Items!);
        Assert.Equal(2000, h.IdSet!.Count);                 // NSC read directly
        Assert.True(h.IdSet.SetEquals(Enumerable.Range(1, 2000).Select(i => (long)i)));
        Assert.Equal(["x", "x", "y"], h.Bag!.OrderBy(t => t, StringComparer.Ordinal));       // Bag: flattened on the server
        Assert.Equal("Köln", h.BySymbol!["home"].City);      // Symbol keys → string
        Assert.Equal("Portland", h.BySymbol["work"].City);
        Assert.Equal("ABC", h.Code);
        Assert.Equal(BigInteger.Pow(2, 100), h.Big);
        Assert.Equal(new DateTimeOffset(2024, 2, 29, 12, 30, 0, TimeSpan.Zero), h.When);
    }

    [StoneFact]
    public void ServerConvertedValuesAreBatched()
    {
        // 3000 Balances (SmallScaledDecimal) used to cost several round trips each.
        using (var warm = new GsObjectContext(S)) warm.Load<Account[]>("UserGlobals at: #GsMapTestMany");
        var before = S.CallCount;
        using var ctx = new GsObjectContext(S);
        var many = ctx.Load<Account[]>("UserGlobals at: #GsMapTestMany", new GsLoadOptions { TrackChanges = false });
        var calls = S.CallCount - before;

        Assert.Equal(3000, many.Length);
        Assert.Equal(1234m, many[1233].Balance);
        Assert.InRange(calls, 1, 200); // measured: 75 (12068 with one conversion per value)
    }

    [StoneFact]
    public void ManyObjectsAcrossSeveralBuffers()
    {
        using var ctx = new GsObjectContext(S);
        var many = ctx.Load<Account[]>("UserGlobals at: #GsMapTestMany", new GsLoadOptions { BufferSize = 16 * 1024 });

        Assert.Equal(3000, many.Length);
        Assert.Equal("acc3000", many[^1].Name);
        Assert.Equal(1500m, many[1499].Balance);
        Assert.All(many, a => Assert.Same(many[0].Address, a.Address));
    }

    [StoneFact]
    public void ReloadRefreshesTheSameInstance()
    {
        var ctx = new GsObjectContext(S);
        var first = ctx.Load<Order>("UserGlobals at: #GsMapTestOrderNoLines");
        S.Execute("(UserGlobals at: #GsMapTestOrderNoLines) instVarNamed: 'total' put: 8.5");
        try
        {
            var second = ctx.Load<Order>("UserGlobals at: #GsMapTestOrderNoLines");
            Assert.Same(first, second);
            Assert.Equal(8.5, second.Total);
        }
        finally
        {
            S.Execute("(UserGlobals at: #GsMapTestOrderNoLines) instVarNamed: 'total' put: 7.25");
        }
    }

    [StoneFact]
    public void ValidationListsAllProblemsWithSuggestions()
    {
        var ctx = new GsObjectContext(S);
        var e = Assert.Throws<GsMappingException>(() => ctx.Validate(typeof(AddressWithTypo)));
        Assert.Equal(2, e.Problems.Count);
        Assert.Contains(e.Problems, p => p.Contains("'stret'") && p.Contains("did you mean 'street'"));
        Assert.Contains(e.Problems, p => p.Contains("'zip'") && p.Contains("has: street, city"));

        var missing = Assert.Throws<GsMappingException>(() => ctx.Validate(typeof(Phantom)));
        Assert.Contains("GsMapTestNoSuchClass", missing.Message);

        ctx.Validate(typeof(Account)); // the good mapping passes
    }

    [StoneFact]
    public void DataThatDoesNotFitTheMappingIsReported()
    {
        var ctx = new GsObjectContext(S);

        var kind = Assert.Throws<GsMappingException>(() => ctx.Load<OrderWithWrongKind>("((UserGlobals at: #GsMapTestAccounts) first instVarNamed: 'orderList') first"));
        Assert.Contains("KeyValueDictionary", kind.Message);

        var cls = Assert.Throws<GsMappingException>(() => ctx.Load<OrderWithAddressAsAccount>("UserGlobals at: #GsMapTestOrderNoLines"));
        Assert.Contains("expected a GsMapTestAddress", cls.Message);
        Assert.Contains("GsMapTestAccount", cls.Message);

        // nil into a non-nullable property (Order.Number is an int)
        S.Execute("(UserGlobals at: #GsMapTestOrderNoLines) instVarNamed: 'number' put: nil");
        try
        {
            var nil = Assert.Throws<GsMappingException>(() => ctx.Load<Order>("UserGlobals at: #GsMapTestOrderNoLines"));
            Assert.Contains("Order.Number", nil.Message);
        }
        finally
        {
            S.Execute("(UserGlobals at: #GsMapTestOrderNoLines) instVarNamed: 'number' put: 2");
        }
    }

    [StoneFact]
    public void LoadingDoesNotGrowTheExportSet()
    {
        long Size() => S.Evaluate<long>("System hiddenSetSize: (System HiddenSetSpecifiers indexOf: 'PureExportSet')");
        // Warm up caches (the session resolves Date/DateAndTime once and keeps them).
        using (var warm = new GsObjectContext(S))
        {
            warm.Load<Holder>("UserGlobals at: #GsMapTestHolderObj");
            warm.Load<List<Account>>("UserGlobals at: #GsMapTestAccounts");
        }
        var before = Size();
        for (var i = 0; i < 5; i++)
        {
            using (var ctx = new GsObjectContext(S))
                ctx.Load<List<Account>>("UserGlobals at: #GsMapTestAccounts", new GsLoadOptions { Depth = 1 });
            using (var ctx = new GsObjectContext(S))
                ctx.Load<Holder>("UserGlobals at: #GsMapTestHolderObj");
        }
        Assert.Equal(before, Size());

        // Without change tracking nothing is held, even without Dispose.
        var readOnly = new GsLoadOptions { TrackChanges = false };
        new GsObjectContext(S).Load<List<Account>>("UserGlobals at: #GsMapTestAccounts", readOnly);
        new GsObjectContext(S).Load<Holder>("UserGlobals at: #GsMapTestHolderObj", readOnly);
        Assert.Equal(before, Size());
    }
}
