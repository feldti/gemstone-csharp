using System.Text;

namespace GemStone.Gci.Tests;

/// <summary>Store traversal + execution in one round trip (GciStoreTravDo_), the base of batched SaveChanges.</summary>
[Collection("Stone")]
public class GciStoreIntegrationTests
{
    [StoneFact]
    public void CreatesObjectsAndExecutesWithThemAsSelf()
    {
        using var s = StoneEnv.Login();
        var ids = s.GetFreeOops(6);
        Assert.Equal(6, ids.Distinct().Count());
        GciNewObject[] objects =
        [
            // The root first: it references objects that come later in the buffer.
            GciNewObject.Pointers(ids[0], KnownClasses.Array, [ids[1], ids[2], ids[3], ids[4], ids[5], Oop.FromSmallInteger(7)]),
            GciNewObject.ByteObject(ids[1], KnownClasses.String, Encoding.ASCII.GetBytes("plain")),
            GciNewObject.ByteObject(ids[2], KnownClasses.Utf8, Encoding.UTF8.GetBytes("Grüße 😀")),
            GciNewObject.ByteObject(ids[3], KnownClasses.ByteArray, [1, 2, 255]),
            GciNewObject.Float(ids[4], 1.0e100),
            GciNewObject.Pointers(ids[5], KnownClasses.Array, []),
        ];

        var before = s.CallCount;
        var result = s.StoreAndExecute(objects,
            "{ (self at: 1) class name. self at: 1. (self at: 2) decodeToUnicode. self at: 3. (self at: 4) class name. self at: 4. (self at: 5) size. self at: 6 }",
            ids[0]);
        Assert.Equal(2, s.CallCount - before);
        try
        {
            var values = s.ToValues(s.FetchVaryingOops(result));
            Assert.Equal("String", values[0]);
            Assert.Equal("plain", values[1]);
            Assert.Equal("Grüße 😀", values[2]);
            Assert.Equal(new byte[] { 1, 2, 255 }, values[3]);
            Assert.Equal("Float", values[4]);
            Assert.Equal(1.0e100, values[5]);
            Assert.Equal(0L, values[6]);
            Assert.Equal(7L, values[7]);
        }
        finally
        {
            s.Release([result, .. ids]);
        }
    }

    [StoneFact]
    public void CreatedObjectsAreReleasedFromTheExportSet()
    {
        using var s = StoneEnv.Login();
        long Size() => s.Evaluate<long>("System hiddenSetSize: (System HiddenSetSpecifiers indexOf: 'PureExportSet')");
        Size();
        var before = Size();
        var ids = s.GetFreeOops(2);
        var result = s.StoreAndExecute(
            [GciNewObject.Pointers(ids[0], KnownClasses.Array, [ids[1]]), GciNewObject.ByteObject(ids[1], KnownClasses.String, "x"u8.ToArray())],
            "self size", ids[0]);
        Assert.Equal(1L, s.ToValue(result));
        Assert.True(Size() >= before + 2);
        s.Release(ids);
        Assert.Equal(before, Size());
    }

    [StoneFact]
    public void ErrorsInTheCodeAreReported()
    {
        using var s = StoneEnv.Login();
        var ids = s.GetFreeOops(1);
        var ex = Assert.Throws<GciException>(() =>
            s.StoreAndExecute([GciNewObject.Pointers(ids[0], KnownClasses.Array, [])], "self foo", ids[0]));
        Assert.Contains("foo", ex.Message);
        s.Release(ids);
        Assert.Equal(3L, s.Evaluate<long>("1 + 2")); // the session is still usable
    }

    // Diagnosis for Windows clients (protocol error 2231 with a large SaveChanges batch): each
    // size in its own session, so the first size that fails is visible on its own.
    [StoneTheory]
    [InlineData(10)]
    [InlineData(1_000)]
    [InlineData(4_000)]
    [InlineData(20_000)]
    [InlineData(150_000)] // more than GciGetFreeOops hands out per call
    public void ManySmallObjectsInOneStoreTraversal(int count)
    {
        using var s = StoneEnv.Login();
        var ids = s.GetFreeOops(count + 1);
        var objects = new List<GciNewObject>(count + 1)
        {
            GciNewObject.Pointers(ids[0], KnownClasses.Array, ids.AsSpan(1).ToArray()),
        };
        for (var i = 1; i <= count; i++)
            objects.Add(GciNewObject.ByteObject(ids[i], KnownClasses.String, Encoding.ASCII.GetBytes("element " + i)));
        var result = s.StoreAndExecute(objects, "(self inject: 0 into: [:sum :e | sum + e size])", ids[0]);
        var expected = Enumerable.Range(1, count).Sum(i => ("element " + i).Length);
        Assert.Equal((long)expected, s.ToValue(result));
        s.Release(ids);
        Assert.Equal(3L, s.Evaluate<long>("1 + 2"));
    }

    [StoneTheory]
    [InlineData(1_000)]
    [InlineData(64 * 1024)]
    [InlineData(1024 * 1024)]
    [InlineData(8 * 1024 * 1024)]
    public void OneLargeByteObjectInOneStoreTraversal(int size)
    {
        using var s = StoneEnv.Login();
        var ids = s.GetFreeOops(1);
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i * 7);
        var result = s.StoreAndExecute([GciNewObject.ByteObject(ids[0], KnownClasses.ByteArray, bytes)],
            "{ self size. self inject: 0 into: [:sum :b | (sum + b) \\\\ 1000000007] }", ids[0]);
        var values = s.ToValues(s.FetchVaryingOops(result));
        Assert.Equal((long)size, values[0]);
        long sum = 0;
        foreach (var b in bytes) sum = (sum + b) % 1000000007;
        Assert.Equal(sum, values[1]);
        s.Release([result, .. ids]);
        Assert.Equal(3L, s.Evaluate<long>("1 + 2"));
    }

    [StoneTheory]
    [InlineData(1_000)]
    [InlineData(10_000)]
    [InlineData(50_000)]
    public void ManyFreeOopsAndOneRelease(int count)
    {
        using var s = StoneEnv.Login();
        var ids = s.GetFreeOops(count);
        Assert.Equal(count, ids.Distinct().Count());
        var objects = ids.Select(id => GciNewObject.Pointers(id, KnownClasses.Array, [])).ToList();
        var root = s.GetFreeOops(1)[0];
        objects.Insert(0, GciNewObject.Pointers(root, KnownClasses.Array, ids));
        var result = s.StoreAndExecute(objects, "self size", root);
        Assert.Equal((long)count, s.ToValue(result));
        s.Release([root, .. ids]);
        Assert.Equal(3L, s.Evaluate<long>("1 + 2"));
    }

    [StoneFact]
    public void FreeOopsBeyondTheLimitPerCall()
    {
        using var s = StoneEnv.Login();
        var ids = s.GetFreeOops(GciSession.MaxFreeOopsPerCall * 2 + 7);
        Assert.DoesNotContain(default(Oop), ids); // GciGetFreeOops alone leaves everything above 100,000 at 0
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }
}
