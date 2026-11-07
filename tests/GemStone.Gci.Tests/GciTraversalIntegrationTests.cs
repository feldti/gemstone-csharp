namespace GemStone.Gci.Tests;

[Collection("Stone")]
public class GciTraversalIntegrationTests
{
    [StoneTheory]
    [InlineData("1.5", 1.5)]
    [InlineData("-2.25", -2.25)]
    [InlineData("0.0", 0.0)]
    [InlineData("1.0e-10", 1e-10)]
    [InlineData("3.14159", 3.14159)]
    [InlineData("1.0e30", 1e30)] // SmallDouble has an 8-bit exponent: 1.0e100 is a Float
    public void SmallDoubleDecodesLocally(string literal, double expected)
    {
        using var s = StoneEnv.Login();
        var oop = s.Execute(literal);
        Assert.True(oop.IsSmallDouble);
        Assert.Equal(expected, oop.ToSmallDouble());
        Assert.Equal(s.ToDouble(oop), oop.ToSmallDouble()); // same as the GCI conversion
    }

    [StoneFact]
    public void NegativeZeroKeepsItsSign()
    {
        using var s = StoneEnv.Login();
        var oop = s.Execute("-0.0");
        if (!oop.IsSmallDouble) return; // representation detail of the server
        Assert.True(BitConverter.DoubleToInt64Bits(oop.ToSmallDouble()) < 0); // sign bit (also for -0.0)
    }

    [StoneFact]
    public void TraverseRespectsLevels()
    {
        using var s = StoneEnv.Login();
        var root = s.Execute("Array with: 'abc' with: 42 with: (Array with: 'deep' with: #sym)");
        try
        {
            var level1 = s.Traverse(root, 1);
            Assert.Single(level1.Reports);
            var rootReport = level1[root];
            Assert.Equal(KnownClasses.Array, rootReport.Class);
            Assert.Equal(GciObjectFormat.Pointer, rootReport.Format);
            Assert.Equal(3, rootReport.IndexedSize);
            Assert.Equal(42L, level1.ToValue(rootReport.Oops[1]));
            Assert.IsType<Oop>(level1.ToValue(rootReport.Oops[0])); // 'abc' not reported at level 1

            var level2 = s.Traverse(root, 2);
            Assert.Equal(3, level2.Reports.Count); // root, 'abc', inner array
            Assert.Equal(["abc", 42L, level2[root].Oops[2]], level2.ToList(root));
            var inner = level2[root].Oops[2];
            Assert.IsType<Oop>(level2.ToList(inner)[0]); // 'deep' is level 3

            var all = s.Traverse(root, 0);
            Assert.Equal(["deep", "sym"], all.ToList(all[root].Oops[2]));
        }
        finally { s.Release(root); }
    }

    [StoneFact]
    public void ExecuteTraverseDecodesKernelClassesWithoutRoundTrips()
    {
        using var s = StoneEnv.Login();
        var t = s.ExecuteTraverse("""
            Array new
              add: 'latin1 äöü' asString;
              add: #aSymbol;
              add: (String new: 2) asUnicodeString; "two NUL characters"
              add: ((Unicode16 new: 2) at: 1 put: (Character codePoint: 16r65E5); at: 2 put: (Character codePoint: 16r672C); yourself);
              add: ((Unicode32 new: 1) at: 1 put: (Character codePoint: 16r1F600); yourself);
              add: 'utf8 ü' encodeAsUTF8;
              add: #[1 2 255];
              add: 1.0e300;
              add: 2.5;
              add: $x;
              add: nil;
              add: true;
              yourself
            """, 2);

        Assert.NotNull(t.Result);
        Assert.Equal(
            ["latin1 äöü", "aSymbol", "\0\0", "日本", "😀", "utf8 ü", new byte[] { 1, 2, 255 }, 1e300, 2.5, new System.Text.Rune('x'), null, true],
            t.ToList(t.Result!.Value));
    }

    [StoneFact]
    public void NonAsciiSourceAlsoWorks()
    {
        using var s = StoneEnv.Login();
        var t = s.ExecuteTraverse("Array with: 'Grüße' size with: 'ok'", 2);
        Assert.Equal([5L, "ok"], t.ToList(t.Result!.Value));
    }

    [StoneFact]
    public void LargeGraphsNeedSeveralBuffers()
    {
        using var s = StoneEnv.Login();
        var t = s.ExecuteTraverse("(1 to: 3000) collect: [:i | i printString]", 2,
            new GciTraversalOptions { BufferSize = 4096 }); // forces many GciMoreTraversal calls

        Assert.Equal(3001, t.Reports.Count);
        Assert.Equal(Enumerable.Range(1, 3000).Select(i => (object?)i.ToString()), t.ToList(t.Result!.Value));
    }

    [StoneFact]
    public void PerformTraverse()
    {
        using var s = StoneEnv.Login();
        var array = s.Execute("Array with: 'a' with: 'b'");
        try
        {
            var t = s.PerformTraverse(array, "reverse", 2);
            Assert.Equal(["b", "a"], t.ToList(t.Result!.Value));
            var t2 = s.PerformTraverse(array, "copyWith:", 2, s.NewString("c"));
            Assert.Equal(["a", "b", "c"], t2.ToList(t2.Result!.Value));
        }
        finally { s.Release(array); }
    }

    [StoneFact]
    public void ScalarResultsAndErrors()
    {
        using var s = StoneEnv.Login();
        var t = s.ExecuteTraverse("3 + 4", 1);
        Assert.Equal(Oop.FromSmallInteger(7), t.Result);
        Assert.Equal(7L, t.ToValue(t.Result!.Value));

        Assert.Throws<GciException>(() => s.ExecuteTraverse("1/0", 1));
        Assert.Equal(2L, s.Evaluate("1 + 1"));
    }

    [StoneFact]
    public void FetchPathsFollowsNamedAndIndexedSteps()
    {
        using var s = StoneEnv.Login();
        var objects = s.Execute("""
            Array
              with: (Association key: #a value: (Association key: 'inner' value: (Array with: 10 with: 20)))
              with: (Association key: #b value: nil)
            """);
        try
        {
            var roots = s.FetchVaryingOops(objects);
            int[] key = [GciPath.Named(1)];
            int[] innerKey = [GciPath.Named(2), GciPath.Named(1)];
            int[] secondOfArray = [GciPath.Named(2), GciPath.Named(2), GciPath.Indexed(2)];

            var r = s.FetchPaths(roots, key, innerKey, secondOfArray);

            Assert.Equal(2, r.Length);
            Assert.Equal(s.Execute("#a"), r[0][0]);
            Assert.Equal("inner", s.ToStringValue(r[0][1]));
            Assert.Equal(Oop.FromSmallInteger(20), r[0][2]);
            Assert.Equal(s.Execute("#b"), r[1][0]);
            Assert.Equal(Oop.Illegal, r[1][1]); // value is nil: path cannot be followed
            Assert.Equal(Oop.Illegal, r[1][2]);
            Assert.Equal(1L, s.Evaluate("1")); // no stale error left behind
        }
        finally { s.Release(objects); }
    }

    [StoneFact]
    public void TraversalsDoNotGrowTheExportSetUnlessAsked()
    {
        using var s = StoneEnv.Login();
        long ExportSetSize() => s.Evaluate<long>("System hiddenSetSize: (System HiddenSetSpecifiers indexOf: 'PureExportSet')");
        const string source = "(1 to: 50) collect: [:i | i printString]";

        ExportSetSize();
        var before = ExportSetSize();
        for (var i = 0; i < 5; i++) s.ExecuteTraverse(source, 2);
        var afterPlain = ExportSetSize();

        var array = s.Execute("#(1 2 3)");
        for (var i = 0; i < 5; i++) s.PerformTraverse(array, "reverse", 2);
        var afterPerform = ExportSetSize();

        var kept = s.ExecuteTraverse(source, 2, new GciTraversalOptions { KeepResult = true });
        var afterKeep = ExportSetSize();
        s.Release(kept.Result!.Value, array);
        var afterRelease = ExportSetSize();

        Assert.InRange(afterPlain - before, 0, 1);
        Assert.InRange(afterPerform - afterPlain, 1, 2); // just the receiver array
        Assert.Equal(1, afterKeep - afterPerform);       // the kept result
        Assert.InRange(afterRelease - afterPlain, 0, 1);
    }
}
