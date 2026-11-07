using System.Runtime.InteropServices;

namespace GemStone.Gci.Native;

/// <summary>
/// Raw P/Invoke declarations for the (non thread-safe) GemBuilder for C RPC library.
/// Signatures follow gemstone-c/07-gembuilder-c-function-reference/*.md.
/// </summary>
/// <remarks>
/// <c>BoolType</c> is a C <c>int</c>, marshalled as a 4-byte <see cref="UnmanagedType.Bool"/>.
/// All <c>const char[]</c> arguments are null-terminated UTF-8.
/// </remarks>
internal static partial class GciNative
{
    /// <summary>Logical name; mapped to the real file by <see cref="GciLibraryResolver"/>.</summary>
    public const string LibraryName = "gcirpc";

    /// <summary>BoolType GciInit(); — must be called once before any other GCI call.</summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GciInit();

    /// <summary>void GciSetNet(stoneNameNrs, HostUserId, HostPassword, gemServiceNrs);</summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void GciSetNet(
        string stoneNameNrs, string hostUserId, string hostPassword, string gemServiceNrs);

    /// <summary>BoolType GciLogin(gemstoneUsername, gemstonePassword);</summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GciLogin(string gemstoneUsername, string gemstonePassword);

    /// <summary>
    /// BoolType GciLoginEx(gemstoneUsername, gemstonePassword, unsigned int loginFlags, int haltOnErrNum);
    /// haltOnErrNum: -1 keeps the GEM_HALT_ON_ERROR config setting, 0 never halts.
    /// </summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GciLoginEx(
        string gemstoneUsername, string gemstonePassword, uint loginFlags, int haltOnErrNum);

    /// <summary>GciSessionIdType (int) GciGetSessionId(); — GCI_INVALID_SESSION_ID (0) if not logged in.</summary>
    [LibraryImport(LibraryName)]
    public static partial int GciGetSessionId();

    /// <summary>void GciSetSessionId(GciSessionIdType sessionId); — makes another logged-in session current.</summary>
    [LibraryImport(LibraryName)]
    public static partial void GciSetSessionId(int sessionId);

    /// <summary>void GciLogout();</summary>
    [LibraryImport(LibraryName)]
    public static partial void GciLogout();

    /// <summary>
    /// OopType GciExecuteStr(const char source[], OopType symbolList);
    /// Returns OOP_NIL on error — check <see cref="GciErr"/>, since nil is also a valid result.
    /// </summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial ulong GciExecuteStr(string source, ulong symbolList);

    /// <summary>
    /// OopType GciExecute(OopType source, OopType symbolList); — source is a String or Utf8.
    /// Returns OOP_NIL on error.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong GciExecute(ulong source, ulong symbolList);

    /// <summary>
    /// OopType GciNewUtf8String_(const char* unicodeCString, size_t nBytes, BoolType utf8OrUnicode);
    /// utf8OrUnicode = false → instance of Utf8.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static unsafe partial ulong GciNewUtf8String_(
        byte* unicodeCString, nuint nBytes, [MarshalAs(UnmanagedType.Bool)] bool utf8OrUnicode);

    /// <summary>
    /// OopType GciPerform(OopType receiver, const char selector[], const OopType args[], int numArgs);
    /// Returns OOP_NIL on error — check <see cref="GciErr"/>.
    /// </summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial ulong GciPerform(ulong receiver, string selector, ulong* args, int numArgs);

    /// <summary>int64 GciFetchSize_(OopType theObject); — 0 on error.</summary>
    [LibraryImport(LibraryName)]
    public static partial long GciFetchSize_(ulong theObject);

    /// <summary>int64 GciFetchBytes_(OopType theObject, int64 startIndex (1-based), ByteType theBytes[], int64 numBytes); — 0 on error.</summary>
    [LibraryImport(LibraryName)]
    public static unsafe partial long GciFetchBytes_(ulong theObject, long startIndex, byte* theBytes, long numBytes);

    /// <summary>void GciReleaseOops(const OopType theOops[], int numOops); — remove from the PureExportSet.</summary>
    [LibraryImport(LibraryName)]
    public static unsafe partial void GciReleaseOops(ulong* theOops, int numOops);

    /// <summary>void GciClearStack(OopType gsProcess); — abandon a suspended execution.</summary>
    [LibraryImport(LibraryName)]
    public static partial void GciClearStack(ulong gsProcess);

    /// <summary>
    /// void GciSoftBreak(); — interrupt running Smalltalk. One of the few GCI calls that may be
    /// made from another thread while a call is in progress.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial void GciSoftBreak();

    // ---- Transactions ---------------------------------------------------------------------

    /// <summary>BoolType GciCommit(); — FALSE with no pending GciErr means a transaction conflict.</summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GciCommit();

    /// <summary>void GciAbort();</summary>
    [LibraryImport(LibraryName)]
    public static partial void GciAbort();

    /// <summary>void GciBegin(); — aborts a transaction in progress, then begins a new one.</summary>
    [LibraryImport(LibraryName)]
    public static partial void GciBegin();

    // ---- Value conversion ------------------------------------------------------------------

    /// <summary>int64 GciOopToI64_(OopType arg, BoolType *error); — *error set TRUE if not an Integer or out of range.</summary>
    [LibraryImport(LibraryName)]
    public static unsafe partial long GciOopToI64_(ulong arg, int* error);

    /// <summary>OopType GciI64ToOop(int64 arg); — SmallInteger or new LargeInteger.</summary>
    [LibraryImport(LibraryName)]
    public static partial ulong GciI64ToOop(long arg);

    /// <summary>BoolType GciOopToFlt_(OopType theObject, double* result); — SmallDouble, SmallFloat or Float.</summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GciOopToFlt_(ulong theObject, double* result);

    /// <summary>OopType GciFltToOop(double aReal); — SmallDouble or new Float; OOP_NIL on error.</summary>
    [LibraryImport(LibraryName)]
    public static partial ulong GciFltToOop(double aReal);

    /// <summary>OopType GciNewString(const char *cString); — 8-bit String, pass ASCII only.</summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial ulong GciNewString(string cString);

    /// <summary>OopType GciNewSymbol(const char *cString); — pass ASCII only.</summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial ulong GciNewSymbol(string cString);

    /// <summary>
    /// int64 GciFetchUtf8Bytes_(OopType aString, int64 startIndex, ByteType *buf, int64 bufSize,
    ///                          OopType *utf8String, int flags);
    /// Encodes any CharacterCollection as UTF-8. If the result == bufSize, fetch the remaining
    /// bytes from *utf8String; release *utf8String afterwards if it differs from aString.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static unsafe partial long GciFetchUtf8Bytes_(
        ulong aString, long startIndex, byte* buf, long bufSize, ulong* utf8String, int flags);

    /// <summary>OopType GciFetchClass(OopType theObject); — OOP_NIL on error.</summary>
    [LibraryImport(LibraryName)]
    public static partial ulong GciFetchClass(ulong theObject);

    /// <summary>BoolType GciIsKindOfClass(OopType anObj, OopType aClass);</summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GciIsKindOfClass(ulong anObj, ulong aClass);

    /// <summary>
    /// OopType GciNewDateAndTimePosix(int64 microseconds, int offsetSeconds); — OOP_ILLEGAL on error.
    /// Epoch: see <see cref="GciPosixDateAndTime"/>.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong GciNewDateAndTimePosix(long microseconds, int offsetSeconds);

    /// <summary>BoolType GciFetchDateAndTimePosix(OopType dtObj, GciPosixDateAndTime *result);</summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GciFetchDateAndTimePosix(ulong dtObj, GciPosixDateAndTime* result);

    // ---- Object creation and structural access ---------------------------------------------
    // Signatures checked against gci.hf (3.7.5): note the int vs int64 index/count parameters.

    /// <summary>OopType GciNewByteObj(OopType aClass, const ByteType *value, int64 valueSize);</summary>
    [LibraryImport(LibraryName)]
    public static unsafe partial ulong GciNewByteObj(ulong aClass, byte* value, long valueSize);

    /// <summary>void GciNewOops(int numOops, const OopType oclass[], const int64 idxSize[], OopType result[]);</summary>
    [LibraryImport(LibraryName)]
    public static unsafe partial void GciNewOops(int numOops, ulong* oclass, long* idxSize, ulong* result);

    /// <summary>int GciFetchNamedSize(OopType theObject); — 0 on error.</summary>
    [LibraryImport(LibraryName)]
    public static partial int GciFetchNamedSize(ulong theObject);

    /// <summary>int64 GciFetchVaryingSize_(OopType theObject); — 0 on error.</summary>
    [LibraryImport(LibraryName)]
    public static partial long GciFetchVaryingSize_(ulong theObject);

    /// <summary>int GciFetchObjImpl(OopType theObject); — 0 pointer, 1 byte, 2 NSC, 3 special, -1 error.</summary>
    [LibraryImport(LibraryName)]
    public static partial int GciFetchObjImpl(ulong theObject);

    /// <summary>int GciFetchNamedOops(OopType theObject, int startIndex, OopType theOops[], int numOops);</summary>
    [LibraryImport(LibraryName)]
    public static unsafe partial int GciFetchNamedOops(ulong theObject, int startIndex, ulong* theOops, int numOops);

    /// <summary>int GciFetchVaryingOops(OopType theObject, int64 startIndex, OopType theOops[], int numOops);</summary>
    [LibraryImport(LibraryName)]
    public static unsafe partial int GciFetchVaryingOops(ulong theObject, long startIndex, ulong* theOops, int numOops);

    /// <summary>void GciStoreNamedOops(OopType theObject, int64 startIndex, const OopType theOops[], int numOops);</summary>
    [LibraryImport(LibraryName)]
    public static unsafe partial void GciStoreNamedOops(ulong theObject, long startIndex, ulong* theOops, int numOops);

    /// <summary>void GciStoreIdxOops(OopType theObject, int64 startIndex, const OopType theOops[], int numOops); — grows the object if needed.</summary>
    [LibraryImport(LibraryName)]
    public static unsafe partial void GciStoreIdxOops(ulong theObject, long startIndex, ulong* theOops, int numOops);

    /// <summary>void GciAppendOops(OopType theObject, int numOops, const OopType *theOops);</summary>
    [LibraryImport(LibraryName)]
    public static unsafe partial void GciAppendOops(ulong theObject, int numOops, ulong* theOops);

    /// <summary>
    /// void GciStoreBytesInstanceOf(OopType theClass, OopType theObject, int64 startIndex, const ByteType theBytes[], int64 numBytes);
    /// <c>GciStoreBytes</c> is a macro for this with theClass = OOP_NIL.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static unsafe partial void GciStoreBytesInstanceOf(ulong theClass, ulong theObject, long startIndex, byte* theBytes, long numBytes);

    /// <summary>void GciAppendBytes(OopType theObject, int64 numBytes, const ByteType *theBytes);</summary>
    [LibraryImport(LibraryName)]
    public static unsafe partial void GciAppendBytes(ulong theObject, long numBytes, byte* theBytes);

    /// <summary>OopType GciResolveSymbol(const char *cString, OopType symbolList); — result is in the export set.</summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial ulong GciResolveSymbol(string cString, ulong symbolList);

    /// <summary>
    /// OopType GciResolveSymbolObj(OopType aString, OopType symbolList); — like GciResolveSymbol,
    /// but the name is an object owned by the caller (GciResolveSymbol leaks its internal String
    /// in the PureExportSet, verified with 3.7.5).
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong GciResolveSymbolObj(ulong aString, ulong symbolList);

    /// <summary>int GciIvNameToIdx(OopType oclass, const char instVarName[]); — 1-based, 0 if not found.</summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GciIvNameToIdx(ulong oclass, string instVarName);

    /// <summary>void GciAddOopToNsc(OopType theNsc, OopType theOop);</summary>
    [LibraryImport(LibraryName)]
    public static partial void GciAddOopToNsc(ulong theNsc, ulong theOop);

    /// <summary>BoolType GciRemoveOopFromNsc(OopType theNsc, OopType theOop); — FALSE if it was not present.</summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GciRemoveOopFromNsc(ulong theNsc, ulong theOop);

    /// <summary>BoolType GciNscIncludesOop(OopType theNsc, OopType theOop);</summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GciNscIncludesOop(ulong theNsc, ulong theOop);

    // ---- Nonblocking calls ------------------------------------------------------------------
    // Start with GciNb*, then poll GciNbEndPoll until READY/HAS_ERROR. While a call is in
    // progress, only GciErr, GciGetSessionId/GciSetSessionId, GciSoftBreak/GciHardBreak,
    // GciCallInProgress, GciNbEnd* and GciShutdown may be called for that session; other
    // sessions may be used normally (manual 2.5).

    /// <summary>GciNbProgressEType (gci.hf) — HAS_ERROR is not in the manual.</summary>
    public enum NbProgress
    {
        NotReady = 0,    // GCI_RESULT_NOT_READY
        Progressed = 1,  // GCI_RESULT_PROGRESSED
        Ready = 2,       // GCI_RESULT_READY
        HasError = 3,    // GCI_RESULT_HAS_ERROR
    }

    /// <summary>GciNbProgressEType GciNbEndPoll(int64 *vOut, int timeoutMs); — timeoutMs 0 just checks.</summary>
    [LibraryImport(LibraryName)]
    public static unsafe partial NbProgress GciNbEndPoll(long* vOut, int timeoutMs);

    /// <summary>void GciNbExecuteStr(const char source[], OopType symbolList);</summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void GciNbExecuteStr(string source, ulong symbolList);

    /// <summary>void GciNbExecute(OopType source, OopType symbolList);</summary>
    [LibraryImport(LibraryName)]
    public static partial void GciNbExecute(ulong source, ulong symbolList);

    /// <summary>void GciNbPerform(OopType receiver, const char selector[], const OopType args[], int numArgs);</summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial void GciNbPerform(ulong receiver, string selector, ulong* args, int numArgs);

    /// <summary>void GciNbCommit(); — result BoolType as for GciCommit.</summary>
    [LibraryImport(LibraryName)]
    public static partial void GciNbCommit();

    /// <summary>void GciNbAbort();</summary>
    [LibraryImport(LibraryName)]
    public static partial void GciNbAbort();

    /// <summary>void GciNbBegin();</summary>
    [LibraryImport(LibraryName)]
    public static partial void GciNbBegin();

    // ---- Traversal ------------------------------------------------------------------------
    // All return TRUE when complete, FALSE if more reports follow (GciMoreTraversal) — or on
    // error, so GciErr must be checked after every call.

    /// <summary>BoolType GciTraverseObjs(const OopType theOops[], int numOops, GciTravBufType *travBuff, int level);</summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GciTraverseObjs(ulong* theOops, int numOops, byte* travBuff, int level);

    /// <summary>BoolType GciMoreTraversal(GciTravBufType *travBuff);</summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GciMoreTraversal(byte* travBuff);

    /// <summary>BoolType GciPerformTrav(OopType receiver, const char *selector, const OopType *args, int numArgs, GciClampedTravArgsSType *travArgs);</summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GciPerformTrav(ulong receiver, string selector, ulong* args, int numArgs, GciClampedTravArgsSType* travArgs);

    /// <summary>BoolType GciExecuteStrTrav(const char source[], OopType contextObject, OopType symbolList, GciClampedTravArgsSType *travArgs);</summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GciExecuteStrTrav(string source, ulong contextObject, ulong symbolList, GciClampedTravArgsSType* travArgs);

    /// <summary>
    /// BoolType GciFetchPaths(const OopType theOops[], int numOops, const int paths[], const int pathSizes[], int numPaths, OopType results[]);
    /// Path elements: +n = named instVar n, -n = indexed slot n. Failed paths yield OOP_ILLEGAL.
    /// </summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GciFetchPaths(ulong* theOops, int numOops, int* paths, int* pathSizes, int numPaths, ulong* results);

    /// <summary>BoolType GciErr(GciErrSType *errorReport); — true if an error was pending.</summary>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GciErr(GciErrSType* errorReport);
}
