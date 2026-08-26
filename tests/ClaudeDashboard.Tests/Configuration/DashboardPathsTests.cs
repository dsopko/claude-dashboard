using System.IO;
using ClaudeDashboard.App.Configuration;

namespace ClaudeDashboard.Tests.Configuration;

/// <summary>
/// The data folder, and the override that moves it (Impl Part 8).
/// </summary>
/// <remarks>
/// <para>
/// The resolver is exercised directly rather than through the environment. Setting a process-wide
/// variable inside a test leaks into every other test running in the same host, and xUnit runs
/// classes in parallel; the parameterless constructor is covered by the one case that needs no
/// variable at all.
/// </para>
/// <para>
/// Every rejection asserts <em>both</em> that the fallback is in force and that a reason was
/// recorded. The fallback alone would also be produced by a resolver that ignored the override
/// entirely, which is a different bug with the same symptom on a happy day.
/// </para>
/// </remarks>
public sealed class DashboardPathsTests
{
    private const string Fallback = @"C:\fallback\ClaudeDashboard";

    [Fact]
    public void With_no_override_the_default_root_is_used()
    {
        var root = DashboardPaths.ResolveRoot(null, Fallback, out var source, out var problem);

        Assert.Equal(Fallback, root);
        Assert.Equal(DataFolderSource.Default, source);

        // Absent is the ordinary case, not a fault. A "problem" here would put a warning in the
        // log of every dashboard that never set the variable.
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_override_is_the_same_as_none(string value)
    {
        var root = DashboardPaths.ResolveRoot(value, Fallback, out var source, out var problem);

        Assert.Equal(Fallback, root);
        Assert.Equal(DataFolderSource.Default, source);
        Assert.Null(problem);
    }

    [Fact]
    public void A_usable_override_moves_the_root()
    {
        var wanted = UniqueRoot();

        var root = DashboardPaths.ResolveRoot(wanted, Fallback, out var source, out var problem);

        try
        {
            Assert.Equal(wanted, root);
            Assert.Equal(DataFolderSource.Override, source);
            Assert.Null(problem);

            // Created, not merely accepted: "a path you cannot create falls back" is only a real
            // rule if the creation is what decides it.
            Assert.True(Directory.Exists(wanted));
        }
        finally
        {
            Directory.Delete(wanted, recursive: true);
        }
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_off_an_override()
    {
        var wanted = UniqueRoot();

        var root = DashboardPaths.ResolveRoot($"  {wanted}  ", Fallback, out var source, out _);

        try
        {
            Assert.Equal(wanted, root);
            Assert.Equal(DataFolderSource.Override, source);
        }
        finally
        {
            Directory.Delete(wanted, recursive: true);
        }
    }

    /// <summary>
    /// A relative path is refused rather than resolved.
    /// </summary>
    /// <remarks>
    /// It would resolve against the current directory, which for a tray app started from a
    /// shortcut or a scheduled task is whatever the shell chose — so the same variable would name
    /// a different folder depending on how the dashboard was launched.
    /// </remarks>
    [Theory]
    [InlineData(@"dashboard-data")]
    [InlineData(@".\dashboard-data")]
    [InlineData(@"..\dashboard-data")]
    public void A_relative_override_falls_back_and_says_why(string value)
    {
        var root = DashboardPaths.ResolveRoot(value, Fallback, out var source, out var problem);

        Assert.Equal(Fallback, root);
        Assert.Equal(DataFolderSource.RejectedOverride, source);
        Assert.Contains("fully qualified", problem!, StringComparison.Ordinal);
    }

    /// <summary>A path that cannot exist falls back rather than stopping the dashboard.</summary>
    /// <remarks>
    /// A typo in an environment variable must not stop the process starting, for the same reason
    /// a typo in settings.json must not: the operator has no console and no window, and a process
    /// that refuses to start presents as the dashboard being gone rather than as a configuration
    /// error.
    /// </remarks>
    [Theory]
    [InlineData("\u0000\u0001invalid")]
    [InlineData(@"\\?\nonsense<>|")]
    public void An_unusable_override_falls_back_and_says_why(string value)
    {
        var root = DashboardPaths.ResolveRoot(value, Fallback, out var source, out var problem);

        Assert.Equal(Fallback, root);
        Assert.Equal(DataFolderSource.RejectedOverride, source);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    /// <summary>A drive that is not there is the realistic version of the case above.</summary>
    [Fact]
    public void An_override_on_a_drive_that_does_not_exist_falls_back()
    {
        var root = DashboardPaths.ResolveRoot(@"Q:\no-such-volume\dashboard", Fallback, out var source, out var problem);

        Assert.Equal(Fallback, root);
        Assert.Equal(DataFolderSource.RejectedOverride, source);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    /// <summary>An explicitly supplied root is neither an override nor a default.</summary>
    /// <remarks>
    /// Recorded because the startup log names the source, and "Provided" appearing in a
    /// dashboard's log would mean something built it a way the app never does.
    /// </remarks>
    [Fact]
    public void A_root_given_directly_is_recorded_as_provided()
    {
        var paths = new DashboardPaths(@"C:\somewhere");

        Assert.Equal(@"C:\somewhere", paths.Root);
        Assert.Equal(DataFolderSource.Provided, paths.RootSource);
        Assert.Null(paths.RootProblem);
    }

    /// <summary>Every path hangs off the root, so moving the root moves all of them.</summary>
    [Fact]
    public void The_override_moves_every_file_the_dashboard_owns()
    {
        var paths = new DashboardPaths(@"C:\elsewhere");

        Assert.StartsWith(@"C:\elsewhere", paths.SettingsFile, StringComparison.Ordinal);
        Assert.StartsWith(@"C:\elsewhere", paths.LogFolder, StringComparison.Ordinal);
        Assert.StartsWith(@"C:\elsewhere", paths.LogFile, StringComparison.Ordinal);
        Assert.StartsWith(@"C:\elsewhere", paths.SoundFolder, StringComparison.Ordinal);

        // …but not the sounds that ship, which live beside the executable and are not the
        // operator's data.
        Assert.DoesNotContain(@"C:\elsewhere", DashboardPaths.ShippedSoundFolder, StringComparison.Ordinal);
    }

    private static string UniqueRoot() =>
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));
}
