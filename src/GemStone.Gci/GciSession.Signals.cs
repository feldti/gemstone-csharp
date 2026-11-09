using System.Text;
using GemStone.Gci.Native;

namespace GemStone.Gci;

public sealed partial class GciSession
{
    /// <summary>GemStone error numbers of the signals a <see cref="GciSignalListener"/> receives.</summary>
    internal const int SignalCommitError = 6008;          // RT_ERR_SIGNAL_COMMIT: a member of the notify set was committed
    internal const int SignalGemStoneSessionError = 6010; // RT_ERR_SIGNAL_GEMSTONE_SESSION: args serial, int, message, numPending

    private long? _gemSessionId;

    /// <summary>
    /// The session number on the server (<c>System session</c>): the address for
    /// <see cref="SendSignal"/>. Valid while the session is logged in; the server reuses the number
    /// afterwards.
    /// </summary>
    public long GemSessionId => _gemSessionId ??= Evaluate<long>("System session");

    /// <summary>
    /// Sends a signal to another session (<c>System sendSignal:to:withMessage:</c>), e.g. to a
    /// <see cref="GciSignalListener"/>. The target receives it only if it has enabled session
    /// signals; it is queued there until read.
    /// </summary>
    /// <param name="toSessionId">The target's <see cref="GemSessionId"/>.</param>
    /// <param name="value">A number the receiver gets with the signal (a SmallInteger).</param>
    /// <param name="message">
    /// A text the receiver gets with the signal. GemStone sends it as a byte String, so only
    /// characters up to U+00FF (Latin-1) are possible.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="message"/> contains characters beyond Latin-1.</exception>
    public void SendSignal(long toSessionId, long value, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!Oop.FitsSmallInteger(value)) throw new ArgumentOutOfRangeException(nameof(value), "The signal value must be a SmallInteger.");
        if (message.Any(c => c > '\u00FF'))
            throw new ArgumentException("A signal message is a byte String on the server: only characters up to U+00FF (Latin-1).", nameof(message));
        lock (Sync)
        {
            // The primitive accepts a String only, not a Unicode string (which FromValue makes of non-ASCII text).
            var text = FromValue(message);
            var latin1 = Oop.Nil;
            try
            {
                if (!Ascii.IsValid(message)) latin1 = Perform(KnownClasses.String, "withAll:", text);
                Perform(SystemClass(), "sendSignal:to:withMessage:", Oop.FromSmallInteger(value), Oop.FromSmallInteger(toSessionId),
                    latin1.IsNil ? text : latin1);
            }
            finally
            {
                Release(text, latin1);
            }
        }
    }

    private Oop _systemClass = Oop.Illegal;

    private Oop SystemClass()
    {
        if (_systemClass == Oop.Illegal)
        {
            _systemClass = ResolveSymbol("System");
            Release(_systemClass); // a kernel class: persistent
        }
        return _systemClass;
    }

    /// <summary>
    /// Runs a GCI call that executes Smalltalk, on a session with signals enabled. A signal that
    /// reached the session while it was idle surfaces as error 6008/6010 at the start of the call,
    /// before any Smalltalk ran: it is recorded in <paramref name="signals"/> (6010 with its sender,
    /// value and text, read before the error's OOPs are released) and the call is repeated.
    /// </summary>
    internal Oop CallCapturingSignals(Func<ulong> call, List<GciSessionSignal> signals)
    {
        lock (Sync)
        {
            for (var attempt = 1; ; attempt++)
            {
                Activate();
                var result = new Oop(call());
                if (!result.IsNil) return result;
                var error = LastError();
                if (error is null) return result;
                if (attempt > 1000 || !TryCaptureSignal(error, signals)) Throw(error);
            }
        }
    }

    /// <summary>
    /// If <paramref name="error"/> is a signal (6008/6010), records it in <paramref name="signals"/>
    /// (6010 with its sender, value and text, read before the error's OOPs are released), cleans
    /// up after the error and returns true. Under <see cref="Sync"/>.
    /// </summary>
    private bool TryCaptureSignal(GciException error, List<GciSessionSignal> signals)
    {
        if (error.Number is not (SignalCommitError or SignalGemStoneSessionError)) return false;
        if (error.Number == SignalGemStoneSessionError && error.Args.Count >= 3)
        {
            var serial = new Oop(error.Args[0]);
            var value = new Oop(error.Args[1]);
            var text = new Oop(error.Args[2]);
            signals.Add(new GciSessionSignal(
                serial.IsSmallInteger ? serial.ToSmallInteger() : 0,
                null,
                value.IsSmallInteger ? value.ToSmallInteger() : 0,
                text.IsNil ? "" : ToStringValue(text)));
        }
        // 6008 carries nothing: the objects stay in "System signaledObjects".
        DiscardError(error);
        return true;
    }
}

/// <summary>A signal another session sent with <see cref="GciSession.SendSignal"/>.</summary>
/// <param name="SenderSerial">Serial number of the sending session (unique, unlike session numbers).</param>
/// <param name="SenderSessionId">Session number of the sender (<see cref="GciSession.GemSessionId"/>); null if it has logged out meanwhile (with GemStone 3.6 possibly still the old number for a while). Session numbers are reused, serial numbers are not.</param>
/// <param name="Value">The number sent with the signal.</param>
/// <param name="Message">The text sent with the signal.</param>
public sealed record GciSessionSignal(long SenderSerial, long? SenderSessionId, long Value, string Message);

/// <summary>What a <see cref="GciSignalListener"/> received since the last poll.</summary>
public sealed class GciSignals
{
    internal GciSignals(IReadOnlyList<Oop> changedObjects, IReadOnlyList<GciSessionSignal> messages)
    {
        ChangedObjects = changedObjects;
        Messages = messages;
    }

    /// <summary>Watched objects that another session changed and committed (each once).</summary>
    public IReadOnlyList<Oop> ChangedObjects { get; }

    /// <summary>Signals from other sessions, in the order they arrived.</summary>
    public IReadOnlyList<GciSessionSignal> Messages { get; }

    public bool IsEmpty => ChangedObjects.Count == 0 && Messages.Count == 0;
}
