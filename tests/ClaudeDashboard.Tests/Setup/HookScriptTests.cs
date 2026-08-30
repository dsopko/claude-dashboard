using System.IO;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Setup;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// Getting <c>post-status.cmd</c> onto disk, and keeping it right (issue #29).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This file is about the delivery. <c>HookScriptBehaviourTests</c> is about the
/// script.</strong> The two are separated because they fail for different reasons and at different
/// speeds: these assertions are filesystem work in milliseconds, and those start a process per
/// case.
/// </para>
/// <para>
/// <strong>The line endings are asserted here and nowhere else.</strong> A <c>.cmd</c> with LF
/// endings is not reliably parsed by <c>cmd</c>, and the way it fails is a label that is not found
/// — printed to stderr, under the script's own redirect, and therefore silent. The repository
/// stores <c>.cs</c> with LF, so the constant carries LF and the conversion is a real step that
/// somebody could delete while tidying.
/// </para>
/// </remarks>
public sealed class HookScriptTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;

    public HookScriptTests()
    {
        _paths = new DashboardPaths(_root);
        Directory.CreateDirectory(_root);
    }

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

    /// <summary>
    /// <strong>The text that goes to disk has CRLF endings, and no bare LF anywhere.</strong>
    /// </summary>
    [Fact]
    public void The_script_is_written_with_windows_line_endings()
    {
        HookScript.EnsureWritten(_paths, Logger.None);

        var text = File.ReadAllText(_paths.HookScriptFile);

        Assert.Contains("\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>It lands in the data folder under the name the handler will use.</summary>
    /// <remarks>
    /// Beside <c>listening.txt</c>, and that adjacency is load-bearing: the script reads
    /// <c>%~dp0listening.txt</c> — its own directory — so the two can never disagree about which
    /// data folder they belong to, even when <c>CLAUDE_DASHBOARD_HOME</c> has moved it.
    /// </remarks>
    [Fact]
    public void The_script_lands_in_the_data_folder_beside_the_announcement()
    {
        HookScript.EnsureWritten(_paths, Logger.None);

        Assert.True(File.Exists(_paths.HookScriptFile));
        Assert.Equal("post-status.cmd", Path.GetFileName(_paths.HookScriptFile));
        Assert.Equal(
            Path.GetDirectoryName(_paths.ListeningFile),
            Path.GetDirectoryName(_paths.HookScriptFile));
    }

    /// <summary>The name in the file matches the name the registration writes.</summary>
    /// <remarks>
    /// Two places name this file: the path property, and the constant
    /// <see cref="HookRegistration.ScriptFileName"/> that the foreign-path diagnosis matches on. A
    /// rename that touched one would leave the start check unable to explain a data-folder
    /// mismatch, which is the one case it exists to explain.
    /// </remarks>
    [Fact]
    public void The_registration_and_the_path_agree_on_the_file_name() =>
        Assert.Equal(HookRegistration.ScriptFileName, Path.GetFileName(_paths.HookScriptFile));

    /// <summary>Writing it twice writes it once.</summary>
    /// <remarks>
    /// This runs at every start, so a version that rewrote unconditionally would touch a file
    /// Claude Code may be executing at that moment, on every launch, for no reason.
    /// </remarks>
    [Fact]
    public void An_unchanged_script_is_not_rewritten()
    {
        HookScript.EnsureWritten(_paths, Logger.None);
        var written = File.GetLastWriteTimeUtc(_paths.HookScriptFile);

        Assert.True(HookScript.Matches(_paths));
        Assert.True(HookScript.EnsureWritten(_paths, Logger.None));
        Assert.Equal(written, File.GetLastWriteTimeUtc(_paths.HookScriptFile));
    }

    /// <summary>
    /// <strong>A hand-edited script is reverted at the next start, and that is the trade.</strong>
    /// </summary>
    /// <remarks>
    /// The alternative — leave a modified script alone — is the failure this whole mechanism
    /// exists to avoid: a script bug that can never be fixed on an install that already exists,
    /// because the operator has no reason to think a step is needed. The file's own header says
    /// this happens.
    /// </remarks>
    [Fact]
    public void A_hand_edited_script_is_replaced()
    {
        HookScript.EnsureWritten(_paths, Logger.None);
        File.WriteAllText(_paths.HookScriptFile, "@echo off\r\necho tinkered with\r\n");

        Assert.False(HookScript.Matches(_paths));
        Assert.True(HookScript.EnsureWritten(_paths, Logger.None));
        Assert.Equal(HookScript.Text, File.ReadAllText(_paths.HookScriptFile));
    }

    /// <summary>
    /// A script written by an older build is replaced, which is how a fix reaches an install.
    /// </summary>
    /// <remarks>
    /// The same mechanism as the test above and a different claim: that is about an operator's
    /// edit surviving, this is about ours reaching them. Compared by content rather than by a
    /// version stamp — a stamp can be right while the body is wrong, which is exactly what a
    /// partial restore or a half-written file leaves behind.
    /// </remarks>
    [Fact]
    public void A_script_from_an_older_build_is_replaced()
    {
        File.WriteAllText(
            _paths.HookScriptFile,
            HookScript.Text.Replace("--max-time 2", "--max-time 1", StringComparison.Ordinal));

        Assert.False(HookScript.Matches(_paths));

        HookScript.EnsureWritten(_paths, Logger.None);

        Assert.Equal(HookScript.Text, File.ReadAllText(_paths.HookScriptFile));
    }

    /// <summary>A file whose only difference is its line endings is replaced too.</summary>
    /// <remarks>
    /// The case a normalising comparison would miss, and the one it would miss for ever: an LF copy
    /// would be judged identical at every start and would never be corrected, while <c>cmd</c> went
    /// on failing to find a label in it.
    /// </remarks>
    [Fact]
    public void A_copy_with_the_wrong_line_endings_is_replaced()
    {
        File.WriteAllText(
            _paths.HookScriptFile,
            HookScript.Text.Replace("\r\n", "\n", StringComparison.Ordinal));

        Assert.False(HookScript.Matches(_paths));

        HookScript.EnsureWritten(_paths, Logger.None);

        Assert.Equal(HookScript.Text, File.ReadAllText(_paths.HookScriptFile));
    }

    /// <summary>No temporary file survives a write.</summary>
    [Fact]
    public void Writing_leaves_no_temporary_behind()
    {
        HookScript.EnsureWritten(_paths, Logger.None);
        File.WriteAllText(_paths.HookScriptFile, "changed");
        HookScript.EnsureWritten(_paths, Logger.None);

        Assert.Equal([_paths.HookScriptFile], Directory.EnumerateFiles(_root));
    }

    /// <summary>An unwritable folder is a warning and not a throw.</summary>
    /// <remarks>
    /// This runs on the startup path. A dashboard that refused to start because it could not
    /// refresh a script would trade the whole application for a file, and the copy already on disk
    /// is the one Claude Code runs in the meantime — the old version, not a broken one.
    /// </remarks>
    [Fact]
    public void A_folder_that_is_not_there_is_a_warning_and_not_a_throw()
    {
        var missing = new DashboardPaths(Path.Combine(_root, "gone", "deeper"));

        Assert.False(HookScript.EnsureWritten(missing, Logger.None));
        Assert.False(HookScript.Matches(missing));
    }

    /// <summary>
    /// <strong>The script says, in itself, that it is generated and will be replaced.</strong>
    /// </summary>
    /// <remarks>
    /// The revert above is only defensible if the person whose edit is about to be discarded was
    /// told. The header is the only place they will ever look, and there is no build step that
    /// would notice if it stopped saying so.
    /// </remarks>
    [Fact]
    public void The_script_warns_the_reader_that_it_is_generated()
    {
        Assert.Contains("GENERATED FILE", HookScript.Text, StringComparison.Ordinal);
        Assert.Contains("HookScript.cs", HookScript.Text, StringComparison.Ordinal);
        Assert.Contains("REVERTED AT THE NEXT", HookScript.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>Both curl invocations carry the same flags, and only the token header differs.</strong>
    /// </summary>
    /// <remarks>
    /// There are two calls because a header assembled in a variable is re-parsed when it expands,
    /// and the token would be the text doing the parsing. The cost of that decision is two lines
    /// that must stay in step, and a timeout changed in one of them would leave the other quietly
    /// wrong on whichever half of the machines has a token set.
    /// </remarks>
    [Fact]
    public void The_two_curl_calls_differ_only_by_the_token_header()
    {
        var calls = HookScript.Text
            .Split("\r\n", StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => line.Contains("curl.exe", StringComparison.Ordinal)
                && !line.StartsWith("rem", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, calls.Count);

        var withToken = calls.Single(line => line.Contains("X-Dashboard-Token", StringComparison.Ordinal));
        var without = calls.Single(line => !line.Contains("X-Dashboard-Token", StringComparison.Ordinal));

        const string TokenHeader = """ -H "X-Dashboard-Token: !CLAUDE_DASHBOARD_TOKEN!" """;

        Assert.Equal(
            without,
            withToken.Replace(TokenHeader.TrimEnd(), string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void It_needs_its_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => HookScript.EnsureWritten(null!, Logger.None));
        Assert.Throws<ArgumentNullException>(() => HookScript.EnsureWritten(_paths, null!));
        Assert.Throws<ArgumentNullException>(() => HookScript.Matches(null!));
    }
}
