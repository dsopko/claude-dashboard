using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeDashboard.App.Setup;

/// <summary>What a read-modify-write attempt did.</summary>
public enum SettingsWriteOutcome
{
    /// <summary>The change was written.</summary>
    Written = 1,

    /// <summary>The merge produced no change, so nothing was written.</summary>
    NothingToDo = 2,

    /// <summary>Another writer kept winning the race. The file is untouched.</summary>
    Abandoned = 3,

    /// <summary>The file could not be read or parsed. The file is untouched.</summary>
    Unreadable = 4,
}

/// <summary>The outcome, and enough to log why.</summary>
public readonly record struct SettingsWriteResult(
    SettingsWriteOutcome Outcome,
    int Attempts = 0,
    string? Problem = null,
    string? BackupPath = null);

/// <summary>
/// Reads, merges and writes Claude Code's settings file without ever leaving it broken
/// (Impl §9.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three separate problems, and only two of them are ours to control.</strong>
/// </para>
/// <para>
/// <em>A partial file must be impossible.</em> The new content goes to a temporary file in the
/// same directory and is then moved over the target. Any reader sees the whole old file or the
/// whole new one. A crash between the two leaves the temporary file and the original intact,
/// which is why the temporary name is distinctive enough to find and sweep.
/// </para>
/// <para>
/// <em>Our own writers must not interleave.</em> A lock file beside the target, opened with no
/// sharing, is held across read → merge → replace. <strong>It serialises dashboard processes and
/// nothing else.</strong> Claude Code knows nothing about it, and neither does an operator with
/// the file open in an editor; this is not a lock on the settings file and must never be described
/// as one.
/// </para>
/// <para>
/// <em>Losing a race to a writer we cannot lock.</em> The file is fingerprinted when read and
/// re-checked immediately before the replace. If it moved, the merge is thrown away and redone
/// against the new content — never merged onto stale text, which would silently undo whatever the
/// other writer did. After a bounded number of attempts the write is abandoned and the file is
/// left exactly as it was. A registration that did not happen costs a dashboard that hears nothing
/// and says so in its tray; a clobbered file costs the operator their hooks.
/// </para>
/// </remarks>
public sealed class SettingsFileWriter
{
    /// <summary>How many times to redo a merge that lost the race before giving up.</summary>
    public const int DefaultAttempts = 5;

    private readonly string _path;
    private readonly int _attempts;

    /// <summary>Writes the settings file at <paramref name="path"/>.</summary>
    /// <remarks>
    /// The path is a parameter, always. Nothing here resolves <c>~/.claude</c>, so no call site can
    /// reach the operator's real file by accident and every test names its own target.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or whitespace.</exception>
    public SettingsFileWriter(string path, int attempts = DefaultAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempts);

        _path = path;
        _attempts = attempts;
    }

    /// <summary>Where the lock file sits — beside the target, never inside it.</summary>
    public string LockPath => _path + ".claude-dashboard.lock";

    /// <summary>
    /// Copies the file to a timestamped backup beside it, and returns the path.
    /// </summary>
    /// <remarks>
    /// A plain copy at a stated path, restorable by hand with the dashboard uninstalled, deleted,
    /// or refusing to start (Impl §9.3). Deliberately not an archive, not compressed, and not
    /// anything that needs this program to read it: a restore that depends on the thing that broke
    /// is not a restore.
    /// </remarks>
    /// <returns>The backup path, or null when there was no file to back up.</returns>
    public string? BackUp(DateTimeOffset now)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = $"{_path}.dashboard-backup-{stamp}";

        // Never overwrite: a backup taken by hand, or by an earlier run in the same second, is
        // evidence and is not ours to replace.
        for (var suffix = 1; File.Exists(backup); suffix++)
        {
            backup = $"{_path}.dashboard-backup-{stamp}-{suffix}";
        }

        File.Copy(_path, backup);

        return backup;
    }

    /// <summary>
    /// Applies <paramref name="merge"/> to the file, retrying if another writer gets there first.
    /// </summary>
    /// <param name="merge">
    /// Modifies the parsed settings in place. Called once per attempt, always against freshly read
    /// content, so it must not accumulate state across calls.
    /// </param>
    /// <param name="now">The instant a backup is stamped with.</param>
    /// <param name="backUpFirst">Whether to take a backup before the first write.</param>
    /// <remarks>Never throws. Every failure is an outcome, because the caller is startup or shutdown.</remarks>
    public SettingsWriteResult Modify(Action<JsonObject> merge, DateTimeOffset now, bool backUpFirst = true)
    {
        ArgumentNullException.ThrowIfNull(merge);

        FileStream? lockFile = null;
        string? backup = null;

        try
        {
            lockFile = TryTakeLock();

            for (var attempt = 1; attempt <= _attempts; attempt++)
            {
                string? before;

                try
                {
                    before = File.Exists(_path) ? File.ReadAllText(_path) : null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new SettingsWriteResult(SettingsWriteOutcome.Unreadable, attempt, ex.Message);
                }

                JsonObject settings;

                try
                {
                    settings = HookRegistration.Parse(before ?? string.Empty);
                }
                catch (JsonException ex)
                {
                    // Deliberately not "use defaults". That is right for the dashboard's own
                    // settings and catastrophic here: writing back a default object would delete
                    // every hook, permission and preference the operator has.
                    return new SettingsWriteResult(SettingsWriteOutcome.Unreadable, attempt, ex.Message);
                }

                merge(settings);
                var after = HookRegistration.Render(settings);

                if (before is not null && string.Equals(before, after, StringComparison.Ordinal))
                {
                    return new SettingsWriteResult(SettingsWriteOutcome.NothingToDo, attempt);
                }

                if (backUpFirst && backup is null && before is not null)
                {
                    try
                    {
                        backup = BackUp(now);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // No backup means no write. The whole point of taking one is that it
                        // exists before the file changes.
                        return new SettingsWriteResult(SettingsWriteOutcome.Unreadable, attempt, ex.Message);
                    }
                }

                if (TryReplace(after, before))
                {
                    return new SettingsWriteResult(SettingsWriteOutcome.Written, attempt, BackupPath: backup);
                }

                // Somebody else wrote between our read and our replace. Their content is now on
                // disk; go round again and merge onto it rather than over it.
                Thread.Sleep(TimeSpan.FromMilliseconds(20 * attempt));
            }

            return new SettingsWriteResult(
                SettingsWriteOutcome.Abandoned,
                _attempts,
                "another process kept writing the file first",
                backup);
        }
        finally
        {
            lockFile?.Dispose();
        }
    }

    /// <summary>Removes any temporary file a crashed write left behind.</summary>
    /// <remarks>
    /// The residue of the one moment that is not atomic. Harmless — nothing reads it — but a file
    /// nobody can explain beside a settings file is its own small alarm.
    /// </remarks>
    public int SweepAbandonedTemporaries()
    {
        var directory = Path.GetDirectoryName(_path);

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        var swept = 0;

        foreach (var stale in Directory.EnumerateFiles(directory, Path.GetFileName(_path) + ".dashboard-tmp-*"))
        {
            try
            {
                File.Delete(stale);
                swept++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another instance may be mid-write. Leaving it is the safe half.
            }
        }

        return swept;
    }

    /// <summary>
    /// Writes the new content and moves it over the target, unless the target moved first.
    /// </summary>
    private bool TryReplace(string content, string? expected)
    {
        var temporary = $"{_path}.dashboard-tmp-{Guid.NewGuid():N}";

        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // The last look before committing. Everything between here and the move is a window
            // no lock of ours can close, which is why it is as short as it can be made.
            var current = File.Exists(_path) ? File.ReadAllText(_path) : null;

            if (!string.Equals(current, expected, StringComparison.Ordinal))
            {
                return false;
            }

            File.Move(temporary, _path, overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Swept on the next start.
                }
            }
        }
    }

    /// <summary>Takes the inter-process lock, or proceeds without it.</summary>
    /// <remarks>
    /// Failing to take it is not a reason to refuse the write. The lock reduces the chance that
    /// two dashboards collide; the fingerprint check is what makes a collision safe, and it runs
    /// either way.
    /// </remarks>
    private FileStream? TryTakeLock()
    {
        try
        {
            return new FileStream(
                LockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A short fingerprint of the file, for logging which version was seen.</summary>
    internal static string Fingerprint(string? content) =>
        content is null
            ? "(absent)"
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..12];
}
