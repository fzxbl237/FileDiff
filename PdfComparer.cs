using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace FileDiff;

public sealed record PositionedText(string Text, int Line, int Start, double X, double Y, double Width, double FontSize);
public sealed record PdfTextLayout(double Width, double Height, string Text, IReadOnlyList<PositionedText> Blocks);
public sealed record TextDiffSpan(string Text, bool IsChanged);
public sealed record TextDiffLine(string Text, int? Number, ChangeType Type)
{
    public IReadOnlyList<TextDiffSpan> Spans { get; init; } = [];
}
public sealed record PageDifference(int Page, bool LeftMissing, bool RightMissing, string LeftText, string RightText,
    IReadOnlyList<TextDiffLine> LeftLines, IReadOnlyList<TextDiffLine> RightLines)
{
    public bool IgnoreWhitespace { get; init; }
    public PdfTextLayout? LeftLayout { get; init; }
    public PdfTextLayout? RightLayout { get; init; }
    /// <summary>连续比较时没有页码，由比较层给出一个标题。</summary>
    public string? Label { get; init; }
    /// <summary>连续比较时每一页的第一行在整篇文本里是第几行（0 起）；分页比较时为空。</summary>
    public IReadOnlyList<int> LeftPageStarts { get; init; } = [];
    public IReadOnlyList<int> RightPageStarts { get; init; } = [];
    public bool IsChanged => LeftMissing || RightMissing || LeftLines.Any(l => l.Type != ChangeType.Unchanged) || RightLines.Any(l => l.Type != ChangeType.Unchanged);
    /// <summary>两侧行表同下标：第 row 行所在的那一页，在行表里从第几行到第几行。两侧页码可能不同，取并集。</summary>
    public (int First, int Last) PageRowRange(int row)
    {
        var first = -1;
        var last = -1;
        void Merge(IReadOnlyList<int> starts, IReadOnlyList<TextDiffLine> lines)
        {
            var page = PageIndex(starts, lines, row);
            if (page < 0) return;
            for (var i = 0; i < lines.Count; i++)
            {
                if (PageIndex(starts, lines, i) != page) continue;
                if (first < 0 || i < first) first = i;
                if (i > last) last = i;
            }
        }
        Merge(LeftPageStarts, LeftLines);
        Merge(RightPageStarts, RightLines);
        return first < 0 ? (row, row) : (first, last);
    }

    /// <summary>第 row 行属于第几页（0 起）。没有页码信息时返回 -1；对侧空占位行跟着上一行算。</summary>
    private static int PageIndex(IReadOnlyList<int> starts, IReadOnlyList<TextDiffLine> lines, int row)
    {
        if (starts.Count == 0 || row < 0 || row >= lines.Count) return -1;
        while (row >= 0 && lines[row].Number is null) row--;
        if (row < 0 || lines[row].Number is not int number) return -1;
        // starts[p] 是第 p 页第一行的 0 起行号，所以 starts[p] < number 就算进第 p 页。
        var page = 0;
        for (var i = 1; i < starts.Count && starts[i] < number; i++) page = i;
        return page;
    }
    public override string ToString() => Label ?? $"第 {Page + 1} 页 · {(LeftMissing ? "新增页" : RightMissing ? "删除页" : IsChanged ? "文本变化" : "文本相同")}";
}
public sealed record Comparison(IReadOnlyList<PageDifference> Pages)
{
    /// <summary>实际生效的提取方式：PdfPig 页面文本全文模式或页面坐标排版模式。</summary>
    public PdfExtractionMode Mode { get; init; }
    /// <summary>整篇连续比较：Pages 只有一项装着全文，两侧页数不同也按内容对齐。</summary>
    public bool Continuous { get; init; }
    public int LeftPages { get; init; }
    public int RightPages { get; init; }
    /// <summary>这次用的转换/提取方式，显示在状态栏里。</summary>
    public string Engine { get; init; } = "PdfPig";
    /// <summary>两侧格式不同时给出两边的名字，例如 "RTF / Word"。</summary>
    public string Format { get; init; } = "PDF";
    /// <summary>文本里带页边界（PDF 由 PdfPig 逐页提取）；RTF/DOCX 是整篇一段。</summary>
    public bool Paged { get; init; }
}

/// <summary>文本提取方式：Layout 按页面坐标显示，Text 按坐标恢复行列后连续比较。</summary>
public enum PdfExtractionMode
{
    Layout,
    Text
}

public static class PdfComparer
{
    public static Comparison Compare(string leftPath, string rightPath, bool ignoreWhitespace, IProgress<string>? progress, CancellationToken token)
        => Compare(leftPath, rightPath, PdfExtractionMode.Layout, ignoreWhitespace, progress, token);

    /// <summary>
    /// 按扩展名分派：PDF 全文与排版提取都由 PdfPig 完成；有一侧是 RTF/DOCX 时一律整篇比文本。
    /// </summary>
    public static Comparison Compare(string leftPath, string rightPath, PdfExtractionMode mode, bool ignoreWhitespace, IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (TextDocumentConverter.TryConvert(leftPath, rightPath, mode, progress, token) is not { } converted)
            return CompareLayout(leftPath, rightPath, ignoreWhitespace, progress, token);
        return CompareText(converted, ignoreWhitespace, progress, token);
    }

    /// <summary>
    /// 全文路径：转换器给出的是全文，这里整篇连续比较。
    /// 两侧页数常常不同（内容增删会推动后面的页），逐页切会被页码错位放大成假差异，
    /// 所以这里只按内容对齐，页码仅作为下一行以前的背景信息。
    /// </summary>
    private static Comparison CompareText(ConvertedDocument document, bool ignoreWhitespace, IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var left = document.Left;
        var right = document.Right;
        progress?.Report(document.LeftPaged
            ? $"正在比较全文（旧版 {left.Count} 页 / 新版 {right.Count} 页）…"
            : "正在比较整篇文本…");
        var difference = ComparePages(0, Join(left), Join(right), ignoreWhitespace) with
        {
            Label = "全文连续比较",
            // 没有页边界的格式不设页起点：那样也不会冒出"展开整页"的按钮。
            LeftPageStarts = document.LeftPaged ? PageStarts(left) : [],
            RightPageStarts = document.RightPaged ? PageStarts(right) : []
        };
        return new Comparison([difference])
        {
            Mode = PdfExtractionMode.Text, Continuous = true,
            LeftPages = left.Count, RightPages = right.Count,
            Engine = document.Engine, Paged = document.LeftPaged && document.RightPaged,
            Format = document.Format
        };
    }

    /// <summary>每页第一行在整篇里有第几行：页与页之间隔着一行空行，行号要跟着页内的行数走。</summary>
    private static IReadOnlyList<int> PageStarts(IReadOnlyList<string> pages)
    {
        var starts = new List<int>(pages.Count);
        var line = 0;
        foreach (var page in pages)
        {
            starts.Add(line);
            line += page.Split('\n').Length + 1;
        }
        return starts;
    }

    /// <summary>页与页之间补一个空行，这样页码边界仍然可读，但不会伪造出行的差异。</summary>
    private static string Join(IReadOnlyList<string> pages) => string.Join("\n\n", pages).TrimEnd('\n');

    /// <summary>原有的提取路径：PdfPig 逐页取字形，再按坐标还原成排版。</summary>
    private static Comparison CompareLayout(string leftPath, string rightPath, bool ignoreWhitespace, IProgress<string>? progress, CancellationToken token)
    {
        using var left = PdfDocument.Open(leftPath);
        using var right = PdfDocument.Open(rightPath);
        var count = Math.Max(left.NumberOfPages, right.NumberOfPages);
        var results = new List<PageDifference>();
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"正在提取并比较第 {i + 1} / {count} 页文本…");
            var a = i < left.NumberOfPages ? ExtractLayout(left.GetPage(i + 1)) : null;
            token.ThrowIfCancellationRequested();
            var b = i < right.NumberOfPages ? ExtractLayout(right.GetPage(i + 1)) : null;
            results.Add(ComparePages(i, a?.Text, b?.Text, ignoreWhitespace) with { LeftLayout = a, RightLayout = b });
        }
        token.ThrowIfCancellationRequested();
        return new Comparison(results)
        {
            Engine = "PdfPig 原页坐标", Paged = true,
            Format = TextDocumentConverter.FormatOf(TextDocumentConverter.KindOf(leftPath), TextDocumentConverter.KindOf(rightPath))
        };
    }

    private static PdfTextLayout ExtractLayout(Page page)
    {
        var rows = new List<List<Letter>>();
        foreach (var letter in page.Letters.Where(l => !string.IsNullOrEmpty(l.Value)).OrderByDescending(l => l.StartBaseLine.Y).ThenBy(l => l.StartBaseLine.X))
        {
            var row = rows.LastOrDefault();
            if (row is null || Math.Abs(row[0].StartBaseLine.Y - letter.StartBaseLine.Y) > Math.Max(2, Math.Min(row[0].PointSize, letter.PointSize) * 0.25))
            {
                row = [];
                rows.Add(row);
            }
            row.Add(letter);
        }
        var blocks = new List<PositionedText>();
        var texts = new List<string>();
        foreach (var row in rows)
        {
            var ordered = row.OrderBy(l => l.StartBaseLine.X).ToArray();
            var text = new System.Text.StringBuilder();
            var run = new List<Letter>();
            var start = 0;
            void Flush()
            {
                if (run.Count == 0) return;
                var value = text.ToString(start, text.Length - start);
                var x = run[0].StartBaseLine.X;
                var size = Math.Max(1, run.Average(l => l.PointSize));
                blocks.Add(new(value, texts.Count + 1, start, x,
                    page.Height - run[0].StartBaseLine.Y - size,
                    Math.Max(1, run.Max(l => Math.Max(l.EndBaseLine.X, l.StartBaseLine.X + l.Width)) - x), size));
                run.Clear();
            }
            Letter? previous = null;
            foreach (var letter in ordered)
            {
                var gap = previous is null ? 0 : letter.StartBaseLine.X - previous.EndBaseLine.X;
                var size = Math.Max(1, letter.PointSize);
                if (previous is not null && gap > size * 1.5)
                {
                    Flush();
                    text.Append(' ');
                    start = text.Length;
                }
                else if (previous is not null && gap > size * 0.2 && !char.IsWhiteSpace(text[^1]) && !char.IsWhiteSpace(letter.Value[0]))
                    text.Append(' ');
                run.Add(letter);
                text.Append(letter.Value);
                previous = letter;
            }
            Flush();
            texts.Add(text.ToString());
        }
        return new(page.Width, page.Height, string.Join("\n", texts), blocks);
    }

    /// <summary>
    /// 供全文连续比较使用的纯文本布局：按字形坐标恢复行，并把横向距离换成空格，
    /// 避免 PdfPig 的默认 Page.Text 阅读顺序把表格列挤成连续字段。
    /// </summary>
    internal static string ExtractText(Page page)
    {
        var layout = ExtractLayout(page);
        if (layout.Blocks.Count == 0) return layout.Text;

        static int Spaces(double distance, double unit)
        {
            if (!double.IsFinite(distance) || distance <= 0) return 0;
            return (int)Math.Clamp(Math.Round(distance / unit), 0, 4096);
        }

        var lines = layout.Text.Split('\n');
        var blocksByLine = layout.Blocks.GroupBy(block => block.Line)
            .ToDictionary(group => group.Key, group => group.OrderBy(block => block.X).ToArray());
        var result = new System.Text.StringBuilder();
        for (var line = 1; line <= lines.Length; line++)
        {
            if (line > 1) result.Append('\n');
            if (!blocksByLine.TryGetValue(line, out var blocks) || blocks.Length == 0) continue;
            var spaceWidth = Math.Max(1, blocks.Average(block => block.FontSize) * 0.5);
            var first = blocks[0];
            result.Append(' ', Spaces(first.X, spaceWidth));
            result.Append(first.Text);
            var right = first.X + first.Width;
            foreach (var block in blocks.Skip(1))
            {
                result.Append(' ', Spaces(block.X - right, spaceWidth));
                result.Append(block.Text);
                right = Math.Max(right, block.X + block.Width);
            }
        }
        return result.ToString();
    }

    public static PageDifference ComparePages(int page, string? left, string? right, bool ignoreWhitespace = false)
    {
        static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var a = Normalize(left);
        var b = Normalize(right);
        var oldLines = a.Split('\n');
        var newLines = b.Split('\n');
        string Key(string text) => ignoreWhitespace ? new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()) : text;
        var oldNumbers = Enumerable.Range(0, oldLines.Length).Where(i => !ignoreWhitespace || !string.IsNullOrWhiteSpace(oldLines[i])).ToArray();
        var newNumbers = Enumerable.Range(0, newLines.Length).Where(i => !ignoreWhitespace || !string.IsNullOrWhiteSpace(newLines[i])).ToArray();
        var diff = new SideBySideDiffBuilder().BuildDiffModel(
            string.Join("\n", oldNumbers.Select(i => Key(oldLines[i]))), string.Join("\n", newNumbers.Select(i => Key(newLines[i]))), false);
        TextDiffLine Convert(DiffPiece piece, string[] originals, int[] numbers) =>
            piece.Position is int number && number <= numbers.Length
                ? new(originals[numbers[number - 1]], numbers[number - 1] + 1, piece.Type)
                : new("", null, piece.Type);
        var oldRows = diff.OldText.Lines.Select(p => Convert(p, oldLines, oldNumbers)).ToArray();
        var newRows = diff.NewText.Lines.Select(p => Convert(p, newLines, newNumbers)).ToArray();
        for (var i = 0; i < oldRows.Length; i++)
        {
            var oldRow = oldRows[i];
            var newRow = newRows[i];
            var oldMask = new bool[oldRow.Text.Length];
            var newMask = new bool[newRow.Text.Length];
            if (oldRow.Type == ChangeType.Modified || newRow.Type == ChangeType.Modified)
            {
                var oldMap = Enumerable.Range(0, oldRow.Text.Length).Where(j => !ignoreWhitespace || !char.IsWhiteSpace(oldRow.Text[j])).ToArray();
                var newMap = Enumerable.Range(0, newRow.Text.Length).Where(j => !ignoreWhitespace || !char.IsWhiteSpace(newRow.Text[j])).ToArray();
                var characters = new Differ().CreateCharacterDiffs(Key(oldRow.Text), Key(newRow.Text), false);
                foreach (var block in characters.DiffBlocks)
                {
                    for (var j = block.DeleteStartA; j < block.DeleteStartA + block.DeleteCountA; j++) oldMask[oldMap[j]] = true;
                    for (var j = block.InsertStartB; j < block.InsertStartB + block.InsertCountB; j++) newMask[newMap[j]] = true;
                }
            }
            else
            {
                if (oldRow.Type == ChangeType.Deleted) Array.Fill(oldMask, true);
                if (newRow.Type == ChangeType.Inserted) Array.Fill(newMask, true);
            }
            oldRows[i] = oldRow with { Spans = MakeSpans(oldRow.Text, oldMask) };
            newRows[i] = newRow with { Spans = MakeSpans(newRow.Text, newMask) };
        }
        return new(page, left is null, right is null, a, b, oldRows, newRows) { IgnoreWhitespace = ignoreWhitespace };
    }

    private static IReadOnlyList<TextDiffSpan> MakeSpans(string text, bool[] changed)
    {
        // Keep UTF-16 surrogate pairs in the same styled run.
        for (var i = 0; i + 1 < text.Length; i++)
            if (char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1]))
                changed[i] = changed[i + 1] = changed[i] || changed[i + 1];
        var spans = new List<TextDiffSpan>();
        for (var start = 0; start < text.Length;)
        {
            var end = start + 1;
            while (end < text.Length && changed[end] == changed[start]) end++;
            spans.Add(new(text[start..end], changed[start]));
            start = end;
        }
        return spans;
    }
}
