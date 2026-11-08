using System.Collections.Immutable;
using GemStone.Gci.Mapping;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GemStone.Gci.Generators.Tests;

/// <summary>Runs the generator on small sources and compiles the result.</summary>
public sealed class GsMappingGeneratorTests
{
    private static readonly string GemStoneGci = typeof(GsObjectContext).Assembly.Location;

    /// <summary>The runtime of the test host, without GemStone.Gci (it is among the trusted assemblies).</summary>
    private static readonly ImmutableArray<MetadataReference> Runtime =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) != Path.GetFileName(GemStoneGci))
            .Select(p => MetadataReference.CreateFromFile(p)),
    ];

    /// <summary>Generator output and the diagnostics of compiling source plus output.</summary>
    private static (string Generated, ImmutableArray<Diagnostic> GeneratorDiagnostics, ImmutableArray<Diagnostic> Errors) Run(
        string source, LanguageVersion version = LanguageVersion.Latest, string[]? symbols = null, bool withGemStone = true)
    {
        var parse = new CSharpParseOptions(version, preprocessorSymbols: symbols ?? ["NET8_0_OR_GREATER"]);
        var compilation = CSharpCompilation.Create("Sample", [CSharpSyntaxTree.ParseText(source, parse)],
            withGemStone ? [.. Runtime, MetadataReference.CreateFromFile(GemStoneGci)] : Runtime,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var driver = CSharpGeneratorDriver.Create([new GsMappingGenerator().AsSourceGenerator()], parseOptions: parse)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        var generated = driver.GetRunResult().GeneratedTrees.FirstOrDefault()?.ToString() ?? "";
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        return (generated, diagnostics, errors);
    }

    private const string Sample = """
        using System.Collections.Generic;
        using GemStone.Gci;
        using GemStone.Gci.Mapping;

        namespace Shop;

        public enum Kind { Plain, Fancy }

        [GsClass("Customer", Dictionary = "UserGlobals")]
        public class Customer
        {
            protected Customer() { }
            [GsOop] public Oop Oop { get; private set; }
            [GsInstVar("name")] public string? Name { get; init; }
            [GsInstVar("kind")] public Kind Kind { get; set; }
            [GsInstVar("orders"), GsCollection(GsCollectionKind.Ordered)] public IReadOnlyList<Order> Orders { get; set; } = [];
            [GsInstVar("tags")] public Dictionary<string, List<string>>? Tags { get; set; }
            [GsInstVar("class")] public long? @class { get; set; }
            [GsIgnore, GsInstVar("ignored")] public int Ignored { get; set; }
        }

        [GsClass("VipCustomer")]
        public sealed class VipCustomer : Customer
        {
            [GsInstVar("level")] public int Level { get; internal set; }
        }

        [GsClass("Order")]
        public sealed class Order
        {
            [GsInstVar("lines")] public Line[]? Lines { get; set; }
        }

        [GsClass("Line")]
        public sealed class Line
        {
            [GsInstVar("qty")] public decimal Qty { get; set; }
        }

        public static class Use
        {
            public static void Run(GsObjectContext ctx)
            {
                ctx.Load<HashSet<Customer>>("x");
                ctx.Validate(typeof(Dictionary<long, Order>));
            }

            public static T Generic<T>(GsObjectContext ctx) => ctx.Load<T>("x"); // no type to generate
        }
        """;

    [Fact]
    public void GeneratesCompilableMetadata()
    {
        var (generated, diagnostics, errors) = Run(Sample);
        Assert.Empty(diagnostics);
        Assert.Empty(errors);
        Assert.Contains("internal sealed class GsGeneratedMappings", generated);
        Assert.Contains("GsTypeMetadata.Class<global::Shop.Customer>(\n                \"Customer\", \"UserGlobals\",", generated);
        // Roots from Load<T> and Validate(typeof(T)), and the types of mapped properties.
        Assert.Contains("typeof(global::System.Collections.Generic.HashSet<global::Shop.Customer>)", generated);
        Assert.Contains("typeof(global::System.Collections.Generic.Dictionary<long, global::Shop.Order>)", generated);
        Assert.Contains("GsTypeMetadata.List<global::Shop.Order>(typeof(global::System.Collections.Generic.IReadOnlyList<global::Shop.Order>))", generated);
        Assert.Contains("typeof(global::System.Collections.Generic.List<string>)", generated);
        Assert.Contains("GsTypeMetadata.Array<global::Shop.Line>()", generated);
        // Subtypes, most derived first.
        Assert.Contains("new global::System.Type[] { typeof(global::Shop.VipCustomer) }", generated);
        // Private constructor, private and init-only setters go through UnsafeAccessor on .NET 8+.
        Assert.Contains("UnsafeAccessorKind.Constructor", generated);
        Assert.Contains("Name = \"set_Oop\"", generated);
        Assert.Contains("Name = \"set_Name\"", generated);
        // internal set is accessible within the assembly: direct access.
        Assert.Contains("(o, v) => o.Level = v", generated);
        Assert.Contains("o => o.@class", generated);
        Assert.DoesNotContain("ignored", generated);
    }

    [Fact]
    public void CompilesAsCSharp73WithoutNet8()
    {
        // .NET Framework projects default to C# 7.3; without NET8_0_OR_GREATER the accessors use reflection.
        var source = Sample.Replace("namespace Shop;", "namespace Shop {") + "}";
        source = source.Replace("= [];", "= new List<Order>();").Replace("string? Name { get; init; }", "string Name { get; private set; }");
        var (generated, _, errors) = Run(source, LanguageVersion.CSharp7_3, symbols: []);
        Assert.Contains("GetProperty(\"Name\"", generated);
        Assert.Empty(errors.Where(e => e.Location.SourceTree?.FilePath.EndsWith(".g.cs") == true));
    }

    [Fact]
    public void ReportsMappingProblems()
    {
        var (generated, diagnostics, errors) = Run("""
            using GemStone.Gci.Mapping;

            public sealed class NotAConverter { }

            [GsClass("Bad")]
            public sealed class Bad
            {
                public Bad(int x) { }
                [GsInstVar("a")] public int GetOnly { get; }
                [GsOop] public long WrongOop { get; set; }
                [GsInstVar("c"), GsConverter(typeof(NotAConverter))] public string? C { get; set; }
            }

            public class Outer
            {
                [GsClass("Hidden")] private sealed class Hidden { }
                [GsClass("Gen")] public sealed class Gen<T> { }
                public void Use(GsObjectContext ctx) => ctx.Load<Gen<int>>("x");
            }
            """);
        Assert.Empty(errors);
        var problems = diagnostics.Where(d => d.Id == "GSGEN002").Select(d => d.GetMessage()).ToList();
        Assert.Contains("Bad: needs a parameterless constructor.", problems);
        Assert.Contains("Bad.GetOnly: mapped property needs a setter (set or init).", problems);
        Assert.Contains("Bad.WrongOop: [GsOop] requires type Oop.", problems);
        Assert.Contains("Bad.C: converter NotAConverter must implement IGsValueConverter and have a public parameterless constructor.", problems);
        Assert.All(diagnostics.Where(d => d.Id == "GSGEN002"), d => Assert.True(d.Location.IsInSource));
        // The same texts reach the runtime (GsMappingException on first use).
        Assert.Contains("\"Bad: needs a parameterless constructor.\"", generated);

        var skipped = diagnostics.Where(d => d.Id == "GSGEN001").Select(d => d.GetMessage()).ToList();
        Assert.Contains(skipped, m => m.StartsWith("'Hidden'") && m.Contains("not accessible"));
        Assert.Contains(skipped, m => m.StartsWith("'Gen'") && m.Contains("generic"));
    }

    [Fact]
    public void NothingWithoutTheLibrary()
    {
        var (generated, diagnostics, _) = Run("public class C { }", withGemStone: false);
        Assert.Equal("", generated);
        Assert.Empty(diagnostics);
    }
}
