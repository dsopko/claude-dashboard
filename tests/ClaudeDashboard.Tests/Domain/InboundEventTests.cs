using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// Pins the <see cref="InboundEvent"/> hierarchy to Impl §9.1: one variant per consumed
/// event, each carrying the fields §9.1 assigns it, under §9.1's exact names.
/// </summary>
public sealed class InboundEventTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly SessionId Id = new("s-1");

    /// <summary>Every <c>hook_event_name</c> Claude Code actually sends (Impl §9.1).</summary>
    private static readonly string[] HookBackedEventNames =
    [
        "SessionStart", "UserPromptSubmit", "Notification", "Stop",
        "StopFailure", "SessionEnd", "CwdChanged",
    ];

    [Fact]
    public void Every_variant_carries_the_common_fields()
    {
        foreach (var e in AllVariants())
        {
            Assert.Equal(Id, e.SessionId);
            Assert.Equal(At, e.Timestamp);
            Assert.Equal(@"C:\projects\dashboard", e.Cwd);
            Assert.Equal(PromptId, e.PromptId);
            Assert.Equal(@"C:\transcripts\s-1.jsonl", e.TranscriptPath);
        }
    }

    /// <summary>
    /// The variant's <c>HookEventName</c> must equal Claude Code's <c>hook_event_name</c>
    /// exactly — ingress dispatches on that wire value (T1.8).
    /// </summary>
    [Fact]
    public void Every_variant_reports_its_hook_event_name()
    {
        Assert.Equal("SessionStart", Build<SessionStart>().HookEventName);
        Assert.Equal("UserPromptSubmit", Build<UserPromptSubmit>().HookEventName);
        Assert.Equal("Notification", Build<Notification>().HookEventName);
        Assert.Equal("Stop", Build<Stop>().HookEventName);
        Assert.Equal("StopFailure", Build<StopFailure>().HookEventName);
        Assert.Equal("SessionEnd", Build<SessionEnd>().HookEventName);
        Assert.Equal("CwdChanged", Build<CwdChanged>().HookEventName);
    }

    /// <summary>
    /// The hierarchy is closed over Impl §9.1's seven consumed events plus the two synthetic
    /// variants — <c>Ack</c> and <c>SoundCommand</c> — which have no hook behind them
    /// (TS §IV.1; Impl §4, §5.2).
    /// </summary>
    /// <remarks>
    /// Both synthetic variants exist because something the operator does has to travel the same
    /// Channel as a hook: an acknowledgment so it reaches the Registry on the consumer thread
    /// (T1.12), a global sound mode so it reaches the engine there (T1.13). Neither may ever be
    /// mapped from a wire payload — see <c>Ingress_never_maps_a_synthetic_variant</c>.
    /// </remarks>
    [Fact]
    public void The_hierarchy_is_closed_over_the_consumed_events()
    {
        var variants = typeof(InboundEvent).Assembly
            .GetTypes()
            .Where(t => t.IsSubclassOf(typeof(InboundEvent)))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            [
                "Ack", "CwdChanged", "Notification", "PostToolBatch", "SessionEnd", "SessionStart",
                "SoundCommand", "Stop", "StopFailure", "UserPromptSubmit",
            ],
            variants);
    }

    /// <summary>
    /// <c>Ack</c> is the only variant no hook produces. Ingress must never map a wire payload
    /// onto it: anything able to reach the loopback endpoint could otherwise forge an
    /// acknowledgment and silence a session that genuinely needs the operator.
    /// </summary>
    [Fact]
    public void Ack_is_synthetic_and_carries_its_source()
    {
        var ack = new Ack
        {
            SessionId = Id,
            Timestamp = At,
            Cwd = Cwd,
            Source = AckSource.Manual,
        };

        Assert.Equal("Ack", ack.HookEventName);
        Assert.Equal(AckSource.Manual, ack.Source);
        Assert.DoesNotContain(ack.HookEventName, HookBackedEventNames, StringComparer.Ordinal);
    }

    [Fact]
    public void Timestamp_and_session_id_are_required_on_the_base()
    {
        // Both are `required`, so omitting either is a compile error rather than a runtime one.
        // This pins the runtime half: the guard T1.2's stale-drop depends on is never defaulted.
        var e = Build<Stop>();

        Assert.NotEqual(default, e.Timestamp);
        Assert.False(e.SessionId.IsEmpty);
    }

    [Fact]
    public void Rejects_a_null_cwd()
    {
        Assert.Throws<ArgumentNullException>(() => Build<Stop>() with { Cwd = null! });
    }

    [Fact]
    public void Accepts_an_empty_cwd_and_absent_correlation_fields()
    {
        var e = Build<Stop>() with { Cwd = string.Empty, PromptId = null, TranscriptPath = null };

        Assert.Equal(string.Empty, e.Cwd);
        Assert.Null(e.PromptId);
        Assert.Null(e.TranscriptPath);
    }

    [Fact]
    public void Variants_have_value_equality()
    {
        Assert.Equal(Build<Stop>(), Build<Stop>());
        Assert.Equal(Build<Stop>().GetHashCode(), Build<Stop>().GetHashCode());
        Assert.NotEqual(Build<Stop>(), Build<Stop>() with { Timestamp = At.AddTicks(1) });
    }

    /// <summary>Two different events that happen to share every common field are still different events.</summary>
    [Fact]
    public void Variants_of_different_types_are_never_equal()
    {
        Assert.NotEqual<InboundEvent>(Build<Stop>(), Build<CwdChanged>());
    }

    // ---- SessionStart: source, session_title, cwd -------------------------------------

    [Fact]
    public void SessionStart_carries_source_and_session_title()
    {
        var e = Build<SessionStart>() with { Source = "resume", SessionTitle = "dashboard build" };

        Assert.Equal("resume", e.Source);
        Assert.Equal("dashboard build", e.SessionTitle);
        Assert.Equal(SessionStartSource.Resume, e.ParsedSource);
        Assert.Equal(@"C:\projects\dashboard", e.Cwd);
    }

    [Theory]
    [InlineData("startup", SessionStartSource.Startup)]
    [InlineData("resume", SessionStartSource.Resume)]
    [InlineData("fork", SessionStartSource.Fork)]
    [InlineData("clear", SessionStartSource.Clear)]
    [InlineData("compact", SessionStartSource.Compact)]
    public void SessionStart_parses_the_matcher_values(string wire, SessionStartSource expected)
    {
        Assert.Equal(expected, SessionStartSources.Parse(wire));
        Assert.Equal(wire, expected.ToWireValue());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("teleported")]
    public void SessionStart_degrades_an_unrecognized_source_rather_than_throwing(string? wire)
    {
        var e = Build<SessionStart>() with { Source = wire };

        Assert.Equal(SessionStartSource.Unknown, e.ParsedSource);
        Assert.Equal(wire, e.Source);
    }

    /// <summary>Impl §9.1: <c>resume</c> and <c>fork</c> surface a pre-existing session.</summary>
    [Theory]
    [InlineData(SessionStartSource.Resume, true)]
    [InlineData(SessionStartSource.Fork, true)]
    [InlineData(SessionStartSource.Startup, false)]
    [InlineData(SessionStartSource.Unknown, false)]
    public void SessionStart_knows_which_sources_mean_pre_existing(SessionStartSource source, bool expected)
    {
        Assert.Equal(expected, source.IsPreExisting());
    }

    // ---- UserPromptSubmit: prompt, prompt_id, cwd -------------------------------------

    [Fact]
    public void UserPromptSubmit_carries_the_prompt_text()
    {
        var e = Build<UserPromptSubmit>() with { Prompt = "run the tests" };

        Assert.Equal("run the tests", e.Prompt);
        Assert.Equal(PromptId, e.PromptId);
        Assert.Equal(@"C:\projects\dashboard", e.Cwd);
    }

    [Fact]
    public void UserPromptSubmit_rejects_a_null_prompt()
    {
        Assert.Throws<ArgumentNullException>(() => Build<UserPromptSubmit>() with { Prompt = null! });
    }

    [Fact]
    public void UserPromptSubmit_stores_prompt_text_verbatim()
    {
        const string Text = "  $(whoami)\n<b>bold</b>  ";

        Assert.Equal(Text, (Build<UserPromptSubmit>() with { Prompt = Text }).Prompt);
    }

    // ---- Notification: notification type ----------------------------------------------

    [Theory]
    [InlineData("permission_prompt", NotificationKind.PermissionPrompt)]
    [InlineData("idle_prompt", NotificationKind.IdlePrompt)]
    [InlineData("agent_needs_input", NotificationKind.AgentNeedsInput)]
    [InlineData("agent_completed", NotificationKind.AgentCompleted)]
    public void Notification_carries_and_parses_its_type(string wire, NotificationKind expected)
    {
        var e = Build<Notification>() with { NotificationType = wire };

        Assert.Equal(wire, e.NotificationType);
        Assert.Equal(expected, e.Kind);
        Assert.Equal(wire, expected.ToWireValue());
    }

    [Fact]
    public void Notification_degrades_an_unrecognized_type_rather_than_throwing()
    {
        var e = Build<Notification>() with { NotificationType = "brand_new_signal" };

        Assert.Equal(NotificationKind.Unknown, e.Kind);
        Assert.Equal("brand_new_signal", e.NotificationType);
    }

    [Fact]
    public void Notification_rejects_a_null_type()
    {
        Assert.Throws<ArgumentNullException>(() =>
            Build<Notification>() with { NotificationType = null! });
    }

    // ---- Stop: last_assistant_message --------------------------------------------------

    [Fact]
    public void Stop_carries_the_last_assistant_message()
    {
        var e = Build<Stop>() with { LastAssistantMessage = "29 passed, 0 failed" };

        Assert.Equal("29 passed, 0 failed", e.LastAssistantMessage);
    }

    /// <summary>
    /// §9.1 prefers the inline answer but does not promise it, so its absence must be
    /// representable rather than fatal.
    /// </summary>
    [Fact]
    public void Stop_allows_an_absent_answer()
    {
        Assert.Null(Build<Stop>().LastAssistantMessage);
    }

    [Fact]
    public void Stop_stores_the_answer_verbatim()
    {
        const string Text = "```sh\nrm -rf /\n```";

        Assert.Equal(Text, (Build<Stop>() with { LastAssistantMessage = Text }).LastAssistantMessage);
    }

    // ---- StopFailure: error kind (from matcher) ----------------------------------------

    [Theory]
    [InlineData("rate_limit", StopFailureKind.RateLimit)]
    [InlineData("overloaded", StopFailureKind.Overloaded)]
    [InlineData("authentication_failed", StopFailureKind.AuthenticationFailed)]
    public void StopFailure_carries_and_parses_its_error_kind(string wire, StopFailureKind expected)
    {
        var e = Build<StopFailure>() with { ErrorKind = wire };

        Assert.Equal(wire, e.ErrorKind);
        Assert.Equal(expected, e.Kind);
        Assert.Equal(wire, expected.ToWireValue());
    }

    /// <summary>
    /// Impl §9.1's matcher list ends in "…". An unrecognized kind must survive intact so the
    /// operator sees what actually happened, rather than collapsing to "Unknown".
    /// </summary>
    [Fact]
    public void StopFailure_preserves_an_unrecognized_kind_verbatim()
    {
        var e = Build<StopFailure>() with { ErrorKind = "context_length_exceeded" };

        Assert.Equal("context_length_exceeded", e.ErrorKind);
        Assert.Equal(StopFailureKind.Unknown, e.Kind);
    }

    [Fact]
    public void StopFailure_rejects_a_null_error_kind()
    {
        Assert.Throws<ArgumentNullException>(() => Build<StopFailure>() with { ErrorKind = null! });
    }

    // ---- SessionEnd: end reason (from matcher) -----------------------------------------

    [Theory]
    [InlineData("clear", SessionEndReason.Clear)]
    [InlineData("resume", SessionEndReason.Resume)]
    [InlineData("logout", SessionEndReason.Logout)]
    [InlineData("prompt_input_exit", SessionEndReason.PromptInputExit)]
    [InlineData("other", SessionEndReason.Other)]
    public void SessionEnd_carries_and_parses_its_reason(string wire, SessionEndReason expected)
    {
        var e = Build<SessionEnd>() with { Reason = wire };

        Assert.Equal(wire, e.Reason);
        Assert.Equal(expected, e.ParsedReason);
        Assert.Equal(wire, expected.ToWireValue());
    }

    [Fact]
    public void SessionEnd_degrades_an_unrecognized_reason_rather_than_throwing()
    {
        var e = Build<SessionEnd>() with { Reason = "power_cut" };

        Assert.Equal(SessionEndReason.Unknown, e.ParsedReason);
        Assert.Equal("power_cut", e.Reason);
    }

    [Fact]
    public void SessionEnd_rejects_a_null_reason()
    {
        Assert.Throws<ArgumentNullException>(() => Build<SessionEnd>() with { Reason = null! });
    }

    // ---- CwdChanged: cwd ----------------------------------------------------------------

    [Fact]
    public void CwdChanged_carries_the_new_directory()
    {
        var e = Build<CwdChanged>() with { Cwd = @"C:\projects\elsewhere" };

        Assert.Equal(@"C:\projects\elsewhere", e.Cwd);
    }

    // ---- Unknown matcher values never throw ---------------------------------------------

    /// <summary>
    /// Every matcher parser degrades rather than crashes — the ingress path must never break on
    /// a value a future Claude Code introduces (TS §IV.7).
    /// </summary>
    [Fact]
    public void No_matcher_parser_throws_on_an_unknown_value()
    {
        Assert.Equal(SessionStartSource.Unknown, SessionStartSources.Parse("???"));
        Assert.Equal(NotificationKind.Unknown, NotificationKinds.Parse("???"));
        Assert.Equal(StopFailureKind.Unknown, StopFailureKinds.Parse("???"));
        Assert.Equal(SessionEndReason.Unknown, SessionEndReasons.Parse("???"));

        Assert.Null(SessionStartSource.Unknown.ToWireValue());
        Assert.Null(NotificationKind.Unknown.ToWireValue());
        Assert.Null(StopFailureKind.Unknown.ToWireValue());
        Assert.Null(SessionEndReason.Unknown.ToWireValue());
    }

    private static IEnumerable<InboundEvent> AllVariants() =>
    [
        Build<SessionStart>(),
        Build<UserPromptSubmit>(),
        Build<Notification>(),
        Build<Stop>(),
        Build<StopFailure>(),
        Build<SessionEnd>(),
        Build<CwdChanged>(),
    ];

    private const string Cwd = @"C:\projects\dashboard";
    private const string TranscriptPath = @"C:\transcripts\s-1.jsonl";
    private const string PromptId = "p-1";

    /// <summary>Builds a variant with the common fields populated and its own fields at their minimum.</summary>
    private static T Build<T>()
        where T : InboundEvent
    {
        InboundEvent built = typeof(T) switch
        {
            var t when t == typeof(SessionStart) => new SessionStart
            {
                SessionId = Id, Timestamp = At, Cwd = Cwd, PromptId = PromptId, TranscriptPath = TranscriptPath,
            },
            var t when t == typeof(UserPromptSubmit) => new UserPromptSubmit
            {
                SessionId = Id, Timestamp = At, Cwd = Cwd, PromptId = PromptId, TranscriptPath = TranscriptPath,
                Prompt = string.Empty,
            },
            var t when t == typeof(Notification) => new Notification
            {
                SessionId = Id, Timestamp = At, Cwd = Cwd, PromptId = PromptId, TranscriptPath = TranscriptPath,
                NotificationType = string.Empty,
            },
            var t when t == typeof(Stop) => new Stop
            {
                SessionId = Id, Timestamp = At, Cwd = Cwd, PromptId = PromptId, TranscriptPath = TranscriptPath,
            },
            var t when t == typeof(StopFailure) => new StopFailure
            {
                SessionId = Id, Timestamp = At, Cwd = Cwd, PromptId = PromptId, TranscriptPath = TranscriptPath,
                ErrorKind = string.Empty,
            },
            var t when t == typeof(SessionEnd) => new SessionEnd
            {
                SessionId = Id, Timestamp = At, Cwd = Cwd, PromptId = PromptId, TranscriptPath = TranscriptPath,
                Reason = string.Empty,
            },
            var t when t == typeof(CwdChanged) => new CwdChanged
            {
                SessionId = Id, Timestamp = At, Cwd = Cwd, PromptId = PromptId, TranscriptPath = TranscriptPath,
            },
            var t => throw new InvalidOperationException($"Unhandled variant {t.Name}."),
        };

        return (T)built;
    }
}
