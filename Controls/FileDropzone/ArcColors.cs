using Avalonia.Media;

namespace FileDiff.Controls;

/// <summary>
/// The light-surface colors the dropzone draws with (Arc's neutral semantic palette, resolved once here instead of
/// through the token runtime).
/// </summary>
internal static class ArcColors
{
    public static readonly IBrush Foreground = Brush("#0B0B0B");
    public static readonly IBrush TextSecondary = Brush("#585858");
    public static readonly IBrush TextMuted = Brush("#7D7D7D");
    public static readonly IBrush Surface = Brush("#FFFFFF");
    public static readonly IBrush SurfaceMuted = Brush("#F8F8F8");
    public static readonly IBrush Border = Brush("#E9E9E9");
    public static readonly IBrush BorderStrong = Brush("#C4C4C4");
    public static readonly IBrush Success = Brush("#007824");
    public static readonly IBrush Danger = Brush("#CD1526");

    private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));
}
