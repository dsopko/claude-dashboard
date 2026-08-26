namespace ClaudeDashboard.Tests.Configuration;

/// <summary>
/// The collection for tests that read or write the real <c>CLAUDE_DASHBOARD_HOME</c> variable.
/// </summary>
/// <remarks>
/// <para>
/// An environment variable is process-wide, and xUnit runs test classes in parallel. A test that
/// sets one is visible to every other test in the process for as long as it is set, so the
/// classes that touch it — the one that sets it, and the one that asserts the default root when
/// it is <em>not</em> set — have to be serialized against each other.
/// </para>
/// <para>
/// Only against each other. Nothing else in the suite constructs a parameterless
/// <c>DashboardPaths</c>, which is the only thing that reads the variable, so the rest of the
/// suite keeps running in parallel.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DataFolderEnvironment
{
    /// <summary>The collection name.</summary>
    public const string Name = "data-folder-environment";
}
