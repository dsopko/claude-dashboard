namespace ClaudeDashboard.Core;

/// <summary>
/// One question-and-answer turn: the prompt the operator submitted and, once known, the
/// answer Claude finished with (TS §II.2; Impl §2.1).
/// </summary>
/// <remarks>
/// The latest <see cref="Exchange"/> is the session's context line and the payload of an
/// expanded row (Impl §2.1). Both halves arrive inline on events — <c>prompt</c> on
/// <c>UserPromptSubmit</c> and <c>last_assistant_message</c> on <c>Stop</c> — which is why a
/// row can show the answer beside the question without reading the transcript (TS §II.2).
///
/// Both text fields are <strong>data, never instruction</strong> (TS §II.5): stored and
/// rendered verbatim, never interpreted. Nothing here inspects, parses, or trims them.
/// </remarks>
public sealed record Exchange
{
    private readonly string _prompt = string.Empty;

    /// <summary>
    /// The submitted prompt text, verbatim (<c>prompt</c> on <c>UserPromptSubmit</c>).
    /// May be empty; never null.
    /// </summary>
    public required string Prompt
    {
        get => _prompt;
        init => _prompt = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The final assistant message, verbatim (<c>last_assistant_message</c> on <c>Stop</c>),
    /// or null while the turn is still unanswered.
    /// </summary>
    public string? Answer { get; init; }

    /// <summary>
    /// Claude Code's <c>prompt_id</c>, correlating this prompt with the <c>Stop</c> it
    /// produced (TS §II.3). Null when the event carried none.
    /// </summary>
    public string? PromptId { get; init; }

    /// <summary>When the prompt was submitted. Drives age display and nudge timing.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the answer arrived, or null while unanswered.</summary>
    public DateTimeOffset? AnsweredAt { get; init; }

    /// <summary>True once the answer timestamp is known.</summary>
    public bool IsAnswered => AnsweredAt is not null;
}
