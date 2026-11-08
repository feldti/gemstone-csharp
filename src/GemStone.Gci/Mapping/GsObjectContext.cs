using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace GemStone.Gci.Mapping;

/// <summary>Options for <see cref="GsObjectContext.Load{T}(Oop, GsLoadOptions?)"/>.</summary>
public sealed record GsLoadOptions
{
    /// <summary>
    /// Traversal depth per round trip. Only a performance setting: whatever the mapping needs
    /// below this depth is fetched in further round trips. Larger values mean fewer round trips
    /// but may fetch unmapped parts of the graph.
    /// </summary>
    public int Depth { get; init; } = 4;

    /// <summary>Traversal buffer size, see <see cref="GciTraversalOptions.BufferSize"/>.</summary>
    public int BufferSize { get; init; } = 256 * 1024;

    /// <summary>
    /// Remember the loaded state for <see cref="GsObjectContext.SaveChanges"/> and keep the loaded
    /// objects in the export set until the context is disposed (one extra round trip per load).
    /// Turn off for pure reading.
    /// </summary>
    public bool TrackChanges { get; init; } = true;
}

/// <summary>Mapping errors: either the mapping does not fit the image, or a value does not fit its property.</summary>
public sealed class GsMappingException(string message, IReadOnlyList<string> problems) : Exception(message)
{
    public IReadOnlyList<string> Problems { get; } = problems;

    internal static GsMappingException For(IReadOnlyList<string> problems) =>
        new(problems.Count == 1 ? problems[0] : $"{problems.Count} mapping problems:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", problems), problems);

    internal static GsMappingException For(string problem) => For([problem]);
}

/// <summary>
/// Loads GemStone object graphs into C# objects mapped with <see cref="GsClassAttribute"/> and
/// <see cref="GsInstVarAttribute"/>, tracks changes and writes them back with <see cref="SaveChanges"/>.
/// </summary>
/// <remarks>
/// <para>
/// A load starts with one traversal from the root; the mapping then decides which objects are
/// needed. Missing parts are fetched in batches (one traversal per round), hashed collections
/// (Set, Bag, dictionaries) are flattened on the server in one extra round trip per round.
/// </para>
/// <para>
/// The context keeps an identity map: within a context every GemStone object becomes exactly one
/// C# object (shared references and cycles are preserved); loading it again refreshes that
/// instance (and discards unsaved local changes). Mapping metadata is validated against the image
/// on first use of a type. Tracked objects are kept in the export set until <see cref="Dispose"/>
/// or <see cref="Clear"/>. Not thread-safe.
/// </para>
/// </remarks>
[RequiresUnreferencedCode("The object mapper inspects mapped types by reflection.")]
[RequiresDynamicCode("The object mapper creates generic collection types at runtime.")]
public sealed partial class GsObjectContext
{
    private readonly GciSession _session;
    private GsShapeFactory _shapes = new();
    private readonly Dictionary<Oop, object> _identity = [];
    private readonly Dictionary<object, Oop> _oops = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(Oop Sub, Oop Super), bool> _subclassCache = [];
    private readonly Dictionary<Oop, CollectionClass> _collectionCache = [];
    private readonly Dictionary<string, Oop> _kernelClasses = [];
    private readonly Dictionary<Oop, string> _classNames = [];

    public GsObjectContext(GciSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Loads the object graph below <paramref name="root"/> as <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">A [GsClass] type, a collection of them (<c>List&lt;T&gt;</c>, arrays,
    /// <c>HashSet&lt;T&gt;</c>, <c>Dictionary&lt;K,V&gt;</c>) or a scalar type.</typeparam>
    /// <exception cref="GsMappingException">The mapping does not fit the image or the data.</exception>
    public T Load<T>(Oop root, GsLoadOptions? options = null)
    {
        options ??= new GsLoadOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Depth, 1, nameof(options));
        var shape = Prepare(typeof(T));
        var load = new LoadRun(this, options, callerOwnsRoot: !_ownRoot);
        load.Discover(root, shape);
        var result = load.Build(root, shape, "root");
        return result is null && default(T) is not null
            ? throw GsMappingException.For($"root: nil cannot be loaded as {typeof(T)}.")
            : (T)result!;
    }

    /// <summary>Executes Smalltalk and loads the result (see <see cref="Load{T}(Oop, GsLoadOptions?)"/>).</summary>
    public T Load<T>(string source, GsLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var root = _session.Execute(source);
        var keep = false;
        try
        {
            _ownRoot = true;
            var result = Load<T>(root, options);
            keep = _retained.Contains(root); // tracked: the context owns the result now
            return result;
        }
        finally
        {
            _ownRoot = false;
            if (!keep) _session.Release(root);
        }
    }

    private bool _ownRoot;

    /// <summary>OOP of an object loaded by this context.</summary>
    public bool TryGetOop(object entity, out Oop oop) => _oops.TryGetValue(entity, out oop);

    /// <summary>
    /// Forgets all loaded objects (identity map and tracked state) and releases their OOPs;
    /// mapping metadata stays cached. Use after an abort before reloading.
    /// </summary>
    public void Clear() => Dispose();

    /// <summary>
    /// Validates the mapping for <paramref name="type"/> and everything reachable from it against
    /// the image, without loading anything.
    /// </summary>
    /// <exception cref="GsMappingException">All problems found, in one exception.</exception>
    public void Validate(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Prepare(type);
    }

    // ---- Metadata --------------------------------------------------------------------------

    private GsShape Prepare(Type type)
    {
        var errors = new List<string>();
        var shape = _shapes.ShapeOf(type, errors, type.Name);
        var maps = _shapes.NewMaps.ToList();
        _shapes.NewMaps.Clear();
        foreach (var map in maps)
            Resolve(map, errors);
        if (errors.Count > 0)
        {
            // Not usable: start over next time (e.g. after the image was fixed).
            _shapes = new GsShapeFactory();
            throw GsMappingException.For(errors);
        }
        return shape;
    }

    private const string IdentifierRegex = @"^[A-Za-z_][A-Za-z0-9_]*$";
#if NET7_0_OR_GREATER
    [GeneratedRegex(IdentifierRegex)]
    private static partial Regex Identifier();
#else
    private static readonly Regex IdentifierInstance = new(IdentifierRegex);
    private static Regex Identifier() => IdentifierInstance;
#endif

    /// <summary>Resolves the GemStone class and the instVar indices of a type map.</summary>
    private void Resolve(GsTypeMap map, List<string> errors)
    {
        var name = map.Class.Name;
        var dict = map.Class.Dictionary;
        if (!Identifier().IsMatch(name) || (dict is not null && !Identifier().IsMatch(dict)))
        {
            errors.Add($"{map.Type.Name}: invalid class or dictionary name '{(dict is null ? "" : dict + ".")}{name}'.");
            return;
        }

        Oop cls;
        if (dict is null)
        {
            if (!_session.TryResolveSymbol(name, out cls))
            {
                errors.Add($"{map.Type.Name}: GemStone class '{name}' not found in the symbol list.");
                return;
            }
            _session.Release(cls); // classes are referenced from their dictionary
        }
        else
        {
            cls = _session.Execute(
                $"| d | d := System myUserProfile symbolList objectNamed: #{dict}. " +
                $"(d isKindOf: AbstractDictionary) ifTrue: [d at: #{name} otherwise: nil] ifFalse: [nil]");
            _session.Release(cls);
            if (cls.IsNil)
            {
                errors.Add($"{map.Type.Name}: GemStone class '{name}' not found in {dict}.");
                return;
            }
        }

        if (_session.Perform(cls, "isBehavior") != Oop.True)
        {
            errors.Add($"{map.Type.Name}: '{name}' is not a class.");
            return;
        }

        var names = InstVarNames(cls);
        foreach (var p in map.Properties)
        {
            var index = Array.IndexOf(names, p.InstVar);
            if (index < 0)
                errors.Add($"{p.Path} → '{p.InstVar}': no such instance variable in {name}{Suggest(p.InstVar, names)}.");
            else
                p.Index = index + 1;
        }
        map.ClassOop = cls;
        _classNames[cls] = name;
    }

    private string[] InstVarNames(Oop cls)
    {
        var t = _session.PerformTraverse(cls, "allInstVarNames", 2);
        return [.. t.ToList(t.Result!.Value).Select(v => v as string ?? "")];
    }

    private static string Suggest(string name, string[] candidates)
    {
        var best = candidates.Select(c => (c, d: Distance(name.ToLowerInvariant(), c.ToLowerInvariant())))
            .Where(x => x.d <= Math.Max(2, name.Length / 3)).OrderBy(x => x.d).FirstOrDefault();
        return best.c is null ? $" (has: {string.Join(", ", candidates)})" : $" (did you mean '{best.c}'?)";

        static int Distance(string a, string b)
        {
            var prev = Enumerable.Range(0, b.Length + 1).ToArray();
            for (var i = 1; i <= a.Length; i++)
            {
                var cur = new int[b.Length + 1];
                cur[0] = i;
                for (var j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = cur;
            }
            return prev[b.Length];
        }
    }

    // ---- Server-side class facts (cached) --------------------------------------------------

    private enum CollectionClass { None, Sequence, Nsc, Unordered, Dictionary }

    private Oop Kernel(string name)
    {
        if (!_kernelClasses.TryGetValue(name, out var cls))
        {
            if (name == "Array") cls = KnownClasses.Array;
            else
            {
                cls = _session.ResolveSymbol(name);
                _session.Release(cls); // kernel classes are persistent
            }
            _kernelClasses[name] = cls;
        }
        return cls;
    }

    private bool IsSubclass(Oop cls, Oop super)
    {
        if (cls == super) return true;
        if (!_subclassCache.TryGetValue((cls, super), out var result))
        {
            result = _session.Perform(cls, "isSubclassOf:", super) == Oop.True;
            _subclassCache[(cls, super)] = result;
        }
        return result;
    }

    private CollectionClass Classify(Oop cls)
    {
        if (!_collectionCache.TryGetValue(cls, out var kind))
        {
            kind = IsSubclass(cls, Kernel("AbstractDictionary")) ? CollectionClass.Dictionary
                : IsSubclass(cls, Kernel("Array")) || IsSubclass(cls, Kernel("OrderedCollection")) ? CollectionClass.Sequence
                : IsSubclass(cls, Kernel("IdentityBag")) ? CollectionClass.Nsc
                : IsSubclass(cls, Kernel("Collection")) ? CollectionClass.Unordered
                : CollectionClass.None;
            _collectionCache[cls] = kind;
        }
        return kind;
    }

    private bool MatchesKind(Oop cls, GsCollectionKind kind) => kind switch
    {
        GsCollectionKind.Array => IsSubclass(cls, Kernel("Array")),
        GsCollectionKind.Ordered => IsSubclass(cls, Kernel("OrderedCollection")),
        GsCollectionKind.Set => IsSubclass(cls, Kernel("Set")),
        GsCollectionKind.IdentitySet => IsSubclass(cls, Kernel("IdentitySet")),
        GsCollectionKind.Bag => IsSubclass(cls, Kernel("Bag")),
        GsCollectionKind.IdentityBag => IsSubclass(cls, Kernel("IdentityBag")),
        GsCollectionKind.Dictionary => IsSubclass(cls, Kernel("AbstractDictionary")),
        _ => false,
    };

    private string ClassName(Oop cls)
    {
        if (!_classNames.TryGetValue(cls, out var name))
        {
            name = _session.Send(cls, "name") as string ?? cls.ToString();
            _classNames[cls] = name;
        }
        return name;
    }

    /// <summary>Picks the type map for an object of GemStone class <paramref name="cls"/> where <paramref name="expected"/> is required.</summary>
    private GsTypeMap? SelectMap(GsTypeMap expected, Oop cls)
    {
        foreach (var sub in expected.Subtypes)
            if (sub.ClassOop == cls) return sub;
        if (expected.ClassOop == cls) return expected;
        foreach (var sub in expected.Subtypes)
            if (IsSubclass(cls, sub.ClassOop)) return sub;
        return IsSubclass(cls, expected.ClassOop) ? expected : null;
    }

    // ---- One load --------------------------------------------------------------------------

    private const string FlattenBlock =
        "[:cs | cs collect: [:c | (c isKindOf: AbstractDictionary)" +
        " ifTrue: [| a | a := Array new. c keysAndValuesDo: [:k :v | a add: k; add: v]. a]" +
        " ifFalse: [c asArray]]]";

    [RequiresUnreferencedCode("The object mapper inspects mapped types by reflection.")]
    [RequiresDynamicCode("The object mapper creates generic collection types at runtime.")]
    private sealed class LoadRun(GsObjectContext ctx, GsLoadOptions options, bool callerOwnsRoot)
    {
        private Oop _root = Oop.Illegal;
        private readonly List<Oop> _toRetain = [];
        private readonly GciSession _session = ctx._session;
        private readonly Dictionary<Oop, GciObjectReport> _reports = [];
        private readonly Dictionary<Oop, Oop[]> _flattened = [];

        /// <summary>Values the traversal cannot decode (LargeInteger, ScaledDecimal, date/time …), converted in one batch.</summary>
        private readonly HashSet<Oop> _toConvert = [];
        private readonly Dictionary<Oop, object?> _converted = [];
        private readonly Dictionary<(Oop, GsTypeMap), GsTypeMap> _actualMaps = [];
        private readonly Queue<(object Instance, Oop Oop, GsTypeMap Map)> _fill = new();
        private readonly GciTraversalOptions _travOptions = new() { BufferSize = options.BufferSize };

        // ---- Phase 1: fetch everything the mapping needs ----

        public void Discover(Oop root, GsShape shape)
        {
            var visited = new HashSet<(Oop, GsShape)>();
            var queue = new Queue<(Oop Oop, GsShape Shape, string Where)>();
            var blocked = new List<(Oop, GsShape, string)>();
            var missing = new HashSet<Oop>();
            var toFlatten = new HashSet<Oop>();

            if (NeedsReport(root, shape)) Fetch([root]);
            queue.Enqueue((root, shape, "root"));
            while (true)
            {
                while (queue.TryDequeue(out var node))
                {
                    var (oop, s, where) = node;
                    if (s.Kind is GsShapeKind.Scalar or GsShapeKind.Object && oop.IsSpecial && GciTraversal.Decode(oop, _reports) is Oop)
                        _toConvert.Add(oop); // SmallScaledDecimal, SmallDate, SmallDateAndTime …
                    if (!NeedsReport(oop, s) || !visited.Add((oop, s))) continue;
                    if (!_reports.TryGetValue(oop, out var report))
                    {
                        missing.Add(oop);
                        visited.Remove((oop, s));
                        blocked.Add(node);
                        continue;
                    }
                    if (report.NoReadAuthorization)
                        throw GsMappingException.For($"{where}: no read authorization for {oop}.");

                    switch (s.Kind)
                    {
                        case GsShapeKind.Mapped:
                            var map = ActualMap(oop, report, s.Map!, where);
                            foreach (var p in map.Properties)
                                queue.Enqueue((NamedValue(report, p, where), p.Shape, p.Path));
                            break;
                        case GsShapeKind.Scalar or GsShapeKind.Object:
                            if (GciTraversal.Decode(oop, _reports) is Oop) _toConvert.Add(oop);
                            break;
                        case GsShapeKind.List or GsShapeKind.Array or GsShapeKind.Set or GsShapeKind.Dictionary:
                            var elements = Elements(oop, report, s, where);
                            if (elements is null)
                            {
                                toFlatten.Add(oop);
                                visited.Remove((oop, s));
                                blocked.Add(node);
                                continue;
                            }
                            if (s.Kind == GsShapeKind.Dictionary)
                                for (var i = 0; i + 1 < elements.Length; i += 2)
                                {
                                    queue.Enqueue((elements[i], s.Key!, $"key of {where}"));
                                    queue.Enqueue((elements[i + 1], s.Value!, $"value of {where}"));
                                }
                            else
                                foreach (var e in elements)
                                    queue.Enqueue((e, s.Element!, $"element of {where}"));
                            break;
                    }
                }

                if (missing.Count == 0 && toFlatten.Count == 0) return;
                if (missing.Count > 0)
                {
                    Fetch([.. missing]);
                    if (missing.FirstOrDefault(o => !_reports.ContainsKey(o)) is var lost && lost != default)
                        throw GsMappingException.For($"The server did not report {lost}.");
                }
                if (toFlatten.Count > 0) Flatten([.. toFlatten]);
                missing.Clear();
                toFlatten.Clear();
                foreach (var b in blocked) queue.Enqueue(b);
                blocked.Clear();
            }
        }

        private static bool NeedsReport(Oop oop, GsShape shape) =>
            !oop.IsSpecial && oop != Oop.Illegal && shape.Kind != GsShapeKind.Oop;

        private void Fetch(Oop[] roots)
        {
            var t = _session.Traverse(roots, options.Depth, _travOptions);
            foreach (var r in t.Reports) _reports[r.Oop] = r;
        }

        /// <summary>Server-side asArray / key-value pairs for hashed collections, all in one round trip.</summary>
        private void Flatten(Oop[] collections)
        {
            var array = _session.NewObject(KnownClasses.Array, collections.Length);
            Oop block = Oop.Nil;
            try
            {
                _session.StoreVaryingOops(array, 1, collections);
                block = _session.Execute(FlattenBlock);
                var t = _session.PerformTraverse(block, "value:", options.Depth + 2, [array], _travOptions);
                foreach (var r in t.Reports) _reports.TryAdd(r.Oop, r);
                var flat = t[t.Result!.Value].Indexed.ToArray();
                for (var i = 0; i < collections.Length; i++)
                    _flattened[collections[i]] = [.. t[flat[i]].Indexed];
            }
            finally
            {
                _session.Release(array, block);
            }
        }

        private GsTypeMap ActualMap(Oop oop, GciObjectReport report, GsTypeMap expected, string where)
        {
            if (_actualMaps.TryGetValue((oop, expected), out var known)) return known;
            var map = ctx.SelectMap(expected, report.Class)
                ?? throw GsMappingException.For($"{where}: expected a {expected.Class.Name} for {expected.Type.Name}, got a {ctx.ClassName(report.Class)} ({oop}).");
            if (map.Constructor is null)
                throw GsMappingException.For($"{where}: {map.Type.Name} cannot be instantiated (abstract or no parameterless constructor) for a {ctx.ClassName(report.Class)}.");
            _actualMaps[(oop, expected)] = map;
            return map;
        }

        private static Oop NamedValue(GciObjectReport report, GsPropertyMap p, string where)
        {
            if (p.Index > report.NamedSize)
                throw GsMappingException.For($"{where}: {report.Oop} has only {report.NamedSize} named instance variables, '{p.InstVar}' is #{p.Index}.");
            return report.Named(p.Index);
        }

        /// <summary>Collection contents, or <c>null</c> if they have to be flattened on the server first.</summary>
        private Oop[]? Elements(Oop oop, GciObjectReport report, GsShape shape, string where, GsCollectionKind? declared = null)
        {
            if (report.IsPartial) throw GsMappingException.For($"{where}: incomplete report for {oop}.");
            var kind = ctx.Classify(report.Class);
            if (declared is { } d && !ctx.MatchesKind(report.Class, d))
                throw GsMappingException.For($"{where}: declared as {d}, but {oop} is a {ctx.ClassName(report.Class)}.");
            if (kind == CollectionClass.None)
                throw GsMappingException.For($"{where}: {oop} is a {ctx.ClassName(report.Class)}, not a collection.");
            if ((shape.Kind == GsShapeKind.Dictionary) != (kind == CollectionClass.Dictionary))
                throw GsMappingException.For(shape.Kind == GsShapeKind.Dictionary
                    ? $"{where}: {shape.Type.Name} needs a dictionary, but {oop} is a {ctx.ClassName(report.Class)}."
                    : $"{where}: {oop} is a {ctx.ClassName(report.Class)} (a dictionary); use a Dictionary<K,V> property.");
            if (kind is CollectionClass.Sequence or CollectionClass.Nsc)
                return [.. report.Indexed];
            return _flattened.TryGetValue(oop, out var flat) ? flat : null;
        }

        // ---- Phase 2: build the C# objects ----

        public object? Build(Oop root, GsShape shape, string where)
        {
            if (_toConvert.Count > 0)
            {
                Oop[] pending = [.. _toConvert];
                var values = _session.ToValues(pending);
                for (var i = 0; i < pending.Length; i++) _converted[pending[i]] = values[i];
            }
            _root = root;
            var result = Value(root, shape, where, null);
            while (_fill.TryDequeue(out var job))
                Fill(job.Instance, job.Oop, job.Map);
            if (options.TrackChanges)
                // Root passed in by the caller: the caller keeps it alive (and may release it).
                ctx.RetainAll(_toRetain.Where(o => !(callerOwnsRoot && o == _root)));
            return result;
        }

        private void Fill(object instance, Oop oop, GsTypeMap map)
        {
            var report = _reports[oop];
            map.OopProperty?.SetValue(instance, oop);
            EntityEntry? entry = null;
            if (options.TrackChanges)
            {
                entry = new EntityEntry(instance, oop, map);
                ctx._entries[instance] = entry; // reload: replaces the old snapshot
                _toRetain.Add(oop);
            }
            foreach (var p in map.Properties)
            {
                var ivOop = NamedValue(report, p, p.Path);
                object? value;
                CollectionSnapshot? coll = null;
                if (entry is not null && p.Converter is null && p.Shape.IsCollection && !ivOop.IsSpecial)
                {
                    var elements = new List<SnapElement>();
                    value = Collection(ivOop, p.Shape, p.Path, p.CollectionKind, elements);
                    coll = new CollectionSnapshot(ivOop, elements);
                    _toRetain.Add(ivOop);
                }
                else
                    value = Value(ivOop, p.Shape, p.Path, p);
                if (value is null && p.Property.PropertyType.IsValueType && Nullable.GetUnderlyingType(p.Property.PropertyType) is null)
                    throw GsMappingException.For($"{p.Path}: nil cannot be assigned to {p.Property.PropertyType.Name} ({oop}).");
                p.Property.SetValue(instance, value);
                if (entry is not null)
                    entry.Properties[p] = new PropertySnapshot
                    {
                        Oop = ivOop, Value = value, Collection = coll, IsSymbol = IsSymbol(ivOop),
                        LowerFirst = GciTraversal.Decode(ivOop, _reports) is not string { Length: > 0 } text || char.IsLower(text[0]),
                    };
            }
        }

        private bool IsSymbol(Oop oop) =>
            _reports.TryGetValue(oop, out var r) && (r.Class == KnownClasses.Symbol
                || r.Class == KnownClasses.DoubleByteSymbol || r.Class == KnownClasses.QuadByteSymbol);

        private object? Value(Oop oop, GsShape shape, string where, GsPropertyMap? property)
        {
            if (property?.Converter is { } converter)
                return converter.FromGemStone(Decode(oop, forScalar: true), property.Property.PropertyType);

            switch (shape.Kind)
            {
                case GsShapeKind.Oop:
                    return oop;
                case GsShapeKind.Object:
                    return Decode(oop, forScalar: false);
                case GsShapeKind.Scalar:
                    return Coerce(Decode(oop, forScalar: true), shape, where);
                case GsShapeKind.Mapped:
                    if (oop.IsNil) return null;
                    if (oop.IsSpecial)
                        throw GsMappingException.For($"{where}: expected a {shape.Map!.Class.Name}, got the special object {_session.PrintString(oop)}.");
                    return Instance(oop, _actualMaps[(oop, shape.Map!)]);
                default:
                    if (oop.IsNil) return null;
                    if (oop.IsSpecial) throw GsMappingException.For($"{where}: expected a collection, got {_session.PrintString(oop)}.");
                    return Collection(oop, shape, where, property?.CollectionKind);
            }
        }

        private object Instance(Oop oop, GsTypeMap map)
        {
            if (ctx._identity.TryGetValue(oop, out var existing) && map.Type.IsInstanceOfType(existing))
            {
                if (!_filled.Add(oop)) return existing;
                _fill.Enqueue((existing, oop, map)); // loaded again: refresh
                return existing;
            }
            var instance = map.Constructor!.Invoke(null);
            ctx._identity[oop] = instance;
            ctx._oops[instance] = oop;
            _filled.Add(oop);
            _fill.Enqueue((instance, oop, map));
            return instance;
        }

        private readonly HashSet<Oop> _filled = [];

        private object Collection(Oop oop, GsShape shape, string where, GsCollectionKind? declared, List<SnapElement>? snapshot = null)
        {
            var elements = Elements(oop, _reports[oop], shape, where, declared)!;
            object? Element(Oop e, GsShape s, string w)
            {
                if (snapshot is not null && s.IsCollection && !e.IsSpecial)
                {
                    // A collection inside a collection: its own snapshot, so that SaveChanges can patch it in place.
                    var inner = new List<SnapElement>();
                    var c = Collection(e, s, w, null, inner);
                    snapshot.Add(new SnapElement(e, c, false, new CollectionSnapshot(e, inner)));
                    _toRetain.Add(e);
                    return c;
                }
                var v = this.Element(e, s, w);
                snapshot?.Add(new SnapElement(e, v, IsSymbol(e)));
                return v;
            }
            switch (shape.Kind)
            {
                case GsShapeKind.Array:
                {
                    var array = Array.CreateInstance(shape.Element!.Type, elements.Length);
                    for (var i = 0; i < elements.Length; i++)
                        array.SetValue(Element(elements[i], shape.Element, where), i);
                    return array;
                }
                case GsShapeKind.List:
                {
                    var list = (IList)Activator.CreateInstance(shape.Concrete!, elements.Length)!;
                    foreach (var e in elements) list.Add(Element(e, shape.Element!, where));
                    return list;
                }
                case GsShapeKind.Set:
                {
                    var set = Activator.CreateInstance(shape.Concrete!)!;
                    foreach (var e in elements) shape.AddMethod!.Invoke(set, [Element(e, shape.Element!, where)]);
                    return set;
                }
                default:
                {
                    var dict = (IDictionary)Activator.CreateInstance(shape.Concrete!)!;
                    for (var i = 0; i + 1 < elements.Length; i += 2)
                    {
                        var key = Element(elements[i], shape.Key!, $"key of {where}")
                            ?? throw GsMappingException.For($"key of {where}: nil key.");
                        if (dict.Contains(key))
                            throw GsMappingException.For($"key of {where}: '{key}' occurs twice after conversion (e.g. a String and a Symbol with the same text).");
                        dict.Add(key, Element(elements[i + 1], shape.Value!, $"value of {where}"));
                    }
                    return dict;
                }
            }
        }

        private object? Element(Oop oop, GsShape shape, string where)
        {
            var value = Value(oop, shape, where, null);
            if (value is null && shape.Type.IsValueType && Nullable.GetUnderlyingType(shape.Type) is null)
                throw GsMappingException.For($"{where}: nil cannot be stored as {shape.Type.Name}.");
            return value;
        }

        /// <summary>
        /// Offline decoding; values the traversal cannot decode (LargeInteger, ScaledDecimal,
        /// date/time …) were converted in one batch before building (<see cref="GciSession.ToValues"/>).
        /// </summary>
        private object? Decode(Oop oop, bool forScalar)
        {
            var value = GciTraversal.Decode(oop, _reports);
            if (value is not Oop) return value;
            if (_converted.TryGetValue(oop, out var converted)) return converted; // batch (ToValues)
            // Not collected during discovery (should not happen): convert on its own.
            return forScalar || oop.IsSpecial ? _session.ToValue(oop) : oop;
        }

        private object? Coerce(object? value, GsShape shape, string where)
        {
            var target = shape.ScalarType!;
            if (value is null)
                return Nullable.GetUnderlyingType(shape.Type) is not null || !target.IsValueType
                    ? null
                    : throw GsMappingException.For($"{where}: nil cannot be assigned to {target.Name}.");
            if (target.IsInstanceOfType(value)) return value;
            try
            {
                if (target.IsEnum)
                    return value switch
                    {
                        string s => Enum.Parse(target, s, ignoreCase: true),
                        long l => Enum.ToObject(target, l),
                        _ => throw Mismatch(),
                    };
                return (value, target) switch
                {
                    (long l, _) when target == typeof(BigInteger) => new BigInteger(l),
                    (long l, _) when target == typeof(decimal) => (decimal)l,
                    (long l, _) when target == typeof(double) => (double)l,
                    (long l, _) when target == typeof(float) => (float)l,
                    (long, _) when IsInteger(target) => Convert.ChangeType(value, target, CultureInfo.InvariantCulture),
                    (BigInteger b, _) when target == typeof(decimal) => (decimal)b,
                    (BigInteger b, _) when target == typeof(double) => (double)b,
                    (BigInteger b, _) when IsInteger(target) => Convert.ChangeType((decimal)b, target, CultureInfo.InvariantCulture),
                    (double d, _) when target == typeof(float) => (float)d,
                    (double d, _) when target == typeof(decimal) => (decimal)d,
                    (decimal m, _) when target == typeof(double) => (double)m,
                    (Rune r, _) when target == typeof(char) && r.IsBmp => (char)r.Value,
                    (Rune r, _) when target == typeof(string) => r.ToString(),
                    (string s, _) when target == typeof(char) && s.Length == 1 => s[0],
                    (DateTimeOffset dto, _) when target == typeof(DateTime) => dto.UtcDateTime,
                    _ => throw Mismatch(),
                };
            }
            catch (Exception e) when (e is OverflowException or ArgumentException or InvalidCastException)
            {
                throw GsMappingException.For($"{where}: {Describe(value)} cannot be converted to {target.Name} ({e.Message}).");
            }

            GsMappingException Mismatch() => GsMappingException.For($"{where}: {Describe(value)} cannot be assigned to {target.Name}.");

            static bool IsInteger(Type t) => t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(sbyte)
                || t == typeof(byte) || t == typeof(ushort) || t == typeof(uint) || t == typeof(ulong);
        }

        private string Describe(object value) => value is Oop o
            ? $"a {(_reports.TryGetValue(o, out var r) ? ctx.ClassName(r.Class) : o.ToString())}"
            : $"{value.GetType().Name} {value}";
    }
}
