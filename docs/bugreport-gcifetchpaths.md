# GciFetchPaths crashes the gem (SIGSEGV) when a path step starts from nil or another special object

**Product:** GemStone/S 64 Bit 3.6.5, 3.7.5 and 3.7.6 (x86-64 Linux)
**Component:** GemBuilder for C, `GciFetchPaths` (RPC client `libgcirpc`, gem side `GemDoFetchPaths`)
**Severity:** gem process crashes; the client session is lost

## Summary

`GciFetchPaths` crashes the gem with SIGSEGV when the step in a path is applied to a special
(immediate) object. A typical case is `nil`, for example fetching `address.street` from an
object whose `address` is nil.

The manual says this case should not crash. Under "GciFetchPaths" it uses exactly this
example ("if you attempted to access *address.zip* when an Employee's Address instance
variable contained only *nil*") as the most common path-fetching error. For it, the manual
promises `OOP_ILLEGAL` in the results array and FALSE as the return value.

## Steps to reproduce

```c
OopType arr = GciExecuteStr("Array with: nil with: 42", OOP_NIL);

int path[]   = { -1, 1 };  /* indexed slot 1 (-> nil), then named instVar 1 of that */
int pathSize = 2;
OopType result;

BoolType ok = GciFetchPaths(&arr, 1, path, &pathSize, 1, &result);
/* expected: ok == FALSE, result == OOP_ILLEGAL, session still usable */
```

The same crash happens when the intermediate object is a SmallInteger or another special
object. Single-step paths behave correctly in these cases, which never apply a step to a
special object:
- an index out of range returns `OOP_ILLEGAL`;
- a named offset into a byte object returns `OOP_ILLEGAL`.

## Expected result

`GciFetchPaths` returns FALSE, `result` is `OOP_ILLEGAL`, and the session stays usable.

## Actual result

- **Client:** `GciFetchPaths` blocks for about 75 seconds while the gem writes its C stack
  with pstack/gdb. It then returns FALSE with `result == 0`. The next GCI call fails with
  error 4100, "The given session ID is invalid." (In 3.7.5 the client reported a socket read
  EOF.)
- **Gem log** (3.7.6, `gemnetobject1914648stellaris.log`):

```
Gemstone Signal Handler: Signal 11, SIGSEGV Received in thread 0x75b15715a780
HostFaultHandler: signal = 11
    info->si_addr = 0x24
...
#6  <signal handler called>
#7  omHdrSType::thirdWord (this=<optimized out>, this=<optimized out>) at src/omobj.hf:111
#8  om::FetchSize_c (obj=obj@entry=0x14) at src/om.c:4919
#9  om::FetchNamedPlusVarSize_c (objH=<optimized out>) at src/om.c:6435
#10 checkFetchPathOffset (offset=1, objH=<optimized out>, omPtr=<optimized out>) at src/gemdo.c:1159
#11 doFetchOnePath (omPtr=0x75b153c00000, rootObj=0x75b153dcd028, oopIllegalProxy=0x75b153dcd008,
        paths=0x0, pathsH=0x75b153dcd018, pathOffset=0, pathLimit=2, isRpc=1) at src/gemdo.c:1226
#12 GemDoFetchPaths (sourceObjs=0x0, numObjsArg=0, paths=0x0, pathSizes=0x0, numPathsArg=0,
        lnkResults=0x0, isRpc=1, noError=0x0) at src/gemdo.c:1326
#13 processAgentMessage (..., packetKind=LGC_MSG_CMD) at src/gemdorpc.c:2299
#14 dispatchLoop (...) at src/gemdorpc.c:347
#15 serviceGem (...) at src/gemdorpc.c:856
#16 GemDoRpcLoop (..., gemStartKind=GEM_START_TCP) at src/gemdorpc.c:938
#17 Gdbg (..., gemKind=G_RGCIGEM, startKind=GEM_START_TCP, ...) at src/gdbg.c:1389
#18 gemMain (...) at src/gemmain.c:631
#19 main (...) at src/gem.c:59
```

## Analysis

`obj=0x14` in frame #8 is `OOP_NIL`. In `doFetchOnePath`, the object reached after the first
step (nil) is passed on to `checkFetchPathOffset`. That function calls
`om::FetchNamedPlusVarSize_c` → `om::FetchSize_c` on it, and `FetchSize_c` treats the special
OOP as a heap object pointer and reads its object header. The faulting address `0x24` is
consistent with reading a header word at a small offset from `0x14`.

The intermediate object apparently needs a special-object check before
`checkFetchPathOffset`. If the object is special, the path should yield `OOP_ILLEGAL`, as
the manual describes.

## Environment

| | |
|---|---|
| Server/gem | 3.7.6, build 2026-08-14T08:30:20-07:00, commit 38a5ad8f99a6f6bca99454e290a490f558ef7f46 |
| Also reproduced on | 3.7.5, build 2026-03-11T15:07:11-07:00, commit cf61017e7c0c8b8afb92c5ee1df55ca9363bf651; 3.6.5 (`om::FetchSize_c (obj=0x14)` at om.c:4869) |
| Client | `libgcirpc-3.7.6-64.so` / `libgcirpc-3.7.5-64.so` (classic, non-thread-safe GCI), same host, via NetLDI |
| OS | TUXEDO OS 24.04.5 LTS (Ubuntu 24.04 based), Linux 7.0.0, x86-64, AMD Ryzen 7 7840HS |

Gem logs with full stack traces are available:
- 3.7.6: `gemnetobject1914648stellaris.log`
- 3.7.5: `gemnetobject_1890500.log`, `gemnetobject_1890852.log`, `gemnetobject_1891331.log`

## Workaround

The client resolves paths one step at a time and calls `GciFetchPaths` only with single-step
paths. Before each step it checks locally whether the intermediate OOP is special, using
`GCI_OOP_IS_SPECIAL`. If it is, the client sets the result to `OOP_ILLEGAL` without calling
the gem. This costs one round trip per path depth instead of one in total.

---

## Related (fixed in 3.7.6): `GciExecuteStrTrav` leaks its source String in the PureExportSet

In 3.7.5, every call to `GciExecuteStrTrav` leaves one object in the PureExportSet: the String
created from the source text. Its OOP is never returned to the client, so the client cannot
release it.

- **How it was measured:** 50 calls of `GciExecuteStrTrav("3 printString", OOP_NIL, OOP_NIL,
  &travArgs)`, releasing `travArgs.resultOop` after each call.
- **Measurement:** `System hiddenSetSize: (System HiddenSetSpecifiers indexOf:
  'PureExportSet')`.
- **Result:** the size grows by 50 in 3.7.5 and by 0 in 3.7.6. No action is needed for 3.7.6;
  this note is for reference only.
