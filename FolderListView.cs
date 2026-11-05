using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using ArcScrollArea = TKCode.Arc.ScrollArea;
using ArcTooltip = TKCode.Arc.Tooltip;
using ArcLucideIcon = TKCode.Arc.Icons.Lucide.LucideIcon;
using ArcLucideIconKind = TKCode.Arc.Icons.Lucide.LucideIconKind;

namespace FileDiff;

/// <summary>
/// 文件夹比较的两栏列表：两侧各一列名字、大小和修改时间，中间是 BC 的符号列（=、&lt;&gt;、&gt;&gt;、&lt;&lt;）。
/// 行的颜色照 BC 的报告：不同为红、较旧为灰、单侧存在为紫、相同为黑；
/// 目录行按名字折叠/展开整棵子树，双击两侧都有的文件直接做文本比较。
/// </summary>
internal sealed class FolderListView : Grid
{
    private static readonly IBrush Backdrop = Hex("#FFFFFF");
    private static readonly IBrush Chrome = Hex("#F5F5F5");
    private static readonly IBrush Line = Hex("#D4D4D4");
    private static readonly IBrush Strip = Hex("#FAFAFA");
    private static readonly IBrush HoverBack = Hex("#EEF4FD");
    private static readonly IBrush SelectedBack = Hex("#DCE9FA");
    private static readonly IBrush Text = Hex("#1F1F1F");
    private static readonly IBrush Muted = Hex("#6E6E6E");
    private static readonly IBrush Orphan = Hex("#6A00D5");
    private static readonly IBrush Different = Hex("#FF0000");
    private static readonly IBrush Older = Hex("#808080");
    // BC 报告里目录的大小和时间一律是这层灰（DirSegInfo），和文件的状态色无关。
    private static readonly IBrush Segment = Hex("#969696");

    private const double HeaderHeight = 26;
    private const double RowHeight = 21;
    private const double IndentWidth = 15;

    private readonly StackPanel _leftRows = new();
    private readonly StackPanel _rightRows = new();
    private readonly StackPanel _middleRows = new();
    private readonly ArcScrollArea _leftScroll = new()
    {
        Orientation = TKCode.Arc.ScrollAreaOrientation.Vertical,
        OverlayScrollbarMode = TKCode.Arc.ScrollAreaScrollbarMode.Always,
        FadeLength = 0
    };
    private readonly ArcScrollArea _rightScroll = new()
    {
        Orientation = TKCode.Arc.ScrollAreaOrientation.Vertical,
        OverlayScrollbarMode = TKCode.Arc.ScrollAreaScrollbarMode.Always,
        FadeLength = 0
    };
    private readonly ArcScrollArea _middleScroll = new()
    {
        Orientation = TKCode.Arc.ScrollAreaOrientation.Vertical,
        OverlayScrollbarMode = TKCode.Arc.ScrollAreaScrollbarMode.Always,
        FadeLength = 0
    };
    private readonly TextBlock _leftBase = new() { FontSize = 11, Foreground = Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _rightBase = new() { FontSize = 11, Foreground = Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _symbolHeader = new() { FontSize = 10, Foreground = Muted, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    private FolderComparison? _comparison;
    private IReadOnlyList<FolderEntry> _rows = [];
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RowView> _views = [];
    private int _selected = -1;
    private bool _syncing;

    public FolderListView()
    {
        RowDefinitions = new RowDefinitions($"{HeaderHeight},*");
        ColumnDefinitions = new ColumnDefinitions("*,46,*");
        Background = Backdrop;

        var header = new Grid { Background = Chrome, ColumnDefinitions = new ColumnDefinitions("*,46,*") };
        header.Children.Add(BaseBar(_leftBase));
        var middle = new Border
        {
            Child = _symbolHeader, BorderBrush = Line,
            BorderThickness = new Thickness(1, 0, 1, 0), Background = Chrome
        };
        Grid.SetColumn(middle, 1); header.Children.Add(middle);
        var rightHeader = BaseBar(_rightBase);
        Grid.SetColumn(rightHeader, 2); header.Children.Add(rightHeader);
        header.Children.Add(new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), IsHitTestVisible = false });
        Children.Add(header);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,46,*") };
        body.Children.Add(LeftColumn(_leftScroll, _leftRows));
        var center = new Border
        {
            Child = _middleScroll, BorderBrush = Line, BorderThickness = new Thickness(1, 0, 1, 0), Background = Backdrop
        };
        _middleScroll.Content = _middleRows;
        Grid.SetColumn(center, 1); body.Children.Add(center);
        var rightColumn = LeftColumn(_rightScroll, _rightRows);
        Grid.SetColumn(rightColumn, 2); body.Children.Add(rightColumn);
        Grid.SetRow(body, 1); Children.Add(body);

        // 两侧必须一起滚：同一行在两边是同一条记录，滚错位就读不成对比。
        _leftScroll.ScrollChanged += (_, _) => Sync(_leftScroll, _rightScroll);
        _rightScroll.ScrollChanged += (_, _) => Sync(_rightScroll, _leftScroll);
        _middleScroll.ScrollChanged += (_, _) => Sync(_middleScroll, _leftScroll);
    }

    /// <summary>双击两侧都有的文件：交给窗口做文本比较。</summary>
    public event Action<FolderEntry>? EntryActivated;

    /// <summary>键盘或鼠标换行时把当前行报出去，状态栏据此显示路径。</summary>
    public event Action<FolderEntry?>? SelectionMoved;

    public FolderEntry? SelectedEntry => _selected >= 0 && _selected < _rows.Count ? _rows[_selected] : null;

    private static Control LeftColumn(ScrollViewer scroll, StackPanel rows)
    {
        scroll.Content = rows;
        return new Border { Child = scroll, Background = Backdrop };
    }

    private static Control BaseBar(TextBlock label)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = "根目录", FontSize = 10, Foreground = Muted, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(label);
        return new Border { Child = stack, Background = Chrome };
    }

    private static IBrush Hex(string value) => new SolidColorBrush(Color.Parse(value));

    /// <summary>换一份比较结果：收起状态清空，从第一行开始。</summary>
    public void SetComparison(FolderComparison? comparison, string? leftRoot, string? rightRoot)
    {
        _comparison = comparison;
        _collapsed.Clear();
        _selected = -1;
        SetRoots(leftRoot, rightRoot);
        Rebuild();
    }

    private void SetRoots(string? leftRoot, string? rightRoot)
    {
        _leftBase.Text = leftRoot ?? "尚未选择";
        _rightBase.Text = rightRoot ?? "尚未选择";
        ArcTooltip.SetTip(_leftBase, leftRoot);
        ArcTooltip.SetTip(_rightBase, rightRoot);
    }

    /// <summary>离开列表前记下展开状态、当前行和滚动位置，回来时照原样摆回去。</summary>
    public FolderListViewState Capture() =>
        new(_collapsed.ToList(), SelectedEntry?.RelativePath, _leftScroll.Offset.Y);

    /// <summary>
    /// 按记下的现场把列表摆回原样：换回那份结果和根目录，先恢复折叠状态再重建，
    /// 按路径找回当前行，等布局完成后回到原来的滚动位置。
    /// </summary>
    public void Restore(FolderComparison comparison, string? leftRoot, string? rightRoot,
        FolderListViewState state, FolderDisplay display)
    {
        _comparison = comparison;
        SetRoots(leftRoot, rightRoot);
        _collapsed.Clear();
        foreach (var path in state.Collapsed) _collapsed.Add(path);
        _display = display;
        _selected = -1;
        Rebuild(display);
        if (state.SelectedPath is not null)
        {
            var index = _rows.ToList().FindIndex(entry =>
                string.Equals(entry.RelativePath, state.SelectedPath, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) Select(index, reveal: false);
        }
        if (state.OffsetY > 0)
        {
            // 重建后行还没排版，Extent 还是空的，直接设偏移会被夹回 0；等排版完成再滚。
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _leftScroll.Offset = _leftScroll.Offset.WithY(state.OffsetY),
                Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>按当前折叠状态和显示方式重排行；行号会变，选中行尽量跟着同一条路径走。</summary>
    public void Refresh(FolderDisplay display)
    {
        var keep = SelectedEntry?.RelativePath;
        Rebuild(display);
        if (keep is not null)
        {
            var index = _rows.ToList().FindIndex(entry => string.Equals(entry.RelativePath, keep, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) Select(index, reveal: false);
        }
    }

    private void Rebuild(FolderDisplay display = FolderDisplay.All)
    {
        _leftRows.Children.Clear();
        _rightRows.Children.Clear();
        _middleRows.Children.Clear();
        _views.Clear();
        _rows = _comparison?.Filter(display) ?? [];
        _symbolHeader.Text = "";
        if (_comparison is null)
        {
            return;
        }
        // 折叠：某个目录收起时，比它深的所有行都藏起来。
        var hideDepth = -1;
        var hidden = new List<bool>();
        foreach (var entry in _rows)
        {
            if (hideDepth >= 0 && entry.Depth > hideDepth)
            {
                hidden.Add(true);
                continue;
            }
            hideDepth = -1;
            hidden.Add(false);
            if (entry.IsFolder && _collapsed.Contains(entry.RelativePath)) hideDepth = entry.Depth;
        }
        var visible = new List<FolderEntry>();
        for (var i = 0; i < _rows.Count; i++) if (!hidden[i]) visible.Add(_rows[i]);
        _rows = visible;

        for (var i = 0; i < _rows.Count; i++)
        {
            var entry = _rows[i];
            var index = i;
            var left = SideView(entry, true, index);
            var right = SideView(entry, false, index);
            var middle = SymbolView(entry, index);
            _leftRows.Children.Add(left.Root);
            _rightRows.Children.Add(right.Root);
            _middleRows.Children.Add(middle.Root);
            _views.Add(new RowView(left, right, middle));
        }
        _selected = Math.Clamp(_selected, -1, _rows.Count - 1);
        if (_selected >= 0) ApplySelection();
        else if (_rows.Count > 0) Select(0, reveal: false);
        else SelectionMoved?.Invoke(null);
    }

    private (Control Root, Border Frame, TextBlock Name, TextBlock Info, Border Chevron, Border Icon) SideView(
        FolderEntry entry, bool left, int index)
    {
        var state = left ? entry.Sides.Left : entry.Sides.Right;
        var present = state != FolderSide.Absent;
        // BC 的目录行不着状态色（恒为黑），大小和时间则一律走 DirSegInfo 的灰。
        var foreground = entry.IsFolder ? (present ? Text : Brushes.Transparent) : ColorFor(state);
        var name = new TextBlock
        {
            Text = entry.Name + (left ? entry.LeftAttributes : entry.RightAttributes),
            FontSize = 12, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var info = new TextBlock
        {
            Text = InfoText(entry, left), FontSize = 11,
            Foreground = entry.IsFolder ? Segment : foreground,
            Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        // 目录前的三角：展开朝下，收起朝右；文件留空但同样宽，名字才对得齐。这一侧没有的项不画三角。
        var chevron = new Border
        {
            Width = 11, Height = 11, VerticalAlignment = VerticalAlignment.Center,
            Child = entry.IsFolder && present ? ChevronGeometry(entry) : null
        };
        var icon = new Border
        {
            Width = 14, Height = 14, Margin = new Thickness(3, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new ArcLucideIcon
            {
                Kind = entry.IsFolder ? ArcLucideIconKind.Folder : ArcLucideIconKind.File,
                Size = 14, Foreground = foreground
            }
        };
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(IndentWidth * entry.Depth, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        line.Children.Add(chevron);
        line.Children.Add(icon);
        line.Children.Add(name);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(line);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        var frame = new Border
        {
            Height = RowHeight, Background = Brushes.Transparent, Child = grid,
            Padding = new Thickness(4, 0, 2, 0), BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(18, 0, 0, 0))
        };
        frame.PointerPressed += (_, e) => OnRowPressed(e, index);
        frame.DoubleTapped += (_, e) =>
        {
            if (entry.IsFolder) Toggle(entry);
            else if (entry.IsComparable) EntryActivated?.Invoke(entry);
            e.Handled = true;
        };
        if (entry.IsFolder)
        {
            chevron.PointerPressed += (_, e) => { Toggle(entry); e.Handled = true; };
            ArcTooltip.SetTip(chevron, _collapsed.Contains(entry.RelativePath) ? "展开此目录" : "收起此目录");
        }
        ArcTooltip.SetTip(frame, entry.RelativePath + (present ? "" : "（此侧没有）"));
        return (frame, frame, name, info, chevron, icon);
    }

    private Control ChevronGeometry(FolderEntry entry) => new ArcLucideIcon
    {
        Kind = _collapsed.Contains(entry.RelativePath) ? ArcLucideIconKind.ChevronRight : ArcLucideIconKind.ChevronDown,
        Size = 11, StrokeThickness = 2, Foreground = Muted,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static string InfoText(FolderEntry entry, bool left)
    {
        if (left ? !entry.HasLeft : !entry.HasRight) return "";
        var size = left ? entry.LeftSize : entry.RightSize;
        var time = left ? entry.LeftTime : entry.RightTime;
        // 大小和时间都按 BC 的列排；状态较旧的一侧灰掉，两种信息都读得出来。
        return $"{FolderSize.Format(size)}  {time:yyyy/MM/dd HH:mm}";
    }

    private (Control Root, Border Frame, ArcLucideIcon? Icon) SymbolView(FolderEntry entry, int index)
    {
        ArcLucideIcon? icon = entry.IsFolder ? null : new ArcLucideIcon
        {
            Kind = entry.Status switch
            {
                FolderStatus.Same => ArcLucideIconKind.Equal,
                FolderStatus.Different => ArcLucideIconKind.ArrowLeftRight,
                FolderStatus.LeftNewer or FolderStatus.LeftOnly => ArcLucideIconKind.ChevronsRight,
                _ => ArcLucideIconKind.ChevronsLeft
            },
            Size = 14, Foreground = SymbolBrush(entry),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var frame = new Border
        {
            Height = RowHeight, Background = Brushes.Transparent, Child = icon,
            BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = new SolidColorBrush(Color.FromArgb(18, 0, 0, 0))
        };
        frame.PointerPressed += (_, e) => OnRowPressed(e, index);
        frame.DoubleTapped += (_, e) =>
        {
            if (entry.IsFolder) Toggle(entry);
            else if (entry.IsComparable) EntryActivated?.Invoke(entry);
            e.Handled = true;
        };
        return (frame, frame, icon);
    }

    private static IBrush SymbolBrush(FolderEntry entry) => entry.Status switch
    {
        FolderStatus.Same => Muted,
        FolderStatus.LeftNewer or FolderStatus.RightNewer => Different,
        _ => Orphan
    };

    /// <summary>颜色照 BC 报告：较新红、较旧灰、单侧存在紫，其余黑。</summary>
    private static IBrush ColorFor(FolderSide side) => side switch
    {
        FolderSide.Absent => Brushes.Transparent,
        FolderSide.Orphan => Orphan,
        FolderSide.Different or FolderSide.Newer => Different,
        FolderSide.Older => Older,
        _ => Text
    };

    private void Toggle(FolderEntry entry)
    {
        if (!_collapsed.Add(entry.RelativePath)) _collapsed.Remove(entry.RelativePath);
        RefreshCurrentDisplay();
    }

    private FolderDisplay _display = FolderDisplay.All;

    /// <summary>切换显示方式后重建，收起的目录保持收起。</summary>
    public void Display(FolderDisplay display)
    {
        _display = display;
        Refresh(display);
    }

    private void RefreshCurrentDisplay() => Refresh(_display);

    /// <summary>整棵树展开：清空收起集合。</summary>
    public void ExpandAll()
    {
        _collapsed.Clear();
        RefreshCurrentDisplay();
    }

    /// <summary>只留顶层：所有目录都收起，第一层文件和目录还看得见。</summary>
    public void CollapseAll()
    {
        _collapsed.Clear();
        foreach (var entry in _comparison?.Entries ?? [])
        {
            if (entry.IsFolder) _collapsed.Add(entry.RelativePath);
        }
        RefreshCurrentDisplay();
    }

    private void OnRowPressed(PointerEventArgs e, int index)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) Select(index, reveal: true);
    }

    /// <summary>把某一行设为当前行并滚动到可见处。</summary>
    public void Select(int index, bool reveal)
    {
        if (_rows.Count == 0) return;
        index = Math.Clamp(index, 0, _rows.Count - 1);
        _selected = index;
        ApplySelection();
        if (reveal) Reveal(index);
        SelectionMoved?.Invoke(_rows[index]);
    }

    /// <summary>键盘上下移动，一次一行，两端不绕圈。</summary>
    public bool Move(int delta)
    {
        if (_rows.Count == 0) return false;
        var target = _selected < 0 ? 0 : _selected + delta;
        if (target < 0 || target >= _rows.Count) return false;
        Select(target, reveal: true);
        return true;
    }

    /// <summary>跳到下一个有差异的行，到头后回到开头，和 BC 的"下一差异"一样循环。</summary>
    public bool MoveToChanged(int direction)
    {
        if (_rows.Count == 0) return false;
        var start = _selected < 0 ? (direction > 0 ? -1 : 0) : _selected;
        for (var step = 1; step <= _rows.Count; step++)
        {
            var index = ((start + direction * step) % _rows.Count + _rows.Count) % _rows.Count;
            if (!_rows[index].IsChanged) continue;
            Select(index, reveal: true);
            return true;
        }
        return false;
    }

    /// <summary>把当前行所在的目录展开，再把它滚进视口。</summary>
    public void Reveal(FolderEntry entry)
    {
        var current = new List<string>();
        foreach (var segment in entry.RelativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            current.Add(segment);
            _collapsed.Remove(string.Join('\\', current));
        }
        if (_rows.Contains(entry)) return;
        Rebuild();
        var index = _rows.ToList().FindIndex(item => string.Equals(item.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _selected = index;
            ApplySelection();
            Reveal(index);
        }
    }

    private void Reveal(int index)
    {
        var top = index * RowHeight;
        var bottom = top + RowHeight;
        if (top < _leftScroll.Offset.Y) _leftScroll.Offset = _leftScroll.Offset.WithY(top);
        else if (bottom > _leftScroll.Offset.Y + _leftScroll.Viewport.Height)
            _leftScroll.Offset = _leftScroll.Offset.WithY(bottom - _leftScroll.Viewport.Height);
    }

    private void ApplySelection()
    {
        for (var i = 0; i < _views.Count; i++)
        {
            var background = i == _selected ? SelectedBack : Brushes.Transparent;
            _views[i].Left.Frame.Background = background;
            _views[i].Right.Frame.Background = background;
            _views[i].Middle.Frame.Background = background;
        }
    }

    private void Sync(ScrollViewer source, ScrollViewer target)
    {
        if (_syncing) return;
        _syncing = true;
        try { target.Offset = target.Offset.WithY(source.Offset.Y); }
        finally { _syncing = false; }
    }

    /// <summary>窗口级键盘：上下换行、回车打开、左右折叠展开，Esc 不处理（留给窗口）。</summary>
    public void HandleKey(KeyEventArgs e)
    {
        var entry = SelectedEntry;
        switch (e.Key)
        {
            case Key.Down: e.Handled = Move(1); break;
            case Key.Up: e.Handled = Move(-1); break;
            case Key.Home: Select(0, reveal: true); e.Handled = true; break;
            case Key.End: Select(_rows.Count - 1, reveal: true); e.Handled = true; break;
            case Key.Enter when entry is { IsComparable: true }: EntryActivated?.Invoke(entry); e.Handled = true; break;
            case Key.Enter when entry is { IsFolder: true }: Toggle(entry); e.Handled = true; break;
            case Key.Left when entry is { IsFolder: true } && !_collapsed.Contains(entry.RelativePath): Toggle(entry); e.Handled = true; break;
            case Key.Right when entry is { IsFolder: true } && _collapsed.Contains(entry.RelativePath): Toggle(entry); e.Handled = true; break;
        }
    }

    private sealed record RowView(
        (Control Root, Border Frame, TextBlock Name, TextBlock Info, Border Chevron, Border Icon) Left,
        (Control Root, Border Frame, TextBlock Name, TextBlock Info, Border Chevron, Border Icon) Right,
        (Control Root, Border Frame, ArcLucideIcon? Icon) Middle);
}

/// <summary>列表的现场快照：收起的目录、当前行的相对路径、纵向滚动位置。</summary>
internal sealed record FolderListViewState(IReadOnlyList<string> Collapsed, string? SelectedPath, double OffsetY);

/// <summary>BC 报告里的大小写法：字节数不换算（目录行是整棵子树的和），只有超过 1 KB 才进位。</summary>
internal static class FolderSize
{
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " K",
        _ => (bytes / 1024.0 / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " M"
    };
}
