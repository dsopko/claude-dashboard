using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using ClaudeDashboard.App.Configuration;

namespace ClaudeDashboard.Tests;

/// <summary>
/// Points Claude Code's configuration folder at a scratch folder for the whole test process, before
/// any test runs (the T1.68 review, must-fix 1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why.</strong> A <see cref="ClaudeCodePaths"/> built with no folder resolves
/// <c>CLAUDE_CONFIG_DIR</c>, and with none it resolves the operator's real <c>~/.claude</c>. Since T1.68
/// a started host reads <c>~/.claude/settings.json</c> at each prune of the history, and three test
/// hosts that were built with no folder read the operator's real file in every full run. Each test host
/// should still pass its own scratch folder; this makes the one it forgets harmless.
/// </para>
/// <para>
/// <strong>The folder is not created.</strong> To a default <see cref="ClaudeCodePaths"/> in a test,
/// Claude Code looks not installed, and its settings file is "not read": the history deletes nothing.
/// A test that sets <c>CLAUDE_CONFIG_DIR</c> itself (<c>MainSwitchTests.Set</c>) restores this value
/// when it is done. A process the tests start inherits it.
/// </para>
/// <para>
/// Set unconditionally: a developer whose own <c>CLAUDE_CONFIG_DIR</c> points at their real
/// configuration must not have the tests read it either.
/// </para>
/// </remarks>
internal static class ScratchClaudeConfig
{
    /// <summary>The scratch folder: under the temp folder, one for each test process, never created here.</summary>
    public static readonly string Folder = Path.Combine(
        Path.GetTempPath(),
        "claude-dashboard-tests",
        "claude-config-" + Guid.NewGuid().ToString("N"));

    /// <summary>Runs once, when the test assembly loads, before any test.</summary>
    [ModuleInitializer]
    [SuppressMessage(
        "Usage",
        "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "A test assembly, and the guard must be in force before the first test builds a ClaudeCodePaths.")]
    internal static void PointClaudeCodeAtAScratchFolder() =>
        Environment.SetEnvironmentVariable(ClaudeCodePaths.ConfigDirectoryVariable, Folder);
}
