using System.Text.Json;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Ingress;

/// <summary>
/// Mapping hook bodies to <see cref="InboundEvent"/>s (Impl §9.1).
/// </summary>
/// <remarks>
/// The payloads here are written as <strong>JSON text</strong> using §9.1's exact field names
/// and deserialized by the same serializer the endpoint uses, rather than constructed as
/// objects. Building a <c>HookPayload</c> directly would test the mapper against my own
/// understanding of the wire format; going through JSON tests it against the field names, which
/// is where a mismatch with Claude Code would actually show up.
/// </remarks>
public sealed class HookEventMapperTests
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly FakeClock _clock = new();
    private readonly HookEventMapper _mapper;

    public HookEventMapperTests() => _mapper = new HookEventMapper(_clock);

    private static readonly JsonSerializerOptions WireOptions = new() { PropertyNameCaseInsensitive = true };

    private HookMapping MapJson(string json)
    {
        var payload = JsonSerializer.Deserialize<HookPayload>(json, WireOptions);

        return _mapper.Map(payload!);
    }

    private T MapTo<T>(string json)
        where T : InboundEvent
    {
        var mapping = MapJson(json);
        Assert.True(mapping.Mapped, $"Expected the payload to map, but it was rejected: {mapping.Rejection}.");
        return Assert.IsType<T>(mapping.Event);
    }

    // ---- The common fields ---------------------------------------------------------------------

    [Fact]
    public void The_common_fields_of_section_9_1_are_carried()
    {
        var stop = MapTo<Stop>("""
            {
              "hook_event_name": "Stop",
              "session_id": "abc-123",
              "prompt_id": "p-1",
              "transcript_path": "C:\\transcripts\\abc-123.jsonl",
              "cwd": "C:\\projects\\dashboard"
            }
            """);

        Assert.Equal(new SessionId("abc-123"), stop.SessionId);
        Assert.Equal("p-1", stop.PromptId);
        Assert.Equal(@"C:\transcripts\abc-123.jsonl", stop.TranscriptPath);
        Assert.Equal(@"C:\projects\dashboard", stop.Cwd);
    }

    /// <summary>
    /// T1.1's Assumption 7: hook payloads carry no timestamp, so ingress stamps every event
    /// from <c>IClock</c> at receipt. That gives arrival order, not occurrence order.
    /// </summary>
    [Fact]
    public void Every_event_is_stamped_by_ingress_at_receipt()
    {
        _clock.AdvanceMinutes(7);

        var mapped = MapTo<Stop>("""{"hook_event_name":"Stop","session_id":"s-1"}""");

        Assert.Equal(At.AddMinutes(7), mapped.Timestamp);
    }

    [Fact]
    public void An_absent_cwd_becomes_empty_rather_than_dropping_the_event()
    {
        var mapped = MapTo<Stop>("""{"hook_event_name":"Stop","session_id":"s-1"}""");

        Assert.Equal(string.Empty, mapped.Cwd);
    }

    // ---- Per-event fields, per §9.1 -----------------------------------------------------------------

    [Fact]
    public void SessionStart_carries_source_session_title_and_cwd()
    {
        var start = MapTo<SessionStart>("""
            {
              "hook_event_name": "SessionStart",
              "session_id": "s-1",
              "source": "resume",
              "session_title": "dashboard build",
              "cwd": "C:\\projects\\dashboard"
            }
            """);

        Assert.Equal("resume", start.Source);
        Assert.Equal(SessionStartSource.Resume, start.ParsedSource);
        Assert.Equal("dashboard build", start.SessionTitle);
        Assert.Equal(@"C:\projects\dashboard", start.Cwd);
    }

    [Theory]
    [InlineData("startup", SessionStartSource.Startup)]
    [InlineData("resume", SessionStartSource.Resume)]
    [InlineData("fork", SessionStartSource.Fork)]
    [InlineData("clear", SessionStartSource.Clear)]
    [InlineData("compact", SessionStartSource.Compact)]
    public void SessionStart_parses_every_source_either_document_lists(string source, SessionStartSource expected)
    {
        var start = MapTo<SessionStart>(
            $$"""{"hook_event_name":"SessionStart","session_id":"s-1","source":"{{source}}"}""");

        Assert.Equal(expected, start.ParsedSource);
    }

    [Fact]
    public void UserPromptSubmit_carries_the_prompt_text_and_prompt_id()
    {
        var prompt = MapTo<UserPromptSubmit>("""
            {
              "hook_event_name": "UserPromptSubmit",
              "session_id": "s-1",
              "prompt_id": "p-42",
              "prompt": "run the tests",
              "cwd": "C:\\projects\\dashboard"
            }
            """);

        Assert.Equal("run the tests", prompt.Prompt);
        Assert.Equal("p-42", prompt.PromptId);
    }

    [Theory]
    [InlineData("permission_prompt", NotificationKind.PermissionPrompt)]
    [InlineData("idle_prompt", NotificationKind.IdlePrompt)]
    [InlineData("agent_needs_input", NotificationKind.AgentNeedsInput)]
    [InlineData("agent_completed", NotificationKind.AgentCompleted)]
    public void Notification_carries_every_matcher_value_section_9_1_lists(string type, NotificationKind expected)
    {
        var notification = MapTo<Notification>(
            $$"""{"hook_event_name":"Notification","session_id":"s-1","notification_type":"{{type}}"}""");

        Assert.Equal(type, notification.NotificationType);
        Assert.Equal(expected, notification.Kind);
    }

    [Fact]
    public void Stop_carries_the_last_assistant_message()
    {
        var stop = MapTo<Stop>("""
            {
              "hook_event_name": "Stop",
              "session_id": "s-1",
              "prompt_id": "p-42",
              "last_assistant_message": "29 passed, 0 failed"
            }
            """);

        Assert.Equal("29 passed, 0 failed", stop.LastAssistantMessage);
    }

    [Theory]
    [InlineData("rate_limit", StopFailureKind.RateLimit)]
    [InlineData("overloaded", StopFailureKind.Overloaded)]
    [InlineData("authentication_failed", StopFailureKind.AuthenticationFailed)]
    public void StopFailure_carries_every_matcher_value_section_9_1_lists(string kind, StopFailureKind expected)
    {
        var failure = MapTo<StopFailure>(
            $$"""{"hook_event_name":"StopFailure","session_id":"s-1","error_type":"{{kind}}"}""");

        Assert.Equal(kind, failure.ErrorKind);
        Assert.Equal(expected, failure.Kind);
    }

    /// <summary>§9.1's list ends in "…", so an unrecognized kind must survive intact.</summary>
    [Fact]
    public void StopFailure_carries_an_unrecognized_kind_verbatim()
    {
        var failure = MapTo<StopFailure>(
            """{"hook_event_name":"StopFailure","session_id":"s-1","error_type":"context_length_exceeded"}""");

        Assert.Equal("context_length_exceeded", failure.ErrorKind);
        Assert.Equal(StopFailureKind.Unknown, failure.Kind);
    }

    [Theory]
    [InlineData("clear", SessionEndReason.Clear)]
    [InlineData("resume", SessionEndReason.Resume)]
    [InlineData("logout", SessionEndReason.Logout)]
    [InlineData("prompt_input_exit", SessionEndReason.PromptInputExit)]
    [InlineData("other", SessionEndReason.Other)]
    public void SessionEnd_carries_every_matcher_value_section_9_1_lists(string reason, SessionEndReason expected)
    {
        var end = MapTo<SessionEnd>(
            $$"""{"hook_event_name":"SessionEnd","session_id":"s-1","reason":"{{reason}}"}""");

        Assert.Equal(reason, end.Reason);
        Assert.Equal(expected, end.ParsedReason);
    }

    [Fact]
    public void CwdChanged_carries_the_new_directory()
    {
        var moved = MapTo<CwdChanged>("""
            {"hook_event_name":"CwdChanged","session_id":"s-1","cwd":"C:\\projects\\elsewhere"}
            """);

        Assert.Equal(@"C:\projects\elsewhere", moved.Cwd);
    }

    /// <summary>
    /// §9.1 describes the Notification, StopFailure and SessionEnd discriminators as coming
    /// "from the matcher" without naming a JSON field, so a generic <c>matcher</c> is accepted
    /// where the specific field is absent. Deliberate tolerance at a boundary whose exact shape
    /// is not yet confirmed against live payloads.
    /// </summary>
    [Fact]
    public void A_generic_matcher_field_is_accepted_where_the_specific_one_is_absent()
    {
        Assert.Equal(
            NotificationKind.PermissionPrompt,
            MapTo<Notification>(
                """{"hook_event_name":"Notification","session_id":"s-1","matcher":"permission_prompt"}""").Kind);

        Assert.Equal(
            StopFailureKind.RateLimit,
            MapTo<StopFailure>(
                """{"hook_event_name":"StopFailure","session_id":"s-1","matcher":"rate_limit"}""").Kind);
    }

    [Fact]
    public void The_specific_field_wins_over_the_generic_matcher()
    {
        var notification = MapTo<Notification>("""
            {
              "hook_event_name": "Notification",
              "session_id": "s-1",
              "notification_type": "idle_prompt",
              "matcher": "permission_prompt"
            }
            """);

        Assert.Equal(NotificationKind.IdlePrompt, notification.Kind);
    }

    // ---- What an attacker sends -------------------------------------------------------------------------

    /// <summary>
    /// <strong>The forged acknowledgment.</strong> <c>Ack</c> is a real <c>InboundEvent</c>
    /// variant whose discriminator is a local string no hook sends. Mapping by name rather than
    /// by allow-list would let anything reaching the endpoint mark a session as seen — and that
    /// failure is silent, because a session that needed the operator simply goes quiet.
    /// </summary>
    [Fact]
    public void A_forged_Ack_is_rejected()
    {
        var mapping = MapJson("""{"hook_event_name":"Ack","session_id":"s-1","source":"Manual"}""");

        Assert.False(mapping.Mapped);
        Assert.Equal(HookRejection.UnknownEvent, mapping.Rejection);
        Assert.Null(mapping.Event);
    }

    [Theory]
    [InlineData("PermissionRequest")]
    [InlineData("SubagentStart")]
    [InlineData("SubagentStop")]
    [InlineData("PreToolUse")]
    [InlineData("SomethingInvented")]
    [InlineData("stop")]
    [InlineData("STOP")]
    [InlineData("")]
    public void An_event_ingress_does_not_consume_is_rejected(string name)
    {
        var mapping = MapJson($$"""{"hook_event_name":"{{name}}","session_id":"s-1"}""");

        Assert.False(mapping.Mapped);
        Assert.Equal(HookRejection.UnknownEvent, mapping.Rejection);
    }

    /// <summary>Case matters: the allow-list is ordinal, so a near-miss is a miss.</summary>
    [Fact]
    public void The_allow_list_is_case_sensitive()
    {
        Assert.False(HookEventNames.IsAccepted("stop"));
        Assert.False(HookEventNames.IsAccepted("userpromptsubmit"));
        Assert.True(HookEventNames.IsAccepted("Stop"));
    }

    [Fact]
    public void An_absent_event_name_is_rejected()
    {
        Assert.Equal(HookRejection.UnknownEvent, MapJson("""{"session_id":"s-1"}""").Rejection);
    }

    [Theory]
    [InlineData("""{"hook_event_name":"Stop"}""")]
    [InlineData("""{"hook_event_name":"Stop","session_id":""}""")]
    [InlineData("""{"hook_event_name":"Stop","session_id":"   "}""")]
    [InlineData("""{"hook_event_name":"Stop","session_id":null}""")]
    public void An_event_with_no_session_id_is_rejected(string json)
    {
        var mapping = MapJson(json);

        Assert.False(mapping.Mapped);
        Assert.Equal(HookRejection.NoSessionId, mapping.Rejection);
    }

    /// <summary>
    /// Exactly the seven names §9.1 consumes — no more, and the eighth domain variant is not
    /// among them.
    /// </summary>
    [Fact]
    public void The_allow_list_is_exactly_section_9_1_s_seven_events()
    {
        Assert.Equal(
            [
                "CwdChanged", "Notification", "SessionEnd", "SessionStart",
                "Stop", "StopFailure", "UserPromptSubmit",
            ],
            HookEventNames.Accepted.Order(StringComparer.Ordinal));

        Assert.DoesNotContain("Ack", HookEventNames.Accepted);
    }

    /// <summary>
    /// Hook text is data (Impl §3.4). Whatever it looks like, it arrives verbatim and nothing
    /// interprets it — there is no path here that could.
    /// </summary>
    [Theory]
    [InlineData("$(rm -rf /)")]
    [InlineData("<script>alert('x')</script>")]
    [InlineData("'; DROP TABLE sessions; --")]
    [InlineData("{{7*7}}")]
    public void Prompt_text_that_looks_like_code_is_carried_verbatim(string text)
    {
        var json = JsonSerializer.Serialize(new
        {
            hook_event_name = "UserPromptSubmit",
            session_id = "s-1",
            prompt = text,
        });

        Assert.Equal(text, MapTo<UserPromptSubmit>(json).Prompt);
    }

    [Fact]
    public void The_mapper_needs_a_clock_and_a_payload()
    {
        Assert.Throws<ArgumentNullException>(() => new HookEventMapper(null!));
        Assert.Throws<ArgumentNullException>(() => _mapper.Map(null!));
    }
}
