using System.Numerics;
using System.Text;
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

    // ---- Batched writes ----------------------------------------------------------------------

    public static TheoryData<object, object> ScalarValues => new()
    {
        { 12.34m, 12.34m },
        { -0.0001m, -0.0001m },
        { 123456789012345678901234.5678m, 123456789012345678901234.5678m }, // LargeInteger unscaled
        { decimal.MaxValue, decimal.MaxValue },
        { 7L, 7L },
        { long.MaxValue, long.MaxValue }, // LargeInteger
        { long.MinValue, long.MinValue },
        { ulong.MaxValue, new BigInteger(ulong.MaxValue) },
        { BigInteger.Pow(10, 40), BigInteger.Pow(10, 40) },
        { -BigInteger.Pow(7, 50), -BigInteger.Pow(7, 50) },
        { 1.5, 1.5 },
        { 1e100, 1e100 }, // Float object
        { -1e-300, -1e-300 },
        { double.PositiveInfinity, double.PositiveInfinity },
        { "plain", "plain" },
        { "Grüße 😀", "Grüße 😀" },
        { true, true },
        { new Rune('ß'), new Rune('ß') },
        { new DateOnly(1899, 12, 31), new DateOnly(1899, 12, 31) },
        { new TimeOnly(23, 59, 58, 999), new TimeOnly(23, 59, 58, 999) },
        { new DateTimeOffset(2026, 11, 8, 10, 30, 15, TimeSpan.FromHours(2)).AddTicks(1234560), new DateTimeOffset(2026, 11, 8, 10, 30, 15, TimeSpan.FromHours(2)).AddTicks(1234560) },
        { new DateTimeOffset(1950, 1, 1, 0, 0, 0, TimeSpan.FromHours(-5)), new DateTimeOffset(1950, 1, 1, 0, 0, 0, TimeSpan.FromHours(-5)) },
        { new byte[] { 0, 1, 255 }, new byte[] { 0, 1, 255 } },
    };

    [StoneTheory]
    [MemberData(nameof(ScalarValues))]
    public void ScalarValuesSurviveABatchedSave(object written, object expected)
    {
        var key = NewAccount();
        using var ctx = new GsObjectContext(S);
        ctx.Load<Account>($"UserGlobals at: #{key}").Extra = written;
        Assert.Equal(1, ctx.SaveChanges());
        Assert.Equal(0, ctx.SaveChanges());

        using var fresh = new GsObjectContext(S);
        var read = fresh.Load<Account>($"UserGlobals at: #{key}").Extra;
        Assert.Equal(expected, read);
        if (expected is DateTimeOffset dto) Assert.Equal(dto.Offset, ((DateTimeOffset)read!).Offset);
    }

    [StoneFact]
    public void StringsGetTheSameClassesAsNewString()
    {
        var key = NewAccount();
        using var ctx = new GsObjectContext(S);
        var a = ctx.Load<Account>($"UserGlobals at: #{key}");
        var acc = $"(UserGlobals at: #{key})";
        foreach (var text in new[] { "plain", "Grüße", "Grüße 😀", "" })
        {
            a.Name = text;
            ctx.SaveChanges();
            var expected = S.FetchClass(S.NewString(text));
            Assert.Equal(S.Evaluate<string>($"(Object _objectForOop: {expected.Value}) name"), Eval<string>($"({acc} instVarNamed: 'accName') class name"));
            Assert.Equal(text, Eval<string>($"{acc} instVarNamed: 'accName'"));
        }
        a.Status = AccountStatus.Closed;
        ctx.SaveChanges();
        Assert.True(Eval<bool>($"({acc} instVarNamed: 'state') == #closed"));
    }

    [StoneFact]
    public void ManyChangesCostAFewRoundTrips()
    {
        var key = "GsSaveTest_" + Guid.NewGuid().ToString("N");
        S.Execute($$"""
            | all |
            all := OrderedCollection new.
            1 to: 300 do: [:i | | a |
              a := (UserGlobals at: #GsMapTestAccount) new.
              a instVarNamed: 'accName' put: 'acc', i printString; instVarNamed: 'amount' put: (i asScaledDecimal: 2);
                instVarNamed: 'orderList' put: OrderedCollection new; instVarNamed: 'tags' put: Set new; instVarNamed: 'state' put: #active.
              all add: a].
            UserGlobals at: #{{key}} put: all asArray.
            true
            """);
        using var ctx = new GsObjectContext(S);
        var accounts = ctx.Load<Account[]>($"UserGlobals at: #{key}");
        Assert.Equal(300, accounts.Length);

        var before = S.CallCount;
        foreach (var a in accounts)
        {
            a.Balance += 0.01m;
            a.Name += " Müller";
            a.Opened = new DateOnly(2026, 1, 1);
            a.Tags.Add("vip");
            a.Orders.Add(new Order { Number = 1, Total = 1e100, Account = a, Lines = new() { ["x"] = long.MaxValue } });
        }
        var stores = ctx.SaveChanges();
        var calls = S.CallCount - before;

        Assert.Equal(300 * 9, stores); // 3 scalars + 2 collections per account, 4 instVars per new order
        // Measured: 10 (7509 with one or more round trips per change); 4 of them resolve
        // KeyValueDictionary for the new 'lines' once per context.
        Assert.InRange(calls, 1, 15);
        var all = $"(UserGlobals at: #{key})";
        Assert.Equal(300, Eval<long>($"({all} select: [:a | (a instVarNamed: 'accName') endsWith: ' Müller']) size"));
        Assert.Equal(1.01m, Eval<decimal>($"({all} at: 1) instVarNamed: 'amount'"));
        Assert.True(Eval<bool>($"({all} allSatisfy: [:a | ((a instVarNamed: 'orderList') first instVarNamed: 'account') == a])"));
        Assert.Equal(long.MaxValue, Eval<long>($"(((({all} at: 300) instVarNamed: 'orderList') first instVarNamed: 'lines') at: 'x')"));
        Assert.True(ctx.TryGetOop(accounts[299].Orders[0], out var orderOop));
        Assert.True(Eval<bool>($"((({all} at: 300) instVarNamed: 'orderList') first) == (Object _objectForOop: {orderOop.Value})"));
        Assert.Equal(0, ctx.SaveChanges());

        // A second round: only the batch itself (free OOPs, store traversal, execute, fetch the
        // answer, retain the new objects, release the traversal objects).
        before = S.CallCount;
        foreach (var a in accounts) a.Orders.Add(new Order { Number = 2, Lines = new() { ["y"] = 1 } });
        ctx.SaveChanges();
        Assert.Equal(6, S.CallCount - before);
        Assert.Equal(600, Eval<long>($"{all} inject: 0 into: [:sum :a | sum + (a instVarNamed: 'orderList') size]"));
    }

    [StoneFact]
    public void AFailedBatchLeavesTheContextUnchanged()
    {
        var key = NewAccount();
        using var ctx = new GsObjectContext(S);
        var a = ctx.Load<Account>($"UserGlobals at: #{key}");
        S.Execute($"(UserGlobals at: #{key}) immediateInvariant. true");

        var address = new Address { City = "Trier" };
        a.Address = address;
        a.Balance = 5m;
        Assert.Throws<GciException>(() => ctx.SaveChanges());
        Assert.False(ctx.TryGetOop(address, out _)); // not tracked: the batch failed
        Assert.Throws<GciException>(() => ctx.SaveChanges()); // the changes are still pending
        Assert.Equal(10.50m, Eval<decimal>($"(UserGlobals at: #{key}) instVarNamed: 'amount'"));
    }
}
