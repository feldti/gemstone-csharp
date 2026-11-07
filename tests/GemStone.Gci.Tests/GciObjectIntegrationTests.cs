namespace GemStone.Gci.Tests;

/// <summary>Structural access, using kernel classes so no classes have to be defined.</summary>
[Collection("Stone")]
public class GciObjectIntegrationTests
{
    [StoneFact]
    public void NamedInstanceVariablesByIndexAndName()
    {
        using var s = StoneEnv.Login();
        var assoc = s.Execute("Association key: #k value: 42");
        try
        {
            var cls = s.FetchClass(assoc);
            Assert.Equal(GciObjectFormat.Pointer, s.FetchFormat(assoc));
            Assert.Equal(2, s.FetchNamedSize(assoc));
            Assert.Equal(1, s.InstVarIndex(cls, "key"));
            Assert.Equal(2, s.InstVarIndex(cls, "value"));
            Assert.Throws<ArgumentException>(() => s.InstVarIndex(cls, "noSuchVar"));

            Assert.Equal([s.Execute("#k"), Oop.FromSmallInteger(42)], s.FetchNamedOops(assoc));
            Assert.Equal("k", s.GetInstVar(assoc, "key"));
            Assert.Equal(42L, s.GetInstVar(assoc, "value"));

            s.SetInstVar(assoc, "value", "Grüße");
            Assert.Equal("Grüße", s.Send(assoc, "value")); // the server sees the stored object
            s.StoreNamedOop(assoc, 2, Oop.Nil);
            Assert.Null(s.Evaluate("nil")); // sanity
            Assert.Equal(Oop.Nil, s.FetchNamedOop(assoc, "value"));
        }
        finally { s.Release(assoc); }
    }

    [StoneFact]
    public void NewObjectAndIndexedSlots()
    {
        using var s = StoneEnv.Login();
        var array = s.NewObject(KnownClasses.Array, 3);
        try
        {
            Assert.Equal(3, s.FetchVaryingSize(array));
            Assert.Equal([Oop.Nil, Oop.Nil, Oop.Nil], s.FetchVaryingOops(array));

            s.StoreVaryingOops(array, 1, Oop.FromSmallInteger(1), Oop.FromSmallInteger(2));
            s.AppendOops(array, Oop.True);
            Assert.Equal(4, s.FetchVaryingSize(array));
            Assert.Equal([1L, 2L, null, true], s.ToList(array));
            Assert.Equal(Oop.FromSmallInteger(2), s.FetchVaryingOop(array, 2));
            Assert.Equal([Oop.FromSmallInteger(2), Oop.Nil], s.FetchVaryingOops(array, 2, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => s.FetchVaryingOop(array, 5));
            Assert.Equal(4L, s.Send(array, "size"));
        }
        finally { s.Release(array); }
    }

    [StoneFact]
    public void LargeCollectionsAreFetchedAndStoredInChunks()
    {
        using var s = StoneEnv.Login();
        var array = s.NewObject(KnownClasses.Array);
        try
        {
            var values = Enumerable.Range(1, 20_000).Select(i => Oop.FromSmallInteger(i)).ToArray();
            s.StoreVaryingOops(array, 1, values);
            Assert.Equal(20_000L, s.Send(array, "size"));
            Assert.Equal(200_010_000L, s.Send(array, "inject:into:", 0, s.Execute("[:a :b | a + b]")));
            Assert.Equal(values, s.FetchVaryingOops(array));
        }
        finally { s.Release(array); }
    }

    [StoneFact]
    public void ByteObjects()
    {
        using var s = StoneEnv.Login();
        var bytes = s.NewByteArray([1, 2, 3]);
        try
        {
            Assert.Equal(GciObjectFormat.Byte, s.FetchFormat(bytes));
            s.StoreBytes(bytes, 2, [9, 9, 9]); // grows
            s.AppendBytes(bytes, [7]);
            Assert.Equal(new byte[] { 1, 9, 9, 9, 7 }, s.FetchBytes(bytes));
            Assert.Equal(new byte[] { 1, 9, 9, 9, 7 }, s.ToValue(bytes));
        }
        finally { s.Release(bytes); }

        var str = s.NewString("abc");
        try
        {
            s.AppendBytes(str, "def"u8);
            Assert.Equal("abcdef", s.ToStringValue(str));
        }
        finally { s.Release(str); }
    }

    [StoneFact]
    public void NscMembership()
    {
        using var s = StoneEnv.Login();
        var set = s.NewObject(s.ResolveSymbol("IdentitySet"));
        try
        {
            Assert.Equal(GciObjectFormat.Nsc, s.FetchFormat(set));
            s.AddToNsc(set, Oop.FromSmallInteger(1));
            s.AddToNsc(set, Oop.FromSmallInteger(2));
            Assert.True(s.NscIncludes(set, Oop.FromSmallInteger(1)));
            Assert.Equal(2, s.FetchVaryingSize(set));
            Assert.True(s.RemoveFromNsc(set, Oop.FromSmallInteger(1)));
            Assert.False(s.RemoveFromNsc(set, Oop.FromSmallInteger(1)));
            Assert.False(s.NscIncludes(set, Oop.FromSmallInteger(1)));
            Assert.Equal([2L], s.ToList(set));
        }
        finally { s.Release(set); }
    }

    [StoneFact]
    public void ResolveSymbolFindsGlobals()
    {
        using var s = StoneEnv.Login();
        Assert.Equal(KnownClasses.Array, s.ResolveSymbol("Array"));
        Assert.Equal(KnownClasses.String, s.ResolveSymbol("String"));
        Assert.Throws<KeyNotFoundException>(() => s.ResolveSymbol("NoSuchGlobalXyz"));
        Assert.False(s.TryResolveSymbol("NoSuchGlobalXyz", out _));
        Assert.True(s.TryResolveSymbol("UserGlobals", out var userGlobals));
        Assert.False(userGlobals.IsSpecial);
        Assert.Throws<ArgumentException>(() => s.NewObject(KnownClasses.Boolean));
    }

    [StoneFact]
    public void ErrorsAndEvaluateDoNotGrowTheExportSet()
    {
        using var s = StoneEnv.Login();
        long ExportSetSize() => s.Evaluate<long>("System hiddenSetSize: (System HiddenSetSpecifiers indexOf: 'PureExportSet')");

        ExportSetSize(); // warm up lazily resolved helpers
        var before = ExportSetSize();
        for (var i = 0; i < 50; i++)
        {
            Assert.Throws<GciException>(() => s.Execute("1/0"));
            s.Evaluate("'a string' , 'another'");
            s.Evaluate("100 factorial");
            s.Evaluate("Date today");
        }
        Assert.InRange(ExportSetSize() - before, 0, 5);
    }

    [StoneFact]
    public void ResolveSymbolDoesNotGrowTheExportSet()
    {
        // GciResolveSymbol (3.7.5) leaves a String per call in the export set; the wrapper uses
        // GciResolveSymbolObj with its own, released name object instead.
        using var s = StoneEnv.Login();
        long ExportSetSize() => s.Evaluate<long>("System hiddenSetSize: (System HiddenSetSpecifiers indexOf: 'PureExportSet')");

        var before = ExportSetSize();
        for (var i = 0; i < 20; i++)
        {
            s.Release(s.ResolveSymbol("OrderedCollection"));
            Assert.False(s.TryResolveSymbol("NoSuchGlobalXyz", out _));
        }
        Assert.Equal(before, ExportSetSize());
    }
}
