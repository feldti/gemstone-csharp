# Notifications from the server

How a C# application learns that something changed in the repository: what the
`GciSignalListener` does, how fast it is, and the GemStone behaviour the design is based on.
The short version for application code is in
[programming-model.md](programming-model.md#notifications-from-the-server).

## Two kinds of notifications

| | Changed objects | Signals between sessions |
|---|---|---|
| GemStone mechanism | notify set: `System addAllToNotifySet:` | `System sendSignal:to:withMessage:` |
| Who triggers it | any session that commits a change to a watched object | any session, from C# (`SendSignal`) or Smalltalk |
| What arrives | the OOP of the changed object | sender, a number (SmallInteger) and a text (Latin-1) |
| In the listener | `GciSignals.ChangedObjects` | `GciSignals.Messages` |

Only the watched object itself counts. A change to an object it references (the address of a
watched customer, the state of an element of a watched collection) is a change of that other
object and is not reported unless that object is watched too.

Adding to or removing from a watched collection is reported, also for large ones whose
elements live in internal nodes. Some reduced-conflict classes are the exception, because they
write into internal parts per session:

| Watched collection (10, 2,000 and 100,000 elements) | Change reported? |
|---|---|
| Array, OrderedCollection, Set, IdentitySet, Bag, IdentityBag, Dictionary, KeyValueDictionary | yes |
| RcCounter, RcIdentitySet | yes |
| **RcQueue, RcKeyValueDictionary, RcIdentityBag** | **no** |

(3.6.5 and 3.7.6; Rc classes with 10 and 2,000 elements.) For an `RcQueue` of messages, have
the writer send a signal after the commit.

## API

```csharp
using var listener = new GciSignalListener(listenerSession);   // a session only for this
listener.Watch(oop1, oop2);                                      // committed objects
listener.SignalsReceived += (_, e) => { /* e.Signals.ChangedObjects, e.Signals.Messages */ };
listener.PollFailed += (_, e) => { /* e.Error, e.Stopped */ };
listener.Start();                                                // wait on the server in the background
...
listener.Stop();
```

| Call | What it does |
|---|---|
| `Start()` | Background loop that waits on the server (at most `MaxWait`, default 5 s, per call) and raises `SignalsReceived` as soon as something arrives. The recommended mode. |
| `Start(interval)` | Background loop that polls every `interval`. Latency up to the interval. |
| `WaitAsync(timeout)` | One wait: returns as soon as something arrives, or empty after the timeout. |
| `Poll()` | One look: returns what has arrived, without waiting. |
| `Watch` / `Unwatch` / `UnwatchAll` | Change the notify set. Abort the listener session first, so that it sees newly committed objects. |

`SignalsReceived` runs on the `SynchronizationContext` of the thread that called `Start` (the
UI thread in WinForms/WPF), otherwise directly on the background loop. `Stop()` may be called
from the handler.

Each wait and each poll aborts the listener session first, so it never holds an old view of the
repository for longer than `MaxWait`. `Watch`, `Unwatch`, `UnwatchAll` and `Poll` interrupt a
wait in progress, and nothing that arrived gets lost by that.

## How the waiting listener works

1. The client starts one nonblocking call (`GciNbPerform`) on the listener session: a block that
   aborts, empties both queues and, if they were empty, waits on a semaphore for up to
   `MaxWait`.
2. While it waits, the GCI is free: other sessions of the process keep working. The client
   waits for the session's socket (`GciNbGetNotifyHandle`, `GciPollSocketForRead`) on a pool
   thread, outside the GCI lock and without using CPU, in slices of 50 ms to notice a
   cancellation.
3. A commit to a watched object or a signal makes the VM raise a notification inside the
   waiting block. The handler wakes the semaphore, the block empties the queues and answers.
4. The client reads the answer (one traversal), raises the event and starts the next wait.

While nothing happens, that is one round trip per `MaxWait`.

## Measured latency

Writer and listener in separate processes on one machine, stones and NetLDI local, 40 commits
and 40 signals each, 150–300 ms apart. "Event" is the moment `SignalsReceived` runs in the
listener process.

| Mode | Version | Commit → event, median | p90 | max | Signal → event, median | p90 | max | Received |
|---|---|---|---|---|---|---|---|---|
| `Start()` (wait) | 3.7.6 | 0.5 ms | 0.7 ms | 14 ms | 0.6 ms | 0.9 ms | 7.5 ms | 40/40, 40/40 |
| `Start()` (wait) | 3.6.5 | 0.7 ms | 0.9 ms | 12 ms | 0.7 ms | 1.1 ms | 8.4 ms | 40/40, 40/40 |
| `Start(100 ms)` | 3.7.6 | 52 ms | 96 ms | 99 ms | 56 ms | 88 ms | 101 ms | 40/40, 40/40 |
| `Start(250 ms)` | 3.7.6 | 141 ms | 221 ms | 244 ms | 120 ms | 232 ms | 243 ms | 40/40, 40/40 |
| `Start(1 s)` | 3.7.6 | – | – | – | 501 ms | 957 ms | 990 ms | 19/40, 40/40 |

"Commit → event" is measured from the start of the writer's `Commit()`: in wait mode the
listener has the event before the writer's commit call has returned (a commit took about 7 ms).
With a 1 s interval several commits to the same object fall into one poll and are reported once
(the notify set reports *which* objects changed, not how often), hence 19 events for 40 commits.

CPU time of the listener process over 20 s, including start-up and login: 0.33 s in wait mode.
An earlier version that checked every 2 ms instead of waiting for the socket needed 2.4 s
(1.4 s at 5 ms, 0.9 s at 10 ms) and had a median latency of 1.5 ms.

## GemStone behaviour behind the design

Found by experiment with 3.6.5 and 3.7.6; items 3 and 8 checked with all five versions (3.6.5, 3.6.8, 3.7.2, 3.7.5, 3.7.6).

1. **A waiting Smalltalk call is interrupted by signals.** With raising enabled
   (`InterSessionSignal enableSignalling`, `ObjectsCommittedNotification enableSignalling`),
   the VM raises a notification inside `Delay wait` or `Semaphore wait` as soon as a signal
   or commit notification arrives. A handler that signals the semaphore and resumes ends the
   wait at once.
2. **The VM switches raising off after each notification.** It has to be enabled again
   (`enableSignalling`), as the method comment says.
3. **Raised while idle, a signal comes back as an error from the next GCI call (6008 or 6010),
   and that loses signals.** If a session signal and then a commit notification arrive while
   the session is idle, the next call fails with 6008 only, and the session signal is gone: it
   is neither in an error nor in the queue. With raising switched off, both stay in their
   queues (`System signalFromGemStoneSession`, `System signaledObjects`). The listener therefore
   enables raising only while it waits and switches it off afterwards (`ensure:`).
4. **A notification without a handler ends the whole call** with error 6008/6010
   (`defaultAction` → `_signalToDebugger`), and everything the call had read is lost. The
   listener's block has one handler around everything from the abort to the answer.
5. **A soft break can be caught in Smalltalk** (`on: Break do:`, error 6003). The listener
   interrupts a wait with a soft break and still gets what was read so far.
6. **`GciPollForSignal` is not useful as a trigger:** any other call consumes the hint, while
   the data stays in the queues. The wrapper does not use it.
7. **`System signaledObjects` empties when read.** A session sees objects committed after its
   last abort only after another abort; adding one to the notify set before fails with error
   2106. `Watch` aborts first.
8. **`System signalFromGemStoneSession` fails for a sender that has logged out (3.7).** It is
   deprecated since 3.0 and turns the sender's serial number into a `GsSession`, which is nil in
   3.7 once the sender is gone; the caller then fails with "UndefinedObject does not understand
   #serialNumber", and everything read in that call is lost. 3.6 still answers a session. The
   listener uses the primitive `System _signalFromGemStoneSession`, which answers the serial
   number in all versions. Found because the last signal of a measurement run was always
   missing: the writer had logged out before the listener polled.
9. **Rc collections are not reported by the notify set** when elements are added (see the table
   above).
10. **A signal's text must be a byte `String`.** A Unicode string or `Utf8` makes the primitive
   fail (2258/2094). `SendSignal` sends non-ASCII text as `String withAll:` and rejects
   characters beyond U+00FF.

## Replacing a message-based system

Applications that so far kept their data in memory and distributed changes as messages
("telegrams") can map that onto GemStone like this:

- **The change is a transaction.** A message becomes a method on the server (`Send`) or a
  `SaveChanges`, followed by a commit. Instead of processing messages one after another,
  transactions run in parallel; if two change the same object, the second commit fails and is
  repeated (`SaveChangesAndCommit`). Classes such as `RcCounter`, `RcQueue` and
  `RcIdentityBag` avoid such conflicts where many sessions write to the same object.
- **Distribution is a notification.** With the notify set, no message has to be sent at all:
  the listener of every client learns which watched objects changed, and the windows reload
  them (`GsObjectContext.TryGetEntity` leads from the OOP to the loaded C# object).
- **Messages that must not get lost** go into a persistent queue that the writer fills in the
  same transaction, with a signal as a doorbell. A client that was not running reads what it
  missed when it starts. Signals alone are not stored: they disappear with the receiving
  session.
- **Each client sees a consistent state:** a reload after an abort shows the repository as of
  one point in time, never half of another session's transaction.
