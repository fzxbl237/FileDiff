using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace FileDiff.Controls.Motion;

/// <summary>How much motion the theme allows.</summary>
public enum ArcMotionLevel
{
    /// <summary>No transitions or animations.</summary>
    Off = 0,

    /// <summary>Opacity and color changes only; springs jump to their end values.</summary>
    Reduced = 1,

    /// <summary>All motion.</summary>
    Full = 2
}

/// <summary>
/// Motion level policy. <see cref="LevelProperty"/> is inherited: set it on a window or any container.
/// Controls whose theme sets <c>arc:ArcMotion.Tracks="True"</c> get the pseudo-classes <c>:motion-full</c> or
/// <c>:motion-reduced</c> after they are loaded (so the first frame never animates); themes declare their
/// transitions under those pseudo-classes. With level Off neither class is present and nothing animates.
/// </summary>
public sealed class ArcMotion
{
    public const string FullPseudoClass = ":motion-full";
    public const string ReducedPseudoClass = ":motion-reduced";

    private ArcMotion()
    {
    }

    public static readonly AttachedProperty<ArcMotionLevel> LevelProperty =
        AvaloniaProperty.RegisterAttached<ArcMotion, AvaloniaObject, ArcMotionLevel>("Level", ArcMotionLevel.Full, inherits: true);

    public static readonly AttachedProperty<bool> TracksProperty =
        AvaloniaProperty.RegisterAttached<ArcMotion, Control, bool>("Tracks");

    private static readonly AttachedProperty<bool> ActiveProperty =
        AvaloniaProperty.RegisterAttached<ArcMotion, Control, bool>("Active");

    static ArcMotion()
    {
        TracksProperty.Changed.AddClassHandler<Control>(OnTracksChanged);
        LevelProperty.Changed.AddClassHandler<Control>((c, _) => Refresh(c));
    }

    public static ArcMotionLevel GetLevel(AvaloniaObject element) => element.GetValue(LevelProperty);

    public static void SetLevel(AvaloniaObject element, ArcMotionLevel value) => element.SetValue(LevelProperty, value);

    public static bool GetTracks(Control element) => element.GetValue(TracksProperty);

    public static void SetTracks(Control element, bool value) => element.SetValue(TracksProperty, value);

    /// <summary>Effective level for <paramref name="visual"/>: the inherited level (the app theme runs at Full).</summary>
    public static ArcMotionLevel GetEffectiveLevel(AvaloniaObject visual) => visual.GetValue(LevelProperty);

    /// <summary>True when spatial motion (springs, travel, scale) should run.</summary>
    public static bool IsFull(AvaloniaObject visual) => GetEffectiveLevel(visual) == ArcMotionLevel.Full;

    private static void OnTracksChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            control.Loaded += OnLoaded;
            control.Unloaded += OnUnloaded;
            if (control.IsLoaded)
            {
                Activate(control);
            }
        }
        else
        {
            control.Loaded -= OnLoaded;
            control.Unloaded -= OnUnloaded;
            Deactivate(control);
        }
    }

    private static void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control)
        {
            // Posted so that values applied while loading settle before transitions are enabled.
            Dispatcher.UIThread.Post(() => Activate(control), DispatcherPriority.Background);
        }
    }

    private static void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control)
        {
            Deactivate(control);
        }
    }

    private static void Activate(Control control)
    {
        if (!control.IsLoaded || control.GetValue(ActiveProperty))
        {
            return;
        }
        control.SetValue(ActiveProperty, true);
        Refresh(control);
    }

    private static void Deactivate(Control control)
    {
        control.SetValue(ActiveProperty, false);
        var pseudo = (IPseudoClasses)control.Classes;
        pseudo.Set(FullPseudoClass, false);
        pseudo.Set(ReducedPseudoClass, false);
    }

    private static void Refresh(Control control)
    {
        if (!control.GetValue(TracksProperty) || !control.GetValue(ActiveProperty))
        {
            return;
        }
        var level = GetEffectiveLevel(control);
        var pseudo = (IPseudoClasses)control.Classes;
        pseudo.Set(FullPseudoClass, level == ArcMotionLevel.Full);
        pseudo.Set(ReducedPseudoClass, level == ArcMotionLevel.Reduced);
    }
}
