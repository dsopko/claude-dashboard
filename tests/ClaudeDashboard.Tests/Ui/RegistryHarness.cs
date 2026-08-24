using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Pipeline;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// A real Registry and a real projection, driven by real events.
/// </summary>
/// <remarks>
/// The UI tests could hand a view model a hand-built list of sessions, and would then be
/// asserting against my idea of what the pipeline produces rather than what it produces. Every
/// state here is reached the way production reaches it — a prompt, a notification, a stop — so a
/// transition that changes in T1.2 shows up here as a changed row rather than as a test that
/// still passes against a fiction.
/// </remarks>
internal sealed class RegistryHarness : IDisposable
{
    public const string Workspace = @"C:\dev\PennCustQuote";

    private readonly QueueingDispatcher _dispatcher = new();
    private int _prompts;

    public RegistryHarness()
    {
        Projection = new SessionProjection(Registry, _dispatcher);
    }

    public SessionRegistry Registry { get; } = new(new SingleWriterGuard());

    public SessionProjection Projection { get; }

    /// <summary>Applies <paramref name="inboundEvent"/> and drains the marshalling queue.</summary>
    public void Apply(InboundEvent inboundEvent)
    {
        Registry.Apply(inboundEvent);
        _dispatcher.Pump();
    }

    /// <summary>A session that has just started and has done nothing yet — quiet.</summary>
    public void Started(string id, DateTimeOffset at, string cwd = Workspace) => Apply(new SessionStart
    {
        SessionId = new SessionId(id),
        Timestamp = at,
        Cwd = cwd,
        Source = "startup",
    });

    /// <summary>A prompt was submitted, so the session is working.</summary>
    public string Working(
        string id,
        DateTimeOffset at,
        string cwd = Workspace,
        string prompt = "run the full test suite and summarize any failures")
    {
        var promptId = $"p-{++_prompts}";

        Apply(new UserPromptSubmit
        {
            SessionId = new SessionId(id),
            Timestamp = at,
            Cwd = cwd,
            PromptId = promptId,
            Prompt = prompt,
        });

        return promptId;
    }

    /// <summary>Claude is blocked on the operator.</summary>
    public void Blocked(string id, DateTimeOffset at, string type = "permission_prompt", string cwd = Workspace) => Apply(new Notification
    {
        SessionId = new SessionId(id),
        Timestamp = at,
        Cwd = cwd,
        NotificationType = type,
    });

    /// <summary>The turn finished and nobody has looked at it — unread.</summary>
    public void Finished(string id, DateTimeOffset at, string promptId, string answer = "29 passed", string cwd = Workspace) =>
        Apply(new Stop
        {
            SessionId = new SessionId(id),
            Timestamp = at,
            Cwd = cwd,
            PromptId = promptId,
            LastAssistantMessage = answer,
        });

    /// <summary>The turn died.</summary>
    public void Failed(string id, DateTimeOffset at, string promptId, string kind = "rate_limit", string cwd = Workspace) =>
        Apply(new StopFailure
        {
            SessionId = new SessionId(id),
            Timestamp = at,
            Cwd = cwd,
            PromptId = promptId,
            ErrorKind = kind,
        });

    /// <summary>The operator acknowledged the result — quiet.</summary>
    public void Acked(string id, DateTimeOffset at, string cwd = Workspace) => Apply(new Ack
    {
        SessionId = new SessionId(id),
        Timestamp = at,
        Cwd = cwd,
        Source = AckSource.Manual,
    });

    /// <summary>The session terminated.</summary>
    public void Ended(string id, DateTimeOffset at, string cwd = Workspace) => Apply(new SessionEnd
    {
        SessionId = new SessionId(id),
        Timestamp = at,
        Cwd = cwd,
        Reason = "logout",
    });

    /// <summary>A session that has finished and been seen: quiet, in one line.</summary>
    public void Quiet(string id, DateTimeOffset at, string cwd = Workspace)
    {
        var promptId = Working(id, at, cwd);
        Finished(id, at, promptId, cwd: cwd);
        Acked(id, at, cwd);
    }

    public void Dispose() => Projection.Dispose();
}
