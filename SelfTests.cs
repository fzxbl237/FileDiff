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
            var filtered = PdfComparer.ComparePages(0, "A\nB\nC", "A\nX\nC");
            Check(PdfPageView.VisibleRows(filtered, DiffRowFilter.All).SequenceEqual(new[] { 0, 1, 2 }), "all 显示全部行");
            Check(PdfPageView.VisibleRows(filtered, DiffRowFilter.Differences).SequenceEqual(new[] { 1 }), "diffs 只留变化行");
            Check(PdfPageView.VisibleRows(filtered, DiffRowFilter.Same).SequenceEqual(new[] { 0, 2 }), "same 只留相同行");
            var bothSides = PdfComparer.ComparePages(0, "A\nB\nC", "A\nX\nC\nD");
            Check(PdfPageView.VisibleRows(bothSides, DiffRowFilter.Differences).SequenceEqual(new[] { 1, 3 }), "diffs 两侧行表一致");
            Check(PdfPageView.VisibleRows(bothSides, DiffRowFilter.Same).SequenceEqual(new[] { 0, 2 }), "same 两侧行表一致");
            var imaginary = PdfComparer.ComparePages(0, "A\nC", "A\nB\nC");
            Check(PdfPageView.VisibleRows(imaginary, DiffRowFilter.Differences).SequenceEqual(new[] { 1 }), "diffs 带上对侧空占位行");
            Check(PdfPageView.IsChangedRow(imaginary, 1), "占位行导航沿对侧差异行同步");
            Check(PdfPageView.VisibleRows(imaginary, DiffRowFilter.Same).SequenceEqual(new[] { 0, 2 }), "same 排除插入行及其空占位行");
            var paged = PdfComparer.ComparePages(0, "A\nB\nC\nD", "A\nB\nC\nD") with
            {
                LeftPageStarts = new[] { 0, 2 }, RightPageStarts = new[] { 0, 2 }
            };
            Check(paged.PageRowRange(3) == (2, 3), "连续比较按页取行范围");
            var expandable = PdfComparer.ComparePages(0, "A\nB\nC\nD", "A\nX\nC\nD");
            Check(PdfPageView.VisibleRows(expandable, DiffRowFilter.Differences, new[] { (2, 3) }).SequenceEqual(new[] { 1, 2, 3 }), "差异筛选下展开页把整页的行加回行表");
            Check(PdfPageView.VisibleRows(expandable, DiffRowFilter.Differences).SequenceEqual(new[] { 1 }), "展开不影响未展开时筛选");
            Check(PdfPageView.VisibleRows(expandable, DiffRowFilter.Same, new[] { (1, 1) }).SequenceEqual(new[] { 0, 2, 3 }), "same 不被差异页展开影响");
            var a = Path.Combine(directory, "a.pdf");
            var b = Path.Combine(directory, "b.pdf");
            var c = Path.Combine(directory, "c.pdf");
            File.WriteAllBytes(a, MakePdf("Dose 10", false));
            File.WriteAllBytes(b, MakePdf("Dose 20", false));
            File.WriteAllBytes(c, MakePdf("Dose 10", true));
            var equal = PdfComparer.Compare(a, a, false, null, CancellationToken.None);
            Check(equal.Pages.Count == 1 && equal.Pages[0].LeftText.Contains("Dose 10") && !equal.Pages[0].IsChanged, "PdfPig 排版提取与相同文件");
            var changed = PdfComparer.Compare(a, b, false, null, CancellationToken.None);
            Check(changed.Pages[0].IsChanged && changed.Pages[0].RightText.Contains("Dose 20"), "PdfPig 排版模式识别 PDF 数值差异");
            var added = PdfComparer.Compare(a, c, false, null, CancellationToken.None);
            Check(added.Pages.Count == 2 && added.Pages[1].LeftMissing, "PdfPig 排版模式识别新增页");
            var textMode = PdfComparer.Compare(a, b, PdfExtractionMode.Text, false, null, CancellationToken.None);
            Check(textMode.Mode == PdfExtractionMode.Text && textMode.Engine == "PdfPig 全文", "PDF 全文模式使用 PdfPig");
            Check(textMode.Continuous && textMode.Pages.Count == 1, "PdfPig 全文模式连续比较");
            Check(textMode.Pages[0].IsChanged && textMode.Pages[0].RightText.Contains("Dose 20"), "PdfPig 全文模式识别数值差异");
            Check(textMode.Pages[0].LeftLayout is null && textMode.Pages[0].RightLayout is null, "PdfPig 全文模式不生成排版块");
            var textPages = PdfComparer.Compare(a, c, PdfExtractionMode.Text, false, null, CancellationToken.None);
            Check(textPages.LeftPages == 1 && textPages.RightPages == 2, "PdfPig 全文模式记录两侧页数");
            Check(textPages.Pages.Count == 1 && textPages.Pages[0].IsChanged, "PdfPig 全文模式不同页数仍连续比较");
            Check(textPages.Pages[0].RightText.Contains("Dose 10") && textPages.Pages[0].LeftText.Trim() == "Dose 10", "PdfPig 全文模式保留逐页提取文本");
            Check(!PdfComparer.Compare(a, a, PdfExtractionMode.Text, false, null, CancellationToken.None).Pages[0].IsChanged, "PdfPig 全文模式相同文件");
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
            var positionedText = PdfComparer.Compare(positioned, positioned, PdfExtractionMode.Text, false, null, CancellationToken.None).Pages[0].LeftText;
            var positionedTextLines = positionedText.Split('\n');
            var positionedUpper = positionedTextLines.Single(line => line.Contains("Left"));
            Check(positionedTextLines.Length >= 2 && positionedTextLines.Skip(1).Any(line => line.Contains("Lower")), "PdfPig 全文模式按坐标恢复换行");
            Check(positionedUpper.IndexOf("Right", StringComparison.Ordinal) - positionedUpper.IndexOf("Left", StringComparison.Ordinal) > "Left".Length + 2,
                "PdfPig 全文模式保留横向列间距");
            RtfChecks();
            DocxChecks();
            DocumentChecks(directory);
            PlainChecks(directory);
            FolderChecks(directory);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { PdfComparer.Compare(a, b, false, null, cancelled.Token); throw new Exception("取消未生效"); }
            catch (OperationCanceledException) { Console.WriteLine("PASS: 比较取消"); _passed++; }
            Console.WriteLine($"全部 {_passed} 项文本差异自测通过。");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { RemoveReadOnly(directory); Directory.Delete(directory, true); }
    }

    /// <summary>自测会造出只读、隐藏的文件，先去掉属性再删，否则删不掉。</summary>
    private static void RemoveReadOnly(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(path, FileAttributes.Normal); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>RTF 的规则全部来自对 Beyond Compare 的实测：逐条钉住输出，避免以后改坏。</summary>
    private static void RtfChecks()
    {
        static string Rtf(string body) => "{\\rtf1\\ansi\\ansicpg1252 " + body + "}";
        static string ConvertRtf(string body) => RtfToTextConverter.Convert(Encoding.ASCII.GetBytes(Rtf(body)), CancellationToken.None);
        Check(ConvertRtf("M a\\par") == "M a\r\n", "RTF 段落换行");
        Check(ConvertRtf("M a\\par\\par") == "M a\r\n\r\n", "RTF 连续段落");
        Check(ConvertRtf("M a\\line b") == "M a\r\nb", "RTF 行内换行");
        Check(ConvertRtf("M a\\tab b") == "M a\tb", "RTF 制表符");
        Check(ConvertRtf("M a\\tab") == "M a\t", "RTF 末尾制表符保留");
        Check(ConvertRtf("A\\cell B\\row") == "A\tB", "RTF 单元格与行尾裁掉");
        Check(ConvertRtf("M a\\cell") == "M a", "RTF 末尾单元格裁掉制表符");
        Check(ConvertRtf("M a\\row\\row") == "M a", "RTF 空行不留下换行");
        Check(ConvertRtf("M a\\row b") == "M a\r\nb", "RTF 表格行后接文字");
        Check(ConvertRtf("M a\\par\\row b") == "M a\r\n\r\nb", "RTF 段落后的表格行另起一行");
        Check(ConvertRtf("M a\\row\\par b") == "M a\r\nb", "RTF 表格行后的段落不重复换行");
        Check(ConvertRtf("A\\cell\\cell\\row B") == "A\t\r\nB", "RTF 行尾只裁最后一个单元格的制表符");
        Check(ConvertRtf("A\\cell\\cell\\cell\\row B") == "A\t\t\r\nB", "RTF 三个单元格保留前两个制表符");
        Check(ConvertRtf("M a\\row\\cell") == "M a\r\n", "RTF 表格行后的空单元格不留制表符");
        Check(ConvertRtf("\\bullet\\endash\\emdash\\lquote\\rquote\\ldblquote\\rdblquote") == "•–—‘’“”", "RTF 符号控制字");
        Check(ConvertRtf("a\\~b\\_c\\-d") == "a\u00a0b-cd", "RTF 不换行空格、连字符与可选连字符");
        Check(ConvertRtf("caf\\u233? test") == "café test", "RTF Unicode 转义跳过回退字符");
        Check(ConvertRtf("\\uc0 caf\\u233?") == "café?", "RTF uc0 不跳回退字符");
        Check(ConvertRtf("\\uc2 caf\\u233??") == "café", "RTF uc2 跳过两个回退字符");
        Check(ConvertRtf("a\\u-10179?\\u-8704?b") == "a😀b", "RTF 代理对还原成完整字符");
        Check(ConvertRtf("{\\fonttbl{\\f0 Arial;}}{\\colortbl;\\red0;}body") == "body", "RTF 字体表与颜色表丢弃");
        Check(ConvertRtf("{\\*\\generator Riched20;}body") == "body", "RTF 可忽略目的地丢弃");
        Check(ConvertRtf("{\\footnote note}text") == "text", "RTF 脚注丢弃");
        Check(ConvertRtf("{\\field{\\*\\fldinst PAGE}{\\fldrslt 7}}") == "7", "RTF 域只取结果");
        // 真实 RTF 的字体表用 CRLF 分段、组里还套着 {\*\panose ...}：内层组结束时不能把外层的丢弃状态一起清掉。
        Check(ConvertRtf("{\\fonttbl\r\n{\\f1\\fswiss{\\*\\panose 020B0604}Arial;}\r\n}BODY") == "BODY", "RTF 嵌套组的丢弃状态不被内层清掉");
        Check(ConvertRtf("a\\page b") == "ab", "RTF 分页符不出字符");
        Check(ConvertRtf("a\\sect b") == "ab" && ConvertRtf("a\\sect\\par b") == "a\r\nb", "RTF 分节符不出字符");
        Check(ConvertRtf("a\\unknownword b") == "ab", "RTF 未知控制字连词丢弃");
        Check(ConvertRtf("a\\b0 b\\i0 c") == "abc", "RTF 排版控制字不出字符");
        Check(ConvertRtf("a\r\nb") == "ab", "RTF 源文件换行丢弃");
        Check(RtfToTextConverter.Convert(Encoding.ASCII.GetBytes("plain text"), CancellationToken.None) == "plain text", "非 RTF 文件原样输出");
        Check(ConvertRtf("caf\\'e9") == "café", "RTF 十六进制转义按代码页解码");
        static bool Throws(string body)
        {
            try { RtfToTextConverter.Convert(Encoding.ASCII.GetBytes(Rtf(body)), CancellationToken.None); return false; }
            catch (InvalidDataException) { return true; }
        }
        // BC 遇到写坏的 \'hh 会整份转换失败，这里同样报错而不是悄悄吞掉。
        Check(Throws("M a\\'zz b") && Throws("{\\fonttbl \\'zz}"), "RTF 损坏的十六进制转义报错");
        Check(RtfToTextConverter.Convert(Encoding.GetEncoding(936).GetBytes("{\\rtf1\\ansi\\ansicpg936 中文}"), CancellationToken.None) == "中文",
            "RTF 按 ansicpg936 解码双字节");
    }

    /// <summary>DOCX 的规则同样来自实测：段落、单元格、修订标记、隐藏文字和域指令。</summary>
    private static void DocxChecks()
    {
        static string Docx(string body) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + body + "</w:body></w:document>";
        static string ConvertDocx(string body) =>
            DocxToTextConverter.Convert(new MemoryStream(Encoding.UTF8.GetBytes(Docx(body))), CancellationToken.None);
        Check(ConvertDocx("<w:p><w:r><w:t>A</w:t></w:r></w:p>") == "A\r\n", "DOCX 段落一行");
        Check(ConvertDocx("<w:p/><w:p><w:r><w:t>X</w:t></w:r></w:p><w:p/>") == "\r\nX\r\n\r\n", "DOCX 空段落也占行");
        Check(ConvertDocx("<w:p><w:r><w:t>A</w:t><w:tab/><w:t>B</w:t><w:tab/></w:r></w:p>") == "A\tB\t\r\n", "DOCX 制表符");
        Check(ConvertDocx("<w:p><w:r><w:t>A</w:t><w:br/><w:t>B</w:t></w:r></w:p>") == "A\r\nB\r\n", "DOCX 换行元素");
        Check(ConvertDocx("<w:p><w:r><w:t>A</w:t></w:r><w:del w:id=\"1\"><w:r><w:delText>D</w:delText></w:r></w:del><w:r><w:t>B</w:t></w:r></w:p>") == "A-DB\r\n",
            "DOCX 删除文本前加 -");
        Check(ConvertDocx("<w:p><w:r><w:t>A</w:t></w:r><w:ins w:id=\"1\"><w:r><w:t>I</w:t></w:r></w:ins></w:p>") == "A+I\r\n", "DOCX 插入文本前加 +");
        Check(ConvertDocx("<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>PAGE</w:instrText></w:r><w:r><w:t>7</w:t></w:r></w:p>") == "7\r\n",
            "DOCX 域指令丢弃、结果保留");
        Check(ConvertDocx("<w:p><w:fldSimple w:instr=\" PAGE \"><w:r><w:t>9</w:t></w:r></w:fldSimple><w:r><w:t>X</w:t></w:r></w:p>") == "X\r\n",
            "DOCX 简写域内容丢弃");
        Check(ConvertDocx("<w:p><w:r><w:t>keep</w:t></w:r></w:p><w:p><w:r><w:rPr><w:vanish/></w:rPr><w:t>gone</w:t></w:r></w:p>") == "keep\r\n\r\n",
            "DOCX 隐藏文字不输出但保留段落");
        Check(ConvertDocx("<w:p><w:sdt><w:sdtContent><w:r><w:t>A</w:t></w:r></w:sdtContent></w:sdt><w:r><w:t>B</w:t></w:r></w:p>") == "B\r\n",
            "DOCX 内容控件里的文字丢弃");
        Check(ConvertDocx("<w:tbl><w:tr><w:tc><w:p><w:r><w:t>A1</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>B1</w:t></w:r></w:p></w:tc></w:tr></w:tbl>") == "A1\r\nB1\r\n",
            "DOCX 每个单元格各占一行");
        Check(ConvertDocx("<w:p><w:r><w:t xml:space=\"preserve\">  lead  </w:t></w:r></w:p>") == "  lead  \r\n", "DOCX 保留文字两侧空格");
        Check(ConvertDocx("<w:p><w:r><w:t>  lead  </w:t></w:r></w:p>") == "lead\r\n", "DOCX 没写 preserve 时裁掉两侧空格");
        Check(ConvertDocx("<w:p><w:r><w:t>a   b</w:t></w:r></w:p>") == "a   b\r\n", "DOCX 只裁首尾不动中间空格");
        Check(ConvertDocx("<w:p><w:del w:id=\"1\"><w:r><w:delText> old </w:delText></w:r></w:del><w:r><w:t>A</w:t></w:r></w:p>") == "-oldA\r\n",
            "DOCX 删除文本同样裁两侧空格");
    }

    /// <summary>按扩展名分派：RTF/DOCX 走内置解析，整篇连续比较且没有页边界。</summary>
    private static void DocumentChecks(string directory)
    {
        var rtfA = Path.Combine(directory, "a.rtf");
        var rtfB = Path.Combine(directory, "b.rtf");
        var docx = Path.Combine(directory, "c.docx");
        File.WriteAllBytes(rtfA, Encoding.ASCII.GetBytes("{\\rtf1\\ansi\\ansicpg1252 Dose 10\\par}"));
        File.WriteAllBytes(rtfB, Encoding.ASCII.GetBytes("{\\rtf1\\ansi\\ansicpg1252 Dose 20\\par}"));
        using (var archive = System.IO.Compression.ZipFile.Open(docx, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("word/document.xml");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>标题</w:t></w:r></w:p></w:body></w:document>");
        }
        Check(TextDocumentConverter.KindOf(rtfA) == TextDocumentKind.Rtf && TextDocumentConverter.KindOf(docx) == TextDocumentKind.Docx, "按扩展名识别文件类型");
        var rtf = PdfComparer.Compare(rtfA, rtfB, false, null, CancellationToken.None);
        Check(rtf.Continuous && rtf.Pages.Count == 1, "RTF 整篇连续比较");
        Check(rtf.Engine == RtfToTextConverter.EngineName && rtf.Format == "RTF" && !rtf.Paged, "RTF 记录转换器与格式");
        Check(rtf.Pages[0].IsChanged && rtf.Pages[0].RightText.Contains("Dose 20"), "RTF 数值差异");
        Check(rtf.Pages[0].LeftPageStarts.Count == 0, "RTF 没有页边界可展开");
        Check(!PdfComparer.Compare(rtfA, rtfA, false, null, CancellationToken.None).Pages[0].IsChanged, "RTF 相同文件");
        var word = PdfComparer.Compare(docx, docx, false, null, CancellationToken.None);
        Check(word.Pages[0].LeftText == "标题\n" && !word.Pages[0].IsChanged, "DOCX 文本提取与相同文件");
        // 一侧 PDF 一侧 RTF 也按文本比：PDF 那侧用 PdfPig 取文本，整篇仍然连续。
        var mixed = PdfComparer.Compare(Path.Combine(directory, "a.pdf"), rtfA, false, null, CancellationToken.None);
        Check(mixed.Continuous && mixed.Format == "PDF / RTF" && mixed.Engine.Contains(RtfToTextConverter.EngineName), "PDF 与 RTF 混合比较");
    }

    /// <summary>
    /// 代码和配置这类纯文本：BC 不按扩展名分派，只按编码读出字符，换行、行尾空白、末尾换行都原样保留。
    /// </summary>
    private static void PlainChecks(string directory)
    {
        Check(TextDocumentConverter.TextExtensions.Contains(".html") &&
              TextDocumentConverter.TextExtensions.Contains(".css") && TextDocumentConverter.TextExtensions.Contains(".cs") &&
              TextDocumentConverter.TextExtensions.Contains(".sas") && TextDocumentConverter.TextExtensions.Contains(".sql"),
            "代码扩展名纳入可比较类型");
        Check(TextDocumentConverter.Accept.Contains(".html") && TextDocumentConverter.Accept.Contains(".pdf"), "拖放区接受文档与代码扩展名");
        Check(TextDocumentConverter.KindOf(Path.Combine(directory, "a.cs")) == TextDocumentKind.Plain &&
              TextDocumentConverter.KindOf(Path.Combine(directory, "a.HTML")) == TextDocumentKind.Plain &&
              TextDocumentConverter.KindOf(Path.Combine(directory, "noext")) == TextDocumentKind.Plain, "代码文本按纯文本识别");
        Check(TextDocumentConverter.IsConverted(Path.Combine(directory, "a.css")) &&
              TextDocumentConverter.FormatOf(TextDocumentKind.Plain, TextDocumentKind.Plain) == "纯文本",
            "代码文本整篇比文本且格式名为纯文本");

        static string Write(string directory, string name, byte[] bytes)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        static string Read(Comparison result) => result.Pages[0].LeftText;
        var utf8 = new UTF8Encoding(false);
        var utf8Bom = new UTF8Encoding(true);

        var code = Write(directory, "code.cs", utf8.GetBytes("class A\r\n{\r\n    int x = 1;\r\n}\r\n"));
        var codeEqual = PdfComparer.Compare(code, code, false, null, CancellationToken.None);
        Check(codeEqual.Continuous && codeEqual.Pages.Count == 1 && codeEqual.Format == "纯文本" && codeEqual.Engine == "原样读取",
            "代码文件连续比较并记录引擎");
        Check(Read(codeEqual) == "class A\n{\n    int x = 1;\n}\n" && !codeEqual.Pages[0].IsChanged, "代码文件文本原样读出");
        Check(codeEqual.Pages[0].LeftPageStarts.Count == 0, "代码文件没有页边界可展开");
        var html = Write(directory, "a.html", utf8.GetBytes("<p>hi</p>\r\n"));
        var css = Write(directory, "b.css", utf8.GetBytes(".a {\n  color: red;\n}\n"));
        var sas = Write(directory, "c.sas", utf8.GetBytes("/* 注释 */\r\n%let a = 1;\r\n"));
        var gbk = Write(directory, "d.sas", Encoding.GetEncoding(936).GetBytes("/* 注释 */\r\n%let a = 1;\r\n"));
        Check(Read(PdfComparer.Compare(html, html, false, null, CancellationToken.None)) == "<p>hi</p>\n", "HTML 文本原样读出");
        Check(Read(PdfComparer.Compare(css, css, false, null, CancellationToken.None)) == ".a {\n  color: red;\n}", "CSS 保留纯 LF 换行");
        Check(Read(PdfComparer.Compare(sas, sas, false, null, CancellationToken.None)) == "/* 注释 */\n%let a = 1;\n", "SAS 按 UTF-8 读出中文");
        // BC 的区别只在编码：BOM 是"同文本不同编码"，不算文本差异，所以解码时吃掉 BOM。
        Check(Read(PdfComparer.Compare(gbk, gbk, false, null, CancellationToken.None)) == "/* 注释 */\n%let a = 1;\n", "SAS 按系统 ANSI(GBK) 读出中文");
        Check(!PdfComparer.Compare(sas, gbk, false, null, CancellationToken.None).Pages[0].IsChanged, "同一份 SAS 的 UTF-8 与 GBK 文本相同");
        var bomText = Write(directory, "bom.html", utf8Bom.GetBytes("<p>hi</p>\r\n"));
        Check(Read(PdfComparer.Compare(bomText, bomText, false, null, CancellationToken.None)) == "<p>hi</p>\n", "UTF-8 BOM 只用于识别编码");
        Check(!PdfComparer.Compare(html, bomText, false, null, CancellationToken.None).Pages[0].IsChanged, "有无 BOM 不算文本差异");
        // GetBytes 不带 BOM，这里手动写上前导字节，模拟真实的 UTF-16 文件。
        static byte[] WithBom(Encoding encoding, string text) =>
            [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
        var utf16 = Write(directory, "e.cs", WithBom(Encoding.Unicode, "class 甲\r\n"));
        Check(Read(PdfComparer.Compare(utf16, utf16, false, null, CancellationToken.None)) == "class 甲\n", "UTF-16 LE BOM 正确解码");
        var utf16be = Write(directory, "f.cs", WithBom(Encoding.BigEndianUnicode, "class 乙\r\n"));
        Check(Read(PdfComparer.Compare(utf16be, utf16be, false, null, CancellationToken.None)) == "class 乙\n", "UTF-16 BE BOM 正确解码");
        // BC 只认 UTF-8 和 UTF-16 的 BOM，UTF-32 的 FF FE 00 00 被它当成 UTF-16 LE，多出的空字符留在文本里。
        var utf32 = Write(directory, "g.cs", WithBom(new UTF32Encoding(false, true), "class 丙\r\n"));
        Check(Read(PdfComparer.Compare(utf32, utf32, false, null, CancellationToken.None)) == "\0c\0l\0a\0s\0s\0 \0丙\0\n\0\n\0",
            "UTF-32 文件按 UTF-16 LE 读出（与 BC 一致）");
        var mixedEol = Write(directory, "h.json", utf8.GetBytes("a\r\nb\nc\rd"));
        Check(Read(PdfComparer.Compare(mixedEol, mixedEol, false, null, CancellationToken.None)) == "a\nb\nc\nd", "混合换行按行归一化");
        var trailing = Write(directory, "i.cs", utf8.GetBytes("a  \r\nb\t\r\n"));
        Check(Read(PdfComparer.Compare(trailing, trailing, false, null, CancellationToken.None)) == "a  \nb\t\n", "行尾空白原样保留");
        Check(PdfComparer.Compare(trailing, trailing, true, null, CancellationToken.None).Pages[0].IsChanged == false, "忽略空白后行尾空白不算差异");
        var empty = Write(directory, "j.txt", []);
        Check(Read(PdfComparer.Compare(empty, empty, false, null, CancellationToken.None)) == "", "空文本文件读出空串");
        // .cs 与 .txt 内容相同也按文本比，扩展名不参与分派。
        var sameText = Write(directory, "k.txt", utf8.GetBytes("class A\r\n{\r\n    int x = 1;\r\n}\r\n"));
        Check(!PdfComparer.Compare(code, sameText, false, null, CancellationToken.None).Pages[0].IsChanged, "不同扩展名的相同文本没有差异");
        var changed = Write(directory, "l.cs", utf8.GetBytes("class A\r\n{\r\n    int x = 2;\r\n}\r\n"));
        var changedResult = PdfComparer.Compare(code, changed, false, null, CancellationToken.None);
        Check(changedResult.Pages[0].IsChanged && changedResult.Pages[0].RightText.Contains("int x = 2;"), "代码行变化识别为差异");
        var inserted = PdfComparer.Compare(code, Write(directory, "m.cs", utf8.GetBytes("class A\r\n{\r\n    // 新增\r\n    int x = 1;\r\n}\r\n")),
            false, null, CancellationToken.None);
        Check(inserted.Pages[0].RightLines.Count(l => l.Type == ChangeType.Inserted) == 1, "代码新增行只算一行差异");
    }

    /// <summary>
    /// 文件夹比较的规则同样来自对 BC 的实测：名字配对、目录先行、自然排序、四种判定、3 秒容差、
    /// 属性后缀、三种显示筛选。
    /// </summary>
    private static void FolderChecks(string directory)
    {
        var left = Path.Combine(directory, "fl");
        var right = Path.Combine(directory, "fr");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        var stamp = new DateTime(2024, 5, 1, 10, 0, 0, DateTimeKind.Local);
        static string Write(string root, string relative, string text, DateTime time, FileAttributes? attributes = null)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            File.SetLastWriteTime(path, time);
            if (attributes is not null) File.SetAttributes(path, attributes.Value);
            return path;
        }

        Write(left, "same.txt", "A", stamp);
        Write(right, "same.txt", "A", stamp);
        Write(left, "newer.txt", "A", stamp);
        Write(right, "newer.txt", "A", stamp.AddSeconds(10));
        Write(left, "closer.txt", "A", stamp);
        Write(right, "closer.txt", "A", stamp.AddSeconds(2.99));
        Write(left, "size.txt", "A", stamp);
        Write(right, "size.txt", "BB", stamp);
        Write(left, "gone.txt", "A", stamp);
        Write(right, "added.txt", "A", stamp);
        Write(left, "content.txt", "A", stamp);
        Write(right, "content.txt", "B", stamp);
        Write(left, "SUB/in.txt", "A", stamp);
        Write(right, "SUB/in.txt", "A", stamp.AddSeconds(10));
        Write(left, "ONLY/in.txt", "A", stamp);
        Write(left, "a2.txt", "A", stamp);
        Write(right, "a2.txt", "A", stamp);
        Write(left, "a10.txt", "A", stamp);
        Write(right, "a10.txt", "A", stamp);
        Write(left, "hiddenL.txt", "A", stamp, FileAttributes.Hidden);
        Write(right, "hiddenL.txt", "A", stamp);
        Write(left, "attr.txt", "A", stamp, FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReadOnly);
        Write(right, "attr.txt", "A", stamp.AddDays(1));
        // 属性只影响后缀，不改变判定：隐藏一侧的文件仍然判相同。
        var all = FolderComparer.Compare(left, right, FolderCriteria.SizeAndTimestamp, null, CancellationToken.None);
        FolderEntry Row(string relative) => all.Entries.Single(entry => entry.RelativePath == relative);
        Check(Row("same.txt").Status == FolderStatus.Same, "文件夹默认判定：时间大小都相同");
        Check(Row("newer.txt").Status == FolderStatus.RightNewer && Row("newer.txt").Symbol == "<<", "文件夹默认判定：右新为 <<");
        Check(Row("closer.txt").Status == FolderStatus.Same, "时间差 2.99 秒仍算相同");
        Check(Row("size.txt").Status == FolderStatus.Different && Row("size.txt").Symbol == "<>", "时间相近时比大小");
        Check(Row("gone.txt").Status == FolderStatus.LeftOnly && Row("gone.txt").IsOrphan, "单侧文件是孤儿项");
        Check(Row("added.txt").Status == FolderStatus.RightOnly, "右侧单侧文件");
        Check(Row("SUB").Status == FolderStatus.Different, "子树有差异的目录判不同");
        Check(Row("ONLY").Status == FolderStatus.LeftOnly, "单侧目录本身是孤儿");
        Check(all.Folders == 2 && all.Files.Count() == 13, "目录与文件分别计数");

        // BC 的输出顺序：目录先、文件后，同层按 Windows 自然排序（a2 在 a10 前）。
        var order = all.Entries.Select(entry => entry.RelativePath.Replace('\\', '/')).ToList();
        Check(order.IndexOf("ONLY") < order.IndexOf("SUB") && order.IndexOf("SUB") < order.IndexOf("a2.txt"), "目录先于文件");
        Check(order.IndexOf("a2.txt") < order.IndexOf("a10.txt"), "自然排序 a2 在 a10 前");
        Check(order.IndexOf("SUB") < order.IndexOf("SUB/in.txt") && order.IndexOf("ONLY") < order.IndexOf("ONLY/in.txt"), "父目录排在子项前");

        Check(Row("hiddenL.txt").LeftAttributes == " (h)" && Row("hiddenL.txt").RightAttributes == "", "属性后缀只加带属性的一侧");
        Check(Row("attr.txt").LeftAttributes == " (shr)" && Row("attr.txt").RightAttributes == "", "属性后缀顺序 s→h→r");

        var content = FolderComparer.Compare(left, right, FolderCriteria.Content, null, CancellationToken.None);
        Check(content.Entries.Single(e => e.RelativePath == "newer.txt").Status == FolderStatus.Same, "按内容判定忽略时间");
        Check(content.Entries.Single(e => e.RelativePath == "content.txt").Status == FolderStatus.Different, "按内容判定识别字节差异");
        var size = FolderComparer.Compare(left, right, FolderCriteria.Size, null, CancellationToken.None);
        Check(size.Entries.Single(e => e.RelativePath == "newer.txt").Status == FolderStatus.Same, "按大小判定忽略时间");
        Check(size.Entries.Single(e => e.RelativePath == "size.txt").Status == FolderStatus.Different, "按大小判定识别大小差异");
        var time = FolderComparer.Compare(left, right, FolderCriteria.Timestamp, null, CancellationToken.None);
        Check(time.Entries.Single(e => e.RelativePath == "size.txt").Status == FolderStatus.Same, "按时间判定忽略大小");
        Check(time.Entries.Single(e => e.RelativePath == "newer.txt").Status == FolderStatus.RightNewer, "按时间判定给出新旧");

        // 显示筛选：仅差异带上层目录，无孤儿连整棵孤儿目录一起去掉。
        var differences = all.Filter(FolderDisplay.Differences).Select(e => e.RelativePath.Replace('\\', '/')).ToList();
        Check(differences.Contains("SUB") && differences.Contains("SUB/in.txt") && !differences.Contains("same.txt"), "仅差异带上层目录");
        Check(differences.Contains("ONLY") && differences.Contains("ONLY/in.txt"), "仅差异保留孤儿整棵子树");
        var noOrphans = all.Filter(FolderDisplay.NoOrphans).Select(e => e.RelativePath.Replace('\\', '/')).ToList();
        Check(!noOrphans.Contains("ONLY") && !noOrphans.Contains("ONLY/in.txt") && !noOrphans.Contains("gone.txt") && !noOrphans.Contains("added.txt"),
            "无孤儿去掉单侧项及其子树");
        Check(noOrphans.Contains("SUB/in.txt") && noOrphans.Contains("same.txt"), "无孤儿保留两侧都有的项");
        Check(all.Filter(FolderDisplay.All).Count == all.Entries.Count, "全部显示不筛选");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    /// <summary>已通过的条数：结尾文案自己数，不用手写数字对不上。</summary>
    private static int _passed;

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
