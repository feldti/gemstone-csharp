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
}
