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
    public bool IsChanged => LeftMissing || RightMissing || LeftLines.Any(l => l.Type != ChangeType.Unchanged) || RightLines.Any(l => l.Type != ChangeType.Unchanged);
    public override string ToString() => $"第 {Page + 1} 页 · {(LeftMissing ? "新增页" : RightMissing ? "删除页" : IsChanged ? "文本变化" : "文本相同")}";
}
public sealed record Comparison(IReadOnlyList<PageDifference> Pages);

public static class PdfComparer
{
    public static Comparison Compare(string leftPath, string rightPath, bool ignoreWhitespace, IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
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
        return new Comparison(results);
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
