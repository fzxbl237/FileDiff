using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffPlex.DiffBuilder.Model;
using ArcTooltip = TKCode.Arc.Tooltip;

namespace FileDiff;

public enum DiffRowFilter
{
    All,
    Differences,
    Same
}

public sealed class PdfPageView : Grid
{
    // Narrow strip at the left edge that marks changed rows, like a diff gutter.
    private const double GutterWidth = 12;
    private const double SheetMargin = 6;
    // 纸页外框的 1 像素描边：画布内容的原点在它里面，指针坐标要先减掉。
    private const double SheetBorder = 1;
    private static readonly IBrush Backdrop = Hex("#ECECEC");
    private static readonly IBrush GutterBrush = Hex("#E4E4E4");
    private static readonly IBrush ChangedBand = Hex("#FDE3E0");
    private static readonly IBrush ChangedCharBack = Hex("#B54A36");
    private static readonly IBrush ChangedCharText = Hex("#FFFFFF");
    private static readonly IBrush ChangedText = Hex("#E8392B");
    private static readonly IBrush NormalText = Hex("#000000");
    private static readonly IBrush RemovedMark = Hex("#D32F2F");
    private static readonly IBrush AddedMark = Hex("#2E7D32");
    // 当前行：细黑框整行包一圈，行内一条黑色光标竖条。
    private static readonly IBrush CaretBrush = Hex("#000000");
    private const double CaretThickness = 1;
    private const double CaretBarWidth = 2;
    // 光标竖条上下各留一段空白，高度约是行高的四分之一去掉两头。
    private const double CaretBarInset = 0.26;

    private const double TextFontSize = 12;
    private const double TextLeftPad = 6;
    private const double TextRightPad = 8;
    // Virtualized rows: only the lines near the viewport get a control.
    private const int RowOverscan = 24;
    // 跳到差异行时上方留几行，免得差异贴着视口顶端看不出来。
    private const int RowsBeforeJump = 4;

    private PageDifference? _page;
    private bool _showHighlights = true;
    private double _zoom = 1;
    private Canvas? _textCanvas;
    private IReadOnlyList<TextDiffLine> _textLines = [];
    private readonly List<int> _lineStarts = [];
    private readonly Dictionary<int, Control> _realizedRows = [];
    private readonly Dictionary<int, SelectableTextBlock> _realizedText = [];
    private double _rowHeight = 16;
    private ScrollViewer? _scrollHost;
    public bool IsLeft { get; init; }
    /// <summary>比较时实际生效的提取方式：PdfPig 全文模式用等宽字体，排版模式按 PDF 坐标显示。</summary>
    public PdfExtractionMode Mode { get; set; }
    public event Action<int, int>? CaretMoved;
    /// <summary>点了最左边的小按钮：这一行所在的页整页展开或收起，另一侧要跟着做同一个动作，行表才一直对齐。</summary>
    public event Action<int>? PageToggled;
    public bool ShowHighlights
    {
        get => _showHighlights;
        set { _showHighlights = value; Refresh(); }
    }
    public double Zoom
    {
        get => _zoom;
        set { _zoom = value; Refresh(); }
    }
    /// <summary>Filters the shared aligned rows so both panes always retain the same row sequence.</summary>
    public DiffRowFilter RowFilter
    {
        get => _rowFilter;
        set
        {
            if (_rowFilter == value) return;
            _rowFilter = value;
            Refresh();
        }
    }

    private static readonly FontFamily TextFont = new("Consolas,Lucida Console,Microsoft YaHei");
    private static SolidColorBrush Hex(string value) => new(Color.Parse(value));

    private readonly List<(SelectableTextBlock Control, int Start, int Length)> _selectionBlocks = [];
    private string _selectionText = "";
    private int _anchor, _caret;
    private bool _dragging;
    private IPointer? _selectionPointer;
    private int[] _rowStarts = [];
    // 画布上显示的行：按 All / Differences / Same 筛选，值仍是对齐后的原行号。
    private int[] _rows = [];
    private DiffRowFilter _rowFilter;
    private double _charWidth = 7;
    private int _jumpIndex = -1;
    private double _jumpOffset = -1;
    // 当前行：整行描边加一条光标竖条，和画布一起滚动，行控件被回收也不消失。
    private Canvas? _caretCanvas;
    // 当前行在画布上的表行号；排版模式用它描边。
    private int _caretRow = -1;
    // 文本模式当前行的源行号和行内列号：该行被当前筛选隐藏后又回来时靠它找回当前行。
    private int _caretLine = -1;
    private int _caretColumn;
    // 重建画布前的当前行、选区两端，以及当前行离视口上沿的高度（NaN 表示不据此挪视口）。
    private (int Line, int Anchor, int Caret, double Delta) _pendingCaret = (-1, 0, 0, double.NaN);
    // 排版模式：行号 → 该行的竖向范围与水平范围，用来给当前行描边。
    private readonly Dictionary<int, (double Left, double Right, double Top, double Bottom)> _layoutRows = [];
    // 悬停时最左边浮出的展开按钮：蓝底圆角，白色加号/减号。
    private static readonly IBrush HoverBrush = Hex("#2A62D9");
    private Canvas? _hoverCanvas;
    private Control? _hoverButton;
    private int _hoverRow = -1;
    private bool _hoverExpanded;
    private const double HoverButtonSize = 14;
    // 最近一次指针位置（本控件坐标）；画布重建、滚动之后据此判断悬停在哪一行。
    private Point _lastPointer = new(double.NaN, double.NaN);
    // 已展开的页在行表里占的范围；两侧都按同一条行表算，所以两个视图的展开结果总是一致。
    private readonly List<(int First, int Last)> _expanded = [];
    // 重建画布前要钉在屏幕原高度的那一行（点了展开按钮时用）：Line 是源行号，Delta 是它离视口上沿的高度。
    private (int Line, double Delta) _pendingRow = (-1, double.NaN);

    private void ResetSelection()
    {
        _dragging = false;
        _selectionPointer?.Capture(null);
        _selectionPointer = null;
        _selectionBlocks.Clear();
        _selectionText = "";
        _anchor = _caret = 0;
        HideCaret();
    }

    /// <summary>
    /// 点最左边的小按钮：把该行所在页的行全部展开，再点一次收回。
    /// 展开的范围用源行号记，和“仅差异行”的行表同一套坐标，两侧因此总是一致。
    /// </summary>
    public void TogglePage(int line)
    {
        ApplyPageToggle(line);
        PageToggled?.Invoke(line);
    }

    /// <summary>另一侧传来同一个动作时直接照做，不再回传事件，免得两侧互相弹。</summary>
    internal void MirrorPageToggle(int line) => ApplyPageToggle(line);

    private void ApplyPageToggle(int line)
    {
        if (_page is null) return;
        var (first, last) = _page.PageRowRange(line);
        var index = _expanded.FindIndex(range => range.First == first && range.Last == last);
        if (index >= 0) _expanded.RemoveAt(index);
        else _expanded.Add((first, last));
        _pendingRow = PinRow(line);
        Refresh();
    }

    /// <summary>
    /// 展开/收起后要钉在屏幕原高度的那一行。展开时就是点的那一行；收起时它自己会被收走，
    /// 沿旧行表往上找第一行仍然留下的，钉住它，视野才不会整体跳一下。
    /// </summary>
    private (int Line, double Delta) PinRow(int line)
    {
        if (_scrollHost is null || _page is null) return (-1, double.NaN);
        var start = Array.IndexOf(_rows, line);
        if (start < 0) return (-1, double.NaN);
        // 重建后的行表：仍是“仅差异行”加已展开页，再按本侧的行数截断，和 ShowText 里算的一样。
        var lines = IsLeft ? _page.LeftLines : _page.RightLines;
        var next = VisibleRows(_page, _rowFilter, _expanded).Where(i => i < lines.Count).ToArray();
        for (var row = start; row >= 0; row--)
        {
            var candidate = _rows[row];
            if (Array.BinarySearch(next, candidate) < 0) continue;
            return (candidate, row * _rowHeight - _scrollHost.Offset.Y);
        }
        return (-1, double.NaN);
    }

    /// <summary>这一行所在的页是否已经展开。</summary>
    private bool IsPageExpanded(int line) =>
        _page is not null && _expanded.Contains(_page.PageRowRange(line));

    /// <summary>只有连续比较才有页信息；分页比较时一页就是一篇，没有“展开整页”可说。</summary>
    private bool CanExpandPage() =>
        _page is not null && (_page.LeftPageStarts.Count > 0 || _page.RightPageStarts.Count > 0);

    /// <summary>
    /// 指针在正文上移动时算出压着哪一行，并在最左边浮出一个小按钮。
    /// 指针坐标先换成纸页内坐标（减掉纸页上方的页边距），才能和画布行号对上。
    /// </summary>
    private void UpdateHover(Point point)
    {
        _lastPointer = point;
        if (_dragging || _textCanvas is null || _hoverCanvas is null)
        {
            HideHover();
            return;
        }
        // 悬停按钮只在差异筛选下有用：整篇显示或只看相同行时都没有可展开的行。
        if (_rowFilter != DiffRowFilter.Differences || !CanExpandPage())
        {
            HideHover();
            return;
        }
        var (x, y) = ToSheet(point);
        // 只在纸页范围内浮按钮：滚动槽里的空白和纸页以外的背景都不算。
        if (y < 0 || x < 0 || x > _hoverCanvas.Width)
        {
            HideHover();
            return;
        }
        var row = (int)Math.Floor(y / _rowHeight);
        if (row < 0 || row >= _rows.Length)
        {
            HideHover();
            return;
        }
        ShowHoverButton(row, _rows[row]);
    }

    /// <summary>本控件坐标换成纸页内容坐标：减掉纸页上方的页边距和一圈 1 像素的边框。</summary>
    private static (double X, double Y) ToSheet(Point point) =>
        (point.X - SheetBorder, point.Y - SheetMargin - SheetBorder);

    /// <summary>
    /// 画悬停按钮：蓝底圆角方块，白色加号表示“展开该页”，展开后换成减号表示可以收起。
    /// 和参考截图一样只用白加号，不描边、不加箭头，落在左侧空白上不压住正文。
    /// </summary>
    private void ShowHoverButton(int row, int line)
    {
        if (_hoverCanvas is null) return;
        var expanded = IsPageExpanded(line);
        if (_hoverButton is not null && _hoverRow == row && _hoverExpanded == expanded) return;
        HideHover();
        _hoverRow = row;
        _hoverExpanded = expanded;
        var glyph = new Canvas
        {
            Width = 8, Height = 8,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        if (expanded)
        {
            var bar = new Rectangle { Width = 8, Height = 1.6, RadiusX = 0.8, RadiusY = 0.8, Fill = Brushes.White };
            Canvas.SetTop(bar, 3.2);
            glyph.Children.Add(bar);
        }
        else
        {
            var horizontal = new Rectangle { Width = 8, Height = 1.6, RadiusX = 0.8, RadiusY = 0.8, Fill = Brushes.White };
            Canvas.SetTop(horizontal, 3.2);
            glyph.Children.Add(horizontal);
            var vertical = new Rectangle { Width = 1.6, Height = 8, RadiusX = 0.8, RadiusY = 0.8, Fill = Brushes.White };
            Canvas.SetLeft(vertical, 3.2);
            glyph.Children.Add(vertical);
        }
        var button = new Border
        {
            Width = HoverButtonSize, Height = HoverButtonSize, CornerRadius = new CornerRadius(4),
            Background = HoverBrush,
            // 命中由 OverHoverButton 自己算，别让它把行上的指针事件挡掉。
            IsHitTestVisible = false,
            Child = glyph,
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 1, Blur = 2, Color = Color.Parse("#33000000") })
        };
        ArcTooltip.SetTip(button, expanded ? "收起该页" : "展开该行所在页的全部行");
        Canvas.SetLeft(button, 1);
        Canvas.SetTop(button, row * _rowHeight + Math.Max(0, (_rowHeight - HoverButtonSize) / 2));
        _hoverCanvas.Children.Add(button);
        _hoverButton = button;
    }

    private void HideHover()
    {
        _hoverCanvas?.Children.Clear();
        _hoverButton = null;
        _hoverRow = -1;
    }

    /// <summary>点在悬停按钮上：按钮只有 14 像素，判定放宽 2 像素，免得手抖点空变成选文本。</summary>
    private bool OverHoverButton(Point point)
    {
        if (_hoverButton is null || _hoverRow < 0) return false;
        var left = Canvas.GetLeft(_hoverButton) + SheetBorder;
        var top = Canvas.GetTop(_hoverButton) + SheetMargin + SheetBorder;
        return point.X >= left - 2 && point.X <= left + HoverButtonSize + 2
            && point.Y >= top - 2 && point.Y <= top + HoverButtonSize + 2;
    }

    /// <summary>清掉当前行框和光标；画布重建、换页、换筛选都走这里。</summary>
    private void HideCaret()
    {
        _caretCanvas?.Children.Clear();
        _caretRow = -1;
    }

    /// <summary>
    /// 画当前行：细黑框整行包一圈，行内再给一条黑色竖条当光标。
    /// 框画在文本画布之上的独立画布上，行控件被虚拟化回收了框也还在，滚动时跟着画布一起走。
    /// 竖条比框矮一点，贴着字形高度，框尾留白，和参考截图一致。
    /// </summary>
    private void ShowCaret(int canvasRow, double x)
    {
        if (_caretCanvas is null || canvasRow < 0) return;
        _caretRow = canvasRow;
        _caretCanvas.Children.Clear();
        double top, bottom, left, right;
        if (_textCanvas is not null)
        {
            top = canvasRow * _rowHeight;
            bottom = top + _rowHeight;
            // 框从正文区开始，左边那条差异刻度槽不算进去，和参考界面一致。
            left = GutterWidth;
            right = Math.Max(left + 2, _caretCanvas.Width - 2);
        }
        else if (_layoutRows.TryGetValue(canvasRow, out var row))
        {
            top = row.Top - 1;
            bottom = row.Bottom + 1;
            left = Math.Max(1, row.Left - 2);
            right = Math.Max(left + 2, row.Right + 2);
        }
        else return;
        var outline = new Rectangle
        {
            Width = Math.Max(1, right - left), Height = Math.Max(1, bottom - top),
            Stroke = CaretBrush, StrokeThickness = CaretThickness, Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(outline, left);
        Canvas.SetTop(outline, top);
        _caretCanvas.Children.Add(outline);
        if (double.IsNaN(x)) return;
        var inset = Math.Max(1, Math.Round((bottom - top) * CaretBarInset));
        var bar = new Rectangle
        {
            Width = CaretBarWidth, Height = Math.Max(1, bottom - top - 2 * inset), Fill = CaretBrush,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(bar, Math.Clamp(x, left, Math.Max(left, right - CaretBarWidth)));
        Canvas.SetTop(bar, top + inset);
        _caretCanvas.Children.Add(bar);
    }

    internal static string SelectionText(string text, int anchor, int caret)
    {
        var start = Math.Clamp(Math.Min(anchor, caret), 0, text.Length);
        var end = Math.Clamp(Math.Max(anchor, caret), 0, text.Length);
        return text[start..end];
    }

    private void ReportCaret()
    {
        if (_selectionText.Length == 0) return;
        var index = Math.Clamp(_caret, 0, _selectionText.Length);
        var line = 1;
        var lineBreak = -1;
        for (var i = 0; i < index; i++)
            if (_selectionText[i] == '\n') { line++; lineBreak = i; }
        CaretMoved?.Invoke(line, index - lineBreak);
    }

    /// <summary>
    /// 虚拟化之后只有可见行有控件，点选时先把视口内那张表按当前滚动位置补齐，再找最近的行。
    /// 除了全文偏移，还给出所在行控件（画布行号）和点击处的画布横坐标，用来画当前行的框和光标。
    /// </summary>
    private (int Row, int Position, double X) HitTest(Point point)
    {
        if (_textCanvas is null) return HitTestLayout(point);
        if (_realizedText.Count == 0) return (-1, Math.Clamp(_caret, 0, _selectionText.Length), double.NaN);
        var best = _realizedText.MinBy(pair =>
        {
            var local = this.TranslatePoint(point, pair.Value) ?? default;
            var dx = Math.Max(0, Math.Max(-local.X, local.X - pair.Value.Bounds.Width));
            var dy = Math.Max(0, Math.Max(-local.Y, local.Y - pair.Value.Bounds.Height));
            return dy * dy * 100 + dx * dx;
        });
        var position = this.TranslatePoint(point, best.Value) ?? default;
        var clamped = new Point(Math.Clamp(position.X, 0, best.Value.Bounds.Width), 0);
        var x = (best.Value.TranslatePoint(clamped, _caretCanvas ?? (Visual)this) ?? default).X;
        var layout = best.Value.TextLayout;
        var rowStart = _rowStarts[_rows[best.Key]];
        if (layout is null) return (best.Key, Math.Clamp(rowStart, 0, Math.Max(0, _selectionText.Length)), x);
        var offset = Math.Clamp(layout.HitTestPoint(position).TextPosition, 0, best.Value.Text?.Length ?? 0);
        return (best.Key, Math.Clamp(rowStart + offset, 0, Math.Max(0, _selectionText.Length)), x);
    }

    /// <summary>排版模式没有虚拟行，按块列表找最近的文本块，画布行号换成该块所在的排版行。</summary>
    private (int Row, int Position, double X) HitTestLayout(Point point)
    {
        if (_selectionBlocks.Count == 0) return (-1, Math.Clamp(_caret, 0, _selectionText.Length), double.NaN);
        var best = _selectionBlocks.MinBy(block =>
        {
            var local = this.TranslatePoint(point, block.Control) ?? default;
            var dx = Math.Max(0, Math.Max(-local.X, local.X - block.Control.Bounds.Width));
            var dy = Math.Max(0, Math.Max(-local.Y, local.Y - block.Control.Bounds.Height));
            return dy * dy * 100 + dx * dx;
        });
        var hit = this.TranslatePoint(point, best.Control) ?? default;
        var clamped = new Point(Math.Clamp(hit.X, 0, best.Control.Bounds.Width), 0);
        var x = (best.Control.TranslatePoint(clamped, _caretCanvas ?? (Visual)this) ?? default).X;
        var layout = best.Control.TextLayout;
        var offset = layout is null ? 0 : Math.Clamp(layout.HitTestPoint(hit).TextPosition, 0, best.Length);
        var position = Math.Clamp(best.Start + offset, 0, Math.Max(0, _selectionText.Length));
        return (RowOf(position), position, x);
    }

    /// <summary>全文偏移落在第几行（从 0 开始），排版模式用它定位当前行。</summary>
    private int RowOf(int position)
    {
        if (_lineStarts.Count == 0) return -1;
        var index = _lineStarts.BinarySearch(position);
        return index >= 0 ? index : Math.Max(0, ~index - 1);
    }

    /// <summary>把当前选区写回控件；纯文本模式要按可见行重建索引，排版模式沿用已有的块列表。</summary>
    private void ApplySelection()    {
        var start = Math.Min(_anchor, _caret);
        var end = Math.Max(_anchor, _caret);
        if (_textCanvas is not null)
        {
            _selectionBlocks.Clear();
            foreach (var (index, control) in _realizedText)
            {
                var rowStart = _rowStarts[_rows[index]];
                var length = _textLines[_rows[index]].Text.Length;
                _selectionBlocks.Add((control, rowStart, length));
                control.SelectionStart = Math.Clamp(start - rowStart, 0, length);
                control.SelectionEnd = Math.Clamp(end - rowStart, 0, length);
            }
            return;
        }
        foreach (var block in _selectionBlocks)
        {
            block.Control.SelectionStart = Math.Clamp(start - block.Start, 0, block.Length);
            block.Control.SelectionEnd = Math.Clamp(end - block.Start, 0, block.Length);
        }
    }

    public PdfPageView()
    {
        Focusable = true;
        ColumnDefinitions = new ColumnDefinitions($"{GutterWidth},*");
        Background = Backdrop;
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            // 悬停按钮压在选择之上：点在它上面就是展开/收起该页，不算点选文本。
            if (_hoverButton is not null && _hoverRow >= 0 && OverHoverButton(e.GetPosition(this)))
            {
                Focus();
                TogglePage(_rows[_hoverRow]);
                e.Handled = true;
                return;
            }
            if (_selectionBlocks.Count == 0) return;
            HideHover();
            Focus();
            var hit = HitTest(e.GetPosition(this));
            _anchor = _caret = hit.Position;
            _dragging = true;
            _selectionPointer = e.Pointer;
            e.Pointer.Capture(this);
            ApplySelection();
            PlaceCaret(hit.Row, hit.Position, hit.X);
            ReportCaret();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (!_dragging)
            {
                UpdateHover(e.GetPosition(this));
                return;
            }
            if (_selectionBlocks.Count == 0) return;
            var hit = HitTest(e.GetPosition(this));
            _caret = hit.Position;
            ApplySelection();
            PlaceCaret(hit.Row, hit.Position, hit.X);
            ReportCaret();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            e.Pointer.Capture(null);
            UpdateHover(e.GetPosition(this));
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        PointerExited += (_, _) =>
        {
            _lastPointer = new Point(double.NaN, double.NaN);
            HideHover();
        };
        PointerCaptureLost += (_, _) => { _dragging = false; _selectionPointer = null; };
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var selected = SelectionText(_selectionText, _anchor, _caret);
                if (selected.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(selected);
                e.Handled = true;
            }
        };
        ShowMessage("请选择两个文件。提取的文本将在这里显示。");
    }

    public void ShowMessage(string message)
    {
        _page = null;
        ResetSelection();
        _pendingCaret = (-1, 0, 0, double.NaN);
        _pendingRow = (-1, double.NaN);
        _expanded.Clear();
        Children.Clear();
        _textCanvas = null;
        _caretCanvas = null;
        _hoverCanvas = null;
        _hoverButton = null;
        _hoverRow = -1;
        _lastPointer = new Point(double.NaN, double.NaN);
        _textLines = [];
        _rows = [];
        _layoutRows.Clear();
        _realizedRows.Clear();
        _realizedText.Clear();
        var text = new TextBlock
        {
            Text = message, Margin = new Thickness(16), Foreground = Hex("#7A7A7A"),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top,
            IsVisible = message.Length > 0
        };
        Grid.SetColumnSpan(text, 2);
        Children.Add(text);
    }

    public void SetPage(PageDifference difference)
    {
        _page = difference;
        // 换页时上一页的选中行和阅读位置都不作数。
        _caretRow = -1;
        _pendingCaret = (-1, 0, 0, double.NaN);
        Refresh();
    }

    /// <summary>
    /// PdfPig 全文模式：整篇纯文本连续铺开，不分页。
    /// 两个 PDF 的页数常常不同，逐页切会把内容增删放大成整页假差异，所以这里只按内容逐行对齐。
    /// 长文档只为视口附近的行创建控件，其余部分用固定行高的空白画布占位。
    /// anchor 是重建前视口顶端的全文行号，切换行筛选或缩放后据此把视口挪回原处。
    /// </summary>
    private void ShowText(string text, IReadOnlyList<TextDiffLine> lines, int anchor)
    {
        _selectionText = text;
        _textLines = lines;
        _jumpIndex = -1;
        _jumpOffset = -1;
        _lineStarts.Clear();
        _lineStarts.Add(0);
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') _lineStarts.Add(i + 1);

        var fontSize = TextFontSize * _zoom;
        var probe = new SelectableTextBlock { FontFamily = TextFont, FontSize = fontSize, Text = "MMMMMMMMMM" };
        probe.Measure(Size.Infinity);
        _charWidth = Math.Max(1, probe.DesiredSize.Width / 10);
        _rowHeight = Math.Max(1, Math.Round(fontSize * 1.5));

        // 每一行在全文中的起始偏移；占位行没有正文，落在上一行换行之后，选中时不会跳位。
        _rowStarts = new int[lines.Count];
        var next = 0;
        var longest = 1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Number is int number && number >= 1 && number <= _lineStarts.Count)
                next = _lineStarts[number - 1];
            _rowStarts[i] = next;
            next += line.Text.Length + 1;
            longest = Math.Max(longest, line.Text.Length);
        }

        // 两侧使用同一份对齐行表：显示差异时任一侧有变化就保留，显示相同时任一侧有变化就过滤。
        _rows = VisibleRows(_page!, _rowFilter, _expanded).Where(i => i < lines.Count).ToArray();

        var width = Math.Ceiling(GutterWidth + TextLeftPad + longest * _charWidth + TextRightPad);
        var height = Math.Max(1, Math.Ceiling(_rows.Length * _rowHeight));
        _textCanvas = new Canvas { Width = width, Height = height, Background = Brushes.White, ClipToBounds = true };
        _caretCanvas = new Canvas { Width = width, Height = height, IsHitTestVisible = false };
        // 悬停按钮单独一层，跟着纸页一起滚动；按钮本身可点，不随行控件回收。
        _hoverCanvas = new Canvas { Width = width, Height = height, ClipToBounds = false };
        var sheet = new Border
        {
            Child = new Grid { Children = { _textCanvas, _caretCanvas, _hoverCanvas } },
            Width = width + 2, Height = height + 2, Background = Brushes.White,
            BorderBrush = Hex("#BDBDBD"), BorderThickness = new Thickness(1), ClipToBounds = true,
            Margin = new Thickness(0, SheetMargin, 6, SheetMargin), HorizontalAlignment = HorizontalAlignment.Left,
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 1, Blur = 3, Color = Color.Parse("#26000000") })
        };
        Grid.SetColumnSpan(sheet, 2);
        Children.Add(sheet);

        if (lines.Count == 0 || text.Length == 0)
        {
            _pendingCaret = (-1, 0, 0, double.NaN);
            var note = new TextBlock { Text = "无可提取文本（扫描件/空白页）；不包含 OCR。", Foreground = Hex("#7A7A7A"), Margin = new Thickness(8) };
            Canvas.SetTop(note, 8);
            _textCanvas.Children.Add(note);
            return;
        }
        if (_rows.Length == 0)
        {
            _pendingCaret = (-1, 0, 0, double.NaN);
            var message = _rowFilter switch
            {
                DiffRowFilter.Differences => "两侧文本逐行相同，没有差异行。",
                DiffRowFilter.Same => "当前筛选下没有相同行。",
                _ => "没有可显示的文本行。"
            };
            var note = new TextBlock { Text = message, Foreground = Hex("#7A7A7A"), Margin = new Thickness(8) };
            Canvas.SetTop(note, 8);
            _textCanvas.Children.Add(note);
            return;
        }
        RealizeVisibleRows();
        RestoreScroll(anchor);
        // 重建画布后悬停状态已经作废，指针没动就按上次的位置把按钮补回来。
        if (!double.IsNaN(_lastPointer.X)) UpdateHover(_lastPointer);
    }

    /// <summary>
    /// 点击或拖动时落当前行：文本模式记下源行号和行内列号，画布重建后据此把这一行找回来。
    /// </summary>
    private void PlaceCaret(int row, int position, double x)
    {
        if (_textCanvas is not null && row >= 0 && row < _rows.Length)
        {
            _caretLine = _rows[row];
            _caretColumn = Math.Max(0, position - _rowStarts[_caretLine]);
        }
        ShowCaret(row, x);
    }

    /// <summary>
    /// 重建画布之前记下当前行：源行号、选区两端，以及框顶离视口上沿还有多高。
    /// 距离用行顶坐标减滚动偏移算，不依赖布局；当前行已滚出视口时留 NaN，重建后只把框画回去，不动视口。
    /// </summary>
    private void CaptureCaret()
    {
        _pendingCaret = (-1, 0, 0, double.NaN);
        if (Mode != PdfExtractionMode.Text || _caretLine < 0) return;
        var delta = double.NaN;
        if (_scrollHost is not null && _caretRow >= 0)
        {
            var top = _caretRow * _rowHeight - _scrollHost.Offset.Y;
            if (top >= 0 && top + _rowHeight <= _scrollHost.Viewport.Height) delta = top;
        }
        _pendingCaret = (_caretLine, _anchor, _caret, delta);
    }

    /// <summary>
    /// 切换筛选/缩放会把画布重建，这里把视口摆回去。
    /// 当前行还在视口里时以它为准：停在屏幕上原来的高度，切换筛选后不用自己再滚动去找。
    /// 没有当前行（或它已滚出视口）时退回旧办法，让原来视口顶端那一行还在顶端。
    /// </summary>
    private void RestoreScroll(int anchorLine)
    {
        // 点了展开按钮：把点的那行钉在屏幕上原来的高度，展开在它上方插进来的行才不会把内容顶走。
        var (pinnedLine, pinnedDelta) = _pendingRow;
        _pendingRow = (-1, double.NaN);
        var pinnedIndex = pinnedLine >= 0 ? Array.BinarySearch(_rows, pinnedLine) : -1;
        if (_scrollHost is null || _rows.Length == 0)
        {
            RestoreCaret();
            return;
        }
        if (pinnedIndex >= 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_scrollHost is null || _textCanvas is null) return;
                var max = Math.Max(0, _scrollHost.Extent.Height - _scrollHost.Viewport.Height);
                var offsetY = pinnedIndex * _rowHeight - pinnedDelta;
                _scrollHost.Offset = new Vector(_scrollHost.Offset.X, Math.Clamp(offsetY, 0, max));
                RealizeVisibleRows();
                RestoreCaret();
            });
            return;
        }
        var (line, _, _, delta) = _pendingCaret;
        // 当前行在重建后的表里还在、而且原来就在视口里，才以它为准；
        // 否则（没选行、行被筛掉、或已滚出视口）按视口顶端那一行动，别把用户滚到的位置扔掉。
        var caretIndex = line >= 0 ? Array.BinarySearch(_rows, line) : -1;
        var keepCaret = caretIndex >= 0 && !double.IsNaN(delta);
        if (_scrollHost is null || _rows.Length == 0)
        {
            RestoreCaret();
            return;
        }
        var index = keepCaret ? caretIndex : anchorLine > 0 ? Array.BinarySearch(_rows, anchorLine) : -1;
        // 视口顶端那一行被筛掉了：落到它后面第一个仍在表里的行。
        if (!keepCaret && index < 0) index = ~index;
        if (index < 0 || index >= _rows.Length)
        {
            RestoreCaret();
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (_scrollHost is null || _textCanvas is null) return;
            var offsetY = keepCaret ? index * _rowHeight - delta : index * _rowHeight;
            var max = Math.Max(0, _scrollHost.Extent.Height - _scrollHost.Viewport.Height);
            _scrollHost.Offset = new Vector(_scrollHost.Offset.X, Math.Clamp(offsetY, 0, max));
            RealizeVisibleRows();
            RestoreCaret();
        });
    }

    /// <summary>重建后按记下的源行号和列号把当前行框、光标画回去；缩放改了字宽也仍钉在同一个字符上。</summary>
    private void RestoreCaret()
    {
        var (line, anchor, caret, _) = _pendingCaret;
        _pendingCaret = (-1, 0, 0, double.NaN);
        if (line < 0) return;
        _anchor = anchor;
        _caret = caret;
        ApplySelection();
        ReportCaret();
        if (_textCanvas is null || _caretCanvas is null) return;
        var index = Array.BinarySearch(_rows, line);
        if (index < 0) return; // 当前筛选隐藏了这一行：保留行号，切回适用筛选后再画回来
        ShowCaret(index, GutterWidth + TextLeftPad + _caretColumn * _charWidth);
    }

    /// <summary>当前视口顶端对应全文的哪一行，重建画布前先记下来。</summary>
    private int TopSourceLine()
    {
        if (_scrollHost is null || _rows.Length == 0) return 0;
        var index = Math.Clamp((int)Math.Round(_scrollHost.Offset.Y / _rowHeight), 0, _rows.Length - 1);
        return _rows[index];
    }

    /// <summary>纯文本模式靠外层 ScrollViewer 驱动虚拟化，挂载时找到它并监听滚动。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scrollHost = this.FindAncestorOfType<ScrollViewer>();
        if (_scrollHost is not null) _scrollHost.ScrollChanged += OnScrollChanged;
        RealizeVisibleRows();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scrollHost is not null) _scrollHost.ScrollChanged -= OnScrollChanged;
        _scrollHost = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        RealizeVisibleRows();
        // 滚动之后指针下面是另一行了；指针不会因此再发一次移动事件，这里补上。
        if (_hoverButton is not null && !double.IsNaN(_lastPointer.X)) UpdateHover(_lastPointer);
    }

    /// <summary>只保留 [first, last] 范围内的行控件，其余回收。</summary>
    private void RealizeRows(int first, int last)
    {
        if (_textCanvas is null) return;
        foreach (var index in _realizedRows.Keys.Where(i => i < first || i > last).ToArray())
        {
            _textCanvas.Children.Remove(_realizedRows[index]);
            _realizedRows.Remove(index);
            _realizedText.Remove(index);
        }
        for (var i = Math.Max(0, first); i <= last && i < _rows.Length; i++)
        {
            if (_realizedRows.ContainsKey(i)) continue;
            var row = CreateTextRow(i);
            Canvas.SetTop(row, i * _rowHeight);
            _textCanvas.Children.Add(row);
            _realizedRows[i] = row;
        }
        ApplySelection();
    }

    private sealed class MissingRowHatch : Control
    {
        private const double Spacing = 8;
        private static readonly IBrush FillBrush = Hex("#F5F5F5");
        private static readonly Pen StripePen = new(Hex("#C8C8C8"), 0.75);
        public double PatternOffsetY { get; init; }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var width = Bounds.Width;
            var height = Bounds.Height;
            if (width <= 0 || height <= 0) return;

            context.DrawRectangle(FillBrush, null, new Rect(0, 0, width, height));
            var phase = PatternOffsetY % Spacing;
            for (var sum = -phase; sum <= width + height; sum += Spacing)
            {
                var start = sum <= height ? new Point(0, sum) : new Point(sum - height, height);
                var end = sum <= width ? new Point(sum, 0) : new Point(width, sum - width);
                context.DrawLine(StripePen, start, end);
            }
        }
    }

    private Control CreateTextRow(int index)
    {
        var line = _textLines[_rows[index]];
        var changed = line.Type is ChangeType.Modified or ChangeType.Deleted or ChangeType.Inserted;
        var row = new Grid
        {
            Width = _textCanvas!.Width, Height = _rowHeight,
            ColumnDefinitions = new ColumnDefinitions($"{GutterWidth},*"),
            Background = changed ? ChangedBand : Brushes.Transparent
        };
        if (_showHighlights && line.Type == ChangeType.Imaginary)
        {
            var hatch = new MissingRowHatch { PatternOffsetY = index * _rowHeight, IsHitTestVisible = false };
            Grid.SetColumnSpan(hatch, 2);
            row.Children.Add(hatch);
        }
        var content = new SelectableTextBlock
        {
            FontFamily = TextFont, FontSize = TextFontSize * _zoom,
            Foreground = NormalText, TextWrapping = TextWrapping.NoWrap,
            SelectionBrush = new SolidColorBrush(Color.Parse("#1F6FEB")),
            SelectionForegroundBrush = Brushes.White, Background = Brushes.Transparent,
            Margin = new Thickness(TextLeftPad, 0, TextRightPad, 0)
        };
        if (line.Spans.Count > 0)
        {
            foreach (var span in line.Spans)
                content.Inlines!.Add(new Run(span.Text)
                {
                    Background = _showHighlights && span.IsChanged ? ChangedCharBack : Brushes.Transparent,
                    Foreground = _showHighlights && span.IsChanged ? ChangedCharText : NormalText
                });
        }
        else
        {
            content.Text = line.Text;
            if (_showHighlights && changed) content.Foreground = ChangedText;
        }
        Grid.SetColumn(content, 1);
        row.Children.Add(content);
        if (_showHighlights && changed)
        {
            var tick = new Rectangle
            {
                Width = GutterWidth - 6, Height = 3, RadiusX = 1, RadiusY = 1, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = line.Type == ChangeType.Inserted ? AddedMark : RemovedMark
            };
            row.Children.Add(tick);
        }
        _realizedText[index] = content;
        return row;
    }

    /// <summary>滚动时按视口重算可见行：上方和下方各留一段余量，快速滚动也不会看到空白。</summary>
    private void RealizeVisibleRows()
    {
        if (_textCanvas is null || _rows.Length == 0) return;
        double top = 0, bottom = Bounds.Height;
        if (_scrollHost is not null)
        {
            // 视口上沿在本控件里的位置 = 滚动偏移减去页面顶部的页边距。
            // 不用 TranslatePoint 读控件位置：刚设完 Offset 时布局还没跑，它给的还是旧滚动位置，
            // 按旧范围建行，屏幕就会留一片空白（取消"仅差异行"时最明显）。
            top = _scrollHost.Offset.Y - SheetMargin;
            bottom = top + _scrollHost.Viewport.Height;
        }
        var first = (int)Math.Floor(top / _rowHeight) - RowOverscan;
        var last = (int)Math.Ceiling(bottom / _rowHeight) + RowOverscan;
        RealizeRows(first, last);
    }

    /// <summary>
    /// 连续模式没有页可跳，只能按行跳：从当前差异行往下（direction &gt; 0）或往上找下一个差异行，
    /// 找到就把视口挪到它上方留几行余量。没有更多差异行时返回 false，由调用层给出提示。
    /// Differences 筛选时整张表都是差异行，这里照走不误。
    /// </summary>
    public bool ScrollToChangedLine(int direction)
    {
        if (_textCanvas is null || _scrollHost is null || _rows.Length == 0) return false;
        // 上次跳到的行优先（为了让差异行上方留出几行，视口顶行并不是差异行本身）；
        // 用户自己滚过之后就以当前视口顶行为准。
        var from = _jumpIndex >= 0 && Math.Abs(_scrollHost.Offset.Y - _jumpOffset) < 0.5
            ? _jumpIndex
            : (int)Math.Round(_scrollHost.Offset.Y / _rowHeight);
        if (direction > 0)
        {
            for (var i = Math.Max(0, from + 1); i < _rows.Length; i++)
                if (_page is not null && IsChangedRow(_page, _rows[i])) { ScrollToLine(i); return true; }
            return false;
        }
        for (var i = Math.Min(from - 1, _rows.Length - 1); i >= 0; i--)
            if (_page is not null && IsChangedRow(_page, _rows[i])) { ScrollToLine(i); return true; }
        return false;
    }

    private static bool IsChangedLine(TextDiffLine line) =>
        line.Type is ChangeType.Modified or ChangeType.Deleted or ChangeType.Inserted;

    /// <summary>Classifies aligned rows from both sides together; expanded page ranges are added only in Differences mode.</summary>
    internal static bool IsChangedRow(PageDifference page, int index) =>
        (index >= 0 && index < page.LeftLines.Count && IsChangedLine(page.LeftLines[index])) ||
        (index >= 0 && index < page.RightLines.Count && IsChangedLine(page.RightLines[index]));

    internal static int[] VisibleRows(PageDifference page, DiffRowFilter filter, IReadOnlyList<(int First, int Last)>? expanded = null)
    {
        var count = Math.Max(page.LeftLines.Count, page.RightLines.Count);
        if (filter == DiffRowFilter.All) return Enumerable.Range(0, count).ToArray();
        var rows = Enumerable.Range(0, count)
            .Where(index => filter == DiffRowFilter.Differences ? IsChangedRow(page, index) : !IsChangedRow(page, index))
            .ToList();
        if (filter == DiffRowFilter.Differences && expanded is not null && expanded.Count > 0)
        {
            var kept = new HashSet<int>(rows);
            foreach (var (first, last) in expanded)
                for (var i = Math.Max(0, first); i <= last && i < count; i++)
                    if (kept.Add(i)) rows.Add(i);
            rows.Sort();
        }
        return rows.ToArray();
    }

    private void ScrollToLine(int index)
    {
        // 只看差异行时看得见的每行都是差异，跳过去就不用再留上方余量。
        var lead = _rowFilter == DiffRowFilter.Differences ? 0 : RowsBeforeJump;
        var offset = Math.Max(0, index * _rowHeight - _rowHeight * lead);
        _scrollHost!.Offset = new Vector(_scrollHost.Offset.X, offset);
        _jumpIndex = index;
        _jumpOffset = offset;
    }

    private void ShowLayout(PdfTextLayout layout, IReadOnlyList<TextDiffLine> lines)
    {
        _selectionText = layout.Text;
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < layout.Text.Length; i++)
            if (layout.Text[i] == '\n') lineStarts.Add(i + 1);
        var scale = 96d / 72 * _zoom;
        var width = Math.Max(1, layout.Width * scale);
        var height = Math.Max(1, layout.Height * scale);
        var gutter = new Canvas { Width = GutterWidth, Height = height + SheetMargin * 2, Background = GutterBrush, ClipToBounds = true };
        var canvas = new Canvas { Width = width, Height = height, Background = Brushes.White, ClipToBounds = true };
        _caretCanvas = new Canvas { Width = width, Height = height, IsHitTestVisible = false };
        var sheet = new Border
        {
            Child = new Grid { Children = { canvas, _caretCanvas } }, Width = width + 2, Height = height + 2, Background = Brushes.White,
            BorderBrush = Hex("#BDBDBD"), BorderThickness = new Thickness(1), ClipToBounds = true,
            Margin = new Thickness(0, SheetMargin, 6, SheetMargin), HorizontalAlignment = HorizontalAlignment.Left,
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 1, Blur = 3, Color = Color.Parse("#26000000") })
        };
        Children.Add(gutter);
        Grid.SetColumn(sheet, 1);
        Children.Add(sheet);

        var byNumber = lines.Where(l => l.Number.HasValue).ToDictionary(l => l.Number!.Value);
        var rowIndexByNumber = lines.Select((line, index) => (line, index))
            .Where(pair => pair.line.Number.HasValue)
            .ToDictionary(pair => pair.line.Number!.Value, pair => pair.index);
        var visibleRows = VisibleRows(_page!, _rowFilter, _expanded).ToHashSet();
        var visibleBlocks = layout.Blocks
            .Where(block => rowIndexByNumber.TryGetValue(block.Line, out var index) && visibleRows.Contains(index))
            .ToList();
        var rows = new Dictionary<int, (double Top, double Bottom)>();
        foreach (var block in layout.Blocks)
        {
            var top = block.Y * scale;
            var bottom = top + block.FontSize * scale;
            rows[block.Line] = rows.TryGetValue(block.Line, out var row)
                ? (Math.Min(row.Top, top), Math.Max(row.Bottom, bottom))
                : (top, bottom);
            // 同一排版行可能有好几块（表格一行里多个单元格），横竖范围都取并集，当前行框才圈得住整行。
            var index = block.Line - 1;
            var left = block.X * scale;
            var right = (block.X + block.Width) * scale;
            _layoutRows[index] = _layoutRows.TryGetValue(index, out var box)
                ? (Math.Min(box.Left, left), Math.Max(box.Right, right), Math.Min(box.Top, top), Math.Max(box.Bottom, bottom))
                : (left, right, top, bottom);
        }
        _lineStarts.Clear();
        _lineStarts.AddRange(lineStarts);

        // Full-width tint behind changed rows, plus a mark in the gutter.
        foreach (var (number, extent) in rows)
        {
            if (!byNumber.TryGetValue(number, out var line) ||
                !rowIndexByNumber.TryGetValue(number, out var index) || !visibleRows.Contains(index)) continue;
            if (line.Type is not (ChangeType.Modified or ChangeType.Deleted or ChangeType.Inserted)) continue;
            var shade = Math.Max(extent.Bottom - extent.Top + 1, 6);
            var band = new Rectangle { Width = width, Height = shade, Fill = ChangedBand };
            Canvas.SetLeft(band, 0);
            Canvas.SetTop(band, extent.Top - 0.5);
            canvas.Children.Add(band);
            if (!_showHighlights) continue;
            var tickHeight = Math.Clamp(shade, 3, 10);
            var tick = new Rectangle
            {
                Width = GutterWidth - 6, Height = tickHeight, RadiusX = 1, RadiusY = 1,
                Fill = line.Type == ChangeType.Inserted ? AddedMark : RemovedMark
            };
            Canvas.SetLeft(tick, 3);
            Canvas.SetTop(tick, extent.Top + (shade - tickHeight) / 2);
            gutter.Children.Add(tick);
        }

        foreach (var block in visibleBlocks)
        {
            if (_page?.IgnoreWhitespace == true && string.IsNullOrWhiteSpace(block.Text)) continue;
            if (!byNumber.TryGetValue(block.Line, out var line)) continue;
            var changed = line.Type is ChangeType.Modified or ChangeType.Inserted or ChangeType.Deleted;
            var content = new SelectableTextBlock
            {
                FontFamily = new FontFamily("Arial,Microsoft YaHei"), FontSize = block.FontSize * scale,
                Foreground = NormalText,
                TextWrapping = TextWrapping.NoWrap,
                SelectionBrush = new SolidColorBrush(Color.Parse("#1F6FEB")),
                SelectionForegroundBrush = Brushes.White,
                Background = Brushes.Transparent
            };
            var offset = 0;
            foreach (var span in line.Spans)
            {
                var start = Math.Max(offset, block.Start);
                var end = Math.Min(offset + span.Text.Length, block.Start + block.Text.Length);
                if (end > start)
                    content.Inlines!.Add(new Run(span.Text[(start - offset)..(end - offset)])
                    {
                        // Changed characters carry a deep red chip; unchanged text stays plain black.
                        Background = _showHighlights && span.IsChanged ? ChangedCharBack : Brushes.Transparent,
                        Foreground = _showHighlights && span.IsChanged ? ChangedCharText : NormalText
                    });
                offset += span.Text.Length;
            }
            if (line.Spans.Count == 0)
            {
                content.Text = block.Text;
                if (_showHighlights && changed) content.Foreground = ChangedText;
            }
            // Fit the substitute font to the PDF block width without moving its origin.
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (content.DesiredSize.Width > 0 && block.Width > 0)
            {
                content.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
                content.RenderTransform = new ScaleTransform(block.Width * scale / content.DesiredSize.Width, 1);
            }
            Canvas.SetLeft(content, block.X * scale);
            Canvas.SetTop(content, block.Y * scale);
            canvas.Children.Add(content);
            _selectionBlocks.Add((content, lineStarts[block.Line - 1] + block.Start, block.Text.Length));
        }
        if (layout.Blocks.Count == 0 || visibleBlocks.Count == 0)
        {
            var message = layout.Blocks.Count == 0
                ? "无可提取文本（扫描件/空白页）；不包含 OCR。"
                : _rowFilter == DiffRowFilter.Differences
                    ? "两侧文本逐行相同，没有差异行。"
                    : "当前筛选下没有可显示的文本行。";
            var note = new TextBlock { Text = message, Foreground = Hex("#7A7A7A"), Margin = new Thickness(8) };
            Canvas.SetLeft(note, 8);
            Canvas.SetTop(note, 8);
            canvas.Children.Add(note);
        }
    }

    private void Refresh()
    {
        if (_page is null) return;
        // 重建前记住视口顶端那一行和屏幕上那行光标；切换筛选或缩放后阅读位置和当前行都不动。
        CaptureCaret();
        var anchor = Mode == PdfExtractionMode.Text ? TopSourceLine() : 0;
        ResetSelection();
        HideHover();
        Children.Clear();
        _textCanvas = null;
        _caretCanvas = null;
        _hoverCanvas = null;
        _textLines = [];
        _rows = [];
        _layoutRows.Clear();
        _realizedRows.Clear();
        _realizedText.Clear();
        var layout = IsLeft ? _page.LeftLayout : _page.RightLayout;
        if (layout is not null)
        {
            ShowLayout(layout, IsLeft ? _page.LeftLines : _page.RightLines);
            return;
        }
        var missing = IsLeft ? _page.LeftMissing : _page.RightMissing;
        var lines = IsLeft ? _page.LeftLines : _page.RightLines;
        var plain = IsLeft ? _page.LeftText : _page.RightText;
        if (Mode == PdfExtractionMode.Text && !missing)
        {
            ShowText(plain, lines, anchor);
            return;
        }
        var text = new TextBlock
        {
            Text = _rowFilter == DiffRowFilter.Same
                ? "当前筛选下没有相同行。"
                : missing ? "此版本中没有这一页。" : "另一侧此页缺失或无可提取文本；不包含 OCR。",
            Margin = new Thickness(16), Foreground = Hex("#7A7A7A"),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumnSpan(text, 2);
        Children.Add(text);
        _pendingCaret = (-1, 0, 0, double.NaN);
    }
}
