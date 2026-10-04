using System.IO;
using ClaudeDashboard.App.Configuration;

namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// No default <see cref="ClaudeCodePaths"/> in the test process can reach the operator's
/// <c>~/.claude</c> (the T1.68 review, must-fix 1; <see cref="ScratchClaudeConfig"/>).
/// </summary>
public sealed class ScratchClaudeConfigTests
{
    /// <summary>
    /// <strong>In the test process, a default <see cref="ClaudeCodePaths"/> resolves under the temp folder,
    /// and not under the user profile.</strong> The exact folder can be another test's scratch folder for a
    /// moment (<c>MainSwitchTests</c> sets its own and restores this one), so the test asks where it is,
    /// not which one it is.
    /// </summary>
    [Fact]
    public void A_default_path_in_a_test_never_reaches_the_operators_folder()
    {
        var resolved = new ClaudeCodePaths().ConfigDirectory;
        var temp = Path.GetFullPath(Path.GetTempPath());

        Assert.StartsWith(temp, Path.GetFullPath(resolved), StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(ClaudeCodePaths.DefaultConfigDirectory, resolved, StringComparer.OrdinalIgnoreCase);
        Assert.False(
            Path.GetFullPath(new ClaudeCodePaths().UserSettingsFile).Equals(
                Path.Combine(ClaudeCodePaths.DefaultConfigDirectory, "settings.json"),
                StringComparison.OrdinalIgnoreCase),
            "A default ClaudeCodePaths in a test named the operator's settings file.");
    }

    /// <summary>The guard's folder is a scratch folder: under the temp folder, and named for the tests.</summary>
    [Fact]
    public void The_guard_folder_is_under_the_temp_folder()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), ScratchClaudeConfig.Folder, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claude-dashboard-tests", ScratchClaudeConfig.Folder, StringComparison.Ordinal);
    }
}
