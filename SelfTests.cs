using System.Text;
using DiffPlex.DiffBuilder.Model;

namespace FileDiff;

internal static class SelfTests
{
    public static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FileDiff-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Check(!PdfComparer.ComparePages(0, "相同\n文本", "相同\n文本").IsChanged, "相同文本");
            Check(PdfComparer.ComparePages(0, "剂量 10", "剂量 20").IsChanged, "中文与数值变化");
            var inserted = PdfComparer.ComparePages(0, "A\nC", "A\nB\nC");
            Check(inserted.LeftLines.Count == inserted.RightLines.Count && inserted.LeftLines.Any(l => l.Type == ChangeType.Imaginary) && inserted.RightLines.Any(l => l.Type == ChangeType.Inserted), "新增行和占位对齐");
            Check(PdfComparer.ComparePages(0, "A\nB", "A").LeftLines.Any(l => l.Type == ChangeType.Deleted), "删除行");
            Check(!PdfComparer.ComparePages(0, "A\r\nB", "A\nB").IsChanged, "换行规范化");
            Check(!PdfComparer.ComparePages(0, "A  B", "A B", true).IsChanged, "忽略空白");
            Check(PdfComparer.ComparePages(0, "A  B", "A B", false).IsChanged, "保留空白差异");
            Check(PdfComparer.ComparePages(0, null, "").LeftMissing, "新增空白页");
            Check(PdfComparer.ComparePages(0, "", null).RightMissing, "删除空白页");
            static string Highlighted(TextDiffLine line) => string.Concat(line.Spans.Where(s => s.IsChanged).Select(s => s.Text));
            var numeric = PdfComparer.ComparePages(0, "剂量 10 mg", "剂量 20 mg");
            Check(Highlighted(numeric.LeftLines[0]) == "1" && Highlighted(numeric.RightLines[0]) == "2", "只高亮变化数字");
            var multiple = PdfComparer.ComparePages(0, "A1 B2 C", "A3 B4 C");
            Check(Highlighted(multiple.LeftLines[0]) == "12" && Highlighted(multiple.RightLines[0]) == "34", "一行多处字符变化");
            var inlineAdded = PdfComparer.ComparePages(0, "Dose mg", "Dose 10 mg");
            Check(Highlighted(inlineAdded.LeftLines[0]) == "" && Highlighted(inlineAdded.RightLines[0]) == "10 ", "行内新增字符");
            var inlineDeleted = PdfComparer.ComparePages(0, "Dose 10 mg", "Dose mg");
            Check(Highlighted(inlineDeleted.LeftLines[0]) == "10 " && Highlighted(inlineDeleted.RightLines[0]) == "", "行内删除字符");
            var spaces = PdfComparer.ComparePages(0, " A\t B ", "AB", true);
            Check(!spaces.IsChanged && spaces.LeftLines[0].Text == " A\t B " && spaces.RightLines[0].Text == "AB", "忽略空格与制表符但保留原文");
            var mapped = PdfComparer.ComparePages(0, "剂 量\t10 mg", "剂量 20mg", true);
            Check(Highlighted(mapped.LeftLines[0]) == "1" && Highlighted(mapped.RightLines[0]) == "2" && string.Concat(mapped.LeftLines[0].Spans.Select(s => s.Text)) == "剂 量\t10 mg", "忽略空白后高亮映射到原文");
            Check(PdfComparer.ComparePages(0, "A\nB", "AB", true).IsChanged, "忽略空白仍保留换行差异");
            Check(inserted.RightLines.Where(l => l.Type == ChangeType.Inserted).All(l => Highlighted(l) == l.Text), "新增整行字符高亮");
            Check(numeric.LeftLines.Concat(numeric.RightLines).All(l => string.Concat(l.Spans.Select(s => s.Text)) == l.Text), "字符片段完整保留文本");
            var blankLines = PdfComparer.ComparePages(0, "A\nB", " \t\nA\n\u00a0\nB\n  ", true);
            Check(!blankLines.IsChanged && blankLines.RightLines.Select(l => l.Number).SequenceEqual(new int?[] { 2, 4 }), "忽略纯空白行并保留原行号");
            Check(PdfComparer.ComparePages(0, "A\nB", "A\n \nB", false).IsChanged, "关闭忽略空白时保留空白行差异");
            Check(!PdfComparer.ComparePages(0, "", " \t\n\u00a0", true).IsChanged, "纯空白页面文本不产生差异");
            Check(PdfComparer.ComparePages(0, "A\nB", "A\n \nC", true).IsChanged, "过滤空白行后仍识别文字变化");
            Check(PdfPageView.SelectionText("ABC\nDEF", 1, 6) == "BC\nDE", "跨行选择保留换行");
            Check(PdfPageView.SelectionText("ABC\nDEF", 6, 1) == "BC\nDE", "反向跨行选择");
            Check(PdfPageView.SelectionText("ABC", 2, 2) == "", "单击无选择");
            var a = Path.Combine(directory, "a.pdf");
            var b = Path.Combine(directory, "b.pdf");
            var c = Path.Combine(directory, "c.pdf");
            File.WriteAllBytes(a, MakePdf("Dose 10", false));
            File.WriteAllBytes(b, MakePdf("Dose 20", false));
            File.WriteAllBytes(c, MakePdf("Dose 10", true));
            var equal = PdfComparer.Compare(a, a, false, null, CancellationToken.None);
            Check(equal.Pages.Count == 1 && equal.Pages[0].LeftText.Contains("Dose 10") && !equal.Pages[0].IsChanged, "真实 PDF 文字提取与相同文件");
            var changed = PdfComparer.Compare(a, b, false, null, CancellationToken.None);
            Check(changed.Pages[0].IsChanged && changed.Pages[0].RightText.Contains("Dose 20"), "真实 PDF 数值差异");
            var added = PdfComparer.Compare(a, c, false, null, CancellationToken.None);
            Check(added.Pages.Count == 2 && added.Pages[1].LeftMissing, "真实 PDF 新增页");
            var positioned = Path.Combine(directory, "positioned.pdf");
            File.WriteAllBytes(positioned, MakePdf("", false,
                "BT /F1 12 Tf 10 120 Td (Left) Tj ET\nBT /F1 12 Tf 80 120 Td (Right) Tj ET\nBT /F1 12 Tf 80 50 Td (Lower) Tj ET\n"));
            var layoutPage = PdfComparer.Compare(positioned, positioned, false, null, CancellationToken.None).Pages[0];
            var layout = layoutPage.LeftLayout!;
            Check(layout.Width == 120 && layout.Height == 160, "保留 PDF 页尺寸");
            var leftBlock = layout.Blocks.Single(t => t.Text == "Left");
            var rightBlock = layout.Blocks.Single(t => t.Text == "Right");
            var lowerBlock = layout.Blocks.Single(t => t.Text == "Lower");
            Check(rightBlock.X > layout.Width / 2 && leftBlock.X < layout.Width / 2, "保留左右文本块位置");
            Check(Math.Abs(leftBlock.Y - rightBlock.Y) < 1 && lowerBlock.Y > rightBlock.Y + 50, "保留同行与纵向间距");
            Check(layout.Blocks.All(t => layout.Text.Split('\n')[t.Line - 1].Substring(t.Start, t.Text.Length) == t.Text), "布局文本块映射到比较行");
            Check(!layoutPage.IsChanged, "布局提取相同 PDF 不产生差异");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { PdfComparer.Compare(a, b, false, null, cancelled.Token); throw new Exception("取消未生效"); }
            catch (OperationCanceledException) { Console.WriteLine("PASS: 比较取消"); }
            Console.WriteLine("全部 34 项文本差异自测通过。");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(directory, true); }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine("PASS: " + name);
    }

    private static byte[] MakePdf(string value, bool twoPages, string? content = null)
    {
        var stream = content ?? $"BT /F1 12 Tf 10 120 Td ({value}) Tj ET\n";
        var resources = "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            twoPages ? "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>" : "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 120 160] {resources} /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream"
        };
        if (twoPages) objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 120 160] {resources} /Contents 4 0 R >>");
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(text.ToString()));
            text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(text.ToString());
        text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
