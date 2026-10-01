namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// The one source-text scanner every guard in this folder shares.
/// </summary>
/// <remarks>
/// Extracted from <see cref="StartupHookGuardTests"/> when T1.34 needed the same stripping for a
/// guard over <c>MainViewModel.cs</c>. One scanner rather than one per file, because the scanner
/// is where the disarms were fought and closed — a copy would be a copy of that history that
/// stopped earning new lessons.
/// </remarks>
internal static class GuardScan
{
    /// <summary>
    /// Drops both comment styles and the <em>contents</em> of string and char literals, so that
    /// only code that runs can satisfy a positive search.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Each omission here was a proven disarm before it was closed (T1.32 fix cycle
    /// 2).</strong> Stripping only <c>//</c> lines let a <c>/* */</c> block inside one argument
    /// slot carry the token — the first version's remark claimed a block comment could not
    /// compile, which is true of one spanning the list and false of one inside a slot. And
    /// stripping comments alone still leaves string literals, which cut both ways: a decoy call
    /// spelled inside a string would satisfy a search, and a <c>"/*"</c> in one string with a
    /// <c>"*/"</c> in another would open a phantom block that swallows the real call. Blanking
    /// string contents while keeping the quotes closes both directions at once.
    /// </para>
    /// <para>
    /// <strong>A pragmatic scanner, not a C# lexer.</strong> It tracks line comments, block
    /// comments, ordinary string and char literals with backslash escapes, verbatim strings, and
    /// raw string literals.
    /// </para>
    /// <para>
    /// <strong>Raw and verbatim strings were added in the PR #66 review, because a mis-scan does not
    /// always fail closed.</strong> An earlier remark here said a mis-scanned file gives mangled text
    /// in which a positive search finds nothing, so the guard fails and gets read. That holds for a
    /// search that must find something. It is false for one that must find nothing: the raw JSON
    /// strings in <c>HookPlugin.cs</c> made the scanner swallow the code after them, and the file's
    /// real <c>File.WriteAllText</c> was invisible to every "holds no write" check.
    /// </para>
    /// <para>
    /// <strong>Still mis-scanned: a string inside an interpolation hole</strong>, as in
    /// <c>$"{(n &gt; 0 ? " " : "")}"</c>. Four product lines have one (in <c>HealthProbe</c>,
    /// <c>ClaudeCli</c>, <c>HookSwitches</c> and <c>HeaderViewModels</c>). The scanner pairs those
    /// quotes in order, so the mistake stays inside that one statement — part of the literal reads
    /// as code and part of the hole as a literal — and the rest of the file scans correctly.
    /// </para>
    /// </remarks>
    public static string CodeOnly(string text)
    {
        var kept = new System.Text.StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                if (i < text.Length)
                {
                    kept.Append('\n');
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                i += 2;

                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    i++;
                }

                i++;
                kept.Append(' ');
                continue;
            }

            if (c == '"')
            {
                var run = 0;

                while (i + run < text.Length && text[i + run] == '"')
                {
                    run++;
                }

                // A raw string literal: three or more quotes open it, and the same number close
                // it. Its contents may hold any quote run shorter than that, and no escapes.
                if (run >= 3)
                {
                    var close = text.IndexOf(new string('"', run), i + run, StringComparison.Ordinal);
                    i = close < 0 ? text.Length : close + run - 1;
                    kept.Append("\"\"");
                    continue;
                }

                // A verbatim string literal, @"…", $@"…" or @$"…": a doubled quote is a quote, and
                // a backslash is only a backslash.
                if (i > 0 && (text[i - 1] == '@' || (text[i - 1] == '$' && i > 1 && text[i - 2] == '@')))
                {
                    i++;

                    while (i < text.Length && !(text[i] == '"' && (i + 1 >= text.Length || text[i + 1] != '"')))
                    {
                        i += text[i] == '"' ? 2 : 1;
                    }

                    kept.Append("\"\"");
                    continue;
                }
            }

            if (c is '"' or '\'')
            {
                var quote = c;
                kept.Append(quote);
                i++;

                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '\\')
                    {
                        i++;
                    }

                    i++;
                }

                kept.Append(quote);
                continue;
            }

            kept.Append(c);
        }

        return kept.ToString();
    }

    /// <summary>How many times <paramref name="value"/> occurs in <paramref name="text"/>.</summary>
    public static int Occurrences(string text, string value)
    {
        var count = 0;

        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
