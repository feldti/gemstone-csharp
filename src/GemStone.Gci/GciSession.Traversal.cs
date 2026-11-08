using System.Text;
using System.Runtime.InteropServices;
using GemStone.Gci.Native;

namespace GemStone.Gci;

/// <summary>
/// Bulk access for RPC sessions: traversals fetch whole object graphs in one round trip (plus
/// GciMoreTraversal round trips for graphs larger than the buffer); <see cref="FetchPaths"/>
/// fetches selected instance-variable paths of many objects in one round trip per path depth.
/// </summary>
public sealed partial class GciSession
{
    /// <summary>
    /// Traverses the graph below <paramref name="root"/> (GciTraverseObjs + GciMoreTraversal).
    /// </summary>
    /// <param name="root">The object to start from.</param>
    /// <param name="level">1 = only the root, 2 = also its instance variables, …; 0 = unlimited
    /// (beware: may fetch large parts of the repository).</param>
    /// <param name="options">Buffer size and other options; null = defaults.</param>
    public GciTraversal Traverse(Oop root, int level, GciTraversalOptions? options = null) =>
        Traverse([root], level, options);

    /// <inheritdoc cref="Traverse(Oop, int, GciTraversalOptions?)"/>
    public unsafe GciTraversal Traverse(Oop[] roots, int level, GciTraversalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        options ??= new GciTraversalOptions();
        lock (Sync)
        {
            using var buffer = new TravBuffer(options.BufferSize);
            bool done;
            fixed (Oop* p = roots)
            {
                Activate();
                done = GciNative.GciTraverseObjs((ulong*)p, roots.Length, buffer.Pointer, level);
                ThrowIfError();
            }
            var reports = CollectAll(buffer, done);
            return new GciTraversal(roots, null, reports);
        }
    }

    /// <summary>
    /// Executes Smalltalk and traverses the result (<see cref="Execute"/> + <see cref="Traverse(Oop, int, GciTraversalOptions?)"/>).
    /// </summary>
    /// <remarks>
    /// Deliberately not GciExecuteStrTrav: in 3.7.5 it leaves the source String in the
    /// PureExportSet with no way to release it (verified via <c>System hiddenSetAsArray:</c>),
    /// so every call would leak an object in the gem. Costs one extra round trip.
    /// </remarks>
    /// <param name="source">Smalltalk code; its result is traversed.</param>
    /// <param name="level">As for <see cref="Traverse(Oop, int, GciTraversalOptions?)"/>, relative to the result.</param>
    /// <param name="options">Buffer size, KeepResult …; null = defaults.</param>
    /// <param name="symbolList">Symbol list for compiling; null = the user's default.</param>
    public GciTraversal ExecuteTraverse(string source, int level, GciTraversalOptions? options = null, Oop? symbolList = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        options ??= new GciTraversalOptions();
        lock (Sync)
        {
            var result = Execute(source, symbolList);
            try
            {
                var traversal = Traverse(result, level, options);
                return new GciTraversal([result], result, [.. traversal.Reports]);
            }
            finally
            {
                if (!options.KeepResult) Release(result);
            }
        }
    }

    /// <summary>Sends a message and traverses the result in the same round trip (GciPerformTrav).</summary>
    public unsafe GciTraversal PerformTraverse(Oop receiver, string selector, int level, Oop[] args, GciTraversalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        options ??= new GciTraversalOptions();
        lock (Sync)
        {
            using var buffer = new TravBuffer(options.BufferSize);
            var travArgs = GciClampedTravArgsSType.Create(buffer.Pointer, level, retrievalFlags: 0);
            ulong dummy = Oop.Nil.Value;
            bool done;
            fixed (Oop* p = args)
            {
                Activate();
                done = GciNative.GciPerformTrav(receiver.Value, selector, args.Length == 0 ? &dummy : (ulong*)p, args.Length, &travArgs);
                ThrowIfError();
            }
            var traversal = FinishClamped(buffer, done, travArgs.ResultOop);
            if (!options.KeepResult) Release(traversal.Result!.Value); // GciPerformTrav exports the result
            return traversal;
        }
    }

    /// <inheritdoc cref="PerformTraverse(Oop, string, int, Oop[], GciTraversalOptions?)"/>
    public GciTraversal PerformTraverse(Oop receiver, string selector, int level, params Oop[] args) =>
        PerformTraverse(receiver, selector, level, args, null);

    /// <summary>
    /// Fetches instance-variable paths from many objects (GciFetchPaths).
    /// Each path is a sequence of 1-based steps: <c>+n</c> = named instVar n, <c>-n</c> = indexed
    /// slot n (see <see cref="GciPath"/>). Result <c>[i][j]</c> is path j applied to object i;
    /// a path that cannot be followed (nil on the way, out of range, byte object, not readable)
    /// yields <see cref="Oop.Illegal"/>.
    /// </summary>
    /// <remarks>
    /// GemStone 3.7.5 crashes the gem (SIGSEGV in <c>checkFetchPathOffset</c> → <c>om::FetchSize_c</c>)
    /// when a path steps <em>from</em> a special object such as nil or a SmallInteger — the
    /// session is lost. Paths are therefore resolved one step at a time: one GciFetchPaths
    /// call per depth for all live (object, step) combinations, never stepping from a special
    /// object. Round trips = length of the longest path, independent of the number of objects.
    /// </remarks>
    public Oop[][] FetchPaths(Oop[] objects, params int[][] paths)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
            if (path is null || path.Length == 0 || path.Contains(0))
                throw new ArgumentException("Paths must be non-empty and must not contain 0.", nameof(paths));

        var results = objects.Select(_ => new Oop[paths.Length]).ToArray();
        var current = objects.Select(o => paths.Select(_ => o).ToArray()).ToArray();
        var live = new List<(int Obj, int Path)>();
        for (var i = 0; i < objects.Length; i++)
            for (var j = 0; j < paths.Length; j++)
                if (CanStepFrom(objects[i])) live.Add((i, j));
                else results[i][j] = Oop.Illegal;

        lock (Sync)
        {
            for (var depth = 0; live.Count > 0; depth++)
            {
                // One call: all distinct current objects × all distinct steps at this depth.
                var froms = live.Select(p => current[p.Obj][p.Path]).Distinct().ToArray();
                var steps = live.Select(p => paths[p.Path][depth]).Distinct().ToArray();
                var fromIndex = froms.Select((o, k) => (o, k)).ToDictionary(x => x.o, x => x.k);
                var stepIndex = steps.Select((st, k) => (st, k)).ToDictionary(x => x.st, x => x.k);
                var fetched = FetchSingleSteps(froms, steps);

                var next = new List<(int Obj, int Path)>(live.Count);
                foreach (var (i, j) in live)
                {
                    var value = fetched[fromIndex[current[i][j]] * steps.Length + stepIndex[paths[j][depth]]];
                    if (depth == paths[j].Length - 1)
                        results[i][j] = value;
                    else if (!CanStepFrom(value))
                        results[i][j] = Oop.Illegal;
                    else
                    {
                        current[i][j] = value;
                        next.Add((i, j));
                    }
                }
                live = next;
            }
        }
        return results;

        static bool CanStepFrom(Oop o) => !o.IsSpecial && o != Oop.Illegal;
    }

    /// <summary>GciFetchPaths with one-step paths; results[from * steps.Length + step].</summary>
    private unsafe Oop[] FetchSingleSteps(Oop[] froms, int[] steps)
    {
        var sizes = Enumerable.Repeat(1, steps.Length).ToArray();
        var results = new Oop[froms.Length * steps.Length];
        fixed (Oop* o = froms)
        fixed (int* f = steps)
        fixed (int* s = sizes)
        fixed (Oop* r = results)
        {
            Activate();
            // FALSE just means at least one step failed (OOP_ILLEGAL in results); a real
            // error (e.g. an invalid OOP) is reported via GciErr.
            GciNative.GciFetchPaths((ulong*)o, froms.Length, f, s, steps.Length, (ulong*)r);
            ThrowIfError();
        }
        return results;
    }

    // ---- Buffer handling ------------------------------------------------------------------

    private unsafe GciTraversal FinishClamped(TravBuffer buffer, bool done, ulong resultOop)
    {
        var result = new Oop(resultOop);
        return new GciTraversal([result], result, CollectAll(buffer, done));
    }

    /// <summary>Copies the reports of the current buffer, then continues with GciMoreTraversal until done.</summary>
    private unsafe List<GciObjectReport> CollectAll(TravBuffer buffer, bool done)
    {
        var reports = new List<GciObjectReport>();
        while (true)
        {
            buffer.ReadReports(reports);
            if (done) return reports;
            Activate();
            done = GciNative.GciMoreTraversal(buffer.Pointer);
            ThrowIfError();
        }
    }

    /// <summary>A GciTravBufType in native memory: 8-byte header + body.</summary>
    private sealed unsafe class TravBuffer : IDisposable
    {
        private readonly int _bodySize;

        public TravBuffer(int bodySize)
        {
            _bodySize = Math.Max(GciTravBufHeader.MinBodySize, (bodySize + 7) & ~7);
            Pointer = (byte*)NativeMemory.AlignedAlloc((nuint)(GciTravBufHeader.Size + _bodySize), 8);
            Reset();
        }

        public byte* Pointer { get; private set; }

        private ref GciTravBufHeader Header => ref *(GciTravBufHeader*)Pointer;

        private void Reset()
        {
            Header.AllocatedBytes = (uint)_bodySize;
            Header.UsedBytes = 0;
        }

        public void ReadReports(List<GciObjectReport> into)
        {
            var body = Pointer + GciTravBufHeader.Size;
            var used = Header.UsedBytes;
            if (used > _bodySize) throw new GciException($"Traversal buffer reports {used} used bytes of {_bodySize}.");
            for (uint offset = 0; offset + GciObjRepHdrSType.Size <= used;)
            {
                var hdr = *(GciObjRepHdrSType*)(body + offset);
                var values = body + offset + GciObjRepHdrSType.Size;
                var oops = Array.Empty<Oop>();
                var bytes = Array.Empty<byte>();
                if (hdr.ObjImpl == (int)GciObjectFormat.Byte)
                    bytes = new ReadOnlySpan<byte>(values, hdr.ValueBuffSize).ToArray();
                else if (hdr.ObjImpl is (int)GciObjectFormat.Pointer or (int)GciObjectFormat.Nsc)
                    oops = MemoryMarshal.Cast<byte, Oop>(new ReadOnlySpan<byte>(values, hdr.ValueBuffSize)).ToArray();
                into.Add(new GciObjectReport(hdr, oops, bytes));
                offset += (uint)hdr.UsedBytes;
            }
            Reset(); // GciMoreTraversal refills from the start
        }

        public void Dispose()
        {
            if (Pointer == null) return;
            NativeMemory.AlignedFree(Pointer);
            Pointer = null;
        }
    }
}

/// <summary>Builds <see cref="GciSession.FetchPaths"/> path steps.</summary>
public static class GciPath
{
    /// <summary>Named instance variable (1-based).</summary>
    public static int Named(int index) => index > 0 ? index : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Indexed slot (1-based).</summary>
    public static int Indexed(int index) => index > 0 ? -index : throw new ArgumentOutOfRangeException(nameof(index));
}
