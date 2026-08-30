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


    /// <summary>
    /// <strong>Every accepted event carries <c>session_title</c> through, not just the one arm
    /// that used to read it.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The theory runs over <see cref="HookEventNames.Accepted"/> itself rather than over a list
    /// written here, so an event added to the allow-list and forgotten fails this test instead of
    /// quietly losing titles. That is the shape of the defect this task exists to remove: the
    /// field used to be read on the <c>SessionStart</c> arm alone, and <c>SessionStart</c> has
    /// never fired (issue #20), so no row ever showed a title while every test passed.
    /// </para>
    /// <para>
    /// The negative half is asserted too. A payload without the field must produce null rather
    /// than an empty string, because the Registry's latch treats "no title on this event" and
    /// "this event says the title is blank" as the same thing only by way of that null.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(AcceptedEventNames))]
    public void Every_accepted_event_carries_the_session_title(string hookEventName)
    {
        var carrying = MapJson($$"""
            {
              "hook_event_name": "{{hookEventName}}",
              "session_id": "s-1",
              "session_title": "Director"
            }
            """);

        Assert.Equal("Director", Assert.IsAssignableFrom<InboundEvent>(carrying.Event).SessionTitle);

        var without = MapJson($$"""
            {
              "hook_event_name": "{{hookEventName}}",
              "session_id": "s-1"
            }
            """);

        Assert.Null(Assert.IsAssignableFrom<InboundEvent>(without.Event).SessionTitle);
    }

    public static TheoryData<string> AcceptedEventNames()
    {
        var names = new TheoryData<string>();

        foreach (var name in HookEventNames.Accepted)
        {
            names.Add(name);
        }

        return names;
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
    public void SessionStart_carries_source_and_cwd()
    {
        var start = MapTo<SessionStart>("""
            {
              "hook_event_name": "SessionStart",
              "session_id": "s-1",
              "source": "resume",
              "cwd": "C:\\projects\\dashboard"
            }
            """);

        Assert.Equal("resume", start.Source);
        Assert.Equal(SessionStartSource.Resume, start.ParsedSource);
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
    /// <remarks>
    /// <para>
    /// Enumerated from the hierarchy rather than listed, so a synthetic variant added later is
    /// covered the day it appears instead of the day somebody remembers. That is not
    /// hypothetical: T1.13 added <c>SoundCommand</c>, and a forged one is strictly worse than a
    /// forged <c>Ack</c> — an ack silences one session, a <c>PauseMonitoring</c> silences the
    /// whole dashboard until the operator notices the glyph has gone grey.
    /// </para>
    /// <para>
    /// The mapper rejects both by construction, because it dispatches on an allow-list of the
    /// seven literal wire names rather than by matching a variant. This asserts that property
    /// holds for every synthetic variant there is, and the first assertion pins the set itself,
    /// so a variant that stopped being synthetic could not slip out of the check quietly.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_synthetic_variant_can_be_forged()
    {
        string[] wireNames =
        [
            HookEventNames.SessionStart, HookEventNames.UserPromptSubmit, HookEventNames.Notification,
            HookEventNames.Stop, HookEventNames.StopFailure, HookEventNames.SessionEnd,
            HookEventNames.CwdChanged, HookEventNames.PostToolBatch,
        ];

        var synthetic = typeof(InboundEvent).Assembly
            .GetTypes()
            .Where(type => type.IsSubclassOf(typeof(InboundEvent)))
            .Select(type => type.Name)
            .Where(name => !wireNames.Contains(name, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Ack", "RostersChanged", "SoundCommand"], synthetic);

        foreach (var name in synthetic)
        {
            var mapping = MapJson($$"""{"hook_event_name":"{{name}}","session_id":"s-1"}""");

            Assert.False(mapping.Mapped, $"a forged {name} was mapped.");
            Assert.Equal(HookRejection.UnknownEvent, mapping.Rejection);
            Assert.Null(mapping.Event);
        }
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
    public void The_allow_list_is_exactly_the_consumed_events()
    {
        Assert.Equal(
            [
                "CwdChanged", "Notification", "PostToolBatch", "SessionEnd", "SessionStart",
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
