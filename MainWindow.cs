using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace FileDiff;

public sealed class MainWindow : Window
{
    private readonly TextBlock _leftName = new() { Text = "旧版 · 尚未选择", TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _rightName = new() { Text = "新版 · 尚未选择", TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { Text = "选择两个 PDF 自动比较文本。所有处理均在本机完成。", TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _pages = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly PdfPageView _leftView = new() { IsLeft = true };
    private readonly PdfPageView _rightView = new();
    private readonly ScrollViewer _leftScroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    private readonly ScrollViewer _rightScroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    private readonly CheckBox _sync = new() { Content = "同步滚动", IsChecked = true };
    private readonly CheckBox _highlight = new() { Content = "差异高亮", IsChecked = true };
    private readonly CheckBox _onlyChanged = new() { Content = "仅差异页", IsChecked = false };
    private readonly CheckBox _ignoreWhitespace = new() { Content = "忽略空白", IsChecked = true };
    private readonly Button _compare = new() { Content = "开始比较" };
    private readonly Button _cancel = new() { Content = "取消", IsEnabled = false };
    private readonly TextBlock _zoomLabel = new() { Text = "100%", VerticalAlignment = VerticalAlignment.Center };
    private Comparison? _result;
    private string? _leftPath, _rightPath;
    private CancellationTokenSource? _cts;
    private int _currentPage = -1;
    private bool _syncing, _busy, _closed;

    public MainWindow()
    {
        Title = "PDF Diff · 文本差异";
        Width = 1440; Height = 940; MinWidth = 1050; MinHeight = 650;
        Background = new SolidColorBrush(Color.Parse("#0D1117"));
        Styles.Add(new Style(s => s.OfType<Button>())
        {
            Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(10)) }
        });
        Styles.Add(new Style(s => s.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)),
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
                Foreground = new SolidColorBrush(Color.Parse(page.IsChanged ? "#FF8A65" : "#B8C0CC")),
                FontWeight = page.IsChanged ? FontWeight.SemiBold : FontWeight.Normal,
                Margin = new Thickness(8, 4)
            };
        });
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 12) };
        toolbar.Children.Add(new TextBlock { Text = "PDF TEXT DIFF", FontSize = 22, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 4, 14, 0) });
        toolbar.Children.Add(ActionButton("打开旧版", () => PickFile(true)));
        toolbar.Children.Add(ActionButton("打开新版", () => PickFile(false)));
        _compare.Click += async (_, _) => await Compare();
        toolbar.Children.Add(_compare);
        _cancel.Click += (_, _) => _cts?.Cancel();
        toolbar.Children.Add(_cancel);
        toolbar.Children.Add(ActionButton("交换左右", async () =>
        {
            if (_busy) return;
            (_leftPath, _rightPath) = (_rightPath, _leftPath);
            UpdateNames();
            ClearResult("文件已交换，请选择两个 PDF 后比较。");
            if (_leftPath is not null && _rightPath is not null) await Compare();
        }));
        toolbar.Children.Add(_ignoreWhitespace);
        root.Children.Add(toolbar);
        var options = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 10) };
        options.Children.Add(_sync); options.Children.Add(_highlight); options.Children.Add(_onlyChanged);
        options.Children.Add(ActionButton("上一差异页", () => { Navigate(-1); return Task.CompletedTask; }));
        options.Children.Add(ActionButton("下一差异页", () => { Navigate(1); return Task.CompletedTask; }));
        options.Children.Add(ActionButton("缩小 −", () => { SetZoom(_leftView.Zoom - 0.1); return Task.CompletedTask; }));
        options.Children.Add(_zoomLabel);
        options.Children.Add(ActionButton("放大 +", () => { SetZoom(_leftView.Zoom + 0.1); return Task.CompletedTask; }));
        options.Children.Add(ActionButton("恢复 100%", () => { SetZoom(1); return Task.CompletedTask; }));
        ToolTip.SetTip(_zoomLabel, "页面缩放范围 25%–400%；Ctrl+鼠标滚轮缩放。");
        Grid.SetRow(options, 1); root.Children.Add(options);
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*,6,*"), Margin = new Thickness(12, 0) };
        content.Children.Add(new Border
        {
            Child = _pages, CornerRadius = new CornerRadius(12), ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#161B22")),
            BorderBrush = new SolidColorBrush(Color.Parse("#30363D")), BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        });
        _leftScroll.Content = _leftView; _rightScroll.Content = _rightView;
        var leftPane = AddPane(_leftName, _leftScroll, "#F85149");
        Grid.SetColumn(leftPane, 1); content.Children.Add(leftPane);
        var divider = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(divider, 2); content.Children.Add(divider);
        var rightPane = AddPane(_rightName, _rightScroll, "#3FB950");
        Grid.SetColumn(rightPane, 3); content.Children.Add(rightPane);
        Grid.SetRow(content, 2); root.Children.Add(content);
        var footer = new Border
        {
            Child = _status, Padding = new Thickness(16, 10), Margin = new Thickness(12, 10),
            CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.Parse("#161B22"))
        };
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;
        _pages.SelectionChanged += (_, _) => { if (_pages.SelectedItem is PageDifference page) DisplayPage(page.Page); };
        _ignoreWhitespace.IsCheckedChanged += async (_, _) =>
        {
            if (_leftPath is not null && _rightPath is not null) await Compare();
        };
        ToolTip.SetTip(_ignoreWhitespace, "忽略行内空格和制表符，保留原文和换行；切换后自动重新比较。");
        _onlyChanged.IsCheckedChanged += (_, _) => RefreshPages();
        _highlight.IsCheckedChanged += (_, _) => _leftView.ShowHighlights = _rightView.ShowHighlights = _highlight.IsChecked == true;
        _leftScroll.AddHandler(PointerWheelChangedEvent, ZoomWithWheel, RoutingStrategies.Tunnel);
        _rightScroll.AddHandler(PointerWheelChangedEvent, ZoomWithWheel, RoutingStrategies.Tunnel);
        _leftScroll.ScrollChanged += (_, _) => SyncScroll(_leftScroll, _rightScroll);
        _rightScroll.ScrollChanged += (_, _) => SyncScroll(_rightScroll, _leftScroll);
        Closed += (_, _) => { _closed = true; _cts?.Cancel(); };
    }

    private static Button ActionButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new Thickness(4, 0) };
        button.Click += async (_, _) => await action();
        return button;
    }

    private static Border AddPane(TextBlock title, ScrollViewer scroll, string accent)
    {
        var pane = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        pane.Children.Add(new Border
        {
            Child = title, Padding = new Thickness(12), Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(11, 11, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse(accent)), BorderThickness = new Thickness(0, 0, 0, 2)
        });
        Grid.SetRow(scroll, 1); pane.Children.Add(scroll);
        return new Border
        {
            Child = pane, Margin = new Thickness(8, 0), CornerRadius = new CornerRadius(12),
            ClipToBounds = true, Background = new SolidColorBrush(Color.Parse("#161B22")),
            BorderBrush = new SolidColorBrush(Color.Parse("#30363D")), BorderThickness = new Thickness(1)
        };
    }

    private async Task PickFile(bool left)
    {
        if (_busy) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = left ? "选择旧版 PDF" : "选择新版 PDF", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PDF 文档") { Patterns = ["*.pdf"] }]
        });
        if (files.Count == 0 || _closed) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) { _status.Text = "请选择本地 PDF 文件。"; return; }
        if (left) _leftPath = path; else _rightPath = path;
        UpdateNames();
        ClearResult("文件已选择，请选择另一侧 PDF。");
        if (_leftPath is not null && _rightPath is not null) await Compare();
    }

    private void ClearResult(string message)
    {
        _result = null; _currentPage = -1; _pages.ItemsSource = null;
        _leftView.ShowMessage(message); _rightView.ShowMessage(message);
    }

    private void UpdateNames()
    {
        _leftName.Text = $"旧版 · {(_leftPath is null ? "尚未选择" : Path.GetFileName(_leftPath))}";
        _rightName.Text = $"新版 · {(_rightPath is null ? "尚未选择" : Path.GetFileName(_rightPath))}";
        ToolTip.SetTip(_leftName, _leftPath); ToolTip.SetTip(_rightName, _rightPath);
    }

    private async Task Compare()
    {
        if (_busy || _closed) return;
        if (_leftPath is null || _rightPath is null) { _status.Text = "请先选择左右两个 PDF 文件。"; return; }
        _busy = true; _compare.IsEnabled = false; _cancel.IsEnabled = true; _ignoreWhitespace.IsEnabled = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var progress = new Progress<string>(text => { if (_busy && !_closed) _status.Text = text; });
        var ignoreWhitespace = _ignoreWhitespace.IsChecked == true;
        var left = _leftPath; var right = _rightPath;
        ClearResult("正在提取 PDF 文本…");
        try
        {
            var result = await Task.Run(() => PdfComparer.Compare(left, right, ignoreWhitespace, progress, token));
            token.ThrowIfCancellationRequested();
            if (_closed) return;
            _result = result;
            RefreshPages();
            _status.Text = $"共 {result.Pages.Count} 页 · {result.Pages.Count(p => p.IsChanged)} 个文本差异页 · 红：旧版 / 绿：新版 · 按原页坐标展示文本，无 OCR、不比较图像";
        }
        catch (OperationCanceledException) { if (!_closed) { ClearResult("比较已取消。"); _status.Text = "比较已取消。"; } }
        catch (Exception ex) { if (!_closed) { ClearResult("文本提取失败，请检查文件或查看下方错误。"); _status.Text = $"无法提取 PDF 文本：{ex.Message}"; } }
        finally { _busy = false; _compare.IsEnabled = true; _cancel.IsEnabled = false; _ignoreWhitespace.IsEnabled = true; _cts.Dispose(); _cts = null; }
    }

    private void RefreshPages()
    {
        if (_result is null) return;
        var pages = _result.Pages.Where(p => _onlyChanged.IsChecked != true || p.IsChanged).ToList();
        _pages.ItemsSource = pages;
        _pages.SelectedItem = pages.FirstOrDefault(p => p.Page == _currentPage) ?? pages.FirstOrDefault();
        if (pages.Count == 0)
        {
            _currentPage = -1;
            _leftView.ShowMessage("当前筛选下没有差异页；无文本页面不代表图像相同。");
            _rightView.ShowMessage("当前筛选下没有差异页；无文本页面不代表图像相同。");
        }
    }

    private void DisplayPage(int page)
    {
        if (_result is null) return;
        _currentPage = page;
        _leftView.SetPage(_result.Pages[page]); _rightView.SetPage(_result.Pages[page]);
        _leftScroll.Offset = _rightScroll.Offset = Vector.Zero;
    }

    private void Navigate(int direction)
    {
        if (_result is null) return;
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
            // Preserve each PDF page's own coordinates; synchronize relative page progress.
            var maxY = Math.Max(0, source.Extent.Height - source.Viewport.Height);
            target.Offset = new Vector(maxX == 0 ? 0 : source.Offset.X / maxX * Math.Max(0, target.Extent.Width - target.Viewport.Width), maxY == 0 ? 0 : source.Offset.Y / maxY * Math.Max(0, target.Extent.Height - target.Viewport.Height));
        }
        finally { _syncing = false; }
    }
}
