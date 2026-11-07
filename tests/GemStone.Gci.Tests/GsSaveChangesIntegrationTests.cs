using GemStone.Gci.Mapping;

namespace GemStone.Gci.Tests.Mapping;

[GsClass("GsMapTestAccount")]
public sealed class AccountWithReadOnlyName
{
    [GsInstVar("accName"), GsReadOnly] public string? Name { get; set; }
    [GsInstVar("amount")] public decimal Balance { get; set; }
}

/// <summary>
/// SaveChanges against the classes of <see cref="MappingFixture"/> (own session, never committed).
/// Every test works on fresh objects stored under its own key in UserGlobals.
/// </summary>
[Collection("Stone")]
public sealed class GsSaveChangesIntegrationTests(MappingFixture fixture) : IClassFixture<MappingFixture>
{
    private GciSession S => fixture.Session!;

    /// <summary>A fresh account (with one order and a dictionary) under a new key; returns the key.</summary>
    private string NewAccount()
    {
        var key = "GsSaveTest_" + Guid.NewGuid().ToString("N");
        S.Execute($$"""
            | a o addr |
            addr := (UserGlobals at: #GsMapTestAddress) new.
            addr instVarNamed: 'street' put: 'Old Street'; instVarNamed: 'city' put: 'Bonn'.
            a := (UserGlobals at: #GsMapTestAccount) new.
            a instVarNamed: 'accName' put: 'Dora'; instVarNamed: 'amount' put: 10.50s2;
              instVarNamed: 'postalAddress' put: addr; instVarNamed: 'orderList' put: OrderedCollection new;
              instVarNamed: 'tags' put: (Set withAll: #('a' 'b')); instVarNamed: 'state' put: #active.
            o := (UserGlobals at: #GsMapTestOrder) new.
            o instVarNamed: 'number' put: 1; instVarNamed: 'total' put: 1.5; instVarNamed: 'account' put: a;
              instVarNamed: 'lines' put: (KeyValueDictionary new at: 'apple' put: 3; yourself).
            (a instVarNamed: 'orderList') add: o.
            UserGlobals at: #{{key}} put: a.
            true
            """);
        return key;
    }

    private T Eval<T>(string source) => S.Evaluate<T>(source);

    [StoneFact]
    public void UnchangedObjectsWriteNothing()
    {
        using var ctx = new GsObjectContext(S);
        ctx.Load<List<Account>>("UserGlobals at: #GsMapTestAccounts");
        Assert.Equal(0, ctx.SaveChanges());
    }

    [StoneFact]
    public void ChangedScalarsAreWrittenOnce()
    {
        var key = NewAccount();
        using var ctx = new GsObjectContext(S);
        var a = ctx.Load<Account>($"UserGlobals at: #{key}");

        a.Name = "Dora Neu";
        a.Balance = 99.95m;
        a.Status = AccountStatus.Frozen;
        a.Opened = new DateOnly(2025, 12, 24);

        Assert.Equal(4, ctx.SaveChanges());
        Assert.Equal(0, ctx.SaveChanges()); // snapshots updated

        var acc = $"(UserGlobals at: #{key})";
        Assert.Equal("Dora Neu", Eval<string>($"{acc} instVarNamed: 'accName'"));
        Assert.Equal("String", Eval<string>($"({acc} instVarNamed: 'accName') class name"));
        Assert.Equal(99.95m, Eval<decimal>($"{acc} instVarNamed: 'amount'"));
        Assert.True(Eval<bool>($"({acc} instVarNamed: 'state') == #frozen")); // Symbol, Smalltalk case
        Assert.Equal(new DateOnly(2025, 12, 24), Eval<DateOnly>($"{acc} instVarNamed: 'opened'"));

        // A fresh context sees the written state.
        using var fresh = new GsObjectContext(S);
        var again = fresh.Load<Account>($"UserGlobals at: #{key}");
        Assert.Equal(AccountStatus.Frozen, again.Status);
        Assert.Equal(99.95m, again.Balance);
    }

    [StoneFact]
    public void NewObjectsAndReferencesAreCreated()
    {
        var key = NewAccount();
        using var ctx = new GsObjectContext(S);
        var a = ctx.Load<Account>($"UserGlobals at: #{key}");

        a.Address = new Address { Street = "New Street 5", City = "Köln" };
        var order = new Order { Number = 2, Total = 3.25, Account = a, Lines = new() { ["pear"] = 7 } };
        a.Orders.Add(order);

        Assert.True(ctx.SaveChanges() > 0);
        Assert.True(ctx.TryGetOop(order, out var orderOop));
        Assert.True(ctx.TryGetOop(a.Address, out _));

        var acc = $"(UserGlobals at: #{key})";
        Assert.Equal("New Street 5", Eval<string>($"({acc} instVarNamed: 'postalAddress') instVarNamed: 'street'"));
        Assert.Equal("Köln", Eval<string>($"({acc} instVarNamed: 'postalAddress') instVarNamed: 'city'"));
        Assert.Equal(2, Eval<long>($"({acc} instVarNamed: 'orderList') size"));
        Assert.True(Eval<bool>($"(({acc} instVarNamed: 'orderList') last instVarNamed: 'account') == {acc}")); // cycle
        Assert.Equal(7, Eval<long>($"(({acc} instVarNamed: 'orderList') last instVarNamed: 'lines') at: 'pear'"));
        Assert.Equal("KeyValueDictionary", Eval<string>($"(({acc} instVarNamed: 'orderList') last instVarNamed: 'lines') class name"));
        Assert.True(Eval<bool>($"({acc} instVarNamed: 'orderList') last == (Object _objectForOop: {orderOop.Value})"));
    }

    [StoneFact]
    public void ChangedCollectionsAreRewrittenInPlace()
    {
        var key = NewAccount();
        using var ctx = new GsObjectContext(S);
        var a = ctx.Load<Account>($"UserGlobals at: #{key}");
        var acc = $"(UserGlobals at: #{key})";
        var tagsBefore = Eval<long>($"({acc} instVarNamed: 'tags') asOop");

        a.Tags.Remove("a");
        a.Tags.Add("c");
        a.Orders[0].Lines!["apple"] = 4;
        a.Orders[0].Lines!["plum"] = 1;

        Assert.Equal(2, ctx.SaveChanges()); // two collections rewritten
        Assert.Equal(tagsBefore, Eval<long>($"({acc} instVarNamed: 'tags') asOop")); // same Set object
        Assert.Equal("b c", Eval<string>($"(({acc} instVarNamed: 'tags') asSortedCollection: [:x :y | x <= y]) inject: '' into: [:s :e | s isEmpty ifTrue: [e] ifFalse: [s, ' ', e]]"));
        Assert.Equal(4, Eval<long>($"(({acc} instVarNamed: 'orderList') first instVarNamed: 'lines') at: 'apple'"));
        Assert.Equal(1, Eval<long>($"(({acc} instVarNamed: 'orderList') first instVarNamed: 'lines') at: 'plum'"));

        a.Orders.RemoveAt(0);
        Assert.Equal(1, ctx.SaveChanges());
        Assert.Equal(0, Eval<long>($"({acc} instVarNamed: 'orderList') size"));
    }

    [StoneFact]
    public void CollectionsCanBecomeNilAndBeCreated()
    {
        var key = NewAccount();
        using var ctx = new GsObjectContext(S);
        var order = ctx.Load<Account>($"UserGlobals at: #{key}").Orders[0];
        var lines = $"((UserGlobals at: #{key}) instVarNamed: 'orderList') first instVarNamed: 'lines'";

        order.Lines = null;
        Assert.Equal(1, ctx.SaveChanges());
        Assert.True(Eval<bool>($"({lines}) isNil"));

        order.Lines = new Dictionary<string, long> { ["kiwi"] = 9 };
        Assert.Equal(1, ctx.SaveChanges());
        Assert.Equal(9, Eval<long>($"({lines}) at: 'kiwi'"));
    }

    [StoneFact]
    public void SymbolKeysStaySymbols()
    {
        S.Execute("""
            | h |
            h := (UserGlobals at: #GsMapTestHolder) new.
            h instVarNamed: 'symbols' put: (SymbolDictionary new at: #home put: (UserGlobals at: #GsMapTestAddress) new; yourself);
              instVarNamed: 'big' put: 1; instVarNamed: 'when' put: DateAndTime now.
            UserGlobals at: #GsSaveTestHolder put: h.
            true
            """);
        using var ctx = new GsObjectContext(S);
        var h = ctx.Load<Holder>("UserGlobals at: #GsSaveTestHolder");
        h.BySymbol!["office"] = new Address { City = "Berlin" };

        Assert.Equal(3, ctx.SaveChanges()); // dictionary rewritten + street and city of the new Address
        var dict = "((UserGlobals at: #GsSaveTestHolder) instVarNamed: 'symbols')";
        Assert.Equal("SymbolDictionary", Eval<string>($"{dict} class name"));
        Assert.True(Eval<bool>($"{dict} keys allSatisfy: [:k | k isSymbol]"));
        Assert.Equal("Berlin", Eval<string>($"({dict} at: #office) instVarNamed: 'city'"));
        Assert.Equal(2, Eval<long>($"{dict} size"));
    }

    [StoneFact]
    public void ReadOnlyPropertiesAndOneWayConvertersAreRespected()
    {
        var key = NewAccount();
        using (var ctx = new GsObjectContext(S))
        {
            var a = ctx.Load<AccountWithReadOnlyName>($"UserGlobals at: #{key}");
            a.Name = "ignored";
            Assert.Equal(0, ctx.SaveChanges());
            a.Balance = 1m;
            Assert.Equal(1, ctx.SaveChanges());
            Assert.Equal("Dora", Eval<string>($"(UserGlobals at: #{key}) instVarNamed: 'accName'"));
        }

        using var hctx = new GsObjectContext(S);
        var h = hctx.Load<Holder>("UserGlobals at: #GsMapTestHolderObj");
        h.Code = "XYZ"; // UpperCaseConverter has no ToGemStone
        Assert.Throws<NotSupportedException>(() => hctx.SaveChanges());
    }

    [StoneFact]
    public void AddCreatesAStandaloneObject()
    {
        using var ctx = new GsObjectContext(S);
        var address = new Address { Street = "Solo 1", City = "Hamm" };
        var oop = ctx.Add(address);

        Assert.Equal("GsMapTestAddress", Eval<string>($"(Object _objectForOop: {oop.Value}) class name"));
        Assert.Equal("Hamm", Eval<string>($"(Object _objectForOop: {oop.Value}) instVarNamed: 'city'"));
        Assert.Equal(oop, ctx.Add(address)); // already tracked
        Assert.Equal(0, ctx.SaveChanges());
    }

    [StoneFact]
    public void DisposeReleasesTrackedObjects()
    {
        long Size() => S.Evaluate<long>("System hiddenSetSize: (System HiddenSetSpecifiers indexOf: 'PureExportSet')");
        var key = NewAccount();
        using (var warm = new GsObjectContext(S)) warm.Load<Account>($"UserGlobals at: #{key}");

        var before = Size();
        using (var ctx = new GsObjectContext(S))
        {
            var a = ctx.Load<Account>($"UserGlobals at: #{key}");
            Assert.True(Size() > before); // tracked objects are held
            a.Address = new Address { City = "Essen" };
            a.Orders.Add(new Order { Number = 9 });
            ctx.SaveChanges();
        }
        Assert.Equal(before, Size());
    }
}
