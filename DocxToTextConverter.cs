using System.IO.Compression;
using System.Text;
using System.Xml;

namespace FileDiff;

/// <summary>
/// Beyond Compare 用 Scalabium 的 Word 解析器在进程内读 DOCX，只取正文文本，页眉页脚不参与比较。
/// 这里按实测出来的规则走 word/document.xml：段落一行、单元格各占一行、制表符和换行按元素落位，
/// 修订里删除的文本前面加 "-"、插入的加 "+"，隐藏文字（w:vanish）和域指令里的文字不输出。
/// 解析器按元素的限定名（<c>w:p</c> 这种字面前缀）认元素，不解析命名空间：没声明前缀的文件 BC 照样能读。
/// 只往已知的正文容器里走，其余元素（含 <c>w:sdt</c>、<c>w:pict</c>、<c>mc:Fallback</c> 这类回退分支）整棵子树丢弃。
/// </summary>
internal static class DocxToTextConverter
{
    public const string EngineName = "内置 Word 解析";

    private const string NewLine = "\r\n";

    /// <summary>
    /// 会被继续下探的元素。名单来自逐元素实测：BC 不认的元素（哪怕长得像 <c>w:zzz</c>）里的文字一律不出，
    /// 而这些元素里的 <c>w:r</c>/<c>w:t</c> 会被读到。<c>w:pPr</c>/<c>w:rPr</c> 也在名单里——里面若真有文字，BC 同样输出。
    /// </summary>
    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
    {
        "w:document", "w:body", "w:p", "w:r", "w:tbl", "w:tr", "w:tc",
        "w:pPr", "w:rPr", "w:hyperlink", "w:smartTag", "w:ins", "w:del",
        "w:hdr", "w:ftr", "w:txbxContent",
        // DrawingML 文本框那条链；VML（w:pict / v:shape）和 wp:inline 都不走
        "w:drawing", "mc:AlternateContent", "mc:Choice", "wp:anchor",
        "a:graphic", "a:graphicData", "wps:txbx", "wps:wsp"
    };

    /// <summary>直接承载文字的叶子元素：只有它们的文本节点算正文。</summary>
    private static bool IsTextHolder(string name) => name is "w:t" or "w:delText";

    private sealed class Frame(string name, bool skip, bool trim = false)
    {
        /// <summary>元素的限定名，例如 <c>w:t</c>。</summary>
        public string Name { get; } = name;
        /// <summary>所在的子树被丢弃：里面的文字、换行、制表符都不算。</summary>
        public bool Skip { get; } = skip;
        /// <summary>文字节点两侧的空白要裁掉：没写 <c>xml:space="preserve"</c> 时的默认行为。</summary>
        public bool Trim { get; } = trim;
        /// <summary>run 的 w:rPr 里有 w:vanish：这个 run 的文字算隐藏。</summary>
        public bool Hidden { get; set; }
    }

    public static string Convert(string path, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = FindDocument(archive) ?? throw new InvalidDataException("DOCX 里没有 word/document.xml。");
        using var stream = entry.Open();
        return Convert(stream, token);
    }

    public static string Convert(Stream stream, CancellationToken token)
    {
        // 不解析命名空间：BC 连前缀没声明的 document.xml 都能读，按限定名匹配才有同样的容错。
        using var reader = new XmlTextReader(stream) { Namespaces = false, DtdProcessing = DtdProcessing.Prohibit };
        var text = new StringBuilder();
        var stack = new List<Frame>();
        var reads = 0;
        while (reader.Read())
        {
            if ((reads++ & 0x3FF) == 0) token.ThrowIfCancellationRequested();
            var skipping = stack.Count > 0 && stack[^1].Skip;
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    var name = reader.Name;
                    // 隐藏文字标记落在所在的 run 上，run 里的每个 w:t 都要看它。
                    if (!skipping && name == "w:vanish") MarkHidden(stack);
                    if (reader.IsEmptyElement)
                    {
                        if (skipping) break;
                        // <w:p/> 也是一个空段落，占一行；<w:tab/>、<w:br/> 这类空元素照样出字符。
                        if (name == "w:p") text.Append(NewLine);
                        else Emit(text, name);
                        break;
                    }
                    var skip = skipping || !(Containers.Contains(name) || IsTextHolder(name));
                    // 没写 xml:space="preserve" 的文字节点，BC 会把两侧空白裁掉（只裁首尾，中间的留着）。
                    var trim = IsTextHolder(name) && reader.GetAttribute("xml:space") != "preserve";
                    stack.Add(new Frame(name, skip, trim));
                    if (!skip) Emit(text, name);
                    break;
                case XmlNodeType.EndElement:
                    var frame = stack.Count > 0 ? stack[^1] : null;
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    // 段落结束就是一行；被丢弃的子树里的段落不算。
                    if (frame is { Skip: false, Name: "w:p" }) text.Append(NewLine);
                    break;
                // 纯空白节点（含 xml:space="preserve" 里的 SignificantWhitespace）同样算正文。
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    if (stack.Count == 0) break;
                    var parent = stack[^1];
                    if (parent.Skip || !IsTextHolder(parent.Name)) break;
                    if (Hidden(stack)) break;
                    // 元素之间的缩进不是正文；没写 preserve 的正文才裁空白。
                    text.Append(parent.Trim ? reader.Value.Trim() : reader.Value);
                    break;
            }
        }
        return text.ToString();
    }

    private static void MarkHidden(List<Frame> stack)
    {
        for (var i = stack.Count - 1; i >= 0; i--)
        {
            if (stack[i].Name == "w:r") { stack[i].Hidden = true; return; }
            if (stack[i].Name is "w:p" or "w:hdr" or "w:ftr") return;
        }
    }

    /// <summary>所在 run 被标成隐藏时，这段文字不输出。</summary>
    private static bool Hidden(List<Frame> stack)
    {
        for (var i = stack.Count - 1; i >= 0; i--)
        {
            if (stack[i].Name == "w:r") return stack[i].Hidden;
            if (stack[i].Name is "w:p" or "w:hdr" or "w:ftr") return false;
        }
        return false;
    }

    /// <summary>制表符、换行和修订标记按元素落位；<c>w:p</c> 的换行在结束标签处补。</summary>
    private static void Emit(StringBuilder text, string name)
    {
        switch (name)
        {
            case "w:tab": text.Append('\t'); break;
            case "w:br": text.Append(NewLine); break;
            case "w:del": text.Append('-'); break;
            case "w:ins": text.Append('+'); break;
        }
    }

    private static ZipArchiveEntry? FindDocument(ZipArchive archive)
    {
        foreach (var entry in archive.Entries)
            if (string.Equals(entry.FullName.Replace('\\', '/'), "word/document.xml", StringComparison.OrdinalIgnoreCase))
                return entry;
        return null;
    }
}
