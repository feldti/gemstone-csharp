namespace GemStone.Gci;

/// <summary>
/// A persistent change feed in the stone: writers publish the objects they changed, in the same
/// transaction as the change, and ring a doorbell after the commit; each registered consumer takes
/// the objects published since its last take. Nothing gets lost while a consumer is not running,
/// and parallel writers do not conflict.
/// </summary>
/// <remarks>
/// <para>
/// On the server the feed is an instance of the Smalltalk class <c>GciChangeFeed</c>, which
/// <see cref="Install"/> creates. Each consumer has its own <c>RcQueue</c> (many writers, one
/// reader, no conflicts); <c>publish:</c> adds one entry to every consumer's queue. Entries hold the
/// objects themselves, so they stay alive until every consumer has taken them. The doorbell is a
/// session signal (<c>System sendSignal:to:withMessage:</c>) to each consumer's listener session;
/// signals are not persistent, so a consumer also takes now and then without one.
/// </para>
/// <para>
/// Smalltalk writers use the same object:
/// <c>feed publish: changedObjects. System commitTransaction. feed ring</c>.
/// </para>
/// <para>
/// A consumer that is no longer needed must be <see cref="Unregister">unregistered</see>,
/// otherwise its queue keeps growing (and keeps the published objects alive).
/// </para>
/// </remarks>
public sealed class GciChangeFeed
{
    /// <summary>Signal value sent by <see cref="Ring"/>.</summary>
    public const long SignalValue = 7301;

    /// <summary>Signal text sent by <see cref="Ring"/>.</summary>
    public const string SignalMessage = "GciChangeFeed";

    private const string ClassName = "GciChangeFeed";

    private static readonly string[] Methods =
    [
        """
        initialize
            consumers := RcKeyValueDictionary new
        """,
        """
        publish: objects
            "Add the changed objects to every consumer's queue; call it in the writer's transaction, before the commit."
            | entry |
            objects isEmpty ifTrue: [^self].
            entry := objects asArray.
            consumers do: [:e | (e at: 1) add: entry]
        """,
        """
        ring
            "After the commit: wake up the consumers' listener sessions."
            consumers do: [:e | | sid |
                sid := GsSession sessionIdOfSerial: (e at: 2).
                (sid notNil and: [sid > 0]) ifTrue: [
                    [System sendSignal: 7301 to: sid withMessage: 'GciChangeFeed'] on: Error do: [:x | x return: nil]]]
        """,
        """
        register: key serial: serial
            "Create the consumer's queue (or keep it) and remember the serial number of its listener session."
            | e |
            e := consumers at: key ifAbsent: [nil].
            e isNil
                ifTrue: [consumers at: key put: (Array with: RcQueue new with: serial)]
                ifFalse: [e at: 2 put: serial]
        """,
        """
        unregister: key
            consumers removeKey: key ifAbsent: [nil]
        """,
        """
        take: key
            "Remove and answer everything published for this consumer (each object once)."
            | e result |
            e := consumers at: key ifAbsent: [^#()].
            result := IdentitySet new.
            (e at: 1) removeAll do: [:entry | result addAll: entry].
            ^result asArray
        """,
        """
        pendingFor: key
            "Number of entries (publishes) waiting for this consumer."
            | e |
            e := consumers at: key ifAbsent: [^0].
            ^(e at: 1) size
        """,
        """
        consumerKeys
            ^consumers keys asArray
        """,
    ];

    private readonly GciSession _session;
    private readonly Oop _feed;

    /// <summary>Uses the feed stored under <paramref name="name"/> (see <see cref="Install"/>).</summary>
    /// <exception cref="ArgumentException">There is no such feed in the session's symbol list.</exception>
    public GciChangeFeed(GciSession session, string name)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(name);
        _session = session;
        Name = name;
        if (!session.TryResolveSymbol(name, out _feed))
            throw new ArgumentException($"No change feed '{name}' in the symbol list; install it with GciChangeFeed.Install.", nameof(name));
    }

    /// <summary>Name of the feed (its key in the symbol dictionary).</summary>
    public string Name { get; }

    /// <summary>The session this object works with.</summary>
    public GciSession Session => _session;

    /// <summary>
    /// Creates (or updates) the class <c>GciChangeFeed</c> and a feed named <paramref name="name"/>
    /// in <paramref name="dictionary"/>, if it does not exist yet. Commit afterwards.
    /// </summary>
    /// <param name="session">Session that installs the feed.</param>
    /// <param name="name">Key of the feed, a Smalltalk identifier.</param>
    /// <param name="dictionary">Symbol dictionary of the session's symbol list that gets the class and the feed.</param>
    public static GciChangeFeed Install(GciSession session, string name, string dictionary = "UserGlobals")
    {
        ArgumentNullException.ThrowIfNull(session);
        CheckIdentifier(name, nameof(name));
        CheckIdentifier(dictionary, nameof(dictionary));
        session.Execute($$"""
            | dict cls |
            dict := System myUserProfile symbolList objectNamed: #{{dictionary}}.
            cls := dict at: #{{ClassName}} ifAbsent: [nil].
            cls isNil ifTrue: [
                cls := Object subclass: '{{ClassName}}' instVarNames: #(#consumers)
                    classVars: #() classInstVars: #() poolDictionaries: #() inDictionary: dict].
            nil
            """);
        // Methods are compiled every time, so an existing installation gets the current version.
        var compile = string.Concat(Methods.Select(m =>
            $"cls compileMethod: '{m.Replace("\r\n", "\n").Replace("'", "''")}' dictionaries: symbols category: 'GciChangeFeed' environmentId: 0.\n"));
        session.Execute($$"""
            | cls symbols |
            symbols := System myUserProfile symbolList.
            cls := (symbols objectNamed: #{{dictionary}}) at: #{{ClassName}}.
            {{compile}}nil
            """);
        session.Execute($$"""
            | dict |
            dict := System myUserProfile symbolList objectNamed: #{{dictionary}}.
            (dict includesKey: #{{name}}) ifFalse: [dict at: #{{name}} put: ({{ClassName}} new initialize; yourself)].
            nil
            """);
        return new GciChangeFeed(session, name);
    }

    /// <summary>
    /// Publishes changed objects to every registered consumer. Call it in the writer's transaction,
    /// before the commit, then <see cref="Ring"/> after the commit.
    /// </summary>
    public void Publish(params ReadOnlySpan<Oop> objects)
    {
        if (objects.IsEmpty) return;
        var array = _session.NewObject(KnownClasses.Array, objects.Length);
        try
        {
            _session.StoreVaryingOops(array, 1, objects);
            _session.Release(_session.Perform(_feed, "publish:", array));
        }
        finally
        {
            _session.Release(array);
        }
    }

    /// <inheritdoc cref="Publish(ReadOnlySpan{Oop})"/>
    public void Publish(IEnumerable<Oop> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        Publish([.. objects]);
    }

    /// <summary>After the commit: sends the doorbell signal to every consumer's listener session.</summary>
    public void Ring() => _session.Release(_session.Perform(_feed, "ring"));

    /// <summary>
    /// Registers a consumer (or updates the listener session of an existing one). Its queue starts
    /// to collect from this transaction's commit on. Commit afterwards.
    /// </summary>
    /// <param name="consumer">Key of the consumer, stable across restarts.</param>
    /// <param name="listenerSessionId">Session id of the consumer's <see cref="GciSignalListener"/>, which gets the doorbell.</param>
    public void Register(string consumer, long listenerSessionId)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        _session.Send(_feed, "register:serial:", consumer,
            _session.Evaluate<long>($"GsSession serialOfSession: {listenerSessionId}"));
    }

    /// <summary>Removes a consumer and its queue. Commit afterwards.</summary>
    public void Unregister(string consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        _session.Send(_feed, "unregister:", consumer);
    }

    /// <summary>
    /// Removes and answers everything published for <paramref name="consumer"/> that this
    /// session's view contains (abort first), each object once. The removal becomes permanent
    /// with the next commit; abort instead to get the objects again.
    /// </summary>
    public Oop[] Take(string consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        var key = _session.FromValue(consumer);
        var array = Oop.Nil;
        try
        {
            array = _session.Perform(_feed, "take:", key);
            return _session.FetchVaryingOops(array);
        }
        finally
        {
            _session.Release(key, array);
        }
    }

    /// <summary>Number of publishes waiting for <paramref name="consumer"/> in this session's view.</summary>
    public long Pending(string consumer) => _session.Send<long>(_feed, "pendingFor:", consumer);

    /// <summary>Keys of the registered consumers.</summary>
    public IReadOnlyList<string> Consumers()
    {
        var array = _session.Perform(_feed, "consumerKeys");
        try
        {
            return [.. _session.ToList(array).Select(k => (string)k!)];
        }
        finally
        {
            _session.Release(array);
        }
    }

    private static void CheckIdentifier(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (value.Length == 0 || !(char.IsLetter(value[0]) || value[0] == '_') || !value.All(c => char.IsLetterOrDigit(c) || c == '_'))
            throw new ArgumentException($"'{value}' is not a Smalltalk identifier.", paramName);
    }
}
