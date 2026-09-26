using System.Windows.Media.Imaging;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The drawn caption's icon: which of its two frames to draw at a display scale (T1.38,
/// issue #50).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Drawn pixel for pixel, not resampled.</strong> The caption's slot is 20 DIP. That is
/// 20 device pixels at 100% and 30 at 150%, and the asset holds a frame made for each, so at
/// those two scales the frame is the size it is drawn at and nothing resamples it. The window's
/// layout rounding puts it on whole pixels.
/// </para>
/// <para>
/// <strong>Other scales take the 30.</strong> Above 100% the 30 is the nearer frame that is
/// reduced rather than enlarged, up to 150%; above 150% it is enlarged, and softens, because no
/// frame was made for those. The operator runs at 100% and 150%, and those are the two frames
/// that were chosen against the live title bar; a 40 px frame would be a third thing to judge
/// by eye, not a thing to add on spec.
/// </para>
/// <para>
/// Not the exe icon. <c>app.ico</c> stays the <c>ApplicationIcon</c> — taskbar, Alt-Tab, Start
/// menu — byte for byte; this asset is the caption's only. See the project file's remark on the
/// two resources for why they are separate and how both frames are made.
/// </para>
/// </remarks>
internal static class CaptionIcon
{
    /// <summary>The frame made for 100%.</summary>
    internal static readonly Uri Frame20 =
        new("/ClaudeDashboard.App;component/Assets/caption-20.png", UriKind.Relative);

    /// <summary>The frame made for 150%.</summary>
    internal static readonly Uri Frame30 =
        new("/ClaudeDashboard.App;component/Assets/caption-30.png", UriKind.Relative);

    /// <summary>The frame to draw at a display scale, where 1.0 is 100%.</summary>
    internal static Uri FrameFor(double scale) => scale <= 1.0 ? Frame20 : Frame30;

    /// <summary>A frame's absolute pack URI, which is what a bitmap built in code needs.</summary>
    /// <remarks>
    /// Built on demand, not held: the <c>pack</c> scheme exists only once WPF's application
    /// machinery has registered it, and a static absolute URI would throw from this type's
    /// initializer in any process that touched it first — breaking the type for good. The
    /// relative form above needs no scheme, and a bitmap built in code has no base URI to
    /// resolve it against, so it is joined here, at the moment of loading.
    /// </remarks>
    internal static Uri Pack(Uri frame) => new(new Uri("pack://application:,,,"), frame);

    /// <summary>The frame for the scale, decoded at its own size and frozen.</summary>
    /// <remarks>
    /// No decode width is asked for: a decode width is a resample, and the frame is already the
    /// size it will be drawn at.
    /// </remarks>
    internal static BitmapImage Load(double scale)
    {
        var image = new BitmapImage();

        image.BeginInit();
        image.UriSource = Pack(FrameFor(scale));
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();

        return image;
    }
}
