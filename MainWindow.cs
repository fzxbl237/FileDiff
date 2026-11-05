using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using DiffPlex.DiffBuilder.Model;
using FileDiff.Controls;
using ArcFileDropzone = TKCode.Arc.FileDropzone;
using ArcSegmentedControl = TKCode.Arc.SegmentedControl;
using ArcButton = TKCode.Arc.Button;
using ArcCheckbox = TKCode.Arc.Checkbox;
using ArcSelect = TKCode.Arc.Select;
using ArcBadge = TKCode.Arc.Badge;
using ArcScrollArea = TKCode.Arc.ScrollArea;
using ArcTooltip = TKCode.Arc.Tooltip;

namespace FileDiff;

public sealed partial class MainWindow : Window
{
    private static readonly IBrush PageBackdrop = Hex("#ECECEC");
    private static readonly IBrush TextBrush = Hex("#1F1F1F");
    private static readonly IBrush MutedBrush = Hex("#6E6E6E");
    private static readonly IBrush ChangedBrush = Hex("#B71C1C");

    private readonly TextBlock _leftName;
    private readonly TextBlock _rightName;
    private readonly TextBlock _leftInfo;
    private readonly TextBlock _rightInfo;
    private readonly ArcBadge _leftBadge;
    private readonly ArcBadge _rightBadge;
    private readonly TextBlock _leftPosition;
    private readonly TextBlock _rightPosition;
    private readonly TextBlock _pageIndicator;
    private readonly TextBlock _status;
    private readonly TextBlock _zoomLabel;
    private readonly ListBox _pages;
    private readonly Grid _leftSlot;
    private readonly Grid _rightSlot;
    private readonly PdfPageView _leftView = new() { IsLeft = true };
    private readonly PdfPageView _rightView = new();
    private readonly ArcScrollArea _leftScroll;
    private readonly ArcScrollArea _rightScroll;
    private readonly ArcCheckbox _sync;
    private readonly ArcCheckbox _highlight;
    private readonly ArcSegmentedControl _rowFilterMode;
    private readonly ArcCheckbox _ignoreWhitespace;
    private readonly ArcCheckbox _usePdfText;
    private readonly ArcFileDropzone _leftZone;
    private readonly ArcFileDropzone _rightZone;
    // Arc FileDropzone 仅处理文件；文件夹模式保留本地目录拖放区。
    private readonly FileDropzone _leftFolderZone = FolderDropzone("打开左侧文件夹");
    private readonly FileDropzone _rightFolderZone = FolderDropzone("打开右侧文件夹");
    private readonly Control _leftPane;
    private readonly Control _rightPane;
    private readonly Control _sidebar;
    private readonly GridSplitter _divider;
    private readonly Grid _content;
    private readonly ArcButton _compare;
    private readonly ArcButton _cancel;
    private readonly ArcSegmentedControl _comparisonMode;
    private readonly ArcButton _backToFolder;
    private readonly ArcSelect _criteria;
    private readonly ArcSelect _folderDisplay;
    private readonly StackPanel _folderOptions;
    private readonly List<Control> _fileOnly = [];
    private readonly FolderListView _folderView = new();
    private readonly Border _folderHost;
    private Comparison? _result;
    private FolderComparison? _folderResult;
    private FolderViewSnapshot? _folderSnapshot;
    private string? _leftPath, _rightPath;
    private CancellationTokenSource? _cts;
    private int _currentPage = -1;
    private bool _syncing, _busy, _closed, _continuous, _folderMode, _returnPending;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _content = FindRequiredControl<Grid>("ContentGrid");
        _sidebar = FindRequiredControl<Border>("Sidebar");
        _leftPane = FindRequiredControl<Border>("LeftPane");
        _rightPane = FindRequiredControl<Border>("RightPane");
        _leftSlot = FindRequiredControl<Grid>("LeftSlot");
        _rightSlot = FindRequiredControl<Grid>("RightSlot");
        _divider = FindRequiredControl<GridSplitter>("Divider");
        _folderHost = FindRequiredControl<Border>("FolderHost");
        _leftName = FindRequiredControl<TextBlock>("LeftName");
        _rightName = FindRequiredControl<TextBlock>("RightName");
        _leftInfo = FindRequiredControl<TextBlock>("LeftInfo");
        _rightInfo = FindRequiredControl<TextBlock>("RightInfo");
        _leftBadge = FindRequiredControl<ArcBadge>("LeftBadge");
        _rightBadge = FindRequiredControl<ArcBadge>("RightBadge");
        _leftPosition = FindRequiredControl<TextBlock>("LeftPosition");
        _rightPosition = FindRequiredControl<TextBlock>("RightPosition");
        _pageIndicator = FindRequiredControl<TextBlock>("PageIndicator");
        _status = FindRequiredControl<TextBlock>("StatusText");
        _pages = FindRequiredControl<ListBox>("PagesList");
        _zoomLabel = FindRequiredControl<TextBlock>("ZoomLabel");
        _leftScroll = FindRequiredControl<ArcScrollArea>("LeftScrollArea");
        _rightScroll = FindRequiredControl<ArcScrollArea>("RightScrollArea");
        _sync = FindRequiredControl<ArcCheckbox>("SyncCheckbox");
        _highlight = FindRequiredControl<ArcCheckbox>("HighlightCheckbox");
        _rowFilterMode = FindRequiredControl<ArcSegmentedControl>("RowFilterMode");
        _ignoreWhitespace = FindRequiredControl<ArcCheckbox>("IgnoreWhitespaceCheckbox");
        _usePdfText = FindRequiredControl<ArcCheckbox>("PdfTextModeCheckbox");
        _leftZone = FindRequiredControl<ArcFileDropzone>("LeftFileDropzone");
        _rightZone = FindRequiredControl<ArcFileDropzone>("RightFileDropzone");
        _compare = FindRequiredControl<ArcButton>("CompareButton");
        _cancel = FindRequiredControl<ArcButton>("CancelButton");
        _backToFolder = FindRequiredControl<ArcButton>("BackToFolderButton");
        _comparisonMode = FindRequiredControl<ArcSegmentedControl>("ComparisonMode");
        _criteria = FindRequiredControl<ArcSelect>("CriteriaSelect");
        _folderDisplay = FindRequiredControl<ArcSelect>("FolderDisplaySelect");
        _folderOptions = FindRequiredControl<StackPanel>("FolderOptions");
        _leftZone.Accept = TextDocumentConverter.Accept;
        _rightZone.Accept = TextDocumentConverter.Accept;
        Styles.Add(new Style(s => s.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(4)),
                new Setter(Layoutable.MarginProperty, new Thickness(4, 2))
            }
        });
        _pages.ItemTemplate = new FuncDataTemplate<PageDifference>((page, _) =>
        {
            // Virtualized containers can clear their content during recycling.
            if (page is null) return new TextBlock();
            return new TextBlock
            {
                Text = page.ToString(),
                Foreground = page.IsChanged ? ChangedBrush : MutedBrush,
                FontSize = 12,
                FontWeight = page.IsChanged ? FontWeight.SemiBold : FontWeight.Normal,
                Margin = new Thickness(8, 3)
            };
        });

        _leftScroll.Content = _leftView;
        _rightScroll.Content = _rightView;
        _folderHost.Child = _folderView;
        PopulateSlot(_leftSlot, _leftFolderZone, _leftPane);
        PopulateSlot(_rightSlot, _rightFolderZone, _rightPane);
        WireToolbar();
        UpdateSlots();

        _leftZone.FilesChanged += (_, e) => _ = ApplyFilesAsync(true, e.Files);
        _rightZone.FilesChanged += (_, e) => _ = ApplyFilesAsync(false, e.Files);
        _leftFolderZone.FilesChanged += (_, e) => _ = ApplyFilesAsync(true, e.Files, e.Folders);
        _rightFolderZone.FilesChanged += (_, e) => _ = ApplyFilesAsync(false, e.Files, e.Folders);
        _leftZone.FilesRejected += (_, e) => _status.Text = e.Message;
        _rightZone.FilesRejected += (_, e) => _status.Text = e.Message;
        _leftFolderZone.FilesRejected += (_, e) => _status.Text = e.Message;
        _rightFolderZone.FilesRejected += (_, e) => _status.Text = e.Message;

        _folderView.EntryActivated += entry => _ = OpenEntryAsync(entry);
        _folderView.SelectionMoved += entry =>
        {
            if (entry is not null) _status.Text = $"{entry.RelativePath} · {DescribeEntry(entry)}";
        };
        // 列表自己处理上下、回车、左右；窗口只把按键转过去，别让滚动条抢走。
        _folderHost.AddHandler(KeyDownEvent, OnFolderKeyDown, RoutingStrategies.Tunnel);
        // 从文件夹比较点进来的文件比较，Esc 就是退回去，和顶栏那个按钮一个意思。
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        _pages.SelectionChanged += (_, _) => { if (_pages.SelectedItem is PageDifference page) DisplayPage(page.Page); };
        _ignoreWhitespace.IsCheckedChanged += async (_, _) =>
        {
            if (_leftPath is not null && _rightPath is not null) await Compare();
        };
        ArcTooltip.SetTip(_ignoreWhitespace, "忽略行内空格和制表符，保留原文和换行；切换后自动重新比较。");
        _rowFilterMode.SelectionChanged += (_, _) =>
        {
            var filter = _rowFilterMode.SelectedValue?.ToString() switch
            {
                "diffs" => DiffRowFilter.Differences,
                "same" => DiffRowFilter.Same,
                _ => DiffRowFilter.All
            };
            _leftView.RowFilter = _rightView.RowFilter = filter;
            RefreshPages();
        };
        _highlight.IsCheckedChanged += (_, _) => _leftView.ShowHighlights = _rightView.ShowHighlights = _highlight.IsChecked == true;
        _leftView.CaretMoved += (line, column) => _leftPosition.Text = $"行:{line} 列:{column}";
        _rightView.CaretMoved += (line, column) => _rightPosition.Text = $"行:{line} 列:{column}";
        // 两侧行表必须一直一样长，一边展开某页，另一边就照做；否则同步滚动会错行。
        _leftView.PageToggled += line => _rightView.MirrorPageToggle(line);
        _rightView.PageToggled += line => _leftView.MirrorPageToggle(line);
        _leftScroll.AddHandler(PointerWheelChangedEvent, ZoomWithWheel, RoutingStrategies.Tunnel);
        _rightScroll.AddHandler(PointerWheelChangedEvent, ZoomWithWheel, RoutingStrategies.Tunnel);
        _leftScroll.ScrollChanged += (_, _) => SyncScroll(_leftScroll, _rightScroll);
        _rightScroll.ScrollChanged += (_, _) => SyncScroll(_rightScroll, _leftScroll);
        Closed += (_, _) => { _closed = true; _cts?.Cancel(); };
    }

    private T FindRequiredControl<T>(string name) where T : Control =>
        this.FindControl<T>(name) ?? throw new InvalidOperationException($"AXAML control '{name}' was not found.");

    private static IBrush Hex(string value) => new SolidColorBrush(Color.Parse(value));

    private void WireToolbar()
    {
        _compare.Click += async (_, _) => await Compare();
        _cancel.Click += (_, _) => _cts?.Cancel();
        _backToFolder.Click += (_, _) => ReturnToFolder();
        FindRequiredControl<ArcButton>("SwapButton").Click += async (_, _) =>
        {
            if (_busy) return;
            (_leftPath, _rightPath) = (_rightPath, _leftPath);
            SwapZones();
            UpdateNames();
            ClearResult("文件已交换，请选择两个文件后比较。");
            if (_leftPath is not null && _rightPath is not null) await Compare();
        };

        _comparisonMode.SelectedValue = "file";
        _comparisonMode.SelectionChanged += (_, _) =>
        {
            var folder = Equals(_comparisonMode.SelectedValue, "folder");
            if (_busy)
            {
                _comparisonMode.SelectedValue = _folderMode ? "folder" : "file";
                return;
            }
            if (folder && _folderSnapshot is not null && !_folderMode) ReturnToFolder();
            else SetFolderMode(folder);
        };

        _rowFilterMode.SelectedValue = "all";
        ArcTooltip.SetTip(_rowFilterMode, "all：显示所有行；diffs：显示差异行；same：显示相同行。");
        ArcTooltip.SetTip(_usePdfText, "仅对两侧均为 PDF 时生效：使用 PdfPig 提取全文并连续比较；关闭时按 PdfPig 页面坐标排版比较。RTF/Word 走内置解析，代码文本按原样读取。");
        ArcTooltip.SetTip(_zoomLabel, "页面缩放范围 25%–400%；Ctrl+鼠标滚轮缩放。");

        FindRequiredControl<ArcButton>("ExpandAllButton").Click += (_, _) => _folderView.ExpandAll();
        FindRequiredControl<ArcButton>("CollapseAllButton").Click += (_, _) => _folderView.CollapseAll();
        FindRequiredControl<ArcButton>("PreviousDiffButton").Click += (_, _) => Navigate(-1);
        FindRequiredControl<ArcButton>("NextDiffButton").Click += (_, _) => Navigate(1);
        FindRequiredControl<ArcButton>("ZoomOutButton").Click += (_, _) => SetZoom(_leftView.Zoom - 0.1);
        FindRequiredControl<ArcButton>("ZoomInButton").Click += (_, _) => SetZoom(_leftView.Zoom + 0.1);
        FindRequiredControl<ArcButton>("ZoomResetButton").Click += (_, _) => SetZoom(1);

        var removeLeft = FindRequiredControl<ArcButton>("RemoveLeftButton");
        removeLeft.Click += (_, _) => RemoveFile(true);
        ArcTooltip.SetTip(removeLeft, "移除旧版文件，重新选择。");
        var removeRight = FindRequiredControl<ArcButton>("RemoveRightButton");
        removeRight.Click += (_, _) => RemoveFile(false);
        ArcTooltip.SetTip(removeRight, "移除新版文件，重新选择。");

        _fileOnly.AddRange(
        [
            FindRequiredControl<Control>("RowFilterChip"),
            FindRequiredControl<Control>("IgnoreWhitespaceChip"),
            FindRequiredControl<Control>("PdfTextModeChip"),
            FindRequiredControl<Control>("ZoomOutButton"),
            FindRequiredControl<Control>("ZoomLabelHost"),
            FindRequiredControl<Control>("ZoomInButton"),
            FindRequiredControl<Control>("ZoomResetButton")
        ]);

        SetupCriteria();
        SetupFolderDisplay();
    }

    /// <summary>BC 的判定选项：默认、内容、大小、时间，对应脚本里的 criteria。</summary>
    private void SetupCriteria()
    {
        _criteria.SelectedIndex = 0;
        ArcTooltip.SetTip(_criteria,
            "默认和 BC 一样：修改时间差 3 秒以内比大小，超过 3 秒直接判新旧，不读内容。选“内容”才逐字节比对。");
        _criteria.SelectionChanged += async (_, _) =>
        {
            var criteria = Criteria();
            if (_folderResult is not null && _folderResult.Criteria != criteria) await Compare();
        };
    }

    private void SetupFolderDisplay()
    {
        _folderDisplay.SelectedIndex = 0;
        ArcTooltip.SetTip(_folderDisplay, "对应 BC 报告的三种显示：全部、仅差异（带上层目录）、无孤儿（去掉只有一侧存在的项）。");
        _folderDisplay.SelectionChanged += (_, _) =>
        {
            _folderView.Display((FolderDisplay)Math.Max(0, _folderDisplay.SelectedIndex));
            var entry = _folderView.SelectedEntry;
            if (entry is not null) _status.Text = $"{entry.RelativePath} · {DescribeEntry(entry)}";
        };
    }

    private FolderCriteria Criteria() => _criteria.SelectedIndex switch
    {
        1 => FolderCriteria.Content,
        2 => FolderCriteria.Size,
        3 => FolderCriteria.Timestamp,
        _ => FolderCriteria.SizeAndTimestamp
    };

    private static void PopulateSlot(Grid slot, FileDropzone folderZone, Control pane)
    {
        folderZone.VerticalAlignment = VerticalAlignment.Center;
        folderZone.Margin = new Thickness(20, 0);
        slot.Children.Add(folderZone);
        pane.IsVisible = false;
    }

    /// <summary>文件夹模式的拖放区：只收目录，点一下打开目录选择器。</summary>
    private static FileDropzone FolderDropzone(string label) => new()
    {
        AcceptFolders = true,
        Accept = null,
        AllowMultiple = false,
        MaxFiles = 1,
        CompactAt = 1,
        Label = label,
        Description = "拖入一个文件夹，或点击此处从本机选择",
        Note = "按 Beyond Compare 的规则逐层比较：名字配对、目录先行、默认比大小与修改时间",
    };

    private void OnFolderKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_folderMode) return;
        _folderView.HandleKey(e);
    }

    /// <summary>文件比较是从文件夹列表点进来的（顶栏有返回按钮）时，Esc 直接退回那份文件夹比较。</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !_backToFolder.IsVisible || e.Key != Key.Escape) return;
        e.Handled = true;
        ReturnToFolder();
    }

    private void UpdateSlots()
    {
        var both = _leftPath is not null && _rightPath is not null;
        // 文件夹模式选好两侧后由自己的一整块列表接管内容区；选之前仍然是左右两个拖放区。
        var list = _folderMode && both;
        _folderHost.IsVisible = list;
        _content.ColumnDefinitions[0].Width = new GridLength(!_folderMode && both && !_continuous ? 196 : 0);
        _content.ColumnDefinitions[2].Width = new GridLength(_folderMode ? 0 : both ? 5 : 0);
        _sidebar.IsVisible = !_folderMode && both && !_continuous;
        _leftZone.IsVisible = !_folderMode && _leftPath is null;
        _rightZone.IsVisible = !_folderMode && _rightPath is null;
        _leftFolderZone.IsVisible = _folderMode && !list && _leftPath is null;
        _rightFolderZone.IsVisible = _folderMode && !list && _rightPath is null;
        _leftPane.IsVisible = !_folderMode && _leftPath is not null;
        _rightPane.IsVisible = !_folderMode && _rightPath is not null;
        _divider.IsVisible = !_folderMode && both;
        foreach (var control in _fileOnly) control.IsVisible = !_folderMode;
        _folderOptions.IsVisible = _folderMode;
    }

    private void ApplyLayoutForMode()
    {
        UpdateSlots();
    }

    /// <summary>文件 / 文件夹两种比较：换模式时清掉另一种模式的选择和结果，避免两类路径混在一起。</summary>
    private void SetFolderMode(bool folder)
    {
        if (_busy || _folderMode == folder) return;
        _folderSnapshot = null;
        _folderMode = folder;
        _comparisonMode.SelectedValue = folder ? "folder" : "file";
        _compare.Content = folder ? "重新比较" : "开始比较";
        // 每一侧各留一个拖放区：文件模式放文件，文件夹模式放目录；换模式时把两套都清空。
        foreach (var zone in new[] { _leftZone, _rightZone })
        {
            foreach (var item in zone.Items.ToList()) zone.Remove(item);
        }
        foreach (var zone in new[] { _leftFolderZone, _rightFolderZone })
        {
            foreach (var item in zone.Items.ToList()) zone.Remove(item);
        }
        _leftPath = _rightPath = null;
        _folderResult = null;
        _folderView.SetComparison(null, null, null);
        _leftBadge.IsVisible = _rightBadge.IsVisible = false;
        _leftBadge.Content = _rightBadge.Content = folder ? "目录差异" : "文本差异";
        _backToFolder.IsVisible = false;
        _pageIndicator.Text = folder ? "文件夹比较" : "页:0/0";
        _status.Text = folder
            ? "选择左右两个文件夹：按 BC 的规则逐层配对，双击两侧都有的文件可继续做文本比较。"
            : "选择两个 PDF / RTF / Word / 代码文本文件自动比较，所有处理均在本机完成。";
        ClearResult(folder ? "请选择两个文件夹。" : "请选择两个文件。");
        UpdateNames();
        UpdateSlots();
    }

    private async Task ApplyFilesAsync(bool left, IReadOnlyList<IStorageFile> files, IReadOnlyList<IStorageFolder>? folders = null)
    {
        // 重新选目录就离开原来那份文件夹现场，返回按钮随即失效。
        _folderSnapshot = null;
        _backToFolder.IsVisible = false;
        // 文件夹模式收的是目录；文件模式照旧只认文件。
        var path = folders is { Count: > 0 }
            ? folders[0].TryGetLocalPath()
            : files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (left) _leftPath = path; else _rightPath = path;
        UpdateNames();
        UpdateSlots();
        ClearResult(path is null ? "请选择文件夹。" : "已选择，请选择另一侧。");
        if (_leftPath is not null && _rightPath is not null) await Compare();
    }

    /// <summary>
    /// 双击两侧都有的文件：记下文件夹列表的现场，再切到文件比较打开这两份文件。
    /// 顶栏的“返回文件夹比较”会把这套现场原样摆回去。
    /// </summary>
    private async Task OpenEntryAsync(FolderEntry entry)
    {
        if (!entry.IsComparable || entry.LeftPath is null || entry.RightPath is null) return;
        if (_folderResult is null) return;
        // 先把现场抓下来，SetFolderMode 会清掉当前这份结果和列表。
        var snapshot = new FolderViewSnapshot(
            _folderResult, _leftPath, _rightPath, Math.Max(0, _criteria.SelectedIndex),
            _folderDisplay.SelectedIndex, _folderView.Capture());
        SetFolderMode(false);
        _folderSnapshot = snapshot;
        _leftPath = entry.LeftPath;
        _rightPath = entry.RightPath;
        foreach (var zone in new[] { _leftZone, _rightZone }) zone.Items.Clear();
        UpdateNames();
        UpdateSlots();
        _backToFolder.IsVisible = true;
        ArcTooltip.SetTip(_backToFolder, $"回到 {snapshot.LeftRoot} 与 {snapshot.RightRoot} 的文件夹比较");
        await Compare();
    }

    /// <summary>回到双击进入文件比较之前的那份文件夹比较：结果、折叠状态、当前行、滚动位置都还原。</summary>
    private void ReturnToFolder()
    {
        if (_folderSnapshot is not { } snapshot) return;
        if (_busy)
        {
            // 文件比较还在读：先取消，等它收尾后再切回去，免得两边的收尾互相覆盖状态。
            _cts?.Cancel();
            _returnPending = true;
            _status.Text = "正在取消文件比较，随后返回文件夹比较…";
            return;
        }
        _folderSnapshot = null;
        _cts?.Cancel();
        SetFolderMode(true);
        _leftPath = snapshot.LeftRoot;
        _rightPath = snapshot.RightRoot;
        // 判定选项先摆回去，再挂上结果，免得改选中项触发一次重新比较。
        _criteria.SelectedIndex = snapshot.CriteriaIndex;
        _folderDisplay.SelectedIndex = Math.Max(0, snapshot.DisplayIndex);
        _folderResult = snapshot.Comparison;
        // 文件模式留下的页列表、差异高亮和“仅差异页”都要收拾掉，回到文件夹列表时不该还挂着。
        ClearResult("");
        UpdateNames();
        UpdateSlots();
        var changed = snapshot.Comparison.Files.Any(entry => entry.IsChanged);
        _leftBadge.IsVisible = _rightBadge.IsVisible = changed;
        _leftBadge.Content = _rightBadge.Content = "目录差异";
        _pageIndicator.Text = $"文件 {snapshot.Comparison.Files.Count()} · 目录 {snapshot.Comparison.Folders}";
        if (snapshot.LeftRoot is not null) _leftInfo.Text = DescribeFolder(snapshot.LeftRoot, snapshot.Comparison.Entries, true);
        if (snapshot.RightRoot is not null) _rightInfo.Text = DescribeFolder(snapshot.RightRoot, snapshot.Comparison.Entries, false);
        _status.Text = DescribeComparison(snapshot.Comparison, Criteria());
        // 列表放最后：找回当前行时会把状态栏换成那一行的说明，和离开前的显示一致。
        _folderView.Restore(snapshot.Comparison, snapshot.LeftRoot, snapshot.RightRoot,
            snapshot.View, (FolderDisplay)Math.Max(0, snapshot.DisplayIndex));
    }

    private void RemoveFile(bool left)
    {
        if (_busy) return;
        var name = _folderMode ? "文件夹" : "文件";
        if (_folderMode)
        {
            var folderZone = left ? _leftFolderZone : _rightFolderZone;
            foreach (var item in folderZone.Items.ToList()) folderZone.Remove(item);
        }
        else
        {
            var fileZone = left ? _leftZone : _rightZone;
            foreach (var item in fileZone.Items.ToList()) fileZone.Remove(item);
        }
        if (left) _leftPath = null; else _rightPath = null;
        _cts?.Cancel();
        if (_folderMode)
        {
            _folderResult = null;
            _folderView.SetComparison(null, _leftPath, _rightPath);
        }
        UpdateNames();
        UpdateSlots();
        ClearResult($"已移除{name}，请选择{name}。");
        _status.Text = left ? $"已移除旧版{name}，请重新选择。" : $"已移除新版{name}，请重新选择。";
    }

    /// <summary>Moves each side's file to the other, so the panes keep matching the headers.</summary>
    private void SwapZones()
    {
        var left = _leftZone.Items.ToList();
        var right = _rightZone.Items.ToList();
        _leftZone.Items.Clear();
        _rightZone.Items.Clear();
        foreach (var item in right) _leftZone.Items.Add(item);
        foreach (var item in left) _rightZone.Items.Add(item);
        UpdateSlots();
    }


    private void ClearResult(string message)
    {
        _result = null; _currentPage = -1; _pages.ItemsSource = null; _continuous = false;
        _leftView.ShowMessage(message); _rightView.ShowMessage(message);
        _leftBadge.IsVisible = _rightBadge.IsVisible = false;
        _leftInfo.Text = Describe(_leftPath); _rightInfo.Text = Describe(_rightPath);
        _leftPosition.Text = _rightPosition.Text = "行:1 列:1";
        _pageIndicator.Text = _folderMode ? "文件夹比较" : "页:0/0";
        ApplyLayoutForMode();
    }

    private string Describe(string? path)
    {
        if (path is null) return _folderMode ? "尚未选择文件夹" : "尚未选择文件";
        if (Directory.Exists(path))
        {
            try
            {
                var folder = new DirectoryInfo(path);
                return $"{folder.LastWriteTime:yyyy/MM/dd HH:mm} · 文件夹";
            }
            catch (IOException) { return "文件夹"; }
        }
        var kind = TextDocumentConverter.KindOf(path);
        // 纯文本扩展名太多，直接写出扩展名，一眼能看出是哪种代码或配置。
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var format = kind == TextDocumentKind.Plain
            ? extension.Length > 0 ? $"纯文本 {extension}" : "纯文本"
            : $"{TextDocumentConverter.Name(kind)} 文本";
        try
        {
            var file = new FileInfo(path);
            return $"{file.Length / 1024d / 1024d:0.00} MB · {file.LastWriteTime:yyyy/MM/dd HH:mm} · {format}";
        }
        catch (IOException) { return format; }
    }

    private void UpdateNames()
    {
        _leftName.Text = $"旧版 · {(_leftPath is null ? "尚未选择" : Path.GetFileName(_leftPath))}";
        _rightName.Text = $"新版 · {(_rightPath is null ? "尚未选择" : Path.GetFileName(_rightPath))}";
        _leftInfo.Text = Describe(_leftPath);
        _rightInfo.Text = Describe(_rightPath);
        ArcTooltip.SetTip(_leftName, _leftPath); ArcTooltip.SetTip(_rightName, _rightPath);
    }

    private async Task Compare()
    {
        if (_busy || _closed) return;
        if (_folderMode)
        {
            await CompareFolders();
            return;
        }
        if (_leftPath is null || _rightPath is null) { _status.Text = "请先选择左右两个文件。"; return; }
        _busy = true; _compare.IsEnabled = false; _cancel.IsEnabled = true; _ignoreWhitespace.IsEnabled = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var progress = new Progress<string>(text => { if (_busy && !_closed) _status.Text = text; });
        var ignoreWhitespace = _ignoreWhitespace.IsChecked == true;
        var mode = _usePdfText.IsChecked == true ? PdfExtractionMode.Text : PdfExtractionMode.Layout;
        var left = _leftPath; var right = _rightPath;
        // RTF/DOCX/代码文本只能整篇比文本，全文模式开关仅对两侧都是 PDF 生效。
        _usePdfText.IsEnabled = TextDocumentConverter.KindOf(left) == TextDocumentKind.Pdf &&
                                TextDocumentConverter.KindOf(right) == TextDocumentKind.Pdf;
        ClearResult($"正在读取 {TextDocumentConverter.FormatOf(TextDocumentConverter.KindOf(left), TextDocumentConverter.KindOf(right))}…");
        try
        {
            var result = await Task.Run(() => PdfComparer.Compare(left, right, mode, ignoreWhitespace, progress, token));
            token.ThrowIfCancellationRequested();
            if (_closed) return;
            _result = result;
            _leftView.Mode = _rightView.Mode = result.Mode;
            var changed = result.Pages.Count(p => p.IsChanged);
            _leftBadge.IsVisible = _rightBadge.IsVisible = changed > 0;
            _continuous = result.Continuous;
            ApplyLayoutForMode();
            RefreshPages();
            var engine = result.Engine;
            var changedRows = result.Continuous ? PdfPageView.VisibleRows(result.Pages[0], DiffRowFilter.Differences).Length : 0;
            var scope = result.Continuous
                ? result.Paged
                    ? $"全文连续比较（旧版 {result.LeftPages} 页 / 新版 {result.RightPages} 页，{result.Pages[0].LeftLines.Count} 行中 {changedRows} 行有差异）"
                    : $"全文连续比较（{result.Pages[0].LeftLines.Count} 行中 {changedRows} 行有差异）"
                : $"共 {result.Pages.Count} 页";
            _status.Text = changed > 0
                ? $"{scope} · 发现文本差异 · 红：旧版 / 绿：新版 · {result.Format} · {engine} · 无 OCR、不比较图像"
                : $"{scope} · 文本相同 · {result.Format} · {engine} · 无 OCR、不比较图像";
            if (_folderSnapshot is not null) _status.Text += " · Esc 返回文件夹比较";
        }
        catch (OperationCanceledException) { if (!_closed) { ClearResult("比较已取消。"); _status.Text = "比较已取消。"; } }
        catch (Exception ex) { if (!_closed) { ClearResult("文本提取失败，请检查文件或查看下方错误。"); _status.Text = $"无法提取文本：{ex.Message}"; } }
        finally
        {
            _busy = false; _compare.IsEnabled = true; _cancel.IsEnabled = false; _ignoreWhitespace.IsEnabled = true; _cts.Dispose(); _cts = null;
            // 比较期间点了“返回文件夹比较”：等这里收完尾再切，免得文件的收尾把文件夹的界面又盖回去。
            if (_returnPending)
            {
                _returnPending = false;
                Avalonia.Threading.Dispatcher.UIThread.Post(ReturnToFolder);
            }
        }
    }

    /// <summary>
    /// 文件夹比较：整棵树在后台走一遍，进度按已检查的项数报；结果交给两栏列表，
    /// 状态栏照 BC 报告的口径给出各类计数。
    /// </summary>
    private async Task CompareFolders()
    {
        if (_leftPath is null || _rightPath is null) { _status.Text = "请先选择左右两个文件夹。"; return; }
        if (!Directory.Exists(_leftPath) || !Directory.Exists(_rightPath))
        {
            _status.Text = "文件夹比较需要两侧都是本机目录。";
            return;
        }
        _busy = true; _compare.IsEnabled = false; _cancel.IsEnabled = true;
        foreach (var control in _fileOnly) control.IsEnabled = false;
        _criteria.IsEnabled = _folderDisplay.IsEnabled = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var progress = new Progress<string>(text => { if (_busy && !_closed) _status.Text = text; });
        var criteria = Criteria();
        var left = _leftPath; var right = _rightPath;
        _status.Text = "正在比较文件夹…";
        try
        {
            var result = await Task.Run(() => FolderComparer.Compare(left, right, criteria, progress, token), token);
            token.ThrowIfCancellationRequested();
            if (_closed) return;
            _folderResult = result;
            _folderView.SetComparison(result, left, right);
            _folderView.Display((FolderDisplay)Math.Max(0, _folderDisplay.SelectedIndex));
            var changed = result.Files.Count(entry => entry.IsChanged);
            _leftBadge.IsVisible = _rightBadge.IsVisible = changed > 0;
            _leftBadge.Content = _rightBadge.Content = "目录差异";
            _pageIndicator.Text = $"文件 {result.Files.Count()} · 目录 {result.Folders}";
            _leftInfo.Text = DescribeFolder(left, result.Entries, true);
            _rightInfo.Text = DescribeFolder(right, result.Entries, false);
            _status.Text = DescribeComparison(result, criteria);
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = "文件夹比较已取消。"; }
        catch (Exception ex) { if (!_closed) _status.Text = $"文件夹比较失败：{ex.Message}"; }
        finally
        {
            _busy = false; _compare.IsEnabled = true; _cancel.IsEnabled = false; _cts.Dispose(); _cts = null;
            foreach (var control in _fileOnly) control.IsEnabled = true;
            _criteria.IsEnabled = _folderDisplay.IsEnabled = true;
        }
    }

    private static string CriteriaName(FolderCriteria criteria) => criteria switch
    {
        FolderCriteria.Content => "判定：内容",
        FolderCriteria.Size => "判定：大小",
        FolderCriteria.Timestamp => "判定：时间",
        _ => "判定：大小 + 时间"
    };

    /// <summary>文件夹比较的一句话结论：状态栏和“返回文件夹比较”共用同一套措辞。</summary>
    private static string DescribeComparison(FolderComparison result, FolderCriteria criteria)
    {
        var changed = result.Files.Count(entry => entry.IsChanged);
        if (changed == 0) return $"文件夹比较 · {result.Files.Count()} 个文件全部相同 · {CriteriaName(criteria)}";
        var orphans = result.Files.Count(entry => entry.IsOrphan);
        return $"文件夹比较 · {result.Files.Count()} 个文件里 {changed} 个有差异（单侧存在 {orphans} 个）· " +
               $"相同 {result.Count(FolderStatus.Same)} · 新旧 {result.Count(FolderStatus.LeftNewer) + result.Count(FolderStatus.RightNewer)} · " +
               $"{CriteriaName(criteria)} · 双击文件可直接做文本比较";
    }

    private static string DescribeFolder(string path, IReadOnlyList<FolderEntry> entries, bool left)
    {
        // 顶层那一行的子树大小就是整棵树的和，直接拿来显示。
        var total = entries.Where(entry => entry.Depth == 0).Sum(entry => left ? entry.LeftSize : entry.RightSize);
        return $"{FolderSize.Format(total)} · {new DirectoryInfo(path).LastWriteTime:yyyy/MM/dd HH:mm} · 文件夹";
    }

    /// <summary>状态栏里一行文件夹条目的读法：目录说子树里有几项有差异，文件说新旧和大小。</summary>
    private static string DescribeEntry(FolderEntry entry)
    {
        if (entry.IsFolder)
        {
            return entry.IsOrphan ? "只在一侧存在" : entry.IsChanged ? "子树内有差异" : "子树内全部相同";
        }
        var same = entry.LeftSize == entry.RightSize ? "" : $" · {FolderSize.Format(entry.LeftSize)} → {FolderSize.Format(entry.RightSize)}";
        return entry.Status switch
        {
            FolderStatus.Same => "内容相同" + same,
            FolderStatus.Different => "内容不同" + same,
            FolderStatus.LeftNewer => "旧版较新" + same,
            FolderStatus.RightNewer => "新版较新" + same,
            FolderStatus.LeftOnly => "只在旧版存在",
            _ => "只在新版存在"
        };
    }

    private void RefreshPages()
    {
        if (_result is null) return;
        if (_result.Continuous)
        {
            // 连续模式没有页列表：整篇就是一项。已经在看全文时不要重设，否则切换筛选会跳回顶部。
            _pages.ItemsSource = null;
            if (_currentPage != 0) DisplayPage(0);
            return;
        }
        // all/diffs/same 是行筛选，不隐藏页面；same 也应能查看有差异页中的未变化行。
        var pages = _result.Pages.ToList();
        _pages.ItemsSource = pages;
        _pages.SelectedItem = pages.FirstOrDefault(p => p.Page == _currentPage) ?? pages.FirstOrDefault();
    }

    private void DisplayPage(int page)
    {
        if (_result is null) return;
        _currentPage = page;
        _leftView.SetPage(_result.Pages[page]); _rightView.SetPage(_result.Pages[page]);
        _leftScroll.Offset = _rightScroll.Offset = Vector.Zero;
        _pageIndicator.Text = _result.Continuous
            ? $"连续比较 · 旧版 {_result.LeftPages} 页 / 新版 {_result.RightPages} 页"
            : $"页:{page + 1}/{_result.Pages.Count}";
        _leftPosition.Text = _rightPosition.Text = "行:1 列:1";
    }

    private void Navigate(int direction)
    {
        // 文件夹模式按行跳差异，和文件的"上一/下一差异"是同一个按钮。
        if (_folderMode)
        {
            if (!_folderView.MoveToChanged(direction)) _status.Text = "没有更多差异项。";
            return;
        }
        if (_result is null) return;
        // 连续模式没有页，按行在全文里跳；两侧行对齐，滚一边另一边跟着走。
        if (_result.Continuous)
        {
            var moved = _leftView.ScrollToChangedLine(direction);
            if (!moved) _status.Text = direction > 0 ? "已到全文末尾，没有更多差异行。" : "已到全文开头，没有更早的差异行。";
            return;
        }
        var changed = _result.Pages.Where(p => p.IsChanged).ToList();
        if (changed.Count == 0) return;
        var target = direction > 0 ? changed.FirstOrDefault(p => p.Page > _currentPage) ?? changed[0] : changed.LastOrDefault(p => p.Page < _currentPage) ?? changed[^1];
        _pages.SelectedItem = target;
    }

    private void ZoomWithWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;
        SetZoom(_leftView.Zoom + Math.Sign(e.Delta.Y) * 0.1);
        e.Handled = true;
    }

    private void SetZoom(double value)
    {
        var zoom = Math.Clamp(Math.Round(value, 2), 0.25, 4);
        if (Math.Abs(zoom - _leftView.Zoom) < 0.001) return;
        _leftView.Zoom = _rightView.Zoom = zoom;
        _zoomLabel.Text = $"{_leftView.Zoom:P0}";
    }

    private void SyncScroll(ScrollViewer source, ScrollViewer target)
    {
        if (_syncing || _sync.IsChecked != true) return;
        _syncing = true;
        try
        {
            var maxX = Math.Max(0, source.Extent.Width - source.Viewport.Width);
            var targetMaxX = Math.Max(0, target.Extent.Width - target.Viewport.Width);
            var targetMaxY = Math.Max(0, target.Extent.Height - target.Viewport.Height);
            // 连续模式两侧行一一对齐、行高相同，同偏移滚动才落在同一行；分页模式才按页内进度换算。
            var maxY = Math.Max(0, source.Extent.Height - source.Viewport.Height);
            var y = _continuous ? Math.Min(source.Offset.Y, targetMaxY)
                : maxY == 0 ? 0 : source.Offset.Y / maxY * targetMaxY;
            target.Offset = new Vector(maxX == 0 ? 0 : source.Offset.X / maxX * targetMaxX, y);
        }
        finally { _syncing = false; }
    }
}

/// <summary>
/// 双击进入文件比较时留下的现场：根目录、判定与显示选项、比较结果，以及列表的折叠和滚动状态。
/// 只在这两份文件之间往返，退出文件比较或换模式就作废。
/// </summary>
internal sealed record FolderViewSnapshot(
    FolderComparison Comparison,
    string? LeftRoot,
    string? RightRoot,
    int CriteriaIndex,
    int DisplayIndex,
    FolderListViewState View);
