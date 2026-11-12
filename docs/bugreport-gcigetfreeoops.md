# GciGetFreeOops: error 2166 on the Windows client, and an undocumented limit of 100,000 OOPs per call

**Product:** GemStone/S 64 Bit 3.7.6, Windows x64 client (`libgcirpc-3.7.6-64.dll`, on Windows 11
version 25H2, build 26200.9711) connected to a 3.7.6 stone on x86-64 Linux. The second issue
was also seen with the 3.6.5, 3.6.8, 3.7.2, 3.7.5 and 3.7.6 Linux clients.
**Component:** GemBuilder for C, `GciGetFreeOops` (RPC client `libgcirpc`)
**Severity:**
- Issue 1: the call fails and the session is unusable afterwards (protocol errors on later calls).
- Issue 2 (documentation): the limit itself is fine, but the manual does not mention it and
  describes `count` as an output, which it is not. A caller who relies on the manual gets an
  incomplete array without noticing.

## Summary

1. **Windows client:** `GciGetFreeOops(count, oops)` fails with error 2166 when `count` is
   larger than 2,000 and not a multiple of 2,000. The client apparently fetches free OOPs in
   blocks of 2,000 (16,000 bytes) and decompresses the last block into a destination that is
   only as large as the remaining part of the caller's array. Counts up to 2,000, and exact
   multiples of 2,000, work. The Linux client library does not have this problem, neither over
   the loopback interface nor over the network.
2. **All clients we tried, documentation:** `GciGetFreeOops` hands out at most 100,000 OOPs
   per call. For a larger `count`, entries 100,000 and above are left untouched, and `GciErr`
   reports nothing. The manual describes `count` as "On return, the number of OOPs that were
   allocated", but in `gci.hf` the parameter is `int count`, passed by value, so the function
   cannot return that number. We ask for the limit and this behaviour to be documented.

Neither the manual nor `gci.hf` gives a block size or a maximum for `GciGetFreeOops`.

[`repro/gcigetfreeoops.cc`](../repro/gcigetfreeoops.cc) shows both issues with `libgcirpc`
alone; it builds on Linux and on Windows (instructions at the top of the file). Output with the
3.7.6 Linux client, where only issue 2 occurs:

```
logged in, client 3.7.6 build 2026-08-14T08:30:20-07:00 38a5ad8f99a6f6bca99454e290a490f558ef7f46

Issue 2: at most 100,000 OOPs per call, the rest is left untouched, no error
GciGetFreeOops(     1): no error,      1 distinct OOPs,      0 entries untouched
GciGetFreeOops(  1000): no error,   1000 distinct OOPs,      0 entries untouched
GciGetFreeOops(  2000): no error,   2000 distinct OOPs,      0 entries untouched
GciGetFreeOops(  4000): no error,   4000 distinct OOPs,      0 entries untouched
GciGetFreeOops( 10000): no error,  10000 distinct OOPs,      0 entries untouched
GciGetFreeOops(100000): no error, 100000 distinct OOPs,      0 entries untouched
GciGetFreeOops(102000): no error, 100000 distinct OOPs,   2000 entries untouched   <-- not as expected
GciGetFreeOops(150000): no error, 100000 distinct OOPs,  50000 entries untouched   <-- not as expected
  session OK: 3 + 4 = 7

For comparison: GciGetFreeOopsEncoded reports a partial allocation
GciGetFreeOopsEncoded( 10000): no error, count out    3, GciFetchNumEncodedOops  10000,  10000 distinct OOPs, 0 entries 0
GciGetFreeOopsEncoded(150000): no error, count out    3, GciFetchNumEncodedOops  20000,  20000 distinct OOPs, 0 entries 0
  session OK: 3 + 4 = 7

Issue 1: a count > 2000 that is not a multiple of 2000 (expected: 4001 OOPs)
GciGetFreeOops(  4001): no error,   4001 distinct OOPs,      0 entries untouched
  session OK: 3 + 4 = 7
```

## Issue 1: error 2166 on the Windows client

### Steps to reproduce

On Windows, logged in over RPC to a stone on another machine:

```c
OopType oops[4001];
GciErrSType err;

GciGetFreeOops(4001, oops);
if (GciErr(&err))
  printf("%d %s\n", (int)err.number, err.message);
/* expected: no error, 4001 distinct OOPs */
```

### Actual result

```
2166 lgcUncompress failed decompression would exceed size of destination buffer,
     destSize 8 < uncomprSize 16000, line 788
```

Results for other counts in one test run (Windows client, same stone):

| count | Result |
|---|---|
| 1, 2, 6, 11, 1,000, 1,001 | OK |
| 10,000 and 50,000 | OK |
| 4,001 | error 2166, `destSize 8 < uncomprSize 16000` |
| 20,001 | error 2166, `destSize 8 < uncomprSize 16000` |
| 50,001 | error 2166, `destSize 8 < uncomprSize 16000` |
| a count with count mod 2,000 = 1,601 (above 2,000) | error 2166, `destSize 12808 < uncomprSize 16000` |

`destSize` is always (count mod 2,000) × 8 bytes, and `uncomprSize` is always 16,000 bytes =
2,000 OOPs. The same calls with the Linux client library (3.6.5 and 3.7.6) succeed, both with
`!@localhost#netldi:…` and with the machine's LAN address in the NRS.

After the error, the session is broken. Later calls in the same session fail with:
- error 2231 "Internal protocol error in the GCI to Gem connection." (seen in `GciReleaseOops`);
- then error 2233 with the same text (seen in `GciExecuteStr`).

The gem's log shows nothing until the client process exits:

```
Network error - text follows:
, from gemdorpc.dispatchLoop , SSL_read(5,…,16256) sslerr=5 errno=104 failed with ECONNRESET
Error Category: 231169 [GemStone] Number: 4137
```

### Expected result

`GciGetFreeOops` fills all `count` entries, as for counts up to 2,000, and the session stays
usable.

### Workaround

We ask for whole multiples of 2,000 (or fewer than 2,000) and keep the extra OOPs for the
next call. Free OOPs stay allocated to the session until logout, so this is safe.

## Issue 2: at most 100,000 OOPs per call, not documented

This is a documentation issue. A limit per call is reasonable, and a caller can live with it,
but only if the manual tells them.

### What happens

Linux client, 3.6.5, 3.6.8, 3.7.2, 3.7.5 and 3.7.6 (loopback; 3.6.5 and 3.7.6 also over the LAN):

```c
static OopType oops[150000];
GciErrSType err;

memset(oops, 0xAB, sizeof oops);
GciGetFreeOops(150000, oops);
/* GciErr(&err) == FALSE */
/* oops[0] … oops[99999] are distinct free OOPs;
   oops[100000] … oops[149999] still hold 0xABABABABABABABAB */
```

| count | Result |
|---|---|
| 100,000 | 100,000 distinct OOPs |
| 100,001 | 100,000 OOPs, the last entry untouched |
| 150,000 | 100,000 OOPs, 50,000 entries untouched |
| 250,000 | 100,000 OOPs, 150,000 entries untouched |
| 2 calls with 100,000 each | 200,000 distinct OOPs |

The limit does not depend on the gem's temporary object cache: the results are the same with
`GEM_TEMPOBJ_CACHE_SIZE` at 50 MB, 1 GB and 3 GB (set with `gemnetobject -T <KB>` in the gem
service NRS, checked with `System gemConfigurationAt: #GemTempObjCacheSize`). It is also the same
in 3.6.5, 3.6.8, 3.7.2, 3.7.5 and 3.7.6.

A caller who does not know the limit uses the untouched entries as object IDs. If the array was
zeroed, `GciStoreTrav` (with `GCI_STORE_TRAV_CREATE`) then fails with:

```
2106 Attempt to store a forward reference using the object identifier 0 which has not been
     allocated to this session.
```

If it was not zeroed (`malloc`, an array on the stack), the entries hold whatever was there
before.

### What the manual and the header say

The manual page of `GciGetFreeOops` says about `count`: "The number of OOPs to allocate. On
return, the number of OOPs that were allocated." But `gci.hf` declares

```c
void GciGetFreeOops(int count, OopType *resultOops);
```

with `count` passed by value, so nothing is returned. The sentence is the same as on the manual
page of `GciGetFreeOopsEncoded`, where it is true, because there the parameter is `int *count`:

```c
int n = 150000;
GciGetFreeOopsEncoded(&n, encoded);           /* n: number of encoded entries */
GciFetchNumEncodedOops(encoded, n);           /* 20000: the OOPs actually allocated */
```

(`GciGetFreeOopsEncoded` stops at 20,000 OOPs per call, 3.6.5 and 3.7.6, Linux client.)

### Suggested documentation

For the manual page of `GciGetFreeOops` (and the comment in `gci.hf`), something like:

> *count*: the number of OOPs to allocate. At most 100,000 OOPs are allocated per call. If
> *count* is larger, only the first 100,000 entries of *resultOops* are filled; the others are
> left unchanged, and no error is reported. To detect them, set *resultOops* to 0 before the
> call (unallocated entries then stay 0), or call the function again for the rest.

and the sentence "On return, the number of OOPs that were allocated" removed from this page.

### What we do

We set the array to 0 before the call (in .NET a new array is already zeroed), ask for at most
100,000 OOPs per call and check the result for 0 entries.

## Context

We found both issues in a .NET wrapper for the classic GCI. It creates many objects in one
`GciStoreTrav` call, with object IDs from `GciGetFreeOops`. Our test suite covers the
workarounds and the counts listed above.
