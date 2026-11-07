using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace GemStone.Gci.Mapping;

/// <summary>Snapshot of one collection element (or dictionary key/value) as loaded or last saved.</summary>
internal sealed record SnapElement(Oop Oop, object? Value, bool IsSymbol);

/// <summary>Snapshot of a collection-valued property: the GemStone collection and its contents.</summary>
internal sealed class CollectionSnapshot(Oop oop, List<SnapElement> elements)
{
    public Oop Oop { get; } = oop;
    public List<SnapElement> Elements { get; set; } = elements;
}

/// <summary>Snapshot of one mapped property as loaded or last saved.</summary>
internal sealed class PropertySnapshot
{
    public required Oop Oop { get; init; }
    public object? Value { get; init; }
    public CollectionSnapshot? Collection { get; init; }

    /// <summary>The value was a Symbol: a changed string/enum is written as a Symbol again.</summary>
    public bool IsSymbol { get; init; }

    /// <summary>For enums written as Symbols: the Symbol starts lower case (<c>#active</c>).</summary>
    public bool LowerFirst { get; init; } = true;
}

/// <summary>A tracked object: its OOP, mapping and property snapshots.</summary>
internal sealed class EntityEntry(object entity, Oop oop, GsTypeMap map)
{
    public object Entity { get; } = entity;
    public Oop Oop { get; } = oop;
    public GsTypeMap Map { get; } = map;
    public Dictionary<GsPropertyMap, PropertySnapshot> Properties { get; } = [];
}

public sealed partial class GsObjectContext : IDisposable
{
    private readonly Dictionary<object, EntityEntry> _entries = new(ReferenceEqualityComparer.Instance);

    /// <summary>OOPs this context holds in the export set (released by <see cref="Dispose"/> / <see cref="Clear"/>).</summary>
    private readonly HashSet<Oop> _retained = [];

    private const string ReplaceContentsBlock =
        "[:c :new | (c isKindOf: AbstractDictionary)" +
        " ifTrue: [c keys do: [:k | c removeKey: k]. 1 to: new size by: 2 do: [:i | c at: (new at: i) put: (new at: i + 1)]]" +
        " ifFalse: [(c isKindOf: Array)" +
        "   ifTrue: [c size: new size. c replaceFrom: 1 to: new size with: new startingAt: 1]" +
        "   ifFalse: [c removeAll: c copy. c addAll: new]]." +
        " true]";

    /// <summary>
    /// Writes all changes of tracked objects to the session: changed properties, changed collection
    /// contents and new objects reachable from tracked ones. Does <b>not</b> commit; call
    /// <see cref="GciSession.Commit"/> (or use <see cref="GciSession.RunInTransaction(Action{GciSession}, int)"/>) afterwards.
    /// </summary>
    /// <returns>Number of instance variables stored plus collections rewritten.</returns>
    /// <remarks>
    /// Only objects loaded with <see cref="GsLoadOptions.TrackChanges"/> (the default) or added with
    /// <see cref="Add"/> are tracked. Properties with <see cref="GsReadOnlyAttribute"/> are skipped.
    /// A changed collection is rewritten as a whole (its identity is kept). New objects are created
    /// with <c>new</c> on their GemStone class. If SaveChanges throws, part of the changes may already
    /// be written: abort the transaction, <see cref="Clear"/> the context and reload.
    /// </remarks>
    public int SaveChanges()
    {
        var run = new SaveRun(this);
        try
        {
            foreach (var entry in _entries.Values.ToList())
                run.Update(entry);
            return run.Stores;
        }
        finally
        {
            run.ReleaseTemporaries();
        }
    }

    /// <summary>
    /// Starts tracking a new object: <see cref="SaveChanges"/> creates it on the server. It only
    /// becomes persistent if something persistent references it when the transaction commits.
    /// </summary>
    /// <returns>The OOP of the new GemStone object.</returns>
    public Oop Add(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (_oops.TryGetValue(entity, out var known)) return known;
        var run = new SaveRun(this);
        try
        {
            return run.Insert(entity);
        }
        finally
        {
            run.ReleaseTemporaries();
        }
    }

    /// <summary>Releases the OOPs held by this context (the objects stay usable on the server if referenced).</summary>
    public void Dispose()
    {
        ReleaseRetained();
        _entries.Clear();
        _identity.Clear();
        _oops.Clear();
    }

    private void ReleaseRetained()
    {
        if (_retained.Count > 0 && _session.IsLoggedIn)
            foreach (var chunk in _retained.Chunk(10_000))
                _session.Release(chunk);
        _retained.Clear();
    }

    /// <summary>Adds OOPs to the export set once, so that tracked objects survive until saved.</summary>
    private void RetainAll(IEnumerable<Oop> oops)
    {
        var fresh = oops.Where(o => !o.IsSpecial && o != Oop.Illegal && !_retained.Contains(o)).Distinct().ToArray();
        if (fresh.Length == 0) return;
        _session.Retain(fresh);
        _retained.UnionWith(fresh);
    }

    // ---- Comparison ------------------------------------------------------------------------

    private static bool IsEntity(object o) => Attribute.IsDefined(o.GetType(), typeof(GsClassAttribute), inherit: false);

    /// <summary>Entities compare by reference, everything else by value (byte[] by content).</summary>
    private static bool ValueEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (IsEntity(a) || IsEntity(b)) return false;
        if (a is byte[] x && b is byte[] y) return x.AsSpan().SequenceEqual(y);
        return a.Equals(b);
    }

    /// <summary>Elements, or key/value pairs laid end to end for dictionaries.</summary>
    private static List<object?> Items(object collection)
    {
        if (collection is not IDictionary dict) return [.. ((IEnumerable)collection).Cast<object?>()];
        var items = new List<object?>(dict.Count * 2);
        // Not Cast<DictionaryEntry>(): that uses the generic enumerator (KeyValuePair).
        for (var e = dict.GetEnumerator(); e.MoveNext();)
        {
            items.Add(e.Key);
            items.Add(e.Value);
        }
        return items;
    }

    private static bool ContentsEqual(List<object?> current, List<SnapElement> old, GsShapeKind kind)
    {
        if (current.Count != old.Count) return false;
        switch (kind)
        {
            case GsShapeKind.List or GsShapeKind.Array:
                for (var i = 0; i < current.Count; i++)
                    if (!ValueEquals(current[i], old[i].Value)) return false;
                return true;
            case GsShapeKind.Set:
            {
                var unmatched = old.Select(e => e.Value).ToList();
                foreach (var c in current)
                {
                    var i = unmatched.FindIndex(o => ValueEquals(c, o));
                    if (i < 0) return false;
                    unmatched.RemoveAt(i);
                }
                return true;
            }
            default: // dictionary: key/value pairs, order irrelevant
                for (var i = 0; i < current.Count; i += 2)
                {
                    var found = false;
                    for (var j = 0; j < old.Count && !found; j += 2)
                        found = ValueEquals(current[i], old[j].Value) && ValueEquals(current[i + 1], old[j + 1].Value);
                    if (!found) return false;
                }
                return true;
        }
    }

    // ---- One save --------------------------------------------------------------------------

    [RequiresUnreferencedCode("The object mapper inspects mapped types by reflection.")]
    [RequiresDynamicCode("The object mapper creates generic collection types at runtime.")]
    private sealed class SaveRun(GsObjectContext ctx)
    {
        private readonly GciSession _session = ctx._session;
        private readonly List<Oop> _temporaries = [];
        private Oop _replaceBlock = Oop.Nil;

        public int Stores { get; private set; }

        public void Update(EntityEntry entry)
        {
            foreach (var p in entry.Map.Properties)
            {
                if (p.ReadOnly) continue;
                var current = p.Property.GetValue(entry.Entity);
                entry.Properties.TryGetValue(p, out var snap);

                if (p.Converter is null && p.Shape.IsCollection)
                {
                    UpdateCollection(entry, p, current, snap);
                    continue;
                }
                if (snap is not null && ValueEquals(current, snap.Value)) continue;

                var oop = ToOop(current, p.Shape, p, snap, p.Path);
                _session.StoreNamedOop(entry.Oop, p.Index, oop);
                Stores++;
                entry.Properties[p] = new PropertySnapshot
                {
                    Oop = oop, Value = current,
                    IsSymbol = snap?.IsSymbol ?? (current is Enum), LowerFirst = snap?.LowerFirst ?? true,
                };
            }
        }

        public Oop Insert(object entity)
        {
            var shape = ctx.Prepare(entity.GetType());
            if (shape.Kind != GsShapeKind.Mapped)
                throw GsMappingException.For($"{entity.GetType().Name} has no [GsClass] and cannot be saved as a new object.");
            var map = shape.Map!;
            var oop = _session.Perform(map.ClassOop, "new"); // exported: the context owns it now
            ctx._retained.Add(oop);
            ctx._identity[oop] = entity;
            ctx._oops[entity] = oop;
            var entry = new EntityEntry(entity, oop, map);
            ctx._entries[entity] = entry; // before Update: cycles back to this object find the OOP
            map.OopProperty?.SetValue(entity, oop);
            Update(entry);
            return oop;
        }

        private void UpdateCollection(EntityEntry entry, GsPropertyMap p, object? current, PropertySnapshot? snap)
        {
            var old = snap?.Collection;
            if (current is null)
            {
                if (snap is null || !snap.Oop.IsNil)
                {
                    _session.StoreNamedOop(entry.Oop, p.Index, Oop.Nil);
                    Stores++;
                }
                entry.Properties[p] = new PropertySnapshot { Oop = Oop.Nil };
                return;
            }

            var items = Items(current);
            if (old is not null)
            {
                if (!ContentsEqual(items, old.Elements, p.Shape.Kind))
                {
                    old.Elements = ReplaceContents(old.Oop, p.Shape, items, old.Elements, p.Path);
                    Stores++;
                }
                entry.Properties[p] = new PropertySnapshot { Oop = old.Oop, Value = current, Collection = old };
                return;
            }

            // Was nil (or a new object): create the GemStone collection.
            var coll = _session.Perform(ctx.Kernel(CollectionClassName(p)), "new");
            ctx._retained.Add(coll);
            var elements = ReplaceContents(coll, p.Shape, items, [], p.Path);
            _session.StoreNamedOop(entry.Oop, p.Index, coll);
            Stores++;
            entry.Properties[p] = new PropertySnapshot { Oop = coll, Value = current, Collection = new CollectionSnapshot(coll, elements) };
        }

        private static string CollectionClassName(GsPropertyMap p) => p.CollectionKind switch
        {
            GsCollectionKind.Array => "Array",
            GsCollectionKind.Ordered => "OrderedCollection",
            GsCollectionKind.Set => "Set",
            GsCollectionKind.IdentitySet => "IdentitySet",
            GsCollectionKind.Bag => "Bag",
            GsCollectionKind.IdentityBag => "IdentityBag",
            GsCollectionKind.Dictionary => "KeyValueDictionary",
            _ => p.Shape.Kind switch
            {
                GsShapeKind.Array => "Array",
                GsShapeKind.Set => "Set",
                GsShapeKind.Dictionary => "KeyValueDictionary",
                _ => "OrderedCollection",
            },
        };

        /// <summary>Rewrites a collection's contents on the server (keeps its identity).</summary>
        private List<SnapElement> ReplaceContents(Oop coll, GsShape shape, List<object?> items, List<SnapElement> old, string where)
        {
            var isDict = shape.Kind == GsShapeKind.Dictionary;
            var elements = new List<SnapElement>(items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                var elementShape = isDict ? (i % 2 == 0 ? shape.Key! : shape.Value!) : shape.Element!;
                var sameRole = isDict ? old.Where((_, j) => j % 2 == i % 2) : old;
                elements.Add(ElementOop(items[i], elementShape, [.. sameRole], where));
            }

            var array = _session.NewObject(KnownClasses.Array, elements.Count);
            _temporaries.Add(array);
            if (elements.Count > 0)
                _session.StoreVaryingOops(array, 1, [.. elements.Select(e => e.Oop)]);
            if (_replaceBlock.IsNil)
            {
                _replaceBlock = _session.Execute(ReplaceContentsBlock);
                _temporaries.Add(_replaceBlock);
            }
            _session.Perform(_replaceBlock, "value:value:", coll, array); // returns true: nothing to release
            return elements;
        }

        private SnapElement ElementOop(object? value, GsShape shape, List<SnapElement> old, string where)
        {
            if (shape.IsCollection)
                throw new NotSupportedException($"{where}: collections of collections cannot be saved yet.");
            // Unchanged scalar elements keep their OOP (and with it String vs. Symbol).
            if (value is not null && !IsEntity(value) && old.FirstOrDefault(e => ValueEquals(value, e.Value)) is { } same)
                return same;
            var asSymbol = old.Count > 0 ? old.Any(e => e.IsSymbol) : value is Enum;
            var snap = new PropertySnapshot { Oop = Oop.Nil, IsSymbol = asSymbol };
            return new SnapElement(ToOop(value, shape, null, snap, where), value, asSymbol && value is string or Enum);
        }

        private Oop ToOop(object? value, GsShape shape, GsPropertyMap? property, PropertySnapshot? snap, string where)
        {
            if (property?.Converter is { } converter)
                return converter is IGsTwoWayConverter twoWay
                    ? Scalar(twoWay.ToGemStone(value, property.Property.PropertyType), snap, where)
                    : throw new NotSupportedException($"{where}: {converter.GetType().Name} cannot convert values back to GemStone (implement IGsTwoWayConverter).");
            if (value is null) return Oop.Nil;
            return shape.Kind switch
            {
                GsShapeKind.Oop => (Oop)value,
                GsShapeKind.Mapped => EntityOop(value),
                GsShapeKind.Object => value is Oop o ? o : IsEntity(value) ? EntityOop(value) : Scalar(value, snap, where),
                GsShapeKind.Scalar => Scalar(value, snap, where),
                _ => throw new NotSupportedException($"{where}: nested collections cannot be saved yet."),
            };
        }

        private Oop EntityOop(object entity) => ctx._oops.TryGetValue(entity, out var oop) ? oop : Insert(entity);

        private Oop Scalar(object? value, PropertySnapshot? snap, string where)
        {
            Oop oop;
            switch (value)
            {
                case null:
                    return Oop.Nil;
                case Oop o:
                    return o;
                case Enum e when snap?.Oop.IsSmallInteger == true:
                    return Oop.FromSmallInteger(Convert.ToInt64(e, System.Globalization.CultureInfo.InvariantCulture));
                case Enum e:
                {
                    var name = e.ToString();
                    if (snap?.LowerFirst ?? true) name = char.ToLowerInvariant(name[0]) + name[1..];
                    oop = _session.NewSymbol(name);
                    break;
                }
                case string s when snap?.IsSymbol == true:
                    oop = _session.NewSymbol(s);
                    break;
                default:
                    try
                    {
                        oop = _session.FromValue(value);
                    }
                    catch (ArgumentException ex)
                    {
                        throw GsMappingException.For($"{where}: {ex.Message}");
                    }
                    break;
            }
            if (!oop.IsSpecial) _temporaries.Add(oop); // referenced from its holder once stored
            return oop;
        }

        public void ReleaseTemporaries()
        {
            if (_temporaries.Count > 0 && _session.IsLoggedIn)
                _session.Release([.. _temporaries]);
            _temporaries.Clear();
        }
    }
}
