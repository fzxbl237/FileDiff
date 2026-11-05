using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FileDiff.Controls;

/// <summary>Compiled form of <c>FileDropzoneTheme.axaml</c>; AOT publishing needs precompiled XAML.</summary>
public partial class FileDropzoneTheme : ResourceDictionary
{
    public FileDropzoneTheme()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
