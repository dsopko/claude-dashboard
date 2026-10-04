using System.IO;
using System.Text.Json;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Configuration;

/// <summary>Where the archive writer reads Claude Code's <c>cleanupPeriodDays</c> (T1.68, issue #102).</summary>
/// <remarks>A seam, so a test hands the writer what it would have read, and no test reads the operator's file.</remarks>
public interface ICleanupPeriodSource
{
    /// <summary>Reads the value now. Must not throw.</summary>
    CleanupPeriodRead Read();
}

/// <summary>
/// Reads <c>cleanupPeriodDays</c> from Claude Code's <c>~/.claude/settings.json</c>, the file the
/// dashboard already reads (Impl §9.3), found the same way (<see cref="ClaudeCodePaths"/>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A read and a parse, never a write.</strong> The file is opened for reading only, and the
/// dashboard never writes Claude Code's settings (Impl §9.3).
/// </para>
/// <para>
/// <strong>Read at each prune, on the archive writer's thread</strong>, so a change in Claude Code's
/// settings takes effect at the next prune, with no restart. Nothing is cached.
/// </para>
/// <para>
/// What it finds, and nothing more: <see cref="HistoryRetention"/> judges it. No file, a file that cannot
/// be read, one that is not strict JSON (a comment or a trailing comma included), an empty one and one
/// whose root is not an object are all "not read", and delete nothing. The key is matched as Claude
/// Code writes it, <c>cleanupPeriodDays</c>.
/// </para>
/// </remarks>
public sealed class ClaudeCleanupPeriod(ClaudeCodePaths claude) : ICleanupPeriodSource
{
    /// <summary>The key, as Claude Code's settings spell it.</summary>
    public const string Key = "cleanupPeriodDays";

    /// <summary>
    /// Strict JSON: no comments and no comma at the end of a list (the T1.68 review). Unlike the hook
    /// check (Impl §9.3), which accepts both. If Claude Code refuses such a file, it pauses its own
    /// cleanup; a lenient read here would then delete what Claude Code keeps, which is the wrong side
    /// for #102. So a file with either one is "not read", and nothing is deleted.
    /// </summary>
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    private readonly ClaudeCodePaths _claude = claude ?? throw new ArgumentNullException(nameof(claude));

    /// <inheritdoc/>
    public CleanupPeriodRead Read()
    {
        string text;

        try
        {
            text = File.ReadAllText(_claude.UserSettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return CleanupPeriodRead.NotRead;
        }

        try
        {
            using var document = JsonDocument.Parse(text, ReadOptions);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return CleanupPeriodRead.NotRead;
            }

            if (!document.RootElement.TryGetProperty(Key, out var value))
            {
                return CleanupPeriodRead.Absent;
            }

            if (value.ValueKind != JsonValueKind.Number)
            {
                return new CleanupPeriodRead(CleanupPeriodKind.NotANumber);
            }

            if (value.TryGetDecimal(out var number))
            {
                return CleanupPeriodRead.Of(number);
            }

            // A number too large for a decimal: only its sign matters to the rule.
            return new CleanupPeriodRead(value.GetRawText().StartsWith('-')
                ? CleanupPeriodKind.HugeNegativeNumber
                : CleanupPeriodKind.HugeNumber);
        }
        catch (JsonException)
        {
            return CleanupPeriodRead.NotRead;
        }
    }
}
