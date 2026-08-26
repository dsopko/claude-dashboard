using System.Reflection;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// Every place the operator's words or Claude's answers live as an unprotected
/// <see langword="string"/>, asserted as an exact set (T1.17; issue #11).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PayloadJson"/> makes the raw hook body unprintable. It protects that one field. The
/// same text also reaches the domain as plain strings on records with a compiler-generated
/// <c>ToString</c>, so <c>logger.Warning("Declined {Event}", e)</c> prints it — no destructuring
/// operator, nothing unusual. This is the list of those places.
/// </para>
/// <para>
/// <strong>THIS ASSERTS THE EXTENT OF THE GAP, NOT ITS EXISTENCE, AND THE DIFFERENCE IS THE WHOLE
/// POINT.</strong> The first version of this test asserted that a particular field leaks. That
/// made green mean "the vulnerability is present and that is correct", and the only route back to
/// green after a partial fix was to re-assert a leak somewhere else — a well-formed action leading
/// somewhere wrong. Asserting the <em>set</em> inverts it: green means "the gap is exactly this
/// big", which is a true and useful thing to know, and the way back to green after a fix is to
/// <strong>delete the fixed entry from <see cref="CarriesOperatorText"/></strong>. The perverse
/// repair stops being available rather than being discouraged.
/// </para>
/// <para>
/// <strong>It also catches the failure the previous version could not see at all:</strong> a
/// <em>third</em> type acquiring unprotected text. That is the likelier event — Phase 5 adds
/// history search, and search results carry the operator's words — and a test pinned to two known
/// fields would have watched it happen in silence.
/// </para>
/// <para>
/// Nothing in <c>src/</c> logs a whole record today; every <c>{@</c> site was enumerated. This is
/// a gap in a guarantee, not a live disclosure.
/// </para>
/// </remarks>
public sealed class UnprotectedTextInventory
{
    /// <summary>
    /// Properties holding text the operator or Claude wrote. <strong>The inventory.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shrink this when a field is wrapped. Never grow it to make a failure go away: a new entry
    /// means a new place the operator's words can be printed, and it needs the argument on
    /// <see cref="PayloadJson"/>, not a line here.
    /// </para>
    /// <para>
    /// <strong>All five were measured, not assumed.</strong> Each was rendered through a real
    /// Serilog pipeline with a marker in it, and each came back. <c>Session</c> is not listed
    /// separately because it carries no prose string of its own — it holds an <c>Exchange</c>, and
    /// a record printing a record prints the nested one, so <c>{Session}</c> exposes both entries
    /// below transitively. Wrapping <c>Exchange</c>'s two closes that path as well.
    /// </para>
    /// <para>
    /// The Director's ruling that opened this named two of them, both about the operator's half.
    /// Building the list found the model's half — <c>LastAssistantMessage</c> and
    /// <c>Answer</c> — which the same argument covers and which nobody had written down.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> CarriesOperatorText = new(StringComparer.Ordinal)
    {
        "UserPromptSubmit.Prompt",
        "Stop.LastAssistantMessage",
        "SessionStart.SessionTitle",
        "Exchange.Prompt",
        "Exchange.Answer",
    };

    /// <summary>
    /// Properties holding identifiers, paths and wire spellings — safe to print.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This list exists so that a <em>newly added</em> string property cannot land in neither list
    /// and pass unnoticed. Anything found and not classified fails, which forces whoever adds a
    /// string to the domain to say which kind it is. That is a small tax on a common change and it
    /// is the only thing that makes the inventory above trustworthy.
    /// </para>
    /// <para>
    /// <c>Cwd</c> and <c>TranscriptPath</c> are paths rather than prose. They can be revealing
    /// about what somebody is working on, and the dashboard already logs <c>Cwd</c> deliberately,
    /// so they are classified here rather than silently omitted — if that judgement is ever
    /// revisited, this is the line to revisit.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> CarriesIdentifiersOnly = new(StringComparer.Ordinal)
    {
        "Ack.Cwd", "Ack.HookEventName", "Ack.PromptId", "Ack.TranscriptPath",
        "CwdChanged.Cwd", "CwdChanged.HookEventName", "CwdChanged.PromptId", "CwdChanged.TranscriptPath",
        "Exchange.PromptId",
        "Notification.Cwd", "Notification.HookEventName", "Notification.NotificationType",
        "Notification.PromptId", "Notification.TranscriptPath",
        "PostToolBatch.Cwd", "PostToolBatch.HookEventName", "PostToolBatch.PromptId", "PostToolBatch.TranscriptPath",
        "Session.Cwd", "Session.ErrorKind",
        "SessionEnd.Cwd", "SessionEnd.HookEventName", "SessionEnd.PromptId", "SessionEnd.Reason",
        "SessionEnd.TranscriptPath",
        "SessionStart.Cwd", "SessionStart.HookEventName", "SessionStart.PromptId", "SessionStart.Source",
        "SessionStart.TranscriptPath",
        "SoundCommand.Cwd", "SoundCommand.HookEventName", "SoundCommand.PromptId", "SoundCommand.TranscriptPath",
        "Stop.Cwd", "Stop.HookEventName", "Stop.PromptId", "Stop.TranscriptPath",
        "StopFailure.Cwd", "StopFailure.ErrorKind", "StopFailure.HookEventName", "StopFailure.PromptId",
        "StopFailure.TranscriptPath",
        "UserPromptSubmit.Cwd", "UserPromptSubmit.HookEventName", "UserPromptSubmit.PromptId",
        "UserPromptSubmit.TranscriptPath",
    };

    /// <summary>Every public string property on a Core record, found by reflection.</summary>
    /// <remarks>
    /// Records specifically, because a record is what generates a <c>ToString</c> that prints its
    /// properties. A plain class prints its type name and leaks nothing by default.
    /// </remarks>
    private static List<string> StringPropertiesOnRecords() =>
        [.. typeof(Session).Assembly.GetTypes()
            .Where(type => type.IsPublic && !type.IsAbstract && IsRecord(type))
            .SelectMany(type => type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.PropertyType == typeof(string))
                .Select(property => $"{type.Name}.{property.Name}"))
            .OrderBy(name => name, StringComparer.Ordinal)];

    /// <summary>A record has a synthesized clone method; nothing else does.</summary>
    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null;

    /// <summary>
    /// The unprotected text is in exactly the places <see cref="CarriesOperatorText"/> names.
    /// </summary>
    [Fact]
    public void The_unprotected_operator_text_is_exactly_the_inventory()
    {
        var found = StringPropertiesOnRecords();
        var unprotected = found.Where(name => !CarriesIdentifiersOnly.Contains(name)).ToHashSet(StringComparer.Ordinal);

        var appeared = unprotected.Except(CarriesOperatorText).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var gone = CarriesOperatorText.Except(unprotected).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.True(
            appeared.Count == 0,
            $"NEW UNPROTECTED TEXT: {string.Join(", ", appeared)}. " +
            "Either these carry the operator's words or Claude's answers — in which case they belong in " +
            "CarriesOperatorText and need the argument on PayloadJson, not just a line in a list — or they " +
            "are identifiers, in which case classify them in CarriesIdentifiersOnly. Do not leave a string " +
            "property on a Core record unclassified.");

        Assert.True(
            gone.Count == 0,
            $"THESE ARE NO LONGER UNPROTECTED PLAIN STRINGS: {string.Join(", ", gone)}. " +
            "IF YOU JUST WRAPPED THEM (issue #11), THIS IS THE EXPECTED FAILURE AND THE FIX IS TO DELETE " +
            "THOSE ENTRIES FROM CarriesOperatorText IN THIS FILE — then delete the residual paragraphs they " +
            "are named in, in PayloadJson, InboundEvent.Payload, SqliteEventStore and EventArchive. " +
            "Do NOT re-point this test at some other leaking field to restore green.");
    }

    /// <summary>Every string on a Core record is classified as one kind or the other.</summary>
    /// <remarks>
    /// The safe list can rot in the other direction too: a property renamed or removed leaves a
    /// dead entry behind, and a dead entry is a place where a future property could reappear under
    /// the same name already marked safe.
    /// </remarks>
    [Fact]
    public void The_classification_lists_describe_properties_that_exist()
    {
        var found = StringPropertiesOnRecords().ToHashSet(StringComparer.Ordinal);
        var stale = CarriesIdentifiersOnly.Except(found).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.True(
            stale.Count == 0,
            $"STALE ENTRIES IN CarriesIdentifiersOnly: {string.Join(", ", stale)}. These name properties that " +
            "no longer exist. Remove them — a dead entry pre-approves any future property that takes the " +
            "same name.");
    }

    /// <summary>
    /// The inventory is not empty, so a green run means something.
    /// </summary>
    /// <remarks>
    /// The control. Every assertion above is satisfied by a scan that found nothing at all — a
    /// reflection query that quietly stopped matching would turn this whole file green and silent.
    /// This is what makes the other two load-bearing.
    /// </remarks>
    [Fact]
    public void The_scan_actually_finds_the_domain()
    {
        var found = StringPropertiesOnRecords();

        Assert.True(
            found.Count > 40,
            $"the scan found only {found.Count} string properties on Core records; it has stopped matching the " +
            "domain, and every other assertion in this file is passing on an empty set");

        Assert.Contains("UserPromptSubmit.Cwd", found);
    }
}
