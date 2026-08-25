using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The tray glyph: one filled dot per colour, plus the hollow "off duty" ring (Impl §5.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Drawn, not shipped.</strong> Six 16px assets differing only in a fill colour would be
/// six files to keep in step with one enum, and the enum is the thing that changes. Drawing them
/// keeps the palette single-sourced on <see cref="TrayColour"/> and keeps the result inspectable
/// in-process: the tests read pixels back rather than comparing screenshots.
/// </para>
/// <para>
/// <strong>The paused glyph is a ring, not a grey dot.</strong> Impl §5.2 requires it to be
/// visually distinct from all-quiet grey, because "nothing is happening" and "I switched it off"
/// are one click apart and must never look alike. Filled versus hollow reads as <em>off</em>
/// rather than as calm at 16px, where a subtler difference — a slightly dimmer grey — would not
/// survive the scaling. Still static; still no digits.
/// </para>
/// <para>
/// <strong>Why GDI and not WPF.</strong> H.NotifyIcon turns what it is given into a Win32 HICON.
/// Its <c>IconSource</c> path accepts only a URI-backed image, so a generated one has to go
/// through <c>Icon</c>, which the library documents as the property "for dynamically generated
/// System.Drawing.Icons". Drawing here with the same library that consumes it removes a
/// WPF-to-PNG-to-GDI round trip that existed only to satisfy a converter.
/// </para>
/// <para>
/// <strong>Cached, because these own unmanaged handles.</strong> <c>GetHicon</c> allocates an
/// HICON, and the tooltip recomputes on every tick — regenerating per tick would leak one handle
/// every fifteen seconds for the life of the process. There are six possible glyphs, they never
/// change, so they are built once and kept. Nothing disposes them: they live exactly as long as
/// the process, and a static cache being torn down at exit is the shutdown path anyway.
/// </para>
/// <para>
/// <strong>DPI.</strong> Rendered at a nominal 16px, which the shell scales. T1.16 owns
/// Per-Monitor v2 and will have to decide whether the tray wants a larger source at high DPI;
/// because these are drawn from geometry rather than loaded from assets, that is a change to
/// <see cref="Size"/> and not a new set of files.
/// </para>
/// </remarks>
public static class TrayIcons
{
    /// <summary>The rendered edge, in pixels.</summary>
    public const int Size = 16;

    private static readonly ConcurrentDictionary<(TrayColour Colour, bool Paused), Icon> Cache = new();

    /// <summary>The fill for each colour, single-sourced from <see cref="TrayColour"/>.</summary>
    private static readonly Dictionary<TrayColour, Color> Fills = new()
    {
        [TrayColour.Grey] = Color.FromArgb(0x8A, 0x8A, 0x8E),
        [TrayColour.Blue] = Color.FromArgb(0x3B, 0x82, 0xF6),
        [TrayColour.Green] = Color.FromArgb(0x22, 0xC5, 0x5E),
        [TrayColour.Amber] = Color.FromArgb(0xF5, 0x9E, 0x0B),
        [TrayColour.Red] = Color.FromArgb(0xEF, 0x44, 0x44),
    };

    /// <summary>The ring colour used while monitoring is off duty.</summary>
    private static readonly Color OffDuty = Color.FromArgb(0x5A, 0x5A, 0x5E);

    /// <summary>The glyph for <paramref name="colour"/>, or the off-duty ring when paused.</summary>
    /// <remarks>
    /// <paramref name="paused"/> wins over the colour, because that is what pause means: the
    /// glyph stops reporting. It is the one deliberate exception to "the tray tells the truth"
    /// (Design §9), and the caller is expected to have said so in the tooltip.
    /// </remarks>
    /// <param name="colour">The roll-up colour.</param>
    /// <param name="paused">Whether monitoring is off duty.</param>
    public static Icon For(TrayColour colour, bool paused = false) =>
        Cache.GetOrAdd((colour, paused), static key => Render(key.Colour, key.Paused));

    private static Icon Render(TrayColour colour, bool paused)
    {
        using var bitmap = new Bitmap(Size, Size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            if (paused)
            {
                // Stroked, with no fill at all — the centre stays transparent, which is what makes
                // this distinguishable from the all-quiet dot rather than merely a different grey.
                using var pen = new Pen(OffDuty, 2f);
                graphics.DrawEllipse(pen, 3, 3, Size - 6, Size - 6);
            }
            else
            {
                using var brush = new SolidBrush(Fills[colour]);
                graphics.FillEllipse(brush, 2, 2, Size - 4, Size - 4);
            }
        }

        // FromHandle does not own the handle, so the HICON outlives the Icon wrapper — which is
        // what the cache wants: one handle per glyph, for the life of the process.
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
