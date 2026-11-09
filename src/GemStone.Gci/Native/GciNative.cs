using System.Text;

namespace GemStone.Gci.Native;

/// <summary>
/// Entry points of the (non thread-safe) GemBuilder for C RPC library, called through
/// unmanaged function pointers resolved by name on first use (<see cref="GciLibrary"/>).
/// Signatures follow <c>$GEMSTONE/include/gci.hf</c>.
/// </summary>
/// <remarks>
/// Function pointers instead of <c>[LibraryImport]</c>/<c>[DllImport]</c>: the same code runs on
/// .NET 10, .NET 8 and .NET Framework 4.8, and the versioned library file (<c>libgcirpc-3.7.5-64.dll</c>)
/// is loaded by path on all of them (.NET Framework has no DllImport resolver). <c>BoolType</c> is a
/// C <c>int</c>; all <c>const char[]</c> arguments are null-terminated UTF-8. x64 and arm64 each
/// have a single calling convention for non-variadic functions (no GCI function used here is
/// variadic), <c>Cdecl</c> is only spelled out for the compiler. The struct mirrors are the same
/// on both (LP64, little-endian).
/// </remarks>
internal static unsafe partial class GciNative
{

    /// <summary>BoolType GciInit(); — must be called once before any other GCI call.</summary>
    public static bool GciInit()
    {
        return ((delegate* unmanaged[Cdecl]<int>)Fn(ref _GciInit, "GciInit"))() != 0;
    }
    private static nint _GciInit;

    /// <summary>void GciSetNet(stoneNameNrs, HostUserId, HostPassword, gemServiceNrs);</summary>
    public static void GciSetNet(string stoneNameNrs, string hostUserId, string hostPassword, string gemServiceNrs)
    {
        fixed (byte* stoneNameNrsUtf8 = Utf8Z(stoneNameNrs))
        fixed (byte* hostUserIdUtf8 = Utf8Z(hostUserId))
        fixed (byte* hostPasswordUtf8 = Utf8Z(hostPassword))
        fixed (byte* gemServiceNrsUtf8 = Utf8Z(gemServiceNrs))
        ((delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte*, void>)Fn(ref _GciSetNet, "GciSetNet"))(stoneNameNrsUtf8, hostUserIdUtf8, hostPasswordUtf8, gemServiceNrsUtf8);
    }
    private static nint _GciSetNet;

    /// <summary>BoolType GciLogin(gemstoneUsername, gemstonePassword);</summary>
    public static bool GciLogin(string gemstoneUsername, string gemstonePassword)
    {
        fixed (byte* gemstoneUsernameUtf8 = Utf8Z(gemstoneUsername))
        fixed (byte* gemstonePasswordUtf8 = Utf8Z(gemstonePassword))
        return ((delegate* unmanaged[Cdecl]<byte*, byte*, int>)Fn(ref _GciLogin, "GciLogin"))(gemstoneUsernameUtf8, gemstonePasswordUtf8) != 0;
    }
    private static nint _GciLogin;

    /// <summary>
    /// BoolType GciLoginEx(gemstoneUsername, gemstonePassword, unsigned int loginFlags, int haltOnErrNum);
    /// haltOnErrNum: -1 keeps the GEM_HALT_ON_ERROR config setting, 0 never halts.
    /// </summary>
    public static bool GciLoginEx(string gemstoneUsername, string gemstonePassword, uint loginFlags, int haltOnErrNum)
    {
        fixed (byte* gemstoneUsernameUtf8 = Utf8Z(gemstoneUsername))
        fixed (byte* gemstonePasswordUtf8 = Utf8Z(gemstonePassword))
        return ((delegate* unmanaged[Cdecl]<byte*, byte*, uint, int, int>)Fn(ref _GciLoginEx, "GciLoginEx"))(gemstoneUsernameUtf8, gemstonePasswordUtf8, loginFlags, haltOnErrNum) != 0;
    }
    private static nint _GciLoginEx;

    /// <summary>GciSessionIdType (int) GciGetSessionId(); — GCI_INVALID_SESSION_ID (0) if not logged in.</summary>
    public static int GciGetSessionId()
    {
        return ((delegate* unmanaged[Cdecl]<int>)Fn(ref _GciGetSessionId, "GciGetSessionId"))();
    }
    private static nint _GciGetSessionId;

    /// <summary>void GciSetSessionId(GciSessionIdType sessionId); — makes another logged-in session current.</summary>
    public static void GciSetSessionId(int sessionId)
    {
        ((delegate* unmanaged[Cdecl]<int, void>)Fn(ref _GciSetSessionId, "GciSetSessionId"))(sessionId);
    }
    private static nint _GciSetSessionId;

    /// <summary>void GciLogout();</summary>
    public static void GciLogout()
    {
        ((delegate* unmanaged[Cdecl]<void>)Fn(ref _GciLogout, "GciLogout"))();
    }
    private static nint _GciLogout;

    /// <summary>
    /// OopType GciExecuteStr(const char source[], OopType symbolList);
    /// Returns OOP_NIL on error — check <see cref="GciErr"/>, since nil is also a valid result.
    /// </summary>
    public static ulong GciExecuteStr(string source, ulong symbolList)
    {
        fixed (byte* sourceUtf8 = Utf8Z(source))
        return ((delegate* unmanaged[Cdecl]<byte*, ulong, ulong>)Fn(ref _GciExecuteStr, "GciExecuteStr"))(sourceUtf8, symbolList);
    }
    private static nint _GciExecuteStr;

    /// <summary>
    /// OopType GciExecute(OopType source, OopType symbolList); — source is a String or Utf8.
    /// Returns OOP_NIL on error.
    /// </summary>
    public static ulong GciExecute(ulong source, ulong symbolList)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, ulong, ulong>)Fn(ref _GciExecute, "GciExecute"))(source, symbolList);
    }
    private static nint _GciExecute;

    /// <summary>
    /// OopType GciNewUtf8String_(const char* unicodeCString, size_t nBytes, BoolType utf8OrUnicode);
    /// utf8OrUnicode = false → instance of Utf8; true → Unicode7/16/32 (smallest that fits).
    /// </summary>
    public static ulong GciNewUtf8String_(byte* unicodeCString, nuint nBytes, bool utf8OrUnicode)
    {
        return ((delegate* unmanaged[Cdecl]<byte*, nuint, int, ulong>)Fn(ref _GciNewUtf8String_, "GciNewUtf8String_"))(unicodeCString, nBytes, utf8OrUnicode ? 1 : 0);
    }
    private static nint _GciNewUtf8String_;

    /// <summary>
    /// OopType GciPerform(OopType receiver, const char selector[], const OopType args[], int numArgs);
    /// Returns OOP_NIL on error — check <see cref="GciErr"/>.
    /// </summary>
    public static ulong GciPerform(ulong receiver, string selector, ulong* args, int numArgs)
    {
        fixed (byte* selectorUtf8 = Utf8Z(selector))
        return ((delegate* unmanaged[Cdecl]<ulong, byte*, ulong*, int, ulong>)Fn(ref _GciPerform, "GciPerform"))(receiver, selectorUtf8, args, numArgs);
    }
    private static nint _GciPerform;

    /// <summary>int64 GciFetchSize_(OopType theObject); — 0 on error.</summary>
    public static long GciFetchSize_(ulong theObject)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, long>)Fn(ref _GciFetchSize_, "GciFetchSize_"))(theObject);
    }
    private static nint _GciFetchSize_;

    /// <summary>int64 GciFetchBytes_(OopType theObject, int64 startIndex (1-based), ByteType theBytes[], int64 numBytes); — 0 on error.</summary>
    public static long GciFetchBytes_(ulong theObject, long startIndex, byte* theBytes, long numBytes)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, long, byte*, long, long>)Fn(ref _GciFetchBytes_, "GciFetchBytes_"))(theObject, startIndex, theBytes, numBytes);
    }
    private static nint _GciFetchBytes_;

    /// <summary>void GciReleaseOops(const OopType theOops[], int numOops); — remove from the PureExportSet.</summary>
    public static void GciReleaseOops(ulong* theOops, int numOops)
    {
        ((delegate* unmanaged[Cdecl]<ulong*, int, void>)Fn(ref _GciReleaseOops, "GciReleaseOops"))(theOops, numOops);
    }
    private static nint _GciReleaseOops;

    /// <summary>void GciSaveObjs(const OopType theOops[], int numOops); — add to the PureExportSet (protects from GC).</summary>
    public static void GciSaveObjs(ulong* theOops, int numOops)
    {
        ((delegate* unmanaged[Cdecl]<ulong*, int, void>)Fn(ref _GciSaveObjs, "GciSaveObjs"))(theOops, numOops);
    }
    private static nint _GciSaveObjs;

    /// <summary>void GciClearStack(OopType gsProcess); — abandon a suspended execution.</summary>
    public static void GciClearStack(ulong gsProcess)
    {
        ((delegate* unmanaged[Cdecl]<ulong, void>)Fn(ref _GciClearStack, "GciClearStack"))(gsProcess);
    }
    private static nint _GciClearStack;

    /// <summary>
    /// void GciSoftBreak(); — interrupt running Smalltalk. One of the few GCI calls that may be
    /// made from another thread while a call is in progress.
    /// </summary>
    public static void GciSoftBreak()
    {
        ((delegate* unmanaged[Cdecl]<void>)Fn(ref _GciSoftBreak, "GciSoftBreak"))();
    }
    private static nint _GciSoftBreak;

    // ---- Transactions ---------------------------------------------------------------------

    /// <summary>BoolType GciCommit(); — FALSE with no pending GciErr means a transaction conflict.</summary>
    public static bool GciCommit()
    {
        return ((delegate* unmanaged[Cdecl]<int>)Fn(ref _GciCommit, "GciCommit"))() != 0;
    }
    private static nint _GciCommit;

    /// <summary>void GciAbort();</summary>
    public static void GciAbort()
    {
        ((delegate* unmanaged[Cdecl]<void>)Fn(ref _GciAbort, "GciAbort"))();
    }
    private static nint _GciAbort;

    /// <summary>void GciBegin(); — aborts a transaction in progress, then begins a new one.</summary>
    public static void GciBegin()
    {
        ((delegate* unmanaged[Cdecl]<void>)Fn(ref _GciBegin, "GciBegin"))();
    }
    private static nint _GciBegin;

    // ---- Value conversion ------------------------------------------------------------------

    /// <summary>int64 GciOopToI64_(OopType arg, BoolType *error); — *error set TRUE if not an Integer or out of range.</summary>
    public static long GciOopToI64_(ulong arg, int* error)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, int*, long>)Fn(ref _GciOopToI64_, "GciOopToI64_"))(arg, error);
    }
    private static nint _GciOopToI64_;

    /// <summary>OopType GciI64ToOop(int64 arg); — SmallInteger or new LargeInteger.</summary>
    public static ulong GciI64ToOop(long arg)
    {
        return ((delegate* unmanaged[Cdecl]<long, ulong>)Fn(ref _GciI64ToOop, "GciI64ToOop"))(arg);
    }
    private static nint _GciI64ToOop;

    /// <summary>BoolType GciOopToFlt_(OopType theObject, double* result); — SmallDouble, SmallFloat or Float.</summary>
    public static bool GciOopToFlt_(ulong theObject, double* result)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, double*, int>)Fn(ref _GciOopToFlt_, "GciOopToFlt_"))(theObject, result) != 0;
    }
    private static nint _GciOopToFlt_;

    /// <summary>double GciOopToFlt(OopType theObject); — errors via GciErr. Fallback for 3.6, which lacks GciOopToFlt_.</summary>
    public static double GciOopToFlt(ulong theObject)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, double>)Fn(ref _GciOopToFlt, "GciOopToFlt"))(theObject);
    }
    private static nint _GciOopToFlt;

    /// <summary>GciOopToFlt_ exists from GemStone 3.7 on.</summary>
    public static bool HasOopToFltWithStatus => _hasOopToFltWithStatus ??= GciLibrary.TryGetExport("GciOopToFlt_", out _);
    private static bool? _hasOopToFltWithStatus;

    /// <summary>OopType GciFltToOop(double aReal); — SmallDouble or new Float; OOP_NIL on error.</summary>
    public static ulong GciFltToOop(double aReal)
    {
        return ((delegate* unmanaged[Cdecl]<double, ulong>)Fn(ref _GciFltToOop, "GciFltToOop"))(aReal);
    }
    private static nint _GciFltToOop;

    /// <summary>OopType GciNewString(const char *cString); — 8-bit String, pass ASCII only.</summary>
    public static ulong GciNewString(string cString)
    {
        fixed (byte* cStringUtf8 = Utf8Z(cString))
        return ((delegate* unmanaged[Cdecl]<byte*, ulong>)Fn(ref _GciNewString, "GciNewString"))(cStringUtf8);
    }
    private static nint _GciNewString;

    /// <summary>OopType GciNewSymbol(const char *cString); — pass ASCII only.</summary>
    public static ulong GciNewSymbol(string cString)
    {
        fixed (byte* cStringUtf8 = Utf8Z(cString))
        return ((delegate* unmanaged[Cdecl]<byte*, ulong>)Fn(ref _GciNewSymbol, "GciNewSymbol"))(cStringUtf8);
    }
    private static nint _GciNewSymbol;

    /// <summary>
    /// int64 GciFetchUtf8Bytes_(OopType aString, int64 startIndex, ByteType *buf, int64 bufSize,
    ///                          OopType *utf8String, int flags);
    /// Encodes any CharacterCollection as UTF-8. If the result == bufSize, fetch the remaining
    /// bytes from *utf8String; release *utf8String afterwards if it differs from aString.
    /// </summary>
    public static long GciFetchUtf8Bytes_(ulong aString, long startIndex, byte* buf, long bufSize, ulong* utf8String, int flags)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, long, byte*, long, ulong*, int, long>)Fn(ref _GciFetchUtf8Bytes_, "GciFetchUtf8Bytes_"))(aString, startIndex, buf, bufSize, utf8String, flags);
    }
    private static nint _GciFetchUtf8Bytes_;

    /// <summary>OopType GciFetchClass(OopType theObject); — OOP_NIL on error.</summary>
    public static ulong GciFetchClass(ulong theObject)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, ulong>)Fn(ref _GciFetchClass, "GciFetchClass"))(theObject);
    }
    private static nint _GciFetchClass;

    /// <summary>BoolType GciIsKindOfClass(OopType anObj, OopType aClass);</summary>
    public static bool GciIsKindOfClass(ulong anObj, ulong aClass)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, ulong, int>)Fn(ref _GciIsKindOfClass, "GciIsKindOfClass"))(anObj, aClass) != 0;
    }
    private static nint _GciIsKindOfClass;

    /// <summary>
    /// OopType GciNewDateAndTimePosix(int64 microseconds, int offsetSeconds); — OOP_ILLEGAL on error.
    /// Epoch: see <see cref="GciPosixDateAndTime"/>.
    /// </summary>
    public static ulong GciNewDateAndTimePosix(long microseconds, int offsetSeconds)
    {
        return ((delegate* unmanaged[Cdecl]<long, int, ulong>)Fn(ref _GciNewDateAndTimePosix, "GciNewDateAndTimePosix"))(microseconds, offsetSeconds);
    }
    private static nint _GciNewDateAndTimePosix;

    /// <summary>BoolType GciFetchDateAndTimePosix(OopType dtObj, GciPosixDateAndTime *result);</summary>
    public static bool GciFetchDateAndTimePosix(ulong dtObj, GciPosixDateAndTime* result)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, GciPosixDateAndTime*, int>)Fn(ref _GciFetchDateAndTimePosix, "GciFetchDateAndTimePosix"))(dtObj, result) != 0;
    }
    private static nint _GciFetchDateAndTimePosix;

    // ---- Object creation and structural access ---------------------------------------------
    // Signatures checked against gci.hf (3.7.5): note the int vs int64 index/count parameters.

    /// <summary>OopType GciNewByteObj(OopType aClass, const ByteType *value, int64 valueSize);</summary>
    public static ulong GciNewByteObj(ulong aClass, byte* value, long valueSize)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, byte*, long, ulong>)Fn(ref _GciNewByteObj, "GciNewByteObj"))(aClass, value, valueSize);
    }
    private static nint _GciNewByteObj;

    /// <summary>void GciNewOops(int numOops, const OopType oclass[], const int64 idxSize[], OopType result[]);</summary>
    public static void GciNewOops(int numOops, ulong* oclass, long* idxSize, ulong* result)
    {
        ((delegate* unmanaged[Cdecl]<int, ulong*, long*, ulong*, void>)Fn(ref _GciNewOops, "GciNewOops"))(numOops, oclass, idxSize, result);
    }
    private static nint _GciNewOops;

    /// <summary>int GciFetchNamedSize(OopType theObject); — 0 on error.</summary>
    public static int GciFetchNamedSize(ulong theObject)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, int>)Fn(ref _GciFetchNamedSize, "GciFetchNamedSize"))(theObject);
    }
    private static nint _GciFetchNamedSize;

    /// <summary>int64 GciFetchVaryingSize_(OopType theObject); — 0 on error.</summary>
    public static long GciFetchVaryingSize_(ulong theObject)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, long>)Fn(ref _GciFetchVaryingSize_, "GciFetchVaryingSize_"))(theObject);
    }
    private static nint _GciFetchVaryingSize_;

    /// <summary>int GciFetchObjImpl(OopType theObject); — 0 pointer, 1 byte, 2 NSC, 3 special, -1 error.</summary>
    public static int GciFetchObjImpl(ulong theObject)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, int>)Fn(ref _GciFetchObjImpl, "GciFetchObjImpl"))(theObject);
    }
    private static nint _GciFetchObjImpl;

    /// <summary>int GciFetchNamedOops(OopType theObject, int startIndex, OopType theOops[], int numOops);</summary>
    public static int GciFetchNamedOops(ulong theObject, int startIndex, ulong* theOops, int numOops)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, int, ulong*, int, int>)Fn(ref _GciFetchNamedOops, "GciFetchNamedOops"))(theObject, startIndex, theOops, numOops);
    }
    private static nint _GciFetchNamedOops;

    /// <summary>int GciFetchVaryingOops(OopType theObject, int64 startIndex, OopType theOops[], int numOops);</summary>
    public static int GciFetchVaryingOops(ulong theObject, long startIndex, ulong* theOops, int numOops)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, long, ulong*, int, int>)Fn(ref _GciFetchVaryingOops, "GciFetchVaryingOops"))(theObject, startIndex, theOops, numOops);
    }
    private static nint _GciFetchVaryingOops;

    /// <summary>void GciStoreNamedOops(OopType theObject, int64 startIndex, const OopType theOops[], int numOops);</summary>
    public static void GciStoreNamedOops(ulong theObject, long startIndex, ulong* theOops, int numOops)
    {
        ((delegate* unmanaged[Cdecl]<ulong, long, ulong*, int, void>)Fn(ref _GciStoreNamedOops, "GciStoreNamedOops"))(theObject, startIndex, theOops, numOops);
    }
    private static nint _GciStoreNamedOops;

    /// <summary>void GciStoreIdxOops(OopType theObject, int64 startIndex, const OopType theOops[], int numOops); — grows the object if needed.</summary>
    public static void GciStoreIdxOops(ulong theObject, long startIndex, ulong* theOops, int numOops)
    {
        ((delegate* unmanaged[Cdecl]<ulong, long, ulong*, int, void>)Fn(ref _GciStoreIdxOops, "GciStoreIdxOops"))(theObject, startIndex, theOops, numOops);
    }
    private static nint _GciStoreIdxOops;

    /// <summary>void GciAppendOops(OopType theObject, int numOops, const OopType *theOops);</summary>
    public static void GciAppendOops(ulong theObject, int numOops, ulong* theOops)
    {
        ((delegate* unmanaged[Cdecl]<ulong, int, ulong*, void>)Fn(ref _GciAppendOops, "GciAppendOops"))(theObject, numOops, theOops);
    }
    private static nint _GciAppendOops;

    /// <summary>
    /// void GciStoreBytesInstanceOf(OopType theClass, OopType theObject, int64 startIndex, const ByteType theBytes[], int64 numBytes);
    /// <c>GciStoreBytes</c> is a macro for this with theClass = OOP_NIL.
    /// </summary>
    public static void GciStoreBytesInstanceOf(ulong theClass, ulong theObject, long startIndex, byte* theBytes, long numBytes)
    {
        ((delegate* unmanaged[Cdecl]<ulong, ulong, long, byte*, long, void>)Fn(ref _GciStoreBytesInstanceOf, "GciStoreBytesInstanceOf"))(theClass, theObject, startIndex, theBytes, numBytes);
    }
    private static nint _GciStoreBytesInstanceOf;

    /// <summary>void GciAppendBytes(OopType theObject, int64 numBytes, const ByteType *theBytes);</summary>
    public static void GciAppendBytes(ulong theObject, long numBytes, byte* theBytes)
    {
        ((delegate* unmanaged[Cdecl]<ulong, long, byte*, void>)Fn(ref _GciAppendBytes, "GciAppendBytes"))(theObject, numBytes, theBytes);
    }
    private static nint _GciAppendBytes;

    /// <summary>OopType GciResolveSymbol(const char *cString, OopType symbolList); — result is in the export set.</summary>
    public static ulong GciResolveSymbol(string cString, ulong symbolList)
    {
        fixed (byte* cStringUtf8 = Utf8Z(cString))
        return ((delegate* unmanaged[Cdecl]<byte*, ulong, ulong>)Fn(ref _GciResolveSymbol, "GciResolveSymbol"))(cStringUtf8, symbolList);
    }
    private static nint _GciResolveSymbol;

    /// <summary>
    /// OopType GciResolveSymbolObj(OopType aString, OopType symbolList); — like GciResolveSymbol,
    /// but the name is an object owned by the caller (GciResolveSymbol leaks its internal String
    /// in the PureExportSet, verified with 3.7.5).
    /// </summary>
    public static ulong GciResolveSymbolObj(ulong aString, ulong symbolList)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, ulong, ulong>)Fn(ref _GciResolveSymbolObj, "GciResolveSymbolObj"))(aString, symbolList);
    }
    private static nint _GciResolveSymbolObj;

    /// <summary>int GciIvNameToIdx(OopType oclass, const char instVarName[]); — 1-based, 0 if not found.</summary>
    public static int GciIvNameToIdx(ulong oclass, string instVarName)
    {
        fixed (byte* instVarNameUtf8 = Utf8Z(instVarName))
        return ((delegate* unmanaged[Cdecl]<ulong, byte*, int>)Fn(ref _GciIvNameToIdx, "GciIvNameToIdx"))(oclass, instVarNameUtf8);
    }
    private static nint _GciIvNameToIdx;

    /// <summary>void GciAddOopToNsc(OopType theNsc, OopType theOop);</summary>
    public static void GciAddOopToNsc(ulong theNsc, ulong theOop)
    {
        ((delegate* unmanaged[Cdecl]<ulong, ulong, void>)Fn(ref _GciAddOopToNsc, "GciAddOopToNsc"))(theNsc, theOop);
    }
    private static nint _GciAddOopToNsc;

    /// <summary>BoolType GciRemoveOopFromNsc(OopType theNsc, OopType theOop); — FALSE if it was not present.</summary>
    public static bool GciRemoveOopFromNsc(ulong theNsc, ulong theOop)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, ulong, int>)Fn(ref _GciRemoveOopFromNsc, "GciRemoveOopFromNsc"))(theNsc, theOop) != 0;
    }
    private static nint _GciRemoveOopFromNsc;

    /// <summary>BoolType GciNscIncludesOop(OopType theNsc, OopType theOop);</summary>
    public static bool GciNscIncludesOop(ulong theNsc, ulong theOop)
    {
        return ((delegate* unmanaged[Cdecl]<ulong, ulong, int>)Fn(ref _GciNscIncludesOop, "GciNscIncludesOop"))(theNsc, theOop) != 0;
    }
    private static nint _GciNscIncludesOop;

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
    public static NbProgress GciNbEndPoll(long* vOut, int timeoutMs)
    {
        return (NbProgress)((delegate* unmanaged[Cdecl]<long*, int, int>)Fn(ref _GciNbEndPoll, "GciNbEndPoll"))(vOut, timeoutMs);
    }
    private static nint _GciNbEndPoll;

    /// <summary>
    /// int GciNbGetNotifyHandle(void); — OS descriptor of the current session that becomes
    /// readable when GciNbEnd* has something to read; -1 if not available (linked GCI).
    /// </summary>
    public static int GciNbGetNotifyHandle()
    {
        return ((delegate* unmanaged[Cdecl]<int>)Fn(ref _GciNbGetNotifyHandle, "GciNbGetNotifyHandle"))();
    }
    private static nint _GciNbGetNotifyHandle;

    /// <summary>
    /// int GciPollSocketForRead(int socketFd, int timeoutMs); — 1 readable, 0 timeout, &lt; 0 -errno.
    /// Thread-safe and unrelated to the current session: may be called without the GCI lock.
    /// </summary>
    public static int GciPollSocketForRead(int socketFd, int timeoutMs)
    {
        return ((delegate* unmanaged[Cdecl]<int, int, int>)Fn(ref _GciPollSocketForRead, "GciPollSocketForRead"))(socketFd, timeoutMs);
    }
    private static nint _GciPollSocketForRead;

    /// <summary>void GciNbExecuteStr(const char source[], OopType symbolList);</summary>
    public static void GciNbExecuteStr(string source, ulong symbolList)
    {
        fixed (byte* sourceUtf8 = Utf8Z(source))
        ((delegate* unmanaged[Cdecl]<byte*, ulong, void>)Fn(ref _GciNbExecuteStr, "GciNbExecuteStr"))(sourceUtf8, symbolList);
    }
    private static nint _GciNbExecuteStr;

    /// <summary>void GciNbExecute(OopType source, OopType symbolList);</summary>
    public static void GciNbExecute(ulong source, ulong symbolList)
    {
        ((delegate* unmanaged[Cdecl]<ulong, ulong, void>)Fn(ref _GciNbExecute, "GciNbExecute"))(source, symbolList);
    }
    private static nint _GciNbExecute;

    /// <summary>void GciNbPerform(OopType receiver, const char selector[], const OopType args[], int numArgs);</summary>
    public static void GciNbPerform(ulong receiver, string selector, ulong* args, int numArgs)
    {
        fixed (byte* selectorUtf8 = Utf8Z(selector))
        ((delegate* unmanaged[Cdecl]<ulong, byte*, ulong*, int, void>)Fn(ref _GciNbPerform, "GciNbPerform"))(receiver, selectorUtf8, args, numArgs);
    }
    private static nint _GciNbPerform;

    /// <summary>void GciNbCommit(); — result BoolType as for GciCommit.</summary>
    public static void GciNbCommit()
    {
        ((delegate* unmanaged[Cdecl]<void>)Fn(ref _GciNbCommit, "GciNbCommit"))();
    }
    private static nint _GciNbCommit;

    /// <summary>void GciNbAbort();</summary>
    public static void GciNbAbort()
    {
        ((delegate* unmanaged[Cdecl]<void>)Fn(ref _GciNbAbort, "GciNbAbort"))();
    }
    private static nint _GciNbAbort;

    /// <summary>void GciNbBegin();</summary>
    public static void GciNbBegin()
    {
        ((delegate* unmanaged[Cdecl]<void>)Fn(ref _GciNbBegin, "GciNbBegin"))();
    }
    private static nint _GciNbBegin;

    // ---- Traversal ------------------------------------------------------------------------
    // All return TRUE when complete, FALSE if more reports follow (GciMoreTraversal) — or on
    // error, so GciErr must be checked after every call.

    /// <summary>void GciGetFreeOops(int count, OopType *resultOops); — allocate OOPs for objects created later (store traversal).</summary>
    public static void GciGetFreeOops(int count, ulong* resultOops)
    {
        ((delegate* unmanaged[Cdecl]<int, ulong*, void>)Fn(ref _GciGetFreeOops, "GciGetFreeOops"))(count, resultOops);
    }
    private static nint _GciGetFreeOops;

    /// <summary>void GciStoreTrav(GciTravBufType *travBuff, int flag); — create/modify the objects of the reports (GCI_STORE_TRAV_*).</summary>
    public static void GciStoreTrav(byte* travBuff, int flag)
    {
        ((delegate* unmanaged[Cdecl]<byte*, int, void>)Fn(ref _GciStoreTrav, "GciStoreTrav"))(travBuff, flag);
    }
    private static nint _GciStoreTrav;

    /// <summary>OopType GciExecuteStrFromContext(const char source[], OopType contextObject, OopType symbolList); — execute with contextObject as self.</summary>
    public static ulong GciExecuteStrFromContext(string source, ulong contextObject, ulong symbolList)
    {
        fixed (byte* sourceUtf8 = Utf8Z(source))
        return ((delegate* unmanaged[Cdecl]<byte*, ulong, ulong, ulong>)Fn(ref _GciExecuteStrFromContext, "GciExecuteStrFromContext"))(sourceUtf8, contextObject, symbolList);
    }
    private static nint _GciExecuteStrFromContext;

    /// <summary>BoolType GciTraverseObjs(const OopType theOops[], int numOops, GciTravBufType *travBuff, int level);</summary>
    public static bool GciTraverseObjs(ulong* theOops, int numOops, byte* travBuff, int level)
    {
        return ((delegate* unmanaged[Cdecl]<ulong*, int, byte*, int, int>)Fn(ref _GciTraverseObjs, "GciTraverseObjs"))(theOops, numOops, travBuff, level) != 0;
    }
    private static nint _GciTraverseObjs;

    /// <summary>BoolType GciMoreTraversal(GciTravBufType *travBuff);</summary>
    public static bool GciMoreTraversal(byte* travBuff)
    {
        return ((delegate* unmanaged[Cdecl]<byte*, int>)Fn(ref _GciMoreTraversal, "GciMoreTraversal"))(travBuff) != 0;
    }
    private static nint _GciMoreTraversal;

    /// <summary>BoolType GciPerformTrav(OopType receiver, const char *selector, const OopType *args, int numArgs, GciClampedTravArgsSType *travArgs);</summary>
    public static bool GciPerformTrav(ulong receiver, string selector, ulong* args, int numArgs, GciClampedTravArgsSType* travArgs)
    {
        fixed (byte* selectorUtf8 = Utf8Z(selector))
        return ((delegate* unmanaged[Cdecl]<ulong, byte*, ulong*, int, GciClampedTravArgsSType*, int>)Fn(ref _GciPerformTrav, "GciPerformTrav"))(receiver, selectorUtf8, args, numArgs, travArgs) != 0;
    }
    private static nint _GciPerformTrav;

    /// <summary>BoolType GciExecuteStrTrav(const char source[], OopType contextObject, OopType symbolList, GciClampedTravArgsSType *travArgs);</summary>
    public static bool GciExecuteStrTrav(string source, ulong contextObject, ulong symbolList, GciClampedTravArgsSType* travArgs)
    {
        fixed (byte* sourceUtf8 = Utf8Z(source))
        return ((delegate* unmanaged[Cdecl]<byte*, ulong, ulong, GciClampedTravArgsSType*, int>)Fn(ref _GciExecuteStrTrav, "GciExecuteStrTrav"))(sourceUtf8, contextObject, symbolList, travArgs) != 0;
    }
    private static nint _GciExecuteStrTrav;

    /// <summary>
    /// BoolType GciFetchPaths(const OopType theOops[], int numOops, const int paths[], const int pathSizes[], int numPaths, OopType results[]);
    /// Path elements: +n = named instVar n, -n = indexed slot n. Failed paths yield OOP_ILLEGAL.
    /// </summary>
    public static bool GciFetchPaths(ulong* theOops, int numOops, int* paths, int* pathSizes, int numPaths, ulong* results)
    {
        return ((delegate* unmanaged[Cdecl]<ulong*, int, int*, int*, int, ulong*, int>)Fn(ref _GciFetchPaths, "GciFetchPaths"))(theOops, numOops, paths, pathSizes, numPaths, results) != 0;
    }
    private static nint _GciFetchPaths;

    /// <summary>BoolType GciErr(GciErrSType *errorReport); — true if an error was pending.</summary>
    public static bool GciErr(GciErrSType* errorReport)
    {
        return ((delegate* unmanaged[Cdecl]<GciErrSType*, int>)Fn(ref _GciErr, "GciErr"))(errorReport) != 0;
    }
    private static nint _GciErr;

    /// <summary>Address of an export, resolved once and cached in <paramref name="slot"/>.</summary>
    private static nint Fn(ref nint slot, string name)
    {
        var p = slot;
        if (p == 0) slot = p = GciLibrary.GetExport(name);
        return p;
    }

    /// <summary>Null-terminated UTF-8 (<c>null</c> stays <c>null</c>, i.e. a NULL pointer).</summary>
    private static byte[]? Utf8Z(string? s)
    {
        if (s is null) return null;
        var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
        return bytes;
    }

    /// <summary>Names of all entry points, for <c>GciNativeExportTests</c>.</summary>
    internal static IEnumerable<string> EntryPointNames() =>
        typeof(GciNative).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(nint) && f.Name.StartsWith("_", StringComparison.Ordinal))
            .Select(f => f.Name.Substring(1));
}
