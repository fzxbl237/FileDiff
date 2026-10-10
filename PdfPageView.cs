using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using DiffPlex.DiffBuilder.Model;

namespace FileDiff;

public sealed class PdfPageView : StackPanel
{
    private PageDifference? _page;
    private bool _showHighlights = true;
    private double _zoom = 1;
    public bool IsLeft { get; init; }
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

    private readonly List<(SelectableTextBlock Control, int Start, int Length)> _selectionBlocks = [];
    private string _selectionText = "";
    private int _anchor, _caret;
    private bool _dragging;
    private IPointer? _selectionPointer;

    private void ResetSelection()
    {
        _dragging = false;
        _selectionPointer?.Capture(null);
        _selectionPointer = null;
        _selectionBlocks.Clear();
        _selectionText = "";
        _anchor = _caret = 0;
    }

    internal static string SelectionText(string text, int anchor, int caret)
    {
        var start = Math.Clamp(Math.Min(anchor, caret), 0, text.Length);
        var end = Math.Clamp(Math.Max(anchor, caret), 0, text.Length);
        return text[start..end];
    }

    private int HitPosition(Point point)
    {
        var best = _selectionBlocks.MinBy(b =>
        {
            var local = this.TranslatePoint(point, b.Control) ?? default;
            var dx = Math.Max(0, Math.Max(-local.X, local.X - b.Control.Bounds.Width));
            var dy = Math.Max(0, Math.Max(-local.Y, local.Y - b.Control.Bounds.Height));
            return dy * dy * 100 + dx * dx;
        });
        var position = this.TranslatePoint(point, best.Control) ?? default;
        return best.Start + Math.Clamp(best.Control.TextLayout.HitTestPoint(position).TextPosition, 0, best.Length);
    }

    private void UpdateSelection()
    {
        var start = Math.Min(_anchor, _caret);
        var end = Math.Max(_anchor, _caret);
        foreach (var block in _selectionBlocks)
        {
            block.Control.SelectionStart = Math.Clamp(start - block.Start, 0, block.Length);
            block.Control.SelectionEnd = Math.Clamp(end - block.Start, 0, block.Length);
        }
    }

    public PdfPageView()
    {
        Focusable = true;
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (_selectionBlocks.Count == 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            Focus();
            _anchor = _caret = HitPosition(e.GetPosition(this));
            _dragging = true;
            _selectionPointer = e.Pointer;
            e.Pointer.Capture(this);
            UpdateSelection();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (!_dragging || _selectionBlocks.Count == 0) return;
            _caret = HitPosition(e.GetPosition(this));
            UpdateSelection();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
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
        Margin = new Thickness(8);
        ShowMessage("请选择两个 PDF 文件。提取的文本将在这里显示。");
    }

    public void ShowMessage(string message)
    {
        _page = null;
        ResetSelection();
        Children.Clear();
        Children.Add(new TextBlock { Text = message, Margin = new Thickness(12), Foreground = Brushes.LightGray });
    }

    public void SetPage(PageDifference difference)
    {
        _page = difference;
        Refresh();
    }

    private void ShowLayout(PdfTextLayout layout, IReadOnlyList<TextDiffLine> lines)
    {
        _selectionText = layout.Text;
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < layout.Text.Length; i++)
            if (layout.Text[i] == '\n') lineStarts.Add(i + 1);
        var scale = 96d / 72 * _zoom;
        var canvas = new Canvas
        {
            Width = layout.Width * scale, Height = layout.Height * scale,
            Background = new SolidColorBrush(Color.Parse("#161B22")), ClipToBounds = true
        };
        var byNumber = lines.Where(l => l.Number.HasValue).ToDictionary(l => l.Number!.Value);
        foreach (var block in layout.Blocks)
        {
            if (_page?.IgnoreWhitespace == true && string.IsNullOrWhiteSpace(block.Text)) continue;
            if (!byNumber.TryGetValue(block.Line, out var line)) continue;
            var changed = line.Type is ChangeType.Modified or ChangeType.Inserted or ChangeType.Deleted;
            var content = new SelectableTextBlock
            {
                FontFamily = new FontFamily("Arial,Microsoft YaHei"), FontSize = block.FontSize * scale,
                Foreground = _showHighlights && changed
                    ? new SolidColorBrush(Color.Parse(IsLeft ? "#FFD5CB" : "#C0F4E8")) : Brushes.WhiteSmoke,
                TextWrapping = TextWrapping.NoWrap,
                SelectionBrush = new SolidColorBrush(Color.Parse("#1F6FEB")),
                SelectionForegroundBrush = Brushes.White,
                Background = _showHighlights && changed
                    ? new SolidColorBrush(Color.Parse(IsLeft ? "#51251F" : "#173F39")) : Brushes.Transparent
            };
            var offset = 0;
            foreach (var span in line.Spans)
            {
                var start = Math.Max(offset, block.Start);
                var end = Math.Min(offset + span.Text.Length, block.Start + block.Text.Length);
                if (end > start)
                    content.Inlines!.Add(new Run(span.Text[(start - offset)..(end - offset)])
                    {
                        Background = _showHighlights && span.IsChanged
                            ? new SolidColorBrush(Color.Parse(IsLeft ? "#B54A36" : "#287F70")) : Brushes.Transparent
                    });
                offset += span.Text.Length;
            }
            if (line.Spans.Count == 0) content.Text = block.Text;
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
        Children.Add(canvas);
        if (layout.Blocks.Count == 0)
            Children.Add(new TextBlock { Text = "无可提取文本（扫描件/空白页）；不包含 OCR。", Foreground = Brushes.LightGray });
    }

    private void Refresh()
    {
        if (_page is null) return;
        ResetSelection();
        Children.Clear();
        var layout = IsLeft ? _page.LeftLayout : _page.RightLayout;
        if (layout is not null)
        {
            ShowLayout(layout, IsLeft ? _page.LeftLines : _page.RightLines);
            return;
        }
        var missing = IsLeft ? _page.LeftMissing : _page.RightMissing;
        var text = IsLeft ? _page.LeftText : _page.RightText;
        // Put the warning on both sides to preserve aligned row heights.
        if (_page.LeftMissing || _page.RightMissing || string.IsNullOrWhiteSpace(_page.LeftText) || string.IsNullOrWhiteSpace(_page.RightText))
            Children.Add(new TextBlock
            {
                Text = missing ? "此版本中没有这一页。" : string.IsNullOrWhiteSpace(text) ? "无可提取文本（扫描件/空白页）；不包含 OCR。" : "另一侧此页缺失或无可提取文本。",
                Height = 32, Margin = new Thickness(6, 0), Foreground = Brushes.LightGray
            });
        var lines = IsLeft ? _page.LeftLines : _page.RightLines;
        foreach (var line in lines)
        {
            var changed = line.Type is ChangeType.Deleted or ChangeType.Inserted or ChangeType.Modified;
            IBrush background = _showHighlights && changed
                ? new SolidColorBrush(Color.Parse(IsLeft ? "#51251F" : "#173F39")) : Brushes.Transparent;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("60,*"), Height = 28 * _zoom, Background = background };
            row.Children.Add(new TextBlock
            {
                Text = line.Number?.ToString() ?? "", FontSize = 14 * _zoom,
                Foreground = Brushes.Gray, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center
            });
            var content = new SelectableTextBlock
            {
                Text = line.Type == ChangeType.Imaginary ? " " : line.Text,
                FontFamily = new FontFamily("Consolas,Microsoft YaHei"), FontSize = 14 * _zoom,
                Foreground = _showHighlights && changed
                    ? new SolidColorBrush(Color.Parse(IsLeft ? "#FFD5CB" : "#C0F4E8")) : Brushes.WhiteSmoke,
                Margin = new Thickness(4, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center
            };
            if (_showHighlights && line.Spans.Count > 0)
            {
                content.Text = null;
                foreach (var span in line.Spans)
                    content.Inlines!.Add(new Run(span.Text)
                    {
                        Background = span.IsChanged
                            ? new SolidColorBrush(Color.Parse(IsLeft ? "#B54A36" : "#287F70")) : Brushes.Transparent
                    });
            }
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            Children.Add(row);
        }
    }
}
