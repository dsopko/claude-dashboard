using System.Reflection;
using ClaudeDashboard.App.Hosting;
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
/// same text reaches the rest of the product as plain strings, where two separate log-formatting
/// routes will print it: a record's compiler-generated <c>ToString</c> renders every public
/// property for a plain <c>{Event}</c>, and Serilog's destructuring operator <c>{@X}</c> reflects
/// over the public properties of <em>any</em> type at all. This is the list of those places.
/// </para>
/// <para>
/// <strong>THIS ASSERTS THE EXTENT OF THE GAP, NOT ITS EXISTENCE, AND THE DIFFERENCE IS THE WHOLE
/// POINT.</strong> An earlier version asserted that a particular field leaks. That made green mean
/// "the vulnerability is present and that is correct", and the route back to green after a partial
/// fix was to re-assert a leak somewhere else — a well-formed action leading somewhere wrong.
/// Asserting the <em>set</em> inverts it: green means "the gap is exactly this big", and the way
/// back to green after a fix is to <strong>delete the fixed entry</strong> from
/// <see cref="CarriesOperatorText"/>. The perverse repair stops being available rather than being
/// discouraged.
/// </para>
/// <para>
/// <strong>THE SCOPE OF THE SCAN IS THE THING MOST LIKELY TO BE WRONG HERE.</strong> It has been
/// too narrow twice, in one afternoon, and both times the artefact said so in plain sight. First
/// the inventory covered only the operator's words, while the ruling that commissioned it said
/// "the operator's words and the model's answers" — so <c>LastAssistantMessage</c> and
/// <c>Answer</c> were missing. Then it filtered to records, justified by a remark reading "a plain
/// class prints its type name and leaks nothing by default" — true of <c>ToString</c>, false of
/// <c>{@}</c>, which is the route that started this whole thread. That filter hid
/// <c>SessionViewModel</c>: a plain class, in the other assembly, re-exposing the prompt and the
/// answer as its own properties — and the type UI code is most likely to log, because
/// <c>{@Row}</c> while working out why a row rendered oddly is a more natural line than logging a
/// domain object.
/// </para>
/// <para>
/// So the predicate is now the one <c>{@}</c> itself uses — <strong>a public instance string
/// property, on any public type, in either of our assemblies</strong> — and the record filter is
/// gone. If this ever needs narrowing again, narrow it for a reason about the threat, never for a
/// reason about which types happened to be in mind when it was written.
/// </para>
/// <para>
/// Nothing in <c>src/</c> logs a whole object today; every <c>{@</c> site was enumerated and the
/// only one is inside <see cref="PayloadJson"/>'s own remarks. This is a gap in a guarantee, not a
/// live disclosure.
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
    /// <strong>Measured, not assumed.</strong> Each was rendered through a real Serilog pipeline
    /// with a marker in it and came back — the record-shaped ones through a plain <c>{Event}</c>,
    /// the ones on <c>SessionViewModel</c> through <c>{@Row}</c>. That pair is also the
    /// demonstration of why both routes must be in scope: <c>{Row}</c> on the very same object is
    /// clean. The entries T1.24 added are measured the same way, by
    /// <c>SessionTitleLoggingTests</c>, rather than reasoned into the list.
    /// </para>
    /// <para>
    /// Four layers, all carrying the same words. <c>HookPayload</c> is the wire body as
    /// deserialized; <c>UserPromptSubmit</c>, <c>Stop</c> and <c>InboundEvent</c> are the domain
    /// events mapped from it; <c>Exchange</c> and <c>Session</c> are what the Registry keeps;
    /// <c>SessionViewModel</c> is what the screen binds to. <strong>A single prompt exists as a
    /// plain string in four objects at once</strong>, which is worth knowing before anyone
    /// estimates issue #11.
    /// </para>
    /// <para>
    /// <strong><c>Session</c> is listed now, and it was not before.</strong> The old note here
    /// said it "carries no prose string of its own" and reached the right conclusion for the
    /// wrong reason — it did hold an <c>Exchange</c>, and a record printing a record prints the
    /// nested one, so <c>{Session}</c> already exposed the prompt and the answer transitively.
    /// Since T1.24 it also carries <c>Title</c> directly. The transitive path is still there and
    /// wrapping the <c>Exchange</c> entries still closes it.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> CarriesOperatorText = new(StringComparer.Ordinal)
    {
        // The wire body, deserialized.
        "HookPayload.Prompt",
        "HookPayload.LastAssistantMessage",
        "HookPayload.SessionTitle",

        // The domain events mapped from it. `SessionTitle` is common to every variant since
        // T1.24, so the scan reports it once against the base type rather than nine times.
        "UserPromptSubmit.Prompt",
        "Stop.LastAssistantMessage",
        "InboundEvent.SessionTitle",

        // What the Registry keeps.
        "Exchange.Prompt",
        "Exchange.Answer",
        "Session.Title",

        // What the screen binds to — and the type most likely to be logged.
        "SessionViewModel.Prompt",
        "SessionViewModel.PromptSnippet",
        "SessionViewModel.Answer",
        "SessionViewModel.TitleDisplay",
        "SessionViewModel.TitlePrefix",
        "SessionViewModel.TitleTooltip",
        "SessionViewModel.RowName",
    };

    /// <summary>
    /// Properties holding identifiers, paths, wire vocabulary and derived display text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This list exists so that a <em>newly added</em> string property cannot land in neither list
    /// and pass unnoticed. Anything found and unclassified fails, which forces whoever adds a
    /// string to say which kind it is. That is a small tax on a common change and it is the only
    /// thing that makes the inventory above trustworthy.
    /// </para>
    /// <para>
    /// <strong>The wire-vocabulary entries were checked against the hook contract rather than
    /// assumed</strong> (<c>docs/claude-code-hooks-reference.md</c>), because an error string is
    /// the classic place a fragment of somebody's content ends up.
    /// <c>StopFailure.ErrorKind</c> comes from <c>error_type</c>, a closed set of ten spellings;
    /// <c>SessionEnd.Reason</c> from <c>end_reason</c>, a closed set of five; both fall back to the
    /// matcher, which is also a token. <c>SessionStart.Source</c> is undocumented as a JSON field
    /// but carries the same matcher spellings.
    /// </para>
    /// <para>
    /// <strong>The reason that classification is safe is not that errors cannot carry prose — it is
    /// that the prose field is a different one and ingress does not read it.</strong>
    /// <c>StopFailure</c> documents <c>error_message</c> beside <c>error_type</c>, and
    /// <c>Notification</c> documents <c>notification_text</c>; we consume neither. The reference's
    /// own "leaving on the table" note proposes reading both, to put <em>what is happening</em> on
    /// a row that currently shows only <em>that</em> something is. <strong>If that is ever taken
    /// up, those fields belong in <see cref="CarriesOperatorText"/>, not here.</strong>
    /// </para>
    /// <para>
    /// <c>Cwd</c> and <c>TranscriptPath</c> are paths rather than prose. They can be revealing
    /// about what somebody is working on, and the dashboard already logs <c>Cwd</c> deliberately,
    /// so they are classified rather than silently omitted — if that judgement is revisited, this
    /// is the line to revisit. <c>SessionViewModel.Detail</c> is <c>ErrorKind</c> under another
    /// name; <c>TrayViewModel.Tooltip</c>, the header labels and the age strings are built from
    /// counts and clocks, never from a payload.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> CarriesIdentifiersOnly = new(StringComparer.Ordinal)
    {
        // Wire discriminators and vocabulary.
        "Ack.HookEventName", "CwdChanged.HookEventName", "Notification.HookEventName",
        "PostToolBatch.HookEventName", "SessionEnd.HookEventName", "SessionStart.HookEventName",
        "RostersChanged.HookEventName", "SoundCommand.HookEventName", "Stop.HookEventName",
        "StopFailure.HookEventName",
        "UserPromptSubmit.HookEventName",
        "Notification.NotificationType", "SessionEnd.Reason", "SessionStart.Source",
        "StopFailure.ErrorKind",

        // The operator's own label for a roster (T1.25, issue #16). AN IDENTIFIER: they type it to
        // name a group, it is never derived from a prompt or an answer, and it is deliberately
        // logged — the mis-mark warning names the roster so that it need never name a member.
        //
        // A roster's MEMBERS are the other half and they are session titles, which T1.24 classified
        // as operator text. They are absent from this list because the scan cannot see them: the
        // predicate is a public instance STRING property, and members are a collection of strings.
        // That hole is filed as its own issue rather than widened here; what closes the real
        // exposure meanwhile is RosterLoggingTests, which proves no line names a member.
        "Roster.Name",

        // The port choice (T1.21): a candidate list like "52888:Free → 52889:Unrecognised".
        // Port numbers and occupant names, formed here and never from a payload.
        "PortChoice.Trail",

        // Identifiers and paths on the domain.
        "InboundEvent.Cwd", "InboundEvent.PromptId", "InboundEvent.TranscriptPath",
        "Exchange.PromptId", "Session.Cwd", "Session.ErrorKind",
        "SessionId.Value", "GroupKey.Value", "SoundId.Name", "StateTransition.Cause",

        // The wire DTO's non-prose fields.
        "HookPayload.Cwd", "HookPayload.ErrorType", "HookPayload.HookEventName",
        "HookPayload.Matcher", "HookPayload.NotificationType", "HookPayload.PromptId",
        "HookPayload.Reason", "HookPayload.SessionId", "HookPayload.Source",
        "HookPayload.TranscriptPath",

        // Derived display text: counts, clocks and labels, never a payload.
        "BandHeaderViewModel.Label",
        "GroupViewModel.IdleText", "GroupViewModel.Label", "GroupViewModel.Workspace",
        "QuietFooterViewModel.Key", "QuietFooterViewModel.Text",
        "SessionViewModel.AgeText", "SessionViewModel.AskedAtText", "SessionViewModel.BadgeText",
        "SessionViewModel.Cwd", "SessionViewModel.Detail", "SessionViewModel.ErrorKind",
        "SessionViewModel.GroupTag",

        // The session id on the expanded row (T1.23, issue #15). An IDENTIFIER, not operator or
        // Claude text — Claude Code mints it and nothing the operator typed reaches it — so issue
        // #11's wrapping of unprotected text does not extend here. It is displayed deliberately,
        // and SessionId's own remark was rewritten to stop claiming otherwise.
        "SessionViewModel.ShortId", "SessionViewModel.IdTooltip",

        // AND ITS NEIGHBOUR GOES THE OTHER WAY, WHICH IS NOT AN INCONSISTENCY. The session's
        // TITLE is a session-scoped string on the same view model and it is in
        // CarriesOperatorText, because the slot holds two kinds of value with nothing to tell
        // them apart: a name the operator set, and — for a session nobody named — a title a
        // background model call wrote by summarising their first prompt. A classification has to
        // hold for every value the slot can carry, not for the common one, so "Director" being an
        // identifier does not make the slot one. The id above passes the test the title fails:
        // Claude Code mints it and nothing the operator typed reaches it.


        // T1.26's operator-facing strings. IDENTIFIERS AND LABELS, and each for its own reason:
        //
        //   · RosterPromptViewModel.Name is a ROSTER's own name — typed by the operator to label a
        //     group, compared against nothing, never derived from a prompt or an answer. The same
        //     classification Roster.Name already carries.
        //   · MainViewModel.SelectionText is built from a count.
        //   · SessionViewModel.SelectionRefusal is one of two fixed strings.
        //
        // A roster's MEMBERS are the other half and are session titles, which stay out of this list
        // only because the scan cannot see a collection of strings — filed separately, and closed
        // meanwhile by the never-log tests rather than by the inventory.
        "MainViewModel.SelectionText",
        "RosterPromptViewModel.Name",
        "SessionViewModel.SelectionRefusal",
        "TrayViewModel.MuteAllLabel", "TrayViewModel.PauseLabel", "TrayViewModel.Tooltip",

        // Configuration, paths and operational results.
        "ClaudeCodePaths.ConfigDirectory", "ClaudeCodePaths.UserSettingsFile",
        "DashboardPaths.DatabaseFile", "DashboardPaths.LogFile", "DashboardPaths.LogFolder",
        "DashboardPaths.HookScriptFile", "DashboardPaths.ListeningFile",
        "DashboardPaths.PortFile", "DashboardPaths.Root", "DashboardPaths.RootProblem",
        "DashboardPaths.SettingsFile", "DashboardPaths.SoundFolder",
        "HealthProbeResult.Instance", "HealthProbeResult.Problem",

        // Issue #29's hook installer. ScriptPath is a path in the dashboard's own data folder.
        // HookPresence.Problem is why Claude Code's settings file could not be read — an exception
        // message about the file, never anything out of it. THE CHECK MUST NEVER LOG THE FILE'S
        // CONTENTS: those are the operator's hooks, and one of them may carry their prompt text.
        "HookInstaller.ScriptPath", "HookPresence.Problem",
        "IngressStatus.Fault",
        "LogonTaskFacts.Command", "LogonTaskFacts.RestartInterval", "LogonTaskFacts.RunLevel",
        "SettingsFileWriter.LockPath", "SettingsLoadResult.Problem",
        "SettingsWriteResult.BackupPath", "SettingsWriteResult.Problem",
        "ShowSignalResult.Problem", "SingleInstanceGate.Name", "SqliteEventStore.Path",
        "TaskCommandResult.Output", "TokenSetupResult.Problem",
    };

    /// <summary>Both of our assemblies. App as much as Core: App is where the logger lives.</summary>
    private static readonly Assembly[] Ours = [typeof(Session).Assembly, typeof(AppHost).Assembly];

    /// <summary>
    /// Every public instance string property declared in our own code, keyed by where it is
    /// declared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The predicate is the one Serilog's destructurer uses</strong>, minus indexers: a
    /// public instance property whose type is <see langword="string"/>. No record filter —
    /// record-ness decides whether <c>ToString</c> leaks and says nothing about <c>{@}</c>.
    /// </para>
    /// <para>
    /// Keyed by <em>declaring</em> type, so a property inherited by nine event variants is one
    /// entry rather than nine, and restricted to declarations in our assemblies so the scan does
    /// not inventory <c>Window.Title</c> and <c>Exception.Message</c>. Both of those are about
    /// keeping the list short enough that somebody will actually maintain it.
    /// </para>
    /// </remarks>
    private static List<string> StringPropertiesInOurCode() =>
        [.. Ours
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsPublic && !type.IsAbstract)
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            .Where(property =>
                property.PropertyType == typeof(string) &&
                property.GetIndexParameters().Length == 0 &&
                property.DeclaringType is not null &&
                Ours.Contains(property.DeclaringType.Assembly))
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)];

    /// <summary>
    /// The unprotected text is in exactly the places <see cref="CarriesOperatorText"/> names.
    /// </summary>
    [Fact]
    public void The_unprotected_operator_text_is_exactly_the_inventory()
    {
        var found = StringPropertiesInOurCode();
        var unprotected = found.Where(name => !CarriesIdentifiersOnly.Contains(name)).ToHashSet(StringComparer.Ordinal);

        var appeared = unprotected.Except(CarriesOperatorText).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var gone = CarriesOperatorText.Except(unprotected).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.True(
            appeared.Count == 0,
            $"NEW UNCLASSIFIED STRING: {string.Join(", ", appeared)}. " +
            "Either it carries the operator's words or Claude's answers — in which case it belongs in " +
            "CarriesOperatorText and needs the argument on PayloadJson, not just a line in a list — or it " +
            "is an identifier, path, wire token or derived label, in which case classify it in " +
            "CarriesIdentifiersOnly. Do not leave a public string property unclassified: this test is the " +
            "only thing that notices a new place the operator's words can be printed.");

        Assert.True(
            gone.Count == 0,
            $"THESE ARE NO LONGER UNPROTECTED PLAIN STRINGS: {string.Join(", ", gone)}. " +
            "IF YOU JUST WRAPPED THEM (issue #11), THIS IS THE EXPECTED FAILURE AND THE FIX IS TO DELETE " +
            "THOSE ENTRIES FROM CarriesOperatorText IN THIS FILE — then delete the residual paragraphs they " +
            "are named in, in PayloadJson, InboundEvent.Payload, SqliteEventStore and EventArchive. " +
            "DO NOT re-point this test at some other leaking field to restore green.");
    }

    /// <summary>Every classified name still names something that exists.</summary>
    /// <remarks>
    /// The lists rot in the other direction too: a property renamed or removed leaves a dead entry,
    /// and a dead entry pre-approves any future property that takes the same name.
    /// </remarks>
    [Fact]
    public void The_classification_lists_describe_properties_that_exist()
    {
        var found = StringPropertiesInOurCode().ToHashSet(StringComparer.Ordinal);

        var stale = CarriesIdentifiersOnly
            .Concat(CarriesOperatorText)
            .Except(found)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"STALE CLASSIFICATION ENTRIES: {string.Join(", ", stale)}. These name properties that no longer " +
            "exist. Remove them — a dead entry pre-approves any future property that takes the same name. " +
            "If one disappeared because you wrapped it, the other test in this file says what to do.");
    }

    /// <summary>
    /// The scan reaches both assemblies and both shapes, so a green run means something.
    /// </summary>
    /// <remarks>
    /// The control, and it is not decoration. Every assertion above is satisfied by a scan that
    /// found nothing: a reflection query that quietly stopped matching would turn this whole file
    /// green and silent. The two named properties are one record in Core and one plain class in
    /// App — the exact pair whose absence is what "too narrow" looked like both times.
    /// </remarks>
    [Fact]
    public void The_scan_reaches_both_assemblies_and_both_shapes()
    {
        var found = StringPropertiesInOurCode();

        Assert.True(
            found.Count > 60,
            $"the scan found only {found.Count} string properties; it has stopped matching the product, and " +
            "every other assertion in this file is passing on an empty set");

        Assert.Contains("InboundEvent.Cwd", found);
        Assert.Contains("SessionViewModel.Prompt", found);
    }
}
