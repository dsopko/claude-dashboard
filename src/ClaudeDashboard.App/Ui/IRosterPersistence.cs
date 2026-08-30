using System.IO;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// Writes the operator's rosters to <c>settings.json</c>, so a remembered one survives a restart
/// (T1.26, issue #16 rule 2).
/// </summary>
/// <remarks>
/// <strong>This is the ONLY thing that makes a roster persistent.</strong> A group formed and not
/// remembered lives in <see cref="RosterStore"/> alone and is gone when those sessions end — which
/// is what "declining leaves nothing behind" means, and why declining needs no code of its own.
/// </remarks>
public interface IRosterPersistence
{
    /// <summary>Writes <paramref name="book"/>'s rosters into the settings file.</summary>
    void Remember(RosterBook book);
}

/// <summary>Writes the rosters through the ordinary settings store.</summary>
/// <remarks>
/// <para>
/// It re-reads the file and overrides one section, exactly as the window-position save does. Two
/// reasons: the file may hold settings this process never loaded, and rewriting only what changed is
/// what keeps a non-atomic save (issue #7) from being a chance to lose everything else.
/// </para>
/// <para>
/// Best effort. A dashboard that cannot write its settings must still run — the roster is already
/// in force in memory, and what is lost is only that it survives the next restart.
/// </para>
/// </remarks>
public sealed class SettingsRosterPersistence(SettingsStore settings, Serilog.ILogger logger) : IRosterPersistence
{
    private readonly SettingsStore _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly Serilog.ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc/>
    public void Remember(RosterBook book)
    {
        ArgumentNullException.ThrowIfNull(book);

        try
        {
            var current = _settings.Load().Settings;

            _settings.Save(current with
            {
                Rosters = book.Rosters.ToDictionary(
                    roster => roster.Name,
                    roster => (IReadOnlyList<string>)[.. roster.Members],
                    StringComparer.Ordinal),
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // BY ROSTER COUNT, NEVER BY NAME OR MEMBER. A member name is a session title, and a
            // title can be a model-written summary of the operator's prompt (T1.24).
            _logger.Warning(ex, "Could not save {Count} roster(s). They are in force but will not survive a restart.", book.Rosters.Count);
        }
    }
}
