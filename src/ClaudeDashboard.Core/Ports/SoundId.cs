namespace ClaudeDashboard.Core.Ports;

/// <summary>
/// Names a sound in the dashboard's sound language (TS §IV.5; Impl §1.3, Part 7).
/// </summary>
/// <remarks>
/// An identifier rather than an enum because the mapping from id to audio is a
/// <em>configuration</em> concern: sounds ship in the app directory and the operator can
/// override them from the config directory (Impl Part 7). Core names the sound; the adapter
/// resolves it to a file. The well-known ids below are TS §IV.5's existing sound language;
/// the type stays open so a later phase can add one without changing Core.
///
/// The id carries no volume. A notice and a nudge are the <em>same</em> sound at different
/// gains — "the same melody, softer and quieter" (TS §IV.5) — which is why
/// <see cref="ISoundPlayer.Play"/> takes gain separately instead of there being a
/// "quiet" variant of each id.
/// </remarks>
public readonly record struct SoundId
{
    /// <summary>A turn finished — the session is now Unread.</summary>
    public static readonly SoundId Finished = new("finished");

    /// <summary>A permission prompt is blocking the session.</summary>
    public static readonly SoundId Permission = new("permission");

    /// <summary>Claude is waiting on an answer.</summary>
    public static readonly SoundId Question = new("question");

    /// <summary>The turn died on an error.</summary>
    public static readonly SoundId Error = new("error");

    private readonly string? _name;

    /// <summary>Names a sound.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty, or whitespace.</exception>
    public SoundId(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A sound id must be a non-empty name.", nameof(name));
        }

        _name = name;
    }

    /// <summary>The sound's name. Never null; empty only for <c>default</c>.</summary>
    public string Name => _name ?? string.Empty;

    /// <summary>True for <c>default(SoundId)</c>, which names no sound.</summary>
    /// <remarks>See <see cref="ValueTypeConventions"/> for why these types stay structs.</remarks>
    public bool IsEmpty => string.IsNullOrEmpty(_name);

    public override string ToString() => Name;
}
