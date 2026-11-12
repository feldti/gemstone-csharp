using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GemStone.Gci.Generators;

/// <summary>
/// Emits <c>GemStone.Gci.Generated.GsGeneratedMappings</c>: the mapping metadata of all
/// <c>[GsClass]</c> types of the project, the types of their mapped properties and the
/// <c>T</c> of <c>GsObjectContext.Load&lt;T&gt;</c> / <c>Validate(typeof(T))</c> calls, as plain
/// code (no reflection, trimming- and AOT-safe).
/// </summary>
/// <remarks>
/// Members the generated class cannot reach (private setters, init-only setters, non-public
/// constructors) go through <c>UnsafeAccessor</c> on .NET 8 and later, and through reflection on
/// .NET Framework (which is neither trimmed nor compiled ahead of time).
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class GsMappingGenerator : IIncrementalGenerator
{
    internal const string ContextType = "GemStone.Gci.Mapping.GsObjectContext";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var roots = context.SyntaxProvider
            .CreateSyntaxProvider(static (node, _) => IsCandidate(node), static (ctx, ct) => RootType(ctx, ct))
            .Where(static t => t is not null)
            .Collect();
        context.RegisterSourceOutput(context.CompilationProvider.Combine(roots),
            static (spc, pair) => new Emitter(pair.Left, spc).Run(pair.Right!));
    }

    private static bool IsCandidate(SyntaxNode node) =>
        node is InvocationExpressionSyntax inv && NameOf(inv.Expression) switch
        {
            GenericNameSyntax g => g.Identifier.ValueText == "Load" && g.TypeArgumentList.Arguments.Count == 1,
            IdentifierNameSyntax i => i.Identifier.ValueText == "Validate" && inv.ArgumentList.Arguments.Count == 1
                                      && inv.ArgumentList.Arguments[0].Expression is TypeOfExpressionSyntax,
            _ => false,
        };

    private static SimpleNameSyntax? NameOf(ExpressionSyntax e) => e switch
    {
        MemberAccessExpressionSyntax m => m.Name,
        MemberBindingExpressionSyntax b => b.Name,
        SimpleNameSyntax s => s,
        _ => null,
    };

    private static ITypeSymbol? RootType(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var inv = (InvocationExpressionSyntax)ctx.Node;
        if (ctx.SemanticModel.GetSymbolInfo(inv, ct).Symbol is not IMethodSymbol method
            || method.ContainingType?.ToDisplayString() != ContextType)
            return null;
        if (method.Name == "Load" && method.TypeArguments.Length == 1)
            return method.TypeArguments[0];
        if (method.Name == "Validate" && inv.ArgumentList.Arguments[0].Expression is TypeOfExpressionSyntax t)
            return ctx.SemanticModel.GetTypeInfo(t.Type, ct).Type;
        return null;
    }
}

internal static class Diagnostics
{
    public static readonly DiagnosticDescriptor Skipped = new(
        "GSGEN001", "Type skipped by the GemStone mapping generator",
        "'{0}' is not part of the generated mappings: {1}", "GemStone.Gci.Mapping",
        DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor Problem = new(
        "GSGEN002", "Mapping problem",
        "{0}", "GemStone.Gci.Mapping", DiagnosticSeverity.Warning, isEnabledByDefault: true);
}

/// <summary>Collects the types to describe and writes the provider class.</summary>
internal sealed class Emitter
{
    private const string Meta = "global::GemStone.Gci.Mapping";

    private static readonly SymbolDisplayFormat Format = SymbolDisplayFormat.FullyQualifiedFormat;

    private static readonly HashSet<string> ScalarNames =
    [
        "System.Numerics.BigInteger", "System.Text.Rune", "System.DateTimeOffset", "System.DateOnly", "System.TimeOnly",
        "GemStone.Gci.Oop",
    ];

    private static readonly HashSet<string> ListTypes =
    [
        "System.Collections.Generic.List<T>", "System.Collections.Generic.IList<T>", "System.Collections.Generic.ICollection<T>",
        "System.Collections.Generic.IEnumerable<T>", "System.Collections.Generic.IReadOnlyList<T>",
        "System.Collections.Generic.IReadOnlyCollection<T>",
    ];

    private static readonly HashSet<string> SetTypes =
        ["System.Collections.Generic.HashSet<T>", "System.Collections.Generic.ISet<T>", "System.Collections.Generic.IReadOnlySet<T>"];

    private static readonly HashSet<string> DictionaryTypes =
    [
        "System.Collections.Generic.Dictionary<TKey, TValue>", "System.Collections.Generic.IDictionary<TKey, TValue>",
        "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>",
    ];

    private readonly Compilation _compilation;
    private readonly SourceProductionContext _spc;
    private readonly INamedTypeSymbol? _gsClass, _gsInstVar, _gsIgnore, _gsOop, _gsReadOnly, _gsCollection, _gsDictionaryKey, _gsConverter, _converter, _oop;
    private readonly List<INamedTypeSymbol> _mapped = [];
    private readonly HashSet<string> _seen = [];
    private readonly Queue<ITypeSymbol> _todo = new();
    private readonly List<(string Type, string Factory)> _entries = [];
    private readonly StringBuilder _accessors = new();
    private int _next;

    public Emitter(Compilation compilation, SourceProductionContext spc)
    {
        _compilation = compilation;
        _spc = spc;
        INamedTypeSymbol? T(string name) => compilation.GetTypeByMetadataName(name);
        _gsClass = T("GemStone.Gci.Mapping.GsClassAttribute");
        _gsInstVar = T("GemStone.Gci.Mapping.GsInstVarAttribute");
        _gsIgnore = T("GemStone.Gci.Mapping.GsIgnoreAttribute");
        _gsOop = T("GemStone.Gci.Mapping.GsOopAttribute");
        _gsReadOnly = T("GemStone.Gci.Mapping.GsReadOnlyAttribute");
        _gsCollection = T("GemStone.Gci.Mapping.GsCollectionAttribute");
        _gsDictionaryKey = T("GemStone.Gci.Mapping.GsDictionaryKeyAttribute");
        _gsConverter = T("GemStone.Gci.Mapping.GsConverterAttribute");
        _converter = T("GemStone.Gci.Mapping.IGsValueConverter");
        _oop = T("GemStone.Gci.Oop");
    }

    public void Run(ImmutableArray<ITypeSymbol?> roots)
    {
        // Not referencing GemStone.Gci (or an older version without the metadata API): nothing to do.
        if (_gsClass is null || _compilation.GetTypeByMetadataName("GemStone.Gci.Mapping.GsTypeMetadata") is null) return;

        CollectMapped(_compilation.Assembly.GlobalNamespace);
        foreach (var t in _mapped) _todo.Enqueue(t);
        foreach (var t in roots) _todo.Enqueue(t!);
        while (_todo.Count > 0)
        {
            _spc.CancellationToken.ThrowIfCancellationRequested();
            Describe(_todo.Dequeue());
        }
        _spc.AddSource("GsGeneratedMappings.g.cs", Source());
    }

    private void CollectMapped(INamespaceOrTypeSymbol container)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceSymbol ns) CollectMapped(ns);
            else if (member is INamedTypeSymbol type)
            {
                if (type.TypeKind == TypeKind.Class && Attribute(type, _gsClass) is not null) _mapped.Add(type);
                CollectMapped(type);
            }
        }
    }

    // ---- Types ---------------------------------------------------------------------------------

    private void Describe(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        if (HasTypeParameter(type) || type.TypeKind == TypeKind.Error || IsSimple(type)) return;

        var name = Name(type);
        if (!_seen.Add(name)) return;
        var isMapped = type is INamedTypeSymbol { TypeKind: TypeKind.Class } c && Attribute(c, _gsClass) is not null;
        if (!_compilation.IsSymbolAccessibleWithin(type, _compilation.Assembly))
        {
            if (isMapped) Report(Diagnostics.Skipped, type, type.Name, "it is not accessible from the generated class (make it internal or public)");
            return;
        }

        if (type is IArrayTypeSymbol { Rank: 1 } array)
        {
            _entries.Add((name, $"{Meta}.GsTypeMetadata.Array<{Name(array.ElementType)}>()"));
            _todo.Enqueue(array.ElementType);
            return;
        }
        if (type is not INamedTypeSymbol named) return;

        if (named.IsGenericType)
        {
            var def = named.OriginalDefinition.ToDisplayString();
            var args = named.TypeArguments;
            string? factory = ListTypes.Contains(def) ? "List" : SetTypes.Contains(def) ? "Set" : DictionaryTypes.Contains(def) ? "Dictionary" : null;
            if (factory is not null)
            {
                _entries.Add((name, $"{Meta}.GsTypeMetadata.{factory}<{string.Join(", ", args.Select(Name))}>(typeof({name}))"));
                foreach (var a in args) _todo.Enqueue(a);
                return;
            }
        }

        if (isMapped)
        {
            if (named.IsGenericType)
                Report(Diagnostics.Skipped, named, named.Name, "generic [GsClass] types are not supported by the generator");
            else
                DescribeClass(named, name);
        }
    }

    private bool IsSimple(ITypeSymbol type) =>
        type.SpecialType is SpecialType.System_Object or SpecialType.System_Boolean or SpecialType.System_SByte or SpecialType.System_Byte
            or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double
            or SpecialType.System_Decimal or SpecialType.System_Char or SpecialType.System_String or SpecialType.System_DateTime
        || type.TypeKind == TypeKind.Enum
        || type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte }
        || ScalarNames.Contains(type.ToDisplayString());

    private void DescribeClass(INamedTypeSymbol type, string name)
    {
        var problems = new List<(string Text, ISymbol At)>();
        var n = _next++;

        // Constructor
        string create;
        var ctor = type.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0);
        if (type.IsAbstract) create = "null";
        else if (ctor is null)
        {
            create = "null";
            problems.Add(($"{type.Name}: needs a parameterless constructor.", type));
        }
        else if (Accessible(ctor)) create = $"() => new {name}()";
        else
        {
            create = $"() => C{n}()";
            Accessor($"C{n}", "Constructor", ".ctor", name, [],
                $"({name})global::System.Activator.CreateInstance(typeof({name}), true)");
        }

        // Properties, inherited ones from their declaring type; the most derived declaration wins.
        var properties = new List<string>();
        var setOop = "null";
        var names = new HashSet<string>();
        var p = 0;
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (var prop in t.GetMembers().OfType<IPropertySymbol>())
            {
                if (prop.IsStatic || prop.IsIndexer || prop.ExplicitInterfaceImplementations.Length > 0 || !names.Add(prop.Name)) continue;
                if (Attribute(prop, _gsIgnore) is not null) continue;
                var where = $"{type.Name}.{prop.Name}";
                var owner = Name(prop.ContainingType);
                var valueType = Name(prop.Type);
                var id = $"P{n}_{p++}";

                if (Attribute(prop, _gsOop) is not null)
                {
                    if (!SymbolEqualityComparer.Default.Equals(prop.Type, _oop)) problems.Add(($"{where}: [GsOop] requires type Oop.", prop));
                    else if (prop.SetMethod is null) problems.Add(($"{where}: [GsOop] property needs a setter.", prop));
                    else if (NeedsUnsupportedAccessor(prop, false))
                        problems.Add(($"{where}: the generator cannot reach a non-public setter in the generic class {prop.ContainingType.Name}.", prop));
                    else setOop = $"(o, v) => {Setter(prop, id, owner, valueType)}";
                    continue;
                }
                if (Attribute(prop, _gsInstVar) is not { } iv) continue;
                if (prop.SetMethod is null)
                {
                    problems.Add(($"{where}: mapped property needs a setter (set or init).", prop));
                    continue;
                }
                if (prop.GetMethod is null)
                {
                    problems.Add(($"{where}: mapped property needs a getter.", prop));
                    continue;
                }
                if (!_compilation.IsSymbolAccessibleWithin(prop.Type, _compilation.Assembly))
                {
                    problems.Add(($"{where}: the property type is not accessible from the generated class.", prop));
                    continue;
                }
                if (NeedsUnsupportedAccessor(prop, true))
                {
                    problems.Add(($"{where}: the generator cannot reach a non-public accessor in the generic class {prop.ContainingType.Name}.", prop));
                    continue;
                }

                var converter = "null";
                if (Attribute(prop, _gsConverter) is { } conv)
                {
                    var convType = conv.ConstructorArguments.FirstOrDefault().Value as INamedTypeSymbol;
                    if (convType is null || convType.IsAbstract || !convType.AllInterfaces.Contains(_converter!, SymbolEqualityComparer.Default)
                        || !convType.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public)
                        || !_compilation.IsSymbolAccessibleWithin(convType, _compilation.Assembly))
                        problems.Add(($"{where}: converter {convType?.Name} must implement IGsValueConverter and have a public parameterless constructor.", prop));
                    else
                        converter = $"new {Name(convType)}()";
                }
                else
                {
                    _todo.Enqueue(prop.Type);
                }

                var kind = Attribute(prop, _gsCollection)?.ConstructorArguments.FirstOrDefault().Value is int k
                    ? $"({Meta}.GsCollectionKind){k}" : "null";
                var readOnly = Attribute(prop, _gsReadOnly) is not null ? "true" : "false";
                var keySelector = Attribute(prop, _gsDictionaryKey)?.ConstructorArguments.FirstOrDefault().Value is string ks
                    ? Literal(ks) : "null";
                var instVar = (string?)iv.ConstructorArguments.FirstOrDefault().Value ?? "";
                properties.Add($"{Meta}.GsPropertyMetadata.Create<{owner}, {valueType}>({Literal(prop.Name)}, {Literal(instVar)}, " +
                               $"o => {Getter(prop, id, owner, valueType)}, (o, v) => {Setter(prop, id, owner, valueType)}, {kind}, {readOnly}, {converter}, {keySelector})");
            }
        }

        // C# subclasses with their own [GsClass] in this project, most derived first.
        var subtypes = _mapped.Where(s => !SymbolEqualityComparer.Default.Equals(s, type) && DerivesFrom(s, type))
            .OrderByDescending(Depth).ToList();
        foreach (var s in subtypes) _todo.Enqueue(s);

        foreach (var (text, at) in problems) Report(Diagnostics.Problem, at, text);

        var cls = Attribute(type, _gsClass)!;
        var clsName = (string?)cls.ConstructorArguments.FirstOrDefault().Value ?? "";
        var dict = cls.NamedArguments.FirstOrDefault(a => a.Key == "Dictionary").Value.Value as string;
        // Name and dictionary as arguments: an object initializer on GsClassAttribute (init-only) needs C# 9.
        var attr = $"{Literal(clsName)}, {(dict is null ? "null" : Literal(dict))}";

        var sb = new StringBuilder();
        sb.Append($"{Meta}.GsTypeMetadata.Class<{name}>(\n                {attr},\n                {create},\n");
        sb.Append($"                new {Meta}.GsPropertyMetadata[]\n                {{\n");
        foreach (var prop in properties) sb.Append($"                    {prop},\n");
        sb.Append("                },\n");
        sb.Append(subtypes.Count == 0 ? "                new global::System.Type[0],\n"
            : $"                new global::System.Type[] {{ {string.Join(", ", subtypes.Select(s => $"typeof({Name(s)})"))} }},\n");
        sb.Append($"                {setOop},\n");
        sb.Append(problems.Count == 0 ? "                null)"
            : $"                new string[] {{ {string.Join(", ", problems.Select(x => Literal(x.Text)))} }})");
        _entries.Add((name, sb.ToString()));
    }

    // ---- Member access ---------------------------------------------------------------------------

    private bool Accessible(ISymbol symbol) => _compilation.IsSymbolAccessibleWithin(symbol, _compilation.Assembly);

    private string Getter(IPropertySymbol prop, string id, string owner, string valueType)
    {
        if (Accessible(prop.GetMethod!)) return $"o.{Identifier(prop.Name)}";
        var method = SharedAccessor(prop.GetMethod!, $"{id}_get", m => Accessor(m, "Method", prop.GetMethod!.Name, valueType, [(owner, "o")],
            $"({valueType})typeof({owner}).GetProperty({Literal(prop.Name)}, {Flags}).GetValue(o)"));
        return $"{method}(o)";
    }

    private string Setter(IPropertySymbol prop, string id, string owner, string valueType)
    {
        if (Accessible(prop.SetMethod!) && !prop.SetMethod!.IsInitOnly) return $"o.{Identifier(prop.Name)} = v";
        var method = SharedAccessor(prop.SetMethod!, $"{id}_set", m => Accessor(m, "Method", prop.SetMethod!.Name, "void", [(owner, "o"), (valueType, "v")],
            $"typeof({owner}).GetProperty({Literal(prop.Name)}, {Flags}).SetValue(o, v)"));
        return $"{method}(o, v)";
    }

    private readonly Dictionary<IMethodSymbol, string> _accessorNames = new(SymbolEqualityComparer.Default);

    /// <summary>One accessor per property accessor, also when several mapped subclasses inherit it.</summary>
    private string SharedAccessor(IMethodSymbol target, string name, Action<string> emit)
    {
        if (_accessorNames.TryGetValue(target, out var known)) return known;
        _accessorNames[target] = name;
        emit(name);
        return name;
    }

    private const string Flags =
        "global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.Public | " +
        "global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.DeclaredOnly";

    /// <summary>UnsafeAccessor on .NET 8 and later, reflection on .NET Framework.</summary>
    private void Accessor(string method, string kind, string member, string returns, (string Type, string Name)[] parameters, string fallback)
    {
        var list = string.Join(", ", parameters.Select(x => $"{x.Type} {x.Name}"));
        _accessors.Append("#if NET8_0_OR_GREATER\n");
        _accessors.Append($"        [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.{kind}" +
                          (kind == "Constructor" ? "" : $", Name = {Literal(member)}") + ")]\n");
        _accessors.Append($"        private static extern {returns} {method}({list});\n");
        _accessors.Append("#else\n");
        _accessors.Append($"        private static {returns} {method}({list}) {(returns == "void" ? "{ " + fallback + "; }" : "{ return " + fallback + "; }")}\n");
        _accessors.Append("#endif\n");
    }

    // ---- Output ------------------------------------------------------------------------------

    private string Source()
    {
        var sb = new StringBuilder();
        sb.Append("""
            // <auto-generated/>
            // Generated by the GemStone.Gci mapping source generator (package Gs64Net.Gci).

            namespace GemStone.Gci.Generated
            {
                /// <summary>
                /// Mapping metadata of the [GsClass] types of this project, generated at compile time: pass
                /// <see cref="Default"/> to the GsObjectContext constructor for a context without reflection
                /// (trimming- and AOT-safe).
                /// </summary>
                [global::System.CodeDom.Compiler.GeneratedCode("GemStone.Gci.Generators", "1.0")]
                internal sealed class GsGeneratedMappings : global::GemStone.Gci.Mapping.IGsMappingProvider
                {
                    /// <summary>The mappings of this project.</summary>
                    public static GsGeneratedMappings Default { get; } = new GsGeneratedMappings();

                    /// <inheritdoc/>
                    public global::GemStone.Gci.Mapping.GsTypeMetadata GetTypeMetadata(global::System.Type type)
                    {

            """);
        for (var i = 0; i < _entries.Count; i++)
            sb.Append($"            if (type == typeof({_entries[i].Type})) return M{i}();\n");
        sb.Append("            return null;\n        }\n");
        for (var i = 0; i < _entries.Count; i++)
            sb.Append($"\n        private static global::GemStone.Gci.Mapping.GsTypeMetadata M{i}() =>\n            {_entries[i].Factory};\n");
        if (_accessors.Length > 0) sb.Append('\n').Append(_accessors);
        sb.Append("    }\n}\n");
        return sb.ToString();
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static string Name(ITypeSymbol type) => type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(Format);

    private static string Literal(string s) => SymbolDisplay.FormatLiteral(s, quote: true);

    private static string Identifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    private static AttributeData? Attribute(ISymbol symbol, INamedTypeSymbol? attribute) =>
        attribute is null ? null : symbol.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute));

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, baseType)) return true;
        return false;
    }

    private static bool HasTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol a => HasTypeParameter(a.ElementType),
        INamedTypeSymbol n => n.TypeArguments.Any(HasTypeParameter),
        _ => false,
    };

    /// <summary>UnsafeAccessor does not support generic declaring types on .NET 8.</summary>
    private bool NeedsUnsupportedAccessor(IPropertySymbol prop, bool needsGetter) =>
        prop.ContainingType.IsGenericType
        && ((needsGetter && !Accessible(prop.GetMethod!)) || !Accessible(prop.SetMethod!) || prop.SetMethod!.IsInitOnly);

    private static int Depth(INamedTypeSymbol type)
    {
        var d = 0;
        for (var t = type.BaseType; t is not null; t = t.BaseType) d++;
        return d;
    }

    private void Report(DiagnosticDescriptor descriptor, ISymbol at, params object[] args) =>
        _spc.ReportDiagnostic(Diagnostic.Create(descriptor, at.Locations.FirstOrDefault(l => l.IsInSource), args));
}
