# GciClampedTrav crashes the gem (SIGSEGV) when a ClampSpecification has instance variable levels and no instVarLevelsSelector

**Product:** GemStone/S 64 Bit 3.7.6 (x86-64 Linux); other versions not tested yet
**Component:** GemBuilder for C, `GciClampedTrav` (gem side `GemTravClampedTraversal`, `findNextOopToReport`)
**Severity:** gem process crashes; the client session is lost

## Summary

`GciClampedTrav` crashes the gem with SIGSEGV when the ClampSpecification has instance
variable levels (`instVarLevels`) and its `instVarLevelsSelector` is nil, as soon as the
traversal reaches an object whose class is not a key of `instVarLevels`. The gem then sends
selector id 0, and the method lookup dereferences a null pointer.

A nil `instVarLevelsSelector` is the default. Every ClampSpecification created with
`newWithIdClamps:classClamps:instVarLevels:` has it, including the image's own example
`ClampSpecification example1`, which crashes the gem in the first traversal that reaches an
object of another class.

The class comment of ClampSpecification says nil is allowed:

> **instVarLevelsSelector:** A Symbol or nil. If non-nil, a zero argument selector that is sent
> to the object if the class of the object is not found in the instVarLevels dictionary.

So with nil, nothing should be sent, and the object should be traversed without levels.

## Steps to reproduce

```c
OopType root = GciExecuteStr(
    "Array with: (Association newWithKey: #k value: (Array with: 1 with: 2)) with: 'text'", OOP_NIL);

GciClampedTravArgsSType args;            /* constructor: clampSpec OOP_NIL, isRpc 1 */
args.clampSpec = GciExecuteStr("ClampSpecification example1", OOP_NIL);
args.travBuff  = GciTravBufType::malloc(64 * 1024);
args.level     = 4;

BoolType done = GciClampedTrav(&root, 1, &args);
/* expected: the traversal completes; the value of the Association is not followed */
/* actual:   GemStone error 4137 "socket read EOF", the gem has crashed */
```

### Standalone C program

[`repro/gciclampedtrav.cc`](../repro/gciclampedtrav.cc) shows the crash with `libgcirpc` alone
(build and run instructions are at the top of the file). It runs five traversals of the same
root, each in a session of its own. Output against a 3.7.6 stone:

```
logged in, 3.7.6 build 2026-08-14T08:30:20-07:00 38a5ad8f99a6f6bca99454e290a490f558ef7f46
1. no ClampSpecification (expected: traversal done)
  traversal done: 264 bytes of object reports in 1 call(s)
  session OK: 3 + 4 = 7
2. empty ClampSpecification (expected: traversal done)
  traversal done: 264 bytes of object reports in 1 call(s)
  session OK: 3 + 4 = 7
3. ClampSpecification example1, from the image (expected: traversal done)
  GciClampedTrav: GemStone error 4137: Network error - text follows:
linkgc socketReadError, socket read EOF
  GciExecuteStr('3 + 4'): GemStone error 4100: The given session ID is invalid.
  session is lost
4. instVarLevels {Association . #(#value 0)}, instVarLevelsSelector nil (expected: traversal done)
  GciClampedTrav: GemStone error 4137: Network error - text follows:
linkgc socketReadError, socket read EOF
  GciExecuteStr('3 + 4'): GemStone error 4100: The given session ID is invalid.
  session is lost
5. the same with instVarLevelsSelector: #myDependents (answers nil for these objects)
  traversal done: 208 bytes of object reports in 1 call(s)
  session OK: 3 + 4 = 7
```

Case 5 shows the intended behaviour: with a selector that answers nil, the same
ClampSpecification works, and the clamp takes effect (208 instead of 264 bytes: the value of
the Association is not reported).

## Gem log

Every crash (cases 3 and 4, and the same crash from a C# client) shows this stack in the
gem log (addresses shortened):

```
Gemstone Signal Handler: Signal 11, SIGSEGV Received
#6  <signal handler called>
#7  om::objIdIfAssigned (obj=0x0) at om.hf:9562
#8  om::IsIdentical (b=0x0, a=0x14) at om.hf:7294
#9  KeyValueDictMethDictAtPut (...)
#10 om::methodLookupCacheAtPut (methodH=<optimized out>, selectorId=0, ...) at ommethlookup.c:243
#11 om::fullMethodLookup (omPtr=..., startClsH=..., rawSelectorId=0, flags=0) at ommethlookup.c:1090
#12 interpBoot (...) at intloopsup.c:8028
#13 IntLpSupControlLoop (...) at intloopsup.c:8319
#14 IntRecurFromOm_ (...) at interp.c:2614
#15 om::IntRecurFromOm (...)
#16 findNextOopToReport (state=...) at gemtrav.c:1869
#17 fillTraversalBuffer (...) at gemtrav.c:2320
#18 GemTravClampedTraversal_ (...)
#19 GemTravClampedTraversal (...)
```

`findNextOopToReport` sends the `instVarLevelsSelector` without checking it for nil. The
nil selector arrives as selector id 0, and `fullMethodLookup` crashes on it.

## Expected behaviour

As the class comment says: if `instVarLevelsSelector` is nil, nothing is sent, and objects
whose class is not in `instVarLevels` are traversed without levels. At the very least the
traversal should fail with a GemStone error instead of crashing the gem.

## Related observations

- With **no** instance variable levels (an empty ClampSpecification), a nil selector does not
  matter (case 2). The crash needs levels plus an object of another class.
- `traversalCallBackSelector` (the other selector of a ClampSpecification) was not needed in
  these tests: with `instVarLevelsSelector` set, traversals through objects of classes that
  traverse by callback (`RcIdentityBag`) completed with a nil `traversalCallBackSelector`.
- With `traversalCallBackSelector: #_gbsTraversalCallback`, an `RcIdentityBag` is reported with
  the placeholder String that `RcIdentityBag>>_gbsTraversalCallback` answers instead of its
  elements. That is by design (GemBuilder for Smalltalk fetches such bags separately), not
  part of this report.

## Workaround

Set `instVarLevelsSelector` to a unary selector that answers nil for every object the
traversal can reach. `#myDependents` works for objects without dependents; a method that
always answers nil would be the safe choice.
