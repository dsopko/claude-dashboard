using System.IO;

namespace ClaudeDashboard.App.Configuration;

/// <summary>
/// Where <strong>Claude Code</strong> keeps its configuration (Impl Part 9).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A separate type on purpose, and it must stay separate.</strong> There are two files
/// called <c>settings.json</c> in this system: the dashboard's own, at
/// <see cref="DashboardPaths.SettingsFile"/>, and Claude Code's, here. Reaching for the former
/// while writing the hook merge is the obvious move and the property name confirms it — nothing
/// throws, every test passes, the dashboard never receives another hook, and the symptom reads as
/// a quiet day. <see cref="DashboardPaths.Root"/> also moves under <c>CLAUDE_DASHBOARD_HOME</c>,
/// so the wrong file is a moving target as well as the wrong one.
/// </para>
/// <para>
/// Hence the naming: nothing here is called <c>SettingsFile</c>, and nothing in
/// <see cref="DashboardPaths"/> resolves a Claude Code path. Two names that could be confused are
/// worse than two types that cannot.
/// </para>
/// <para>
/// <strong>This finds Claude Code's settings the way Claude Code finds them.</strong> Its
/// documentation states that <c>~/.claude</c> resolves to <c>%USERPROFILE%\.claude</c> on Windows,
/// and that with <c>CLAUDE_CONFIG_DIR</c> set, every <c>~/.claude</c> path lives under that
/// directory instead. Honouring it is not a surface this project invented; it is matching the tool
/// being integrated with, and it is what lets a development run point somewhere harmless without
/// relying on discipline.
/// </para>
/// </remarks>
public sealed class ClaudeCodePaths
{
    /// <summary>The folder name under the user's profile.</summary>
    public const string FolderName = ".claude";

    /// <summary>Claude Code's own configuration-directory override.</summary>
    public const string ConfigDirectoryVariable = "CLAUDE_CONFIG_DIR";

    /// <summary>Resolves the configuration directory the way Claude Code documents it.</summary>
    public ClaudeCodePaths()
        : this(ResolveConfigDirectory(Environment.GetEnvironmentVariable(ConfigDirectoryVariable)))
    {
    }

    /// <summary>Uses <paramref name="configDirectory"/> as Claude Code's configuration directory.</summary>
    /// <exception cref="ArgumentException"><paramref name="configDirectory"/> is null, empty, or whitespace.</exception>
    public ClaudeCodePaths(string configDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            throw new ArgumentException("Claude Code's configuration directory needs a path.", nameof(configDirectory));
        }

        ConfigDirectory = configDirectory;
    }

    /// <summary>Claude Code's configuration directory.</summary>
    public string ConfigDirectory { get; }

    /// <summary>
    /// Claude Code's <em>user</em> settings file — the one the dashboard's hooks are merged into.
    /// </summary>
    /// <remarks>
    /// Named for whose it is. Claude Code reads settings from several scopes (managed, command
    /// line, project, project-local, user); this is the user scope, which is the only one the
    /// dashboard writes, because a machine-wide dashboard belongs to the person rather than to a
    /// repository.
    /// </remarks>
    public string UserSettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    /// <summary>The default directory, ignoring any override.</summary>
    public static string DefaultConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        FolderName);

    /// <summary>
    /// Applies <c>CLAUDE_CONFIG_DIR</c> if it is usable, otherwise the documented default.
    /// </summary>
    /// <remarks>
    /// Never throws, and never creates anything. Unlike the dashboard's own folder, this one
    /// belongs to Claude Code: a value that does not resolve is a reason to fall back to the
    /// documented location, not a reason to make a directory somewhere the operator did not ask
    /// for. If the fallback turns out not to exist either, that surfaces when the merge reads it.
    /// </remarks>
    internal static string ResolveConfigDirectory(string? overrideValue)
    {
        if (string.IsNullOrWhiteSpace(overrideValue))
        {
            return DefaultConfigDirectory;
        }

        var candidate = overrideValue.Trim();

        try
        {
            return Path.IsPathFullyQualified(candidate)
                ? Path.GetFullPath(candidate)
                : DefaultConfigDirectory;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return DefaultConfigDirectory;
        }
    }
}
