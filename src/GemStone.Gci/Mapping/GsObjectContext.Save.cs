using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using GemStone.Gci.Native;

namespace GemStone.Gci.Mapping;

/// <summary>
/// Snapshot of one collection element (or dictionary key/value) as loaded or last saved;
/// <paramref name="Nested"/> for an element that is a collection itself.
/// </summary>
internal sealed record SnapElement(Oop Oop, object? Value, bool IsSymbol, CollectionSnapshot? Nested = null);

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

    /// <summary>
    /// Writes all changes of tracked objects to the session: changed properties, changed collection
    /// contents and new objects reachable from tracked ones. Does <b>not</b> commit; call
    /// <see cref="GciSession.Commit"/> afterwards, or use <see cref="SaveChangesAndCommit"/>.
    /// </summary>
    /// <returns>Number of instance variables stored plus collections changed.</returns>
    /// <remarks>
    /// Only objects loaded with <see cref="GsLoadOptions.TrackChanges"/> (the default) or added with
    /// <see cref="Add"/> are tracked. Properties with <see cref="GsReadOnlyAttribute"/> are skipped.
    /// A changed collection is changed in place: only removed, added or replaced elements are
    /// written, so untouched elements stay the same objects. New objects are created
    /// with <c>new</c> on their GemStone class.
    /// <para>
    /// All changes go to the server as one batch: a store traversal creates the values (strings,
    /// floats, byte arrays …) and a list of operations, which one piece of Smalltalk then carries
    /// out. That costs about six GCI calls, however many objects changed. Snapshots and the
    /// tracked objects change only if the batch succeeds. If it fails, the batch may have
    /// written part of the changes on the server: abort the transaction, <see cref="Clear"/>
    /// the context and reload.
    /// </para>
    /// <para>
    /// Saved but not yet committed changes are taken back by an abort (<see cref="GciSession.Abort"/>,
    /// also after a failed commit): the objects count as changed again, and the next SaveChanges
    /// writes them again. Objects created by SaveChanges keep their GemStone objects (temporary
    /// objects survive an abort). A commit or abort in Smalltalk code is not noticed.
    /// </para>
    /// </remarks>
    public int SaveChanges()
    {
        var run = new SaveRun(this);
        foreach (var entry in _entries.Values.ToList())
            run.Update(entry);
        run.Execute();
        return run.Stores;
    }

    /// <summary>
    /// <see cref="SaveChanges"/> and commit in one step. On a transaction conflict the session
    /// aborts and the changes are written again on top of the newly committed state, up to
    /// <paramref name="maxAttempts"/> times (<see cref="GciSession.RunInTransaction{T}(Func{GciSession, T}, int)"/>).
    /// </summary>
    /// <returns>What the last <see cref="SaveChanges"/> returned.</returns>
    /// <exception cref="GciTransactionConflictException">Still a conflict after the last attempt; the changes are unsaved again.</exception>
    /// <remarks>
    /// Properties changed here overwrite what other sessions committed to the same properties;
    /// other properties and untouched collection elements keep the other sessions' changes. To
    /// let the user decide instead, call <see cref="SaveChanges"/> and <see cref="GciSession.Commit"/>
    /// yourself and handle the conflict.
    /// </remarks>
    public int SaveChangesAndCommit(int maxAttempts = 3) => _session.RunInTransaction(_ => SaveChanges(), maxAttempts);

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
        var oop = run.Insert(entity);
        run.Execute();
        return oop.Resolved;
    }

    /// <summary>Releases the OOPs held by this context (the objects stay usable on the server if referenced).</summary>
    /// <remarks>
    /// Does not throw: if the session is broken (e.g. after a fatal error), the OOPs cannot be
    /// released any more, and an exception here would hide the error that broke the session
    /// (for example in a <c>using</c> block).
    /// </remarks>
    public void Dispose()
    {
        try
        {
            ReleaseRetained();
        }
        catch (GciException)
        {
            _retained.Clear(); // the session is unusable; the gem drops its export set at logout
        }
        _entries.Clear();
        _identity.Clear();
        _oops.Clear();
        ForgetJournal();
    }

    // ---- Uncommitted saves -----------------------------------------------------------------

    /// <summary>
    /// How to take back the snapshot changes of saves since the last commit or abort, in the
    /// order they were made. After an abort the server has the old state again, so the snapshots
    /// must say so too: otherwise the next SaveChanges would think the changes were written and
    /// send nothing.
    /// </summary>
    private readonly List<Action> _undo = [];

    private bool _listening;

    private void JournalProperty(EntityEntry entry, GsPropertyMap p)
    {
        var entity = entry.Entity;
        var previous = entry.Properties.TryGetValue(p, out var snap) ? snap : null;
        Journal(() =>
        {
            // A reload since the save may have replaced the entry: restore into the current one.
            if (!_entries.TryGetValue(entity, out var current)) return;
            if (previous is null) current.Properties.Remove(p);
            else current.Properties[p] = previous;
        });
    }

    private void JournalElements(CollectionSnapshot collection)
    {
        var previous = collection.Elements;
        Journal(() => collection.Elements = previous);
    }

    private void Journal(Action undo)
    {
        _undo.Add(undo);
        if (_listening) return;
        _session.TransactionEnded += OnTransactionEnded;
        _listening = true;
    }

    private void OnTransactionEnded(bool committed)
    {
        if (!committed)
            for (var i = _undo.Count - 1; i >= 0; i--)
                _undo[i]();
        ForgetJournal();
    }

    private void ForgetJournal()
    {
        _undo.Clear();
        if (!_listening) return;
        _session.TransactionEnded -= OnTransactionEnded;
        _listening = false;
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

    // ---- Unsaved changes -------------------------------------------------------------------

    /// <summary>
    /// Throws if a load would refresh tracked instances that have unsaved changes (instances the
    /// load reaches as the same C# type; a different type gets a new instance).
    /// </summary>
    private void ThrowIfUnsavedChanges(List<(Oop Oop, Type Type)> reached)
    {
        if (_entries.Count == 0) return;
        List<object>? dirty = null;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var (oop, type) in reached)
            if (_identity.TryGetValue(oop, out var entity) && type.IsInstanceOfType(entity) && seen.Add(entity)
                && _entries.TryGetValue(entity, out var entry) && HasChanges(entry))
                (dirty ??= []).Add(entity);
        if (dirty is not null) throw new GsUnsavedChangesException(dirty);
    }

    /// <summary>Whether SaveChanges would write anything for this object (the same comparison, without sending).</summary>
    private bool HasChanges(EntityEntry entry)
    {
        var run = new SaveRun(this);
        try
        {
            run.Update(entry);
        }
        catch (Exception e) when (e is NotSupportedException or GsMappingException)
        {
            return true; // a changed value that cannot even be written (e.g. a one-way converter)
        }
        return run.HasWork;
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

    // ---- One save ----------------------------------------------------------------------------

    /// <summary>
    /// Executed with the ops Array as <c>self</c>. Ops:
    /// <list type="bullet">
    /// <item><c>1 class</c>: create with <c>new</c>;</item>
    /// <item><c>2 target index value</c>: store a named instVar;</item>
    /// <item><c>3 target items</c>: fill a new collection (dictionaries: key/value pairs);</item>
    /// <item><c>4 target from removed inserted</c>: change a collection. <c>from</c> &gt; 0 for
    /// ordered collections: remove <c>removed size</c> elements at <c>from</c> and insert there;
    /// <c>from</c> = 0, SortedCollections and unordered collections: <c>remove:</c> and <c>addAll:</c>.
    /// Arrays are changed through an OrderedCollection copy;</item>
    /// <item><c>5 target removedKeys puts</c>: change a dictionary (<c>removeKey:</c>, <c>at:put:</c>).</item>
    /// </list>
    /// Value lists (items, removed …) are Arrays of values.
    /// A value is two slots, <c>kind payload</c>: 0 = the payload itself, 1 = the n-th created
    /// object, 2–8 = converted on the server (Symbol, Unicode string from Utf8, LargeInteger,
    /// ScaledDecimal, Date, Time, DateAndTime). Answers the created objects followed by the
    /// converted values, in the order in which they were evaluated.
    /// </summary>
    private const string BatchSource =
        "| created converted val decode patch i n op c |\n" +
        "created := OrderedCollection new. converted := OrderedCollection new.\n" +
        "val := [:k :p | | r | k == 0 ifTrue: [p] ifFalse: [k == 1 ifTrue: [created at: p] ifFalse: [\n" +
        " r := k == 2 ifTrue: [((p isKindOf: Utf8) ifTrue: [p decodeToUnicode] ifFalse: [p]) asSymbol] ifFalse: [\n" +
        " k == 3 ifTrue: [p decodeToUnicode] ifFalse: [\n" +
        " k == 4 ifTrue: [Integer fromString: p] ifFalse: [\n" +
        " k == 5 ifTrue: [((Integer fromString: (p at: 1)) / (10 raisedTo: (p at: 2))) asScaledDecimal: (p at: 2)] ifFalse: [\n" +
        " k == 6 ifTrue: [Date fromDays: p] ifFalse: [\n" +
        " k == 7 ifTrue: [Time fromMicroseconds: p] ifFalse: [\n" +
        " k == 8 ifTrue: [DateAndTime posixSeconds: (p at: 1) / 1000000 offset: (Duration seconds: (p at: 2))] ifFalse: [\n" +
        " self error: 'unknown value kind']]]]]]].\n" +
        " converted add: r. r]]].\n" +
        "decode := [:src | | a | a := Array new: src size // 2.\n" +
        " 1 to: a size do: [:j | a at: j put: (val value: (src at: j * 2 - 1) value: (src at: j * 2))]. a].\n" +
        "patch := [:coll :from :rem :ins | (coll isKindOf: Array)\n" +
        " ifTrue: [ | r | r := OrderedCollection withAll: coll. patch value: r value: from value: rem value: ins.\n" +
        "  coll size: r size. coll replaceFrom: 1 to: r size with: r asArray startingAt: 1]\n" +
        " ifFalse: [((from > 0) and: [(coll isKindOf: OrderedCollection) and: [(coll isKindOf: SortedCollection) not]])\n" +
        "  ifTrue: [rem size timesRepeat: [coll removeIndex: from].\n" +
        "   from > coll size ifTrue: [coll addAll: ins] ifFalse: [ins reverseDo: [:x | coll add: x beforeIndex: from]]]\n" +
        "  ifFalse: [rem do: [:x | coll remove: x ifAbsent: [nil]]. coll addAll: ins]]].\n" +
        "i := 1. n := self size.\n" +
        "[i <= n] whileTrue: [\n" +
        " op := self at: i.\n" +
        " op == 1 ifTrue: [created add: (self at: i + 1) new. i := i + 2] ifFalse: [\n" +
        " op == 2 ifTrue: [(val value: (self at: i + 1) value: (self at: i + 2)) instVarAt: (self at: i + 3) put: (val value: (self at: i + 4) value: (self at: i + 5)). i := i + 6] ifFalse: [\n" +
        " op == 4 ifTrue: [ | from rem |\n" +
        "  c := val value: (self at: i + 1) value: (self at: i + 2). from := self at: i + 3.\n" +
        "  rem := decode value: (self at: i + 4).\n" +
        "  patch value: c value: from value: rem value: (decode value: (self at: i + 5)). i := i + 6] ifFalse: [\n" +
        " op == 5 ifTrue: [ | keys puts |\n" +
        "  c := val value: (self at: i + 1) value: (self at: i + 2).\n" +
        "  keys := decode value: (self at: i + 3). puts := decode value: (self at: i + 4).\n" +
        "  keys do: [:k | c removeKey: k ifAbsent: [nil]].\n" +
        "  1 to: puts size by: 2 do: [:j | c at: (puts at: j) put: (puts at: j + 1)]. i := i + 5] ifFalse: [\n" +
        " op == 3 ifTrue: [ | src new |\n" +
        "  c := val value: (self at: i + 1) value: (self at: i + 2). src := self at: i + 3.\n" +
        "  new := Array new: src size // 2.\n" +
        "  1 to: new size do: [:j | new at: j put: (val value: (src at: j * 2 - 1) value: (src at: j * 2))].\n" +
        "  (c isKindOf: AbstractDictionary)\n" +
        "   ifTrue: [c keys do: [:k | c removeKey: k]. 1 to: new size by: 2 do: [:j | c at: (new at: j) put: (new at: j + 1)]]\n" +
        "   ifFalse: [(c isKindOf: Array)\n" +
        "    ifTrue: [c size: new size. c replaceFrom: 1 to: new size with: new startingAt: 1]\n" +
        "    ifFalse: [c removeAll: c copy. c addAll: new]].\n" +
        "  i := i + 4] ifFalse: [\n" +
        " self error: 'unknown op']]]]]].\n" +
        "created asArray , converted asArray";

    /// <summary>
    /// A value in the batch. Its OOP is known up front (existing objects, immediates, objects the
    /// store traversal creates) or only after the batch ran (created and converted objects).
    /// </summary>
    private sealed class BatchValue
    {
        public int Kind { get; init; }

        /// <summary>The OOP (kind 0 with <see cref="Spec"/> null), or the payload of a conversion.</summary>
        public Oop Payload { get; init; }

        /// <summary>A new object in the store traversal: the value itself (kind 0) or the payload.</summary>
        public NewObjectSpec? Spec { get; init; }

        /// <summary>Kind 1: 1-based index into the created objects.</summary>
        public int Created { get; init; }

        /// <summary>Kinds 2–8: index into the converted values, assigned when the ops are written.</summary>
        public int Converted { get; set; } = -1;

        public Oop Resolved { get; set; }

        public static BatchValue Of(Oop oop) => new() { Payload = oop, Resolved = oop };
    }

    /// <summary>An object the store traversal creates (its OOP is allocated when the batch is sent).</summary>
    private sealed class NewObjectSpec
    {
        public required Oop Class { get; init; }
        public byte[]? Bytes { get; init; }
        public List<object>? Slots { get; init; } // Oop, NewObjectSpec or BatchValue (two slots)
        public double? Float { get; init; }
        public Oop Oop { get; set; }
    }

    private abstract record BatchOp;
    private sealed record CreateOp(Oop Class) : BatchOp;
    private sealed record StoreOp(BatchValue Target, int Index, BatchValue Value) : BatchOp;
    private sealed record ReplaceOp(BatchValue Target, List<BatchValue> Items) : BatchOp;
    private sealed record PatchOp(BatchValue Target, int From, List<BatchValue> Removed, List<BatchValue> Inserted) : BatchOp;
    private sealed record DictionaryPatchOp(BatchValue Target, List<BatchValue> RemovedKeys, List<BatchValue> Puts) : BatchOp;

    /// <summary>An object whose properties are compared and written: tracked, or new in this save.</summary>
    private sealed class SaveTarget(object entity, GsTypeMap map, BatchValue oop, EntityEntry? entry)
    {
        public object Entity { get; } = entity;
        public GsTypeMap Map { get; } = map;
        public BatchValue Oop { get; } = oop;
        public EntityEntry? Entry { get; } = entry;

        public PropertySnapshot? Snapshot(GsPropertyMap p) =>
            Entry is not null && Entry.Properties.TryGetValue(p, out var snap) ? snap : null;
    }

    /// <summary>
    /// Plans one SaveChanges/Add: compares, collects the operations and new objects, then sends
    /// everything at once (<see cref="GciSession.StoreAndExecute"/>). Snapshots and the identity
    /// map change only after the batch succeeded.
    /// </summary>
    private sealed class SaveRun(GsObjectContext ctx)
    {
        private readonly GciSession _session = ctx._session;
        private readonly List<BatchOp> _ops = [];
        private readonly List<BatchValue> _created = [];
        private readonly Dictionary<object, SaveTarget> _inserted = new(ReferenceEqualityComparer.Instance);
        private readonly List<Action> _onSuccess = [];

        public int Stores { get; private set; }

        /// <summary>Whether <see cref="Execute"/> would send a batch.</summary>
        public bool HasWork => _ops.Count > 0;

        public void Update(EntityEntry entry) =>
            Update(new SaveTarget(entry.Entity, entry.Map, BatchValue.Of(entry.Oop), entry));

        /// <summary>Plans a new object (once per entity) and returns its value.</summary>
        public BatchValue Insert(object entity)
        {
            if (_inserted.TryGetValue(entity, out var known)) return known.Oop;
            var shape = ctx.Prepare(entity.GetType());
            if (shape.Kind != GsShapeKind.Mapped)
                throw GsMappingException.For($"{entity.GetType().Name} has no [GsClass] and cannot be saved as a new object.");
            var map = shape.Map!;
            var oop = Create(map.ClassOop);
            var target = new SaveTarget(entity, map, oop, null);
            _inserted[entity] = target; // before Update: cycles back to this object find it
            var entry = new EntityEntry[1];
            _onSuccess.Add(() =>
            {
                var resolved = oop.Resolved;
                ctx._identity[resolved] = entity;
                ctx._oops[entity] = resolved;
                ctx._entries[entity] = entry[0] = new EntityEntry(entity, resolved, map);
                map.SetOop?.Invoke(entity, resolved);
            });
            Update(target, () => entry[0]);
            return oop;
        }

        private BatchValue Create(Oop cls)
        {
            _ops.Add(new CreateOp(cls));
            var value = new BatchValue { Kind = 1, Created = _created.Count + 1 };
            _created.Add(value);
            return value;
        }

        private void Update(SaveTarget target, Func<EntityEntry>? newEntry = null)
        {
            var entry = target.Entry;
            void Remember(GsPropertyMap p, Func<PropertySnapshot> snapshot) =>
                _onSuccess.Add(() =>
                {
                    // New objects are temporary until the commit and keep their state on an
                    // abort: only snapshots of existing objects are journaled.
                    if (entry is not null) ctx.JournalProperty(entry, p);
                    (entry ?? newEntry!()).Properties[p] = snapshot();
                });

            foreach (var p in target.Map.Properties)
            {
                if (p.ReadOnly) continue;
                var current = p.Get(target.Entity);
                var snap = target.Snapshot(p);

                if (p.Converter is null && p.Shape.IsCollection)
                {
                    UpdateCollection(target, p, current, snap, Remember);
                    continue;
                }
                if (snap is not null && ValueEquals(current, snap.Value)) continue;

                var value = ToValue(current, p.Shape, p, snap, p.Path);
                Store(target, p, value);
                var isSymbol = snap?.IsSymbol ?? (current is Enum);
                var lowerFirst = snap?.LowerFirst ?? true;
                Remember(p, () => new PropertySnapshot { Oop = value.Resolved, Value = current, IsSymbol = isSymbol, LowerFirst = lowerFirst });
            }
        }

        private void Store(SaveTarget target, GsPropertyMap p, BatchValue value)
        {
            _ops.Add(new StoreOp(target.Oop, p.Index, value));
            Stores++;
        }

        private void UpdateCollection(SaveTarget target, GsPropertyMap p, object? current, PropertySnapshot? snap,
            Action<GsPropertyMap, Func<PropertySnapshot>> remember)
        {
            var old = snap?.Collection;
            if (current is null)
            {
                if (snap is null || !snap.Oop.IsNil) Store(target, p, BatchValue.Of(Oop.Nil));
                remember(p, () => new PropertySnapshot { Oop = Oop.Nil });
                return;
            }

            if (old is not null)
            {
                PatchContents(old, current, p.Shape, p.Path);
                remember(p, () => new PropertySnapshot { Oop = old.Oop, Value = current, Collection = old });
                return;
            }

            // Was nil (or a new object): create the GemStone collection.
            var coll = Create(ctx.Kernel(CollectionClassName(p.CollectionKind, p.Shape)));
            var created = ReplaceContents(coll, p.Shape, Items(current), [], p.Path);
            Store(target, p, coll);
            remember(p, () => new PropertySnapshot
            {
                Oop = coll.Resolved, Value = current, Collection = new CollectionSnapshot(coll.Resolved, created()),
            });
        }

        /// <summary>
        /// Changes a loaded collection in place if its contents differ from the snapshot, then does
        /// the same for the collections inside it (they are compared by reference one level up).
        /// </summary>
        private void PatchContents(CollectionSnapshot old, object current, GsShape shape, string where)
        {
            if (!_patched.Add(old)) return; // the same collection reachable twice
            var items = Items(current);
            var before = old.Elements;
            if (!ContentsEqual(items, before, shape.Kind))
            {
                var collOop = BatchValue.Of(old.Oop);
                var elements = shape.Kind switch
                {
                    GsShapeKind.Dictionary => PatchDictionary(collOop, shape, items, before, where),
                    GsShapeKind.Set => PatchUnordered(collOop, shape, items, before, where),
                    _ => PatchOrdered(collOop, shape, items, before, where),
                };
                Stores++;
                _onSuccess.Add(() =>
                {
                    ctx.JournalElements(old);
                    old.Elements = elements();
                });
            }

            var isDict = shape.Kind == GsShapeKind.Dictionary;
            for (var i = 0; i < items.Count; i++)
            {
                var elementShape = isDict ? (i % 2 == 0 ? shape.Key! : shape.Value!) : shape.Element!;
                if (!elementShape.IsCollection || items[i] is not { } inner) continue;
                if (before.FirstOrDefault(e => e.Nested is not null && ReferenceEquals(e.Value, inner)) is { } same)
                    PatchContents(same.Nested!, inner, elementShape, $"element of {where}");
            }
        }

        private readonly HashSet<CollectionSnapshot> _patched = new(ReferenceEqualityComparer.Instance);

        private static string CollectionClassName(GsCollectionKind? kind, GsShape shape) => kind switch
        {
            GsCollectionKind.Array => "Array",
            GsCollectionKind.Ordered => "OrderedCollection",
            GsCollectionKind.Set => "Set",
            GsCollectionKind.IdentitySet => "IdentitySet",
            GsCollectionKind.Bag => "Bag",
            GsCollectionKind.IdentityBag => "IdentityBag",
            GsCollectionKind.Dictionary => "KeyValueDictionary",
            _ => shape.Kind switch
            {
                GsShapeKind.Array => "Array",
                GsShapeKind.Set => "Set",
                GsShapeKind.Dictionary => "KeyValueDictionary",
                _ => "OrderedCollection",
            },
        };

        /// <summary>Plans rewriting a collection's contents (keeps its identity); returns the new snapshot elements, resolved after the batch.</summary>
        private Func<List<SnapElement>> ReplaceContents(BatchValue coll, GsShape shape, List<object?> items, List<SnapElement> old, string where)
        {
            var isDict = shape.Kind == GsShapeKind.Dictionary;
            var planned = new List<Planned>(items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                var elementShape = isDict ? (i % 2 == 0 ? shape.Key! : shape.Value!) : shape.Element!;
                var sameRole = isDict ? old.Where((_, j) => j % 2 == i % 2).ToList() : old;
                planned.Add(Element(items[i], elementShape, sameRole, where));
            }
            _ops.Add(new ReplaceOp(coll, [.. planned.Select(e => e.Value)]));
            return () => [.. planned.Select((e, i) => e.Snap(items[i]))];
        }

        /// <summary>
        /// Ordered: the common head and tail stay, the middle is replaced (one op). On the server
        /// the elements in between are removed by position and the new ones inserted there.
        /// </summary>
        private Func<List<SnapElement>> PatchOrdered(BatchValue coll, GsShape shape, List<object?> items, List<SnapElement> old, string where)
        {
            var min = Math.Min(items.Count, old.Count);
            var head = 0;
            while (head < min && ValueEquals(items[head], old[head].Value)) head++;
            var tail = 0;
            while (tail < min - head && ValueEquals(items[items.Count - 1 - tail], old[old.Count - 1 - tail].Value)) tail++;

            var removed = old.Skip(head).Take(old.Count - head - tail).Select(e => BatchValue.Of(e.Oop)).ToList();
            var middle = items.Skip(head).Take(items.Count - head - tail).ToList();
            var inserted = middle.Select(item => Element(item, shape.Element!, old, where)).ToList();
            _ops.Add(new PatchOp(coll, head + 1, removed, [.. inserted.Select(e => e.Value)]));
            return () =>
            [
                .. old.Take(head),
                .. inserted.Select((e, i) => e.Snap(middle[i])),
                .. old.Skip(old.Count - tail),
            ];
        }

        /// <summary>Unordered (Set, Bag …): elements that are gone are removed, new ones added.</summary>
        private Func<List<SnapElement>> PatchUnordered(BatchValue coll, GsShape shape, List<object?> items, List<SnapElement> old, string where)
        {
            var unmatched = new List<SnapElement>(old);
            var snapshot = new List<Func<SnapElement>>(items.Count);
            var inserted = new List<BatchValue>();
            foreach (var item in items)
            {
                var i = unmatched.FindIndex(e => ValueEquals(item, e.Value));
                if (i >= 0)
                {
                    var kept = unmatched[i];
                    unmatched.RemoveAt(i);
                    snapshot.Add(() => kept);
                    continue;
                }
                var planned = Element(item, shape.Element!, old, where);
                inserted.Add(planned.Value);
                snapshot.Add(() => planned.Snap(item));
            }
            _ops.Add(new PatchOp(coll, 0, [.. unmatched.Select(e => BatchValue.Of(e.Oop))], inserted));
            return () => [.. snapshot.Select(f => f())];
        }

        /// <summary>Dictionaries: keys that are gone are removed, new or changed pairs are put (existing keys keep their object).</summary>
        private Func<List<SnapElement>> PatchDictionary(BatchValue coll, GsShape shape, List<object?> items, List<SnapElement> old, string where)
        {
            var oldKeys = old.Where((_, j) => j % 2 == 0).ToList();
            var oldValues = old.Where((_, j) => j % 2 == 1).ToList();
            var unmatched = Enumerable.Range(0, oldKeys.Count).ToList();
            var puts = new List<BatchValue>();
            var snapshot = new List<Func<SnapElement>>(items.Count);
            for (var i = 0; i < items.Count; i += 2)
            {
                var (key, value) = (items[i], items[i + 1]);
                var k = unmatched.FindIndex(j => ValueEquals(key, oldKeys[j].Value));
                if (k >= 0)
                {
                    var j = unmatched[k];
                    unmatched.RemoveAt(k);
                    var oldKey = oldKeys[j];
                    if (ValueEquals(value, oldValues[j].Value))
                    {
                        var oldValue = oldValues[j];
                        snapshot.Add(() => oldKey);
                        snapshot.Add(() => oldValue);
                        continue;
                    }
                    var v = Element(value, shape.Value!, oldValues, where);
                    puts.Add(BatchValue.Of(oldKey.Oop));
                    puts.Add(v.Value);
                    snapshot.Add(() => oldKey);
                    snapshot.Add(() => v.Snap(value));
                    continue;
                }
                var newKey = Element(key, shape.Key!, oldKeys, where);
                var newValue = Element(value, shape.Value!, oldValues, where);
                puts.Add(newKey.Value);
                puts.Add(newValue.Value);
                snapshot.Add(() => newKey.Snap(key));
                snapshot.Add(() => newValue.Snap(value));
            }
            _ops.Add(new DictionaryPatchOp(coll, [.. unmatched.Select(j => BatchValue.Of(oldKeys[j].Oop))], puts));
            return () => [.. snapshot.Select(f => f())];
        }

        /// <summary>A planned collection element: its value in the batch and how to snapshot it afterwards.</summary>
        private sealed record Planned(BatchValue Value, bool IsSymbol, Func<CollectionSnapshot>? Nested = null)
        {
            public SnapElement Snap(object? item) => new(Value.Resolved, item, IsSymbol, Nested?.Invoke());
        }

        private Planned Element(object? value, GsShape shape, List<SnapElement> old, string where)
        {
            if (shape.IsCollection)
            {
                if (value is null) return new Planned(BatchValue.Of(Oop.Nil), false);
                // The same C# collection as before (maybe moved): keep the GemStone collection;
                // PatchContents brings its contents up to date.
                if (old.FirstOrDefault(e => e.Nested is not null && ReferenceEquals(e.Value, value)) is { Nested: { } nested } kept)
                    return new Planned(BatchValue.Of(kept.Oop), false, () => nested);
                var coll = Create(ctx.Kernel(CollectionClassName(null, shape)));
                var created = ReplaceContents(coll, shape, Items(value), [], $"element of {where}");
                return new Planned(coll, false, () => new CollectionSnapshot(coll.Resolved, created()));
            }
            // Unchanged scalar elements keep their OOP (and with it String vs. Symbol).
            if (value is not null && !IsEntity(value) && old.FirstOrDefault(e => ValueEquals(value, e.Value)) is { } same)
                return new Planned(BatchValue.Of(same.Oop), same.IsSymbol);
            var asSymbol = old.Count > 0 ? old.Any(e => e.IsSymbol) : value is Enum;
            var snap = new PropertySnapshot { Oop = Oop.Nil, IsSymbol = asSymbol };
            return new Planned(ToValue(value, shape, null, snap, where), asSymbol && value is string or Enum);
        }

        private BatchValue ToValue(object? value, GsShape shape, GsPropertyMap? property, PropertySnapshot? snap, string where)
        {
            if (property?.Converter is { } converter)
                return converter is IGsTwoWayConverter twoWay
                    ? Scalar(twoWay.ToGemStone(value, property.PropertyType), snap, where)
                    : throw new NotSupportedException($"{where}: {converter.GetType().Name} cannot convert values back to GemStone (implement IGsTwoWayConverter).");
            if (value is null) return BatchValue.Of(Oop.Nil);
            return shape.Kind switch
            {
                GsShapeKind.Oop => BatchValue.Of((Oop)value),
                GsShapeKind.Mapped => EntityValue(value),
                GsShapeKind.Object => value is Oop o ? BatchValue.Of(o) : IsEntity(value) ? EntityValue(value) : Scalar(value, snap, where),
                GsShapeKind.Scalar => Scalar(value, snap, where),
                _ => throw new NotSupportedException($"{where}: nested collections cannot be saved yet."),
            };
        }

        private BatchValue EntityValue(object entity) =>
            ctx._oops.TryGetValue(entity, out var oop) ? BatchValue.Of(oop) : Insert(entity);

        // ---- Scalars ------------------------------------------------------------------------

        private BatchValue Scalar(object? value, PropertySnapshot? snap, string where)
        {
            switch (value)
            {
                case null: return BatchValue.Of(Oop.Nil);
                case Oop o: return BatchValue.Of(o);
                case Enum e when snap?.Oop.IsSmallInteger == true:
                    return BatchValue.Of(Oop.FromSmallInteger(Convert.ToInt64(e, CultureInfo.InvariantCulture)));
                case Enum e:
                {
                    var name = e.ToString();
                    if (snap?.LowerFirst ?? true) name = char.ToLowerInvariant(name[0]) + name[1..];
                    return Converted(2, Text(name));
                }
                case string s when snap?.IsSymbol == true: return Converted(2, Text(s));
                case string s: return Ascii.IsValid(s) ? New(Text(s)) : Converted(3, Text(s));
                case bool b: return BatchValue.Of(Oop.FromBoolean(b));
                case Rune r: return BatchValue.Of(Oop.FromCharacter(r));
                case char c when !char.IsSurrogate(c): return BatchValue.Of(Oop.FromCharacter(new Rune(c)));
                case sbyte or byte or short or ushort or int or uint or long:
                    return Integer(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                case ulong u: return u <= long.MaxValue ? Integer((long)u) : Converted(4, Ascii8(u.ToString(CultureInfo.InvariantCulture)));
                case BigInteger big:
                    return big >= long.MinValue && big <= long.MaxValue ? Integer((long)big) : Converted(4, Ascii8(big.ToString(CultureInfo.InvariantCulture)));
                case float f: return Double(f);
                case double d: return Double(d);
                case decimal m:
                {
                    // decimal = 96-bit integer / 10^scale: exactly a ScaledDecimal.
                    var bits = decimal.GetBits(m);
                    var unscaled = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
                    if (bits[3] < 0) unscaled = -unscaled;
                    var scale = (bits[3] >> 16) & 0xFF;
                    return Converted(5, Pair(Ascii8(unscaled.ToString(CultureInfo.InvariantCulture)), Oop.FromSmallInteger(scale)));
                }
                case DateOnly date: return Converted(6, Oop.FromSmallInteger(date.DayNumber - GciSession.SmalltalkDateBase));
                case TimeOnly time: return Converted(7, Oop.FromSmallInteger(time.Ticks / TimeSpan.TicksPerMicrosecond));
                case DateTime dt: return DateAndTime(new DateTimeOffset(dt));
                case DateTimeOffset dto: return DateAndTime(dto);
                case byte[] bytes: return New(new NewObjectSpec { Class = KnownClasses.ByteArray, Bytes = (byte[])bytes.Clone() });
                default: throw GsMappingException.For($"{where}: no GemStone mapping for {value.GetType()}.");
            }
        }

        private BatchValue Integer(long value) =>
            Oop.FitsSmallInteger(value) ? BatchValue.Of(Oop.FromSmallInteger(value)) : Converted(4, Ascii8(value.ToString(CultureInfo.InvariantCulture)));

        private BatchValue Double(double value) =>
            Oop.TryFromSmallDouble(value, out var small) ? BatchValue.Of(small) : New(new NewObjectSpec { Class = KnownClasses.Float, Float = value });

        private BatchValue DateAndTime(DateTimeOffset value)
        {
            var micros = (value.UtcTicks - GciPosixDateAndTime.PosixEpoch.Ticks) / TimeSpan.TicksPerMicrosecond;
            return Converted(8, Pair(Oop.FromSmallInteger(micros), Oop.FromSmallInteger((long)value.Offset.TotalSeconds)));
        }

        /// <summary>A String (ASCII) or Utf8 (anything else) for the store traversal.</summary>
        private static NewObjectSpec Text(string s) => Ascii.IsValid(s)
            ? new NewObjectSpec { Class = KnownClasses.String, Bytes = Encoding.ASCII.GetBytes(s) }
            : new NewObjectSpec { Class = KnownClasses.Utf8, Bytes = Encoding.UTF8.GetBytes(s) };

        private static NewObjectSpec Ascii8(string s) => new() { Class = KnownClasses.String, Bytes = Encoding.ASCII.GetBytes(s) };

        private static NewObjectSpec Pair(object first, object second) => new() { Class = KnownClasses.Array, Slots = [first, second] };

        private BatchValue New(NewObjectSpec spec)
        {
            var value = new BatchValue { Spec = spec };
            _onResolve.Add(() => value.Resolved = spec.Oop);
            return value;
        }

        private static BatchValue Converted(int kind, object payload) =>
            new() { Kind = kind, Spec = payload as NewObjectSpec, Payload = payload is Oop o ? o : default };

        private readonly List<Action> _onResolve = [];

        // ---- Sending ------------------------------------------------------------------------

        /// <summary>Sends the batch (if there is anything to do) and applies the snapshots.</summary>
        public void Execute()
        {
            if (_ops.Count > 0) Send();
            foreach (var action in _onSuccess) action();
        }

        private void Send()
        {
            // Every op becomes slots of the ops Array; values are (kind, payload) pairs.
            var converted = new List<BatchValue>();
            var slots = new List<object>();
            void Value(BatchValue v)
            {
                slots.Add(Oop.FromSmallInteger(v.Kind));
                if (v.Kind == 1) slots.Add(Oop.FromSmallInteger(v.Created));
                else slots.Add((object?)v.Spec ?? v.Payload);
                if (v.Kind >= 2)
                {
                    v.Converted = converted.Count;
                    converted.Add(v);
                }
            }
            NewObjectSpec Values(List<BatchValue> values)
            {
                var items = new List<object>(values.Count * 2);
                foreach (var v in values)
                {
                    items.Add(Oop.FromSmallInteger(v.Kind));
                    items.Add(v.Kind == 1 ? Oop.FromSmallInteger(v.Created) : (object?)v.Spec ?? v.Payload);
                    if (v.Kind >= 2)
                    {
                        v.Converted = converted.Count;
                        converted.Add(v);
                    }
                }
                return new NewObjectSpec { Class = KnownClasses.Array, Slots = items };
            }
            foreach (var op in _ops)
            {
                switch (op)
                {
                    case CreateOp c:
                        slots.Add(Oop.FromSmallInteger(1));
                        slots.Add(c.Class);
                        break;
                    case StoreOp st:
                        slots.Add(Oop.FromSmallInteger(2));
                        Value(st.Target);
                        slots.Add(Oop.FromSmallInteger(st.Index));
                        Value(st.Value);
                        break;
                    case ReplaceOp r:
                        slots.Add(Oop.FromSmallInteger(3));
                        Value(r.Target);
                        slots.Add(Values(r.Items));
                        break;
                    case PatchOp pa: // the server evaluates target, removed, inserted in this order
                        slots.Add(Oop.FromSmallInteger(4));
                        Value(pa.Target);
                        slots.Add(Oop.FromSmallInteger(pa.From));
                        slots.Add(Values(pa.Removed));
                        slots.Add(Values(pa.Inserted));
                        break;
                    case DictionaryPatchOp d:
                        slots.Add(Oop.FromSmallInteger(5));
                        Value(d.Target);
                        slots.Add(Values(d.RemovedKeys));
                        slots.Add(Values(d.Puts));
                        break;
                }
            }
            var root = new NewObjectSpec { Class = KnownClasses.Array, Slots = slots };
            var specs = Reachable(root);

            var ids = _session.GetFreeOops(specs.Count);
            for (var i = 0; i < specs.Count; i++) specs[i].Oop = ids[i];
            var objects = specs.Select(ToNewObject).ToList();

            var result = Oop.Nil;
            try
            {
                result = _session.StoreAndExecute(objects, BatchSource, root.Oop);
                var answer = _session.FetchVaryingOops(result);
                if (answer.Length != _created.Count + converted.Count)
                    throw new GciException($"SaveChanges batch answered {answer.Length} objects, expected {_created.Count + converted.Count}.");
                for (var i = 0; i < _created.Count; i++) _created[i].Resolved = answer[i];
                for (var i = 0; i < converted.Count; i++) converted[i].Resolved = answer[_created.Count + i];
                foreach (var action in _onResolve) action();
                ctx.RetainAll(_created.Select(c => c.Resolved)); // new objects: the context owns them now
            }
            finally
            {
                // The objects of the store traversal are referenced from their holders now (or garbage).
                if (_session.IsLoggedIn) _session.Release([.. ids, result]);
            }
        }

        /// <summary>All objects of the store traversal: the ops Array and everything its slots create.</summary>
        private static List<NewObjectSpec> Reachable(NewObjectSpec root)
        {
            var all = new List<NewObjectSpec>();
            var seen = new HashSet<NewObjectSpec>(ReferenceEqualityComparer.Instance);
            var todo = new Stack<NewObjectSpec>();
            todo.Push(root);
            while (todo.Count > 0)
            {
                var spec = todo.Pop();
                if (!seen.Add(spec)) continue;
                all.Add(spec);
                foreach (var slot in spec.Slots ?? [])
                    if (slot is NewObjectSpec child) todo.Push(child);
            }
            return all;
        }

        private static GciNewObject ToNewObject(NewObjectSpec spec)
        {
            if (spec.Float is { } d) return GciNewObject.Float(spec.Oop, d);
            if (spec.Bytes is { } bytes) return GciNewObject.ByteObject(spec.Oop, spec.Class, bytes);
            var oops = spec.Slots!.Select(s => s switch
            {
                Oop o => o,
                NewObjectSpec n => n.Oop,
                _ => throw new InvalidOperationException("Unexpected slot."),
            }).ToArray();
            return GciNewObject.Pointers(spec.Oop, spec.Class, oops);
        }
    }
}
