using System.IO;
using ClaudeDashboard.App.Configuration;

namespace ClaudeDashboard.Tests.Configuration;

/// <summary>
/// The one hop production actually uses: the environment variable reaching
/// <see cref="DashboardPaths.Root"/> (Impl Part 8).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Everything else about the override is tested through
/// <see cref="DashboardPaths.ResolveRoot"/>, which leaves this hop unobserved — and it is the
/// only hop the product takes.</strong> Rename the constant to <c>CLAUDE_DASHBOARD_HOM</c> and
/// the whole feature silently ceases to exist: every rejection test still passes, the fallback
/// still works, and the operator's override is ignored for ever, with the dashboard reading a
/// folder they are not editing. Measured — the rename leaves the entire suite green without this
/// file.
/// </para>
/// <para>
/// This is the same species as the tray tooltip binding: the value was tested, the delivery was
/// not.
/// </para>
/// </remarks>
[Collection(DataFolderEnvironment.Name)]
public sealed class DataFolderWiringTests
{
    /// <summary>
    /// The variable an operator sets is the variable the dashboard reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The literal name is deliberate, and using the constant here would be the bug.</strong>
    /// A test that set <c>DashboardPaths.HomeVariable</c> and then read it back is self-consistent
    /// under any rename — it would set <c>CLAUDE_DASHBOARD_HOM</c>, read <c>CLAUDE_DASHBOARD_HOM</c>,
    /// and pass while the documented variable did nothing. So the name is written out, because
    /// here the literal <em>is</em> the contract: it is what Impl Part 8 states and what a person
    /// types into their environment.
    /// </para>
    /// <para>
    /// That is the opposite of the rule followed for the gate name on <c>/health</c>, where the
    /// expected value is computed rather than written out. The difference is which side owns the
    /// truth. There, the naming rule is the product's own and a literal would be a second copy of
    /// it; here, the name is fixed outside the code by the specification and the operator, and
    /// the constant is what has to agree with it.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_documented_variable_is_the_one_that_moves_the_data_folder()
    {
        Assert.Equal("CLAUDE_DASHBOARD_HOME", DashboardPaths.HomeVariable);

        var wanted = UniqueRoot();

        using (Set("CLAUDE_DASHBOARD_HOME", wanted))
        {
            var paths = new DashboardPaths();

            Assert.Equal(wanted, paths.Root);
            Assert.Equal(DataFolderSource.Override, paths.RootSource);
            Assert.Null(paths.RootProblem);

            // Everything hangs off the root, so this is where the operator's settings file
            // actually is — the thing that is silently wrong when this hop breaks.
            Assert.Equal(Path.Combine(wanted, "settings.json"), paths.SettingsFile);
        }

        Directory.Delete(wanted, recursive: true);
    }

    /// <summary>An unusable value still starts the dashboard, through the real constructor.</summary>
    /// <remarks>
    /// The fallback is tested against the resolver elsewhere. This is the same claim taken through
    /// the wiring, because a constructor that read the variable and then threw would satisfy the
    /// resolver's tests and stop the process from starting at all.
    /// </remarks>
    [Fact]
    public void An_unusable_variable_falls_back_through_the_real_constructor()
    {
        using (Set("CLAUDE_DASHBOARD_HOME", "not-an-absolute-path"))
        {
            var paths = new DashboardPaths();

            Assert.Equal(DashboardPaths.DefaultRoot, paths.Root);
            Assert.Equal(DataFolderSource.RejectedOverride, paths.RootSource);
            Assert.False(string.IsNullOrWhiteSpace(paths.RootProblem));
        }
    }

    /// <summary>With the variable unset, the default under %LOCALAPPDATA% is used.</summary>
    /// <remarks>
    /// The control. Without it, a constructor that always took the override path — or one that
    /// ignored it and always used the default — would each satisfy one of the tests above.
    /// </remarks>
    [Fact]
    public void With_no_variable_the_default_root_is_used()
    {
        using (Set("CLAUDE_DASHBOARD_HOME", null))
        {
            var paths = new DashboardPaths();

            Assert.Equal(DashboardPaths.DefaultRoot, paths.Root);
            Assert.Equal(DataFolderSource.Default, paths.RootSource);
            Assert.Null(paths.RootProblem);
        }
    }

    private static string UniqueRoot() =>
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Sets a process environment variable for the life of the returned scope.</summary>
    private static Restore Set(string name, string? value)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);

        return new Restore(() => Environment.SetEnvironmentVariable(name, previous));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
