# GciGetFreeOops: error 2166 on the Windows client, and a silent limit of 100,000 OOPs per call

**Product:** GemStone/S 64 Bit 3.7.6, Windows x64 client (`libgcirpc` for Windows, on Windows 11
version 25H2, build 26200.9711) connected to a 3.7.6 stone on x86-64 Linux. The second issue was also seen with the 3.6.5 and 3.7.6
Linux clients.
**Component:** GemBuilder for C, `GciGetFreeOops` (RPC client `libgcirpc`)
**Severity:**
- Issue 1: the call fails and the session is unusable afterwards (protocol errors on later calls).
- Issue 2: the call reports no error, but the result array is incomplete, so later calls fail.

## Summary

1. **Windows client:** `GciGetFreeOops(count, oops)` fails with error 2166 when `count` is
   larger than 2,000 and not a multiple of 2,000. The client apparently fetches free OOPs in
   blocks of 2,000 (16,000 bytes) and decompresses the last block into a destination that is
   only as large as the remaining part of the caller's array. Counts up to 2,000, and exact
   multiples of 2,000, work. The Linux client library does not have this problem, neither over
   the loopback interface nor over the network.
2. **All clients we tried:** `GciGetFreeOops` hands out at most 100,000 OOPs per call. For a
   larger `count`, entries 100,000 and above are left 0, and `GciErr` reports nothing.

Neither the manual nor `gci.hf` mentions a block size or a limit for `GciGetFreeOops`.

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

## Issue 2: at most 100,000 OOPs per call, without an error

### Steps to reproduce

Linux client, 3.6.5 and 3.7.6 (loopback and LAN):

```c
static OopType oops[150000];
GciErrSType err;

memset(oops, 0, sizeof oops);
GciGetFreeOops(150000, oops);
/* GciErr(&err) == FALSE */
/* oops[0] … oops[99999] are distinct free OOPs; oops[100000] … oops[149999] are 0 */
```

| count | Result |
|---|---|
| 100,000 | 100,000 distinct OOPs |
| 100,001 | 100,000 OOPs, the last entry is 0 |
| 150,000 | 100,000 OOPs, 50,000 entries are 0 |
| 250,000 | 100,000 OOPs, 150,000 entries are 0 |
| 2 calls with 100,000 each | 200,000 distinct OOPs |

Using such a 0 entry later as an object ID in `GciStoreTrav` (with `GCI_STORE_TRAV_CREATE`)
fails with:

```
2106 Attempt to store a forward reference using the object identifier 0 which has not been
     allocated to this session.
```

### Expected result

Either all `count` OOPs, or an error (or a documented maximum) when `count` exceeds what the
call can return.

### Workaround

We call `GciGetFreeOops` with at most 100,000 OOPs at a time and check the results for 0.

## Context

We found both issues in a .NET wrapper for the classic GCI. It creates many objects in one
`GciStoreTrav` call, with object IDs from `GciGetFreeOops`. Our test suite covers the
workarounds and the counts listed above.
