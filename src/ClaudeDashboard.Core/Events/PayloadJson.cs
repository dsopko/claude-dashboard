namespace ClaudeDashboard.Core.Events;

/// <summary>
/// <strong>The raw hook body</strong>, carried to the event archive and kept out of the log by
/// construction (Impl Part 8; T1.17). It protects the body and nothing else — see the residuals.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this text is stored at all, when the same text must never reach a log file.</strong>
/// The distinction is not prompt-versus-everything-else; it is what the two artefacts are for.
/// <em>The log is diagnostic and it leaves the machine</em> — it is what an operator pastes into a
/// bug report, attaches to an issue, or sends to whoever is helping them. <em>The database is the
/// product's own store and it never leaves</em>; it lives beside <c>settings.json</c> under the
/// operator's profile and exists so Phase 5 can search their own history. So the rule that prompt
/// and answer text never reach a log file does not simply extend to the database, and history
/// search without the text would be worthless.
/// </para>
/// <para>
/// <strong>What this type is: a wrapper that cannot be printed.</strong> The value is reachable
/// only through <see cref="Value"/>. <see cref="ToString"/> — which is what Serilog calls when
/// somebody writes <c>{Payload}</c> in a message template, and what an interpolated string calls
/// too — returns a size, never the content.
/// </para>
/// <para>
/// <strong>Structural, because measured was not enough.</strong> Both routes by which this text
/// could reach a sink were probed at T1.17 and both were clean: six malformed bodies through
/// <c>System.Text.Json</c> and four failing statements through <c>Microsoft.Data.Sqlite</c>, each
/// carrying a marker string, and no exception message quoted a value. But those are properties of
/// two libraries' current message formats, not guarantees, and the next person to add a log line
/// here will not have run those probes. A careless <c>{Payload}</c> being redacted by construction
/// is worth more than clean probes, so this type exists rather than a rule in a document.
/// </para>
/// <para>
/// <strong>WHAT THIS DOES NOT PROTECT, WHICH IS EASIER TO HIT THAN WHAT IT DOES.</strong> The
/// guarantee above is exactly one type wide. The <em>same words</em> also live as plain
/// <see langword="string"/> fields on <see cref="UserPromptSubmit"/> — <c>Prompt</c> — and on
/// <c>Exchange</c>, which the Registry copies them onto. Those are public properties of records
/// with a compiler-generated <c>ToString</c>, so <c>logger.Warning("Declined {Event}", e)</c>
/// prints the operator's prompt verbatim. <strong>Measured, not feared</strong>: with a marker in
/// both, a plain <c>{Event}</c> leaks the mapped prompt while <c>{Payload}</c> and
/// <c>{@Payload}</c> stay clean.
/// </para>
/// <para>
/// That hole needs no destructuring operator. The case this type was redesigned for required
/// somebody to reach for <c>@</c>; this one requires nothing, and <em>"log the event that
/// failed"</em> is the most natural line anyone will ever write about a declined event. Nothing in
/// <c>src/</c> logs it today — every <c>{@</c> site was enumerated — so this is a gap in the
/// guarantee rather than a live disclosure, and wrapping those two fields is filed as its own
/// task because <c>Prompt</c> is read by the view models and is not a one-caller field like this.
/// </para>
/// <para>
/// <strong>The lesson, which is about the sentence and not the type.</strong> An earlier version
/// of this remark claimed the operator's words were kept out of the log by construction. The type
/// was then made genuinely safe — and the claim around it was never resized, so it went on
/// describing a guarantee about the operator's words when it had only ever been about one field.
/// <em>A correct fix under an unscoped claim is still a false statement in the file.</em> Anyone
/// narrowing or widening what this protects must move this paragraph with it.
/// </para>
/// <para>
/// <strong>The other residual, which belongs to existing code as much as to this type.</strong>
/// A <c>System.Text.Json</c> syntax error reports the offending character — <c>'M' is an invalid
/// start of a value</c> — so one byte of the operator's text can reach the log at a malformed
/// body. <c>IngressEndpoints</c> logs that message today and did before this type existed, and it
/// was seen on a live staged run. One byte at a syntax error is not a disclosure; it is also not
/// nothing, and it is written down here rather than discovered later.
/// </para>
/// </remarks>
public readonly record struct PayloadJson
{
    private readonly string? _value;

    /// <summary>Wraps a raw hook body.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public PayloadJson(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        _value = value;
    }

    /// <summary>
    /// The raw JSON. <strong>The only way to the text, and the only place it may be read.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every caller of this is a place where the operator's words become reachable. There is
    /// exactly one in the product — the archive's insert — and there should stay exactly one. If a
    /// second appears, it needs the same argument this type's remarks make.
    /// </para>
    /// <para>
    /// <strong>A method rather than a property, and that is load-bearing rather than a style
    /// choice.</strong> It was a property, and the test that asks Serilog what it rendered caught
    /// it: <c>{@Payload}</c> destructures a value by reflecting over its public <em>properties</em>,
    /// so the body came straight back out despite <see cref="ToString"/> being careful. Nothing
    /// about that was hypothetical — <c>@</c> is the first thing anyone reaches for when a value
    /// renders as unhelpfully as this one deliberately does. A method is invisible to that
    /// reflection, and it reads as deliberate at the call site, which is what a line revealing the
    /// operator's prompt should look like.
    /// </para>
    /// </remarks>
    public string Reveal() => _value ?? string.Empty;

    /// <summary>How many characters the body holds. Safe to log, and safe to destructure.</summary>
    public int Length => _value?.Length ?? 0;

    /// <summary>True for <c>default(PayloadJson)</c>, which carries no body.</summary>
    /// <remarks>See <c>ValueTypeConventions</c> for why these types stay structs.</remarks>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>
    /// A size, never the content. This is what Serilog renders for <c>{Payload}</c>.
    /// </summary>
    /// <remarks>
    /// <strong>Do not "improve" this to show a prefix of the body.</strong> A prefix of a hook
    /// body is the beginning of the operator's prompt, which is the single thing this type exists
    /// to keep out of a diagnostic file.
    /// </remarks>
    public override string ToString() =>
        IsEmpty ? "<payload: none>" : $"<payload: {Length} chars>";
}
