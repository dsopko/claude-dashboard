using System.IO;
using ClaudeDashboard.App.Setup;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// Finding the <c>claude</c> program (issue #30).
/// </summary>
/// <remarks>
/// Only the search is covered. Running the program changes Claude Code's configuration, so the
/// real run is exercised once, through <c>Main</c> against redirected roots, in
/// <c>MainSwitchTests</c>.
/// </remarks>
public sealed class ClaudeCliTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string FolderWithClaude(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "claude.exe"), string.Empty);

        return folder;
    }

    private string EmptyFolder(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);

        return folder;
    }

    [Fact]
    public void The_first_folder_on_the_path_that_has_it_wins()
    {
        var empty = EmptyFolder("empty");
        var first = FolderWithClaude("first");
        var second = FolderWithClaude("second");

        var found = ClaudeCli.Locate(string.Join(Path.PathSeparator, empty, first, second), userProfile: null);

        Assert.Equal(Path.Combine(first, "claude.exe"), found);
    }

    [Fact]
    public void The_native_install_folder_is_tried_after_the_path()
    {
        var profile = EmptyFolder("profile");
        var bin = Path.Combine(profile, ".local", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "claude.exe"), string.Empty);

        Assert.Equal(Path.Combine(bin, "claude.exe"), ClaudeCli.Locate(EmptyFolder("empty"), profile));
        Assert.Equal(Path.Combine(bin, "claude.exe"), ClaudeCli.Locate(null, profile));
    }

    [Fact]
    public void A_path_entry_in_quotes_or_one_that_is_not_a_path_is_survived()
    {
        var folder = FolderWithClaude("quoted");

        var path = string.Join(Path.PathSeparator, "C:\\bad\0entry", "  ", $"\"{folder}\"");

        Assert.Equal(Path.Combine(folder, "claude.exe"), ClaudeCli.Locate(path, userProfile: null));
    }

    /// <summary>
    /// <strong>A <c>claude.cmd</c> shim is not found, on purpose.</strong> The caller then falls
    /// back to the settings file, which works; running the shim would need quoting rules nothing
    /// here measured.
    /// </summary>
    [Fact]
    public void Nothing_is_found_where_there_is_no_claude_exe()
    {
        var shim = EmptyFolder("shim");
        File.WriteAllText(Path.Combine(shim, "claude.cmd"), string.Empty);

        Assert.Null(ClaudeCli.Locate(shim, EmptyFolder("profile")));
        Assert.Null(ClaudeCli.Locate(null, null));
        Assert.Null(ClaudeCli.Locate(string.Empty, "  "));
    }
}
