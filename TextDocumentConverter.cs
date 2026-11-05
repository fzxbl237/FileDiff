using System.Text;
using UglyToad.PdfPig;

namespace FileDiff;

/// <summary>比较入口认得的文件类型。</summary>
internal enum TextDocumentKind
{
    Pdf,
    Rtf,
    Docx,
    Plain
}

/// <summary>两侧转换好的文本。Paged 为真时两侧都有页边界，否则整篇只有一段。</summary>
internal sealed record ConvertedDocument(
    IReadOnlyList<string> Left, IReadOnlyList<string> Right, string Engine, bool LeftPaged, bool RightPaged, string Format);

/// <summary>
/// 按扩展名把文件转成纯文本：PDF 完全由 PdfPig 提取，RTF 和 DOCX 由内置解析器就地读出，
/// 其余（代码、配置、日志这类纯文本）按 BC 的"文本格式"原样读字节。
/// </summary>
internal static class TextDocumentConverter
{
    /// <summary>
    /// 按原样读的扩展名：BC 对它们不做任何格式转换，纯文本直接参与比较。
    /// 这里只列常见的代码与配置格式，漏掉的扩展名也走同一条文本路径，只是拖放区不收。
    /// </summary>
    public static readonly IReadOnlyList<string> TextExtensions =
    [
        // 纯文本与数据
        ".txt", ".text", ".log", ".csv", ".tsv", ".md", ".markdown", ".rst", ".adoc",
        // 配置
        ".json", ".jsonc", ".xml", ".xsd", ".xslt", ".yml", ".yaml", ".ini", ".cfg", ".conf",
        ".properties", ".toml", ".env", ".reg", ".plist",
        // Web
        ".html", ".htm", ".xhtml", ".css", ".scss", ".sass", ".less", ".vue", ".svelte",
        ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx",
        // 后端与系统语言
        ".cs", ".csx", ".vb", ".fs", ".java", ".kt", ".kts", ".scala", ".groovy",
        ".py", ".rb", ".php", ".go", ".rs", ".swift", ".lua", ".pl", ".dart", ".ex", ".exs",
        ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hxx", ".m", ".mm",
        ".sas", ".sql", ".r", ".do", ".ado", ".stata",
        // 脚本
        ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".mk", ".gradle"
    ];

    /// <summary>拖放区与文件选择器接受的全部类型：文档三种格式加上面这些纯文本扩展名。</summary>
    public static readonly string Accept =
        string.Join(",", new[] { ".pdf", ".rtf", ".docx", ".docm" }.Concat(TextExtensions));

    private static readonly UTF8Encoding Utf8 = new(false, true);

    static TextDocumentConverter() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static TextDocumentKind KindOf(string? path) => Path.GetExtension(path ?? "").ToLowerInvariant() switch
    {
        ".pdf" => TextDocumentKind.Pdf,
        ".rtf" => TextDocumentKind.Rtf,
        // docm 是同一个 OOXML 包，只是多带宏，正文结构完全一致。
        ".docx" or ".docm" => TextDocumentKind.Docx,
        _ => TextDocumentKind.Plain
    };

    /// <summary>RTF/DOCX 和纯文本都没有排版信息，只能整篇比文本。</summary>
    public static bool IsConverted(string? path) => KindOf(path) is not TextDocumentKind.Pdf;

    public static string Name(TextDocumentKind kind) => kind switch
    {
        TextDocumentKind.Rtf => "RTF",
        TextDocumentKind.Docx => "Word",
        TextDocumentKind.Pdf => "PDF",
        _ => "纯文本"
    };

    public static string EngineName(TextDocumentKind kind) => kind switch
    {
        TextDocumentKind.Rtf => RtfToTextConverter.EngineName,
        TextDocumentKind.Docx => DocxToTextConverter.EngineName,
        TextDocumentKind.Pdf => "PdfPig",
        _ => "原样读取"
    };

    /// <summary>状态栏里的格式名：两侧不同就写成 "RTF / Word"。</summary>
    public static string FormatOf(TextDocumentKind left, TextDocumentKind right) =>
        left == right ? Name(left) : $"{Name(left)} / {Name(right)}";

    /// <summary>
    /// 把两侧都转成文本；两侧都是 PDF 且选择全文模式时，由 PdfPig 逐页提取后连续比较。
    /// 排版模式返回 null，让调用方用 PdfPig 的页面坐标提取。
    /// </summary>
    public static ConvertedDocument? TryConvert(
        string left, string right, PdfExtractionMode mode, IProgress<string>? progress, CancellationToken token)
    {
        var leftKind = KindOf(left);
        var rightKind = KindOf(right);
        if (leftKind == TextDocumentKind.Pdf && rightKind == TextDocumentKind.Pdf && mode == PdfExtractionMode.Layout)
            return null;

        progress?.Report(mode == PdfExtractionMode.Text && leftKind == TextDocumentKind.Pdf && rightKind == TextDocumentKind.Pdf
            ? "正在使用 PdfPig 提取 PDF 全文…"
            : $"正在读取 {FormatOf(leftKind, rightKind)}…");
        var converted = new ConvertedDocument(
            ConvertSide(left, leftKind, token), ConvertSide(right, rightKind, token),
            leftKind == TextDocumentKind.Pdf && rightKind == TextDocumentKind.Pdf && mode == PdfExtractionMode.Text
                ? "PdfPig 全文"
                : leftKind == rightKind ? EngineName(leftKind) : $"{EngineName(leftKind)} + {EngineName(rightKind)}",
            leftKind == TextDocumentKind.Pdf, rightKind == TextDocumentKind.Pdf, FormatOf(leftKind, rightKind));
        token.ThrowIfCancellationRequested();
        return converted;
    }

    private static IReadOnlyList<string> ConvertSide(string path, TextDocumentKind kind, CancellationToken token) => kind switch
    {
        TextDocumentKind.Rtf => [RtfToTextConverter.Convert(path, token)],
        TextDocumentKind.Docx => [DocxToTextConverter.Convert(path, token)],
        TextDocumentKind.Pdf => PdfPages(path, token),
        // 代码、配置这类纯文本按 BC 的"文本格式"处理：不做格式转换，只按编码读成字符。
        _ => [DecodePlain(File.ReadAllBytes(path))]
    };

    private static IReadOnlyList<string> PdfPages(string path, CancellationToken token)
    {
        using var document = PdfDocument.Open(path);
        var pages = new List<string>(document.NumberOfPages);
        for (var i = 1; i <= document.NumberOfPages; i++)
        {
            token.ThrowIfCancellationRequested();
            pages.Add(PdfComparer.ExtractText(document.GetPage(i)));
        }
        return pages;
    }

    /// <summary>
    /// BC 对代码、配置这类文件只按编码读出字符，不做任何格式转换：先认 BOM（UTF-8 / UTF-16），
    /// 否则当 UTF-8，解不开再退回系统 ANSI（本机是 GBK）。BOM 只用来判断编码，不进比较文本，
    /// 因为 BC 把"编码不同"单独提示，不会在文本里显示成一行差异。
    /// </summary>
    private static string DecodePlain(byte[] data)
    {
        if (Bom(data) is var (encoding, skip)) return encoding.GetString(data, skip, data.Length - skip);
        try { return Utf8.GetString(data); }
        catch (DecoderFallbackException)
        {
            try { return Encoding.GetEncoding(0).GetString(data); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.Latin1.GetString(data); }
        }
    }

    /// <summary>
    /// 文件开头的 BOM 和它的字节数；没有 BOM 时返回空，交给调用方按 UTF-8 试探。
    /// BC 只认得这三种：UTF-8、UTF-16 LE/BE。UTF-32 的 BOM 以 FF FE 开头，它当成 UTF-16 LE 读，
    /// 多出来的空字符就留在文本里，这里照做。
    /// </summary>
    private static (Encoding Encoding, int Length)? Bom(byte[] data) => data switch
    {
        [0xEF, 0xBB, 0xBF, ..] => (new UTF8Encoding(false), 3),
        [0xFF, 0xFE, ..] => (Encoding.Unicode, 2),
        [0xFE, 0xFF, ..] => (Encoding.BigEndianUnicode, 2),
        _ => null
    };
}
