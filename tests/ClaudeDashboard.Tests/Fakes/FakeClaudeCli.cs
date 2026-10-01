using System.IO;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Setup;

namespace ClaudeDashboard.Tests.Fakes;

/// <summary>
/// A <c>claude</c> program that starts no process: it records what it was asked and, when given
/// Claude Code's paths, writes what Claude Code was measured to write.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The settings it writes are the measured ones.</strong> On 2026-09-30, Claude Code
/// 2.1.286 recorded a marketplace added from a folder as
/// <c>extraKnownMarketplaces.&lt;name&gt;.source = { "source": "directory", "path": … }</c> and an
/// installed plugin as <c>enabledPlugins["plugin@marketplace"] = true</c>, and both removal
/// commands took their key out again. That is the whole of what this fake reproduces, and it is
/// what lets a test go round the full loop — ask, then read the answer back through
/// <c>HookInstaller.Check</c> — without the real program.
/// </para>
/// <para>
/// A call is recorded even when <see cref="Found"/> is false, because "it tried once and stopped"
/// is a claim tests make.
/// </para>
/// </remarks>
public sealed class FakeClaudeCli(ClaudeCodePaths? claude = null) : IClaudeCli
{
    /// <summary>Whether the program exists at all.</summary>
    public bool Found { get; set; } = true;

    /// <summary>Every run, in order.</summary>
    public List<IReadOnlyList<string>> Calls { get; } = [];

    /// <summary>Every run as one line, for assertions that read better as text.</summary>
    public IReadOnlyList<string> Commands => [.. Calls.Select(call => string.Join(' ', call))];

    /// <summary>Runs before the answer, so a test can look at the disk at the moment of asking.</summary>
    public Action<IReadOnlyList<string>>? Before { get; set; }

    /// <summary>An answer for some runs; null means "answer as usual".</summary>
    public Func<IReadOnlyList<string>, ClaudeCliResult?>? Answer { get; set; }

    /// <summary>
    /// For some runs, record as Claude Code would and then report this failure anyway: the run that
    /// writes <c>enabledPlugins</c> and then exits non-zero, or is stopped at the budget after the
    /// write (the issue #30 review, M2). Null, or a null answer, means "succeed as usual".
    /// </summary>
    public Func<IReadOnlyList<string>, ClaudeCliResult?>? FailAfterRecording { get; set; }

    /// <inheritdoc/>
    public ClaudeCliResult Run(IReadOnlyList<string> arguments)
    {
        Calls.Add([.. arguments]);

        if (!Found)
        {
            return ClaudeCliResult.NotFound;
        }

        Before?.Invoke(arguments);

        if (Answer?.Invoke(arguments) is { } answer)
        {
            return answer;
        }

        if (claude is not null)
        {
            Record(claude, arguments);
        }

        if (FailAfterRecording?.Invoke(arguments) is { } failure)
        {
            return failure;
        }

        return new ClaudeCliResult(true, 0, "ok");
    }

    private static void Record(ClaudeCodePaths claude, IReadOnlyList<string> arguments)
    {
        var settings = File.Exists(claude.UserSettingsFile)
            ? HookRegistration.Parse(File.ReadAllText(claude.UserSettingsFile))
            : [];

        switch (arguments)
        {
            case ["plugin", "marketplace", "add", var folder]:
                Section(settings, HookPlugin.MarketplacesKey)[HookPlugin.Name] = new JsonObject
                {
                    ["source"] = new JsonObject { ["source"] = "directory", ["path"] = folder },
                };
                break;

            case ["plugin", "install", var id]:
                Section(settings, HookPlugin.EnabledKey)[id] = true;
                break;

            case ["plugin", "uninstall", var id]:
                Section(settings, HookPlugin.EnabledKey).Remove(id);
                break;

            case ["plugin", "marketplace", "remove", var name]:
                Section(settings, HookPlugin.MarketplacesKey).Remove(name);
                break;

            default:
                return;
        }

        Directory.CreateDirectory(claude.ConfigDirectory);
        File.WriteAllText(claude.UserSettingsFile, HookRegistration.Render(settings));
    }

    private static JsonObject Section(JsonObject settings, string key)
    {
        if (settings[key] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        settings[key] = created;

        return created;
    }
}
