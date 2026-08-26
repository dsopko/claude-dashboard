using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeDashboard.Tests.Storage;

/// <summary>
/// Reads a SQLite file with a library the product does not use, so the event log can be checked
/// by something with no stake in the answer (T1.17).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A write-only feature has no oracle inside the product.</strong> Every test of it would
/// otherwise assert that we wrote what we meant to write, using the same understanding on both
/// sides of the assertion, with nothing downstream able to contradict it. That is the exact
/// condition that produces confident wrong claims: our own reader would agree with our own writer
/// about a schema that was wrong, a serialization that was wrong, or a file that was never a
/// database at all.
/// </para>
/// <para>
/// <strong>So this is not our SQLite.</strong> The product writes through
/// <c>Microsoft.Data.Sqlite</c> over SQLitePCLRaw's <c>e_sqlite3.dll</c>. This opens the same file
/// through <c>winsqlite3.dll</c> — Windows' own copy, shipped by Microsoft in System32, built from
/// a different source tree by a different vendor. They report different versions on this machine,
/// which is the evidence that they are not the same binary. Nothing our write path does can make
/// this reader agree with it.
/// </para>
/// <para>
/// <strong>And the second half of the rule, which is the half that works.</strong> An oracle alone
/// is not enough — see <c>tools/verify-pin.ps1</c> for the run where a perfectly good oracle
/// reported a verified result under conditions that made it meaningless. Here the trap is that
/// <em>an empty database and a database you failed to open both produce "no rows"</em>. So this
/// throws rather than returning an empty list when the file will not open or the table is not
/// there: a caller cannot mistake a failure for a measurement, because a failure is not a value.
/// <c>ForeignSqliteReaderTests</c> is the control that proves those two refusals are real.
/// </para>
/// </remarks>
internal static class ForeignSqliteReader
{
    private const string Dll = "winsqlite3.dll";

    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteOpenReadOnly = 0x00000001;

    [DllImport(Dll, EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Open(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(Dll, EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Prepare(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);

    [DllImport(Dll, EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Step(IntPtr statement);

    [DllImport(Dll, EntryPoint = "sqlite3_column_count", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ColumnCount(IntPtr statement);

    [DllImport(Dll, EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ColumnText(IntPtr statement, int column);

    [DllImport(Dll, EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Finalize(IntPtr statement);

    [DllImport(Dll, EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Close(IntPtr db);

    [DllImport(Dll, EntryPoint = "sqlite3_libversion", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr LibVersion();

    /// <summary>Which SQLite this is, so a test can show it is not the one the product uses.</summary>
    public static string Version() => Marshal.PtrToStringUTF8(LibVersion()) ?? "unknown";

    /// <summary>
    /// Runs <paramref name="sql"/> and returns every row as its column values.
    /// </summary>
    /// <exception cref="ForeignReadFailed">
    /// The file would not open, or the statement would not prepare. Deliberately not an empty
    /// result: see this class's remarks.
    /// </exception>
    public static List<string[]> Query(string path, string sql)
    {
        var opened = Open(Utf8(path), out var db, SqliteOpenReadOnly, IntPtr.Zero);

        if (opened != SqliteOk)
        {
            _ = Close(db);

            throw new ForeignReadFailed(
                $"The foreign reader could not open '{path}' (sqlite result {opened}). " +
                "This is not an empty database; it is no database.");
        }

        try
        {
            if (Prepare(db, Utf8(sql), -1, out var statement, IntPtr.Zero) != SqliteOk)
            {
                throw new ForeignReadFailed(
                    $"The foreign reader opened '{path}' but could not prepare \"{sql}\". " +
                    "The file is a database; it does not have the shape this query expects.");
            }

            try
            {
                var rows = new List<string[]>();
                var columns = ColumnCount(statement);

                while (Step(statement) == SqliteRow)
                {
                    var row = new string[columns];

                    for (var column = 0; column < columns; column++)
                    {
                        row[column] = Marshal.PtrToStringUTF8(ColumnText(statement, column)) ?? string.Empty;
                    }

                    rows.Add(row);
                }

                return rows;
            }
            finally
            {
                _ = Finalize(statement);
            }
        }
        finally
        {
            _ = Close(db);
        }
    }

    /// <summary>Every value of the first column.</summary>
    public static List<string> Column(string path, string sql) =>
        [.. Query(path, sql).Select(row => row[0])];

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");
}

/// <summary>The foreign reader could not answer. Not an empty answer — no answer.</summary>
internal sealed class ForeignReadFailed(string message) : Exception(message);
