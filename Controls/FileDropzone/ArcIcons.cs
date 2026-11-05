using System.Collections.Concurrent;
using Avalonia.Media;

namespace FileDiff.Controls;

/// <summary>The Lucide glyphs (24x24 viewBox, stroked) the dropzone uses, taken from Arc's built-in set.</summary>
public static partial class ArcIcons
{
    private static readonly ConcurrentDictionary<string, Geometry> s_cache = new(StringComparer.Ordinal);

    private static Geometry Get(string name, string data) => s_cache.GetOrAdd(name, static (_, d) => StreamGeometry.Parse(d), data);

    /// <summary>Parses arbitrary 24x24 SVG path data (cached by content).</summary>
    public static Geometry Parse(string data) => s_cache.GetOrAdd(data, static d => StreamGeometry.Parse(d));

    public static Geometry ArrowUp => Get("arrow-up", "M5 12l7-7 7 7 M12 19V5");
    public static Geometry CircleAlert => Get("circle-alert", "M2 12a10 10 0 1 0 20 0a10 10 0 1 0 -20 0z M12 8L12 12 M12 16L12.01 16");
    public static Geometry X => Get("x", "M18 6 6 18 M6 6l12 12");
    public static Geometry RotateCw => Get("rotate-cw", "M21 12a9 9 0 1 1-9-9c2.52 0 4.93 1 6.74 2.74L21 8 M21 3v5h-5");
    public static Geometry File => Get("file", "M6 22a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h8a2.4 2.4 0 0 1 1.704.706l3.588 3.588A2.4 2.4 0 0 1 20 8v12a2 2 0 0 1-2 2z M14 2v5a1 1 0 0 0 1 1h5");
    public static Geometry Folder => Get("folder", "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z");
    public static Geometry FileArchive => Get("file-archive", "M13.659 22H18a2 2 0 0 0 2-2V8a2.4 2.4 0 0 0-.706-1.706l-3.588-3.588A2.4 2.4 0 0 0 14 2H6a2 2 0 0 0-2 2v11.5 M14 2v5a1 1 0 0 0 1 1h5 M8 12v-1 M8 18v-2 M8 7V6 M6 20a2 2 0 1 0 4 0a2 2 0 1 0 -4 0z");
    public static Geometry FileImage => Get("file-image", "M6 22a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h8a2.4 2.4 0 0 1 1.704.706l3.588 3.588A2.4 2.4 0 0 1 20 8v12a2 2 0 0 1-2 2z M14 2v5a1 1 0 0 0 1 1h5 M8 12a2 2 0 1 0 4 0a2 2 0 1 0 -4 0z M20 17l-1.296-1.296a2.41 2.41 0 0 0-3.408 0L9 22");
    public static Geometry FilePlay => Get("file-play", "M6 22a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h8a2.4 2.4 0 0 1 1.704.706l3.588 3.588A2.4 2.4 0 0 1 20 8v12a2 2 0 0 1-2 2z M14 2v5a1 1 0 0 0 1 1h5 M15.033 13.44a.647.647 0 0 1 0 1.12l-4.065 2.352a.645.645 0 0 1-.968-.56v-4.704a.645.645 0 0 1 .967-.56z");
    public static Geometry FileText => Get("file-text", "M6 22a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h8a2.4 2.4 0 0 1 1.704.706l3.588 3.588A2.4 2.4 0 0 1 20 8v12a2 2 0 0 1-2 2z M14 2v5a1 1 0 0 0 1 1h5 M10 9H8 M16 13H8 M16 17H8");
}
