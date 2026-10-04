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

        // A background task's description (T1.41, issue #52): what the agent said the task is,
        // agent-written, so it is operator-adjacent text in every layer that carries it — the
        // domain event, the session, and the two places the row shows it. Added on the brief's
        // instruction and because it is true, not to quiet this test; that it never reaches a
        // log is WaitingLoggingTests' to measure. The task's command is in none of these: it is
        // never read off the wire.
        "BackgroundTask.Description",
        "WaitingTask.Description",
        "SessionViewModel.WaitingSummary",
        "WaitingOnLine.Description",
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
    /// <c>StopFailure.ErrorKind</c> comes from <c>error</c> on the wire, or <c>error_type</c> as
    /// documented, a closed set of ten spellings (T1.53: the archive holds three of them, and
    /// nothing else);
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

        // T1.44: the error matcher a quiet tick puts back — Session.ErrorKind, copied. The cron
        // prompts a tick is recognised by are prompt text, and are deliberately not a string
        // property anywhere: ScheduledPrompts holds them to compare and prints only a count.
        "TickSnapshot.ErrorKind",

        // T1.46's /state report: the session id, the workspace group key, the working directory,
        // the error matcher and Claude Code's task id — each a copy of a field already classified
        // here (Session.Cwd, Session.ErrorKind, SessionId.Value, GroupKey.Value, WaitingTask.Id).
        // The report's prose — the title and the task description — is NOT a string property: it
        // travels as OperatorText, which the scan cannot see and Serilog cannot print, so
        // CarriesOperatorText did not grow. OperatorTextTests measures that through Serilog.
        "SessionStateEntry.Cwd", "SessionStateEntry.ErrorKind", "SessionStateEntry.Group",
        "SessionStateEntry.Id", "WaitingTaskEntry.Id",

        // The wire DTO's non-prose fields.
        "HookPayload.Cwd", "HookPayload.ErrorType", "HookPayload.HookEventName",
        "HookPayload.Matcher", "HookPayload.NotificationType", "HookPayload.PromptId",
        "HookPayload.Reason", "HookPayload.SessionId", "HookPayload.Source",
        "HookPayload.TranscriptPath",

        // Derived display text: counts, clocks and labels, never a payload.
        "BandHeaderViewModel.Label",
        "GroupViewModel.IdleText", "GroupViewModel.Label", "GroupViewModel.Workspace",
        "QuietFooterViewModel.Key", "QuietFooterViewModel.Text",
        // T1.41: a fixed label, a fixed kind word, a duration, and Claude Code's task ids.
        "SessionViewModel.AnswerLabel", "WaitingOnLine.Age", "WaitingOnLine.Kind",
        "BackgroundTask.Id", "WaitingTask.Id",
        "SessionViewModel.AgeText", "SessionViewModel.AskedAgoText", "SessionViewModel.AskedAtText",
        "SessionViewModel.BadgeText",
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
        //   · MainViewModel.CountsText (T1.39) is the four counts and the strip's fixed words —
        //     "sessions", "need you", "unread", "working" — and nothing a session carries.
        //   · SessionViewModel.SelectionRefusal is one of two fixed strings.
        //
        // A roster's MEMBERS are the other half and are session titles, which stay out of this list
        // only because the scan cannot see a collection of strings — filed separately, and closed
        // meanwhile by the never-log tests rather than by the inventory.
        "MainViewModel.CountsText",
        "MainViewModel.SelectionText",
        "MainViewModel.SessionsWord",
        "RosterPromptViewModel.Name",
        "SessionViewModel.SelectionRefusal",
        "TrayViewModel.MuteAllLabel", "TrayViewModel.PauseLabel", "TrayViewModel.Tooltip",

        // The hook-route notice (the ruling of 2026-10-01): two fixed texts, built from constants and
        // the plugin's id, never from a payload; and the tray's copy of the window's one.
        "HookNotice.Text", "HookNotice.TrayText", "TrayViewModel.NoticeText",

        // The history notice (T1.54, issue #71): two fixed texts. The board's tray text is the shown
        // notices' tray texts joined, each of them classified here.
        "HistoryNotice.Text", "HistoryNotice.TrayText", "NoticeBoard.TrayText",

        // The sound device notice (T1.55, issue #72): two fixed texts.
        "SoundDeviceNotice.Text", "SoundDeviceNotice.TrayText",

        // The queue notices (T1.58, issue #3): fixed texts.
        "FellBehindNotice.Text", "FellBehindNotice.TrayText", "EventsLostNotice.Text", "EventsLostNotice.TrayText",

        // The settings keep-aside (T1.56, issue #73): the backup's full path; why a keep-aside
        // failed, which is Windows' own I/O message and names a file; and the notice, built from fixed
        // text, the backup's file name and the data folder. No setting value and no parse text.
        "SettingsAtStart.BackupFile", "SettingsAtStart.KeepAsideProblem",
        "SettingsNotice.Text", "SettingsNotice.TrayText",

        // Start with Windows (issue #36): the installed exe's path and its quoted Run data; a line of
        // fixed text, or the registry's own refusal message; and that refusal message itself.
        "StartWithWindows.ExePath", "StartWithWindows.RunData", "SettingsViewModel.Note", "StartupState.Problem",

        // Configuration, paths and operational results.
        "ClaudeCodePaths.ConfigDirectory", "ClaudeCodePaths.UserSettingsFile",
        "DashboardPaths.DatabaseFile", "DashboardPaths.LogFile", "DashboardPaths.LogFolder",
        "DashboardPaths.HookScriptFile", "DashboardPaths.ListeningFile",
        "DashboardPaths.PortFile", "DashboardPaths.Root", "DashboardPaths.RootProblem",
        "DashboardPaths.SettingsFile", "DashboardPaths.SoundFolder",
        "HealthProbeResult.Instance", "HealthProbeResult.Problem",

        // T1.37's decisions record. Every field is an enum name, a session or group identifier,
        // a state name, or key=value identifier pairs — the record's own contract (issue #48:
        // "never a title, prompt, payload or message body"), and the tests per kind assert it.
        // The archived payload itself travels as the PayloadJson wrapper, not as a string here.
        "Decision.Detail", "Decision.FromState", "Decision.Reason",
        "Decision.SessionId", "Decision.ToState",

        // The log file's floor: an enum name from the operator's own settings file.
        "LoggingSettings.MinimumLevel",

        // The read of Claude Code's settings. ScriptPath is a path in the dashboard's own data
        // folder; ClaudeConfigDirectory (T1.33) is Claude Code's configuration path — our
        // configuration, not the operator's words, and the refusal line names it so a
        // CLAUDE_CONFIG_DIR pointing somewhere odd is diagnosable.
        "HookCheck.ClaudeConfigDirectory",
        // HookPresence.Problem is why Claude Code's settings file could not be read — an exception
        // message about the file, never anything out of it. THE CHECK MUST NEVER LOG THE FILE'S
        // CONTENTS: those are the operator's hooks, and one of them may carry their prompt text.
        "HookCheck.ScriptPath", "HookPresence.Problem",

        // Issue #30's plugin route. Three are folders: the dashboard's own plugin folder, twice,
        // and the folder Claude Code's settings give for a plugin of the same name that belongs
        // to another data folder — a path read out of one settings key, never a hook. The other
        // two are what the claude program printed about a plugin command: its status line and
        // paths in our data folder. The program is given a folder and a plugin name and nothing
        // else, so it has no session text to print.
        "ClaudeCliResult.Output", "DashboardPaths.PluginFolder", "HookPresence.ForeignPlugin",
        "PluginInstaller.PluginFolder", "PluginResult.Problem",
        // T1.57: the port notice's window text and its tray copy: fixed text, port numbers and the
        // path of settings.json.
        "IngressStatus.Fault", "IngressStatus.Text", "IngressStatus.TrayText",
        // T1.60: a runs row beside its times. The informational version, and the data folder, which is
        // the one path the runs table holds.
        "RunStart.DataRoot", "RunStart.Version",
        "SettingsLoadResult.Problem",
        "ShowSignalResult.Problem", "SingleInstanceGate.Name", "SqliteEventStore.Path",
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
