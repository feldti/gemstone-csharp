using System.Text;
using GemStone.Gci.Native;

namespace GemStone.Gci;

/// <summary>Network parameters passed to <c>GciSetNet</c>.</summary>
/// <param name="StoneName">Stone name or NRS, e.g. <c>"gs64stone"</c> or <c>"!@host!gs64stone"</c>.</param>
/// <param name="HostUserId">OS user on the gem host; empty if NetLDI runs with <c>-a -g</c>.</param>
/// <param name="HostPassword">OS password on the gem host; empty if not required.</param>
/// <param name="GemService">Gem service NRS, usually <c>"gemnetobject"</c>.</param>
public sealed record GciNetParameters(
    string StoneName,
    string HostUserId = "",
    string HostPassword = "",
    string GemService = "gemnetobject");

/// <summary>
/// A GemStone user session via the classic GCI. Dispose to log out.
/// </summary>
/// <remarks>
/// <para>
/// Several sessions may be logged in at once (each <see cref="Login"/> creates a new, independent
/// gem session). The classic GCI is not thread-safe and talks to one "current session" at a
/// time; every call therefore runs under the process-wide <see cref="Sync"/> lock and first makes
/// its own session current (<c>GciSetSessionId</c>). Sessions can be used from different
/// threads, but their calls are serialized.
/// </para>
/// <para>
/// OOPs of temporary objects belong to the session that created them; only persistent objects
/// (committed, or kernel classes) have the same OOP in every session.
/// </para>
/// </remarks>
public sealed partial class GciSession : IDisposable
{
    private const int InvalidSessionId = 0; // GCI_INVALID_SESSION_ID

    internal static readonly object Sync = new();
    private static bool _initialized;
    private static int _currentSessionId = InvalidSessionId; // mirrors GciGetSessionId(), guarded by Sync

    private readonly int _sessionId;
    private bool _loggedIn;

    private GciSession(int sessionId)
    {
        _sessionId = sessionId;
        _loggedIn = true;
    }

    /// <summary>False after <see cref="Dispose"/> or after a fatal error dropped the connection.</summary>
    public bool IsLoggedIn { get { lock (Sync) return _loggedIn; } }

    /// <summary>The GCI session id (<c>GciGetSessionId</c>), unique within this process.</summary>
    public int SessionId => _sessionId;

    /// <summary>Calls <c>GciInit</c> once per process. Called implicitly by <see cref="Login"/>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not a 64-bit process.</exception>
    public static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (_initialized) return;
            // GemStone/S 64 Bit only ships 64-bit client libraries (also on Windows). Fail with a
            // clear message instead of a BadImageFormatException from the loader.
            if (!Environment.Is64BitProcess)
                throw new PlatformNotSupportedException(
                    "GemStone/S 64 Bit client libraries require a 64-bit process (x64).");
            if (!GciNative.GciInit())
                throw new GciException("GciInit failed");
            _initialized = true;
        }
    }

    /// <summary>
    /// GciSetNet + GciLoginEx. Throws <see cref="GciException"/> on failure.
    /// </summary>
    /// <param name="flags">Login flags; the default <see cref="GciLoginFlags.Quiet"/> suppresses
    /// the banner the GCI library would otherwise print to stdout.</param>
    /// <param name="haltOnErrNum">-1 keeps the GEM_HALT_ON_ERROR config setting, 0 never halts.</param>
    public static GciSession Login(
        GciNetParameters net, string userName, string password,
        GciLoginFlags flags = GciLoginFlags.Quiet, int haltOnErrNum = -1)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(password);

        lock (Sync)
        {
            EnsureInitialized();
            GciNative.GciSetNet(net.StoneName, net.HostUserId, net.HostPassword, net.GemService);
            if (!GciNative.GciLoginEx(userName, password, (uint)flags, haltOnErrNum))
            {
                // A failed login leaves the previously current session in place.
                _currentSessionId = GciNative.GciGetSessionId();
                throw LastError() ?? new GciException("GciLoginEx failed without error report");
            }
            // The new session is now the current one.
            _currentSessionId = GciNative.GciGetSessionId();
            return new GciSession(_currentSessionId);
        }
    }

    /// <summary>
    /// Executes Smalltalk source via <c>GciExecuteStr</c> and returns the result OOP.
    /// </summary>
    /// <param name="source">One or more Smalltalk statements.</param>
    /// <param name="symbolList">Symbol list to resolve names in; <c>null</c> (OOP_NIL) uses
    /// <c>System myUserProfile symbolList</c>.</param>
    /// <exception cref="GciException">The code raised an error.</exception>
    /// <remarks>
    /// <c>GciExecuteStr</c> compiles its C string as an 8-bit String, which would turn UTF-8
    /// multi-byte sequences into garbage. Non-ASCII source is therefore sent as a Utf8 object
    /// via <c>GciNewUtf8String_</c> + <c>GciExecute</c> (one extra round trip).
    /// </remarks>
    public unsafe Oop Execute(string source, Oop? symbolList = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var symList = (symbolList ?? Oop.Nil).Value;
        lock (Sync)
        {
            Activate();
            if (Ascii.IsValid(source))
                return CheckResult(new Oop(GciNative.GciExecuteStr(source, symList)));

            var bytes = Encoding.UTF8.GetBytes(source);
            Oop sourceObj;
            fixed (byte* p = bytes)
                sourceObj = new Oop(GciNative.GciNewUtf8String_(p, (nuint)bytes.Length, utf8OrUnicode: false));
            ThrowIfError();
            try
            {
                return CheckResult(new Oop(GciNative.GciExecute(sourceObj.Value, symList)));
            }
            finally
            {
                Release(sourceObj);
            }
        }
    }

    /// <summary>
    /// Sends <paramref name="selector"/> to <paramref name="receiver"/> via <c>GciPerform</c>.
    /// </summary>
    /// <param name="selector">Selector with keywords concatenated, e.g. <c>"at:put:"</c>.</param>
    /// <exception cref="GciException">The message send raised an error.</exception>
    public unsafe Oop Perform(Oop receiver, string selector, params Oop[] args)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(args);

        // Oop is a single-ulong record struct, so the array can be passed as OopType[] directly.
        // The C API wants a valid pointer even for zero arguments ("use a dummy OOP").
        ulong dummy = Oop.Nil.Value;
        lock (Sync)
        {
            Activate();
            fixed (Oop* p = args)
            {
                var argPtr = args.Length == 0 ? &dummy : (ulong*)p;
                var result = new Oop(GciNative.GciPerform(receiver.Value, selector, argPtr, args.Length));
                return CheckResult(result);
            }
        }
    }

    /// <summary>Size of an object (bytes for byte objects, elements for collections).</summary>
    public long FetchSize(Oop obj)
    {
        lock (Sync)
        {
            Activate();
            var size = GciNative.GciFetchSize_(obj.Value);
            ThrowIfError();
            return size;
        }
    }

    /// <summary>All bytes of a byte-format object (String, Utf8, ByteArray …).</summary>
    public unsafe byte[] FetchBytes(Oop obj)
    {
        lock (Sync)
        {
            var size = FetchSize(obj);
            var bytes = new byte[size];
            if (size == 0) return bytes;
            fixed (byte* p = bytes)
            {
                var n = GciNative.GciFetchBytes_(obj.Value, 1, p, size);
                ThrowIfError();
                return n == size ? bytes : bytes[..(int)n];
            }
        }
    }

    /// <summary>
    /// The object's <c>printString</c>, transferred as UTF-8. Intermediate objects are released.
    /// </summary>
    public string PrintString(Oop obj)
    {
        lock (Sync)
        {
            var str = Perform(obj, "printString");
            try { return ToStringValue(str); }
            finally { Release(str); }
        }
    }

    /// <summary>
    /// Removes OOPs from the PureExportSet. Results of <see cref="Execute"/> and
    /// <see cref="Perform"/> stay referenced there until released. Special objects
    /// (SmallInteger, nil, …) are never in an export set and are skipped.
    /// </summary>
    public unsafe void Release(params Oop[] oops)
    {
        ArgumentNullException.ThrowIfNull(oops);
        if (Array.TrueForAll(oops, o => o.IsSpecial)) return;
        var heapOops = Array.FindAll(oops, o => !o.IsSpecial);
        lock (Sync)
        {
            Activate();
            fixed (Oop* p = heapOops)
                GciNative.GciReleaseOops((ulong*)p, heapOops.Length);
            ThrowIfError();
        }
    }

    /// <summary>
    /// Adds OOPs to the PureExportSet (GciSaveObjs), so that the gem does not garbage-collect
    /// them while the client holds them, e.g. temporary objects found by a traversal. Undo with
    /// <see cref="Release"/>. The export set is a set: one Release cancels any number of Retains.
    /// </summary>
    public unsafe void Retain(params Oop[] oops)
    {
        ArgumentNullException.ThrowIfNull(oops);
        var heapOops = Array.FindAll(oops, o => !o.IsSpecial && o != Oop.Illegal);
        if (heapOops.Length == 0) return;
        lock (Sync)
        {
            Activate();
            fixed (Oop* p = heapOops)
                GciNative.GciSaveObjs((ulong*)p, heapOops.Length);
            ThrowIfError();
        }
    }

    /// <summary>
    /// Interrupts Smalltalk code this session is currently running; the running
    /// <see cref="Execute"/>/<see cref="Perform"/> then throws a <see cref="GciException"/>
    /// (RT_ERR_SOFT_BREAK). Called from another thread while the call is in progress, so it
    /// deliberately takes no lock (GciGetSessionId and GciSoftBreak are allowed then). Does
    /// nothing if another session is current, i.e. this one is not executing.
    /// </summary>
    public void SoftBreak()
    {
        if (GciNative.GciGetSessionId() == _sessionId)
            GciNative.GciSoftBreak();
    }

    /// <summary>OOP_NIL is both a valid result and the error marker; disambiguate via GciErr.</summary>
    private Oop CheckResult(Oop result)
    {
        if (result.IsNil) ThrowIfError();
        return result;
    }

    private void ThrowIfError()
    {
        var error = LastError();
        if (error is null) return;
        if (error.IsFatal)
        {
            _loggedIn = false; // connection is gone; GciLogout would be invalid
            _currentSessionId = GciNative.GciGetSessionId(); // reset to GCI_INVALID_SESSION_ID by the GCI
        }
        else
        {
            if (error.Context != Oop.Nil.Value && error.Context != 0)
                GciNative.GciClearStack(error.Context); // we never resume (GciContinue), so drop the suspended GsProcess
            ReleaseErrorOops(error);
        }
        throw error;
    }

    /// <summary>
    /// GciErr adds all OOPs of the error report to the PureExportSet (manual 1.4). Since the
    /// stack is cleared and the exception only carries them for information, release them so
    /// repeated errors do not grow the gem's memory.
    /// </summary>
    private static unsafe void ReleaseErrorOops(GciException error)
    {
        var oops = new List<ulong>(2 + error.Args.Count) { error.Context, error.ExceptionObj };
        oops.AddRange(error.Args);
        var heap = oops.Where(o => o != 0 && !new Oop(o).IsSpecial).ToArray();
        if (heap.Length == 0) return;
        fixed (ulong* p = heap)
            GciNative.GciReleaseOops(p, heap.Length);
        LastError(); // a failure here must not mask the original error
    }

    /// <summary>
    /// Guard for every native call (caller holds <see cref="Sync"/>): throws if logged out, then
    /// makes this session the GCI's current session.
    /// </summary>
    private void Activate()
    {
        SwitchTo();
        if (_nbInProgress)
            throw new InvalidOperationException(
                "An asynchronous operation is in progress on this session; await it before making further calls.");
    }

    /// <summary>Makes this session current without the in-progress check (used while polling a nonblocking call).</summary>
    private void SwitchTo()
    {
        if (!_loggedIn)
            throw new ObjectDisposedException(nameof(GciSession), "Session is logged out or was lost after a fatal error.");
        if (_currentSessionId == _sessionId) return;
        GciNative.GciSetSessionId(_sessionId);
        _currentSessionId = _sessionId;
    }

    /// <summary>Fetches and clears the pending GCI error, or null if none.</summary>
    internal static unsafe GciException? LastError()
    {
        GciErrSType err;
        return GciNative.GciErr(&err) ? new GciException(err) : null;
    }

    public void Dispose()
    {
        lock (Sync)
        {
            if (!_loggedIn) return;
            SwitchTo();
            if (_nbInProgress) FinishPendingNonblockingCall();
            _loggedIn = false;
            GciNative.GciLogout();
            _currentSessionId = InvalidSessionId; // GciLogout resets the current session
            LastError(); // nothing left to report to; don't leak it into the next call
        }
    }
}
