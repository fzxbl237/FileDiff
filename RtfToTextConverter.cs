using System.Text;

namespace FileDiff;

/// <summary>
/// Beyond Compare 把 RTF 当文本格式（格式表里的 <c>convertRTF</c>）在进程内解析，不调用 Word。
/// 这里按实测结果复刻它的输出规则：只出正文文本，表格用制表符和换行，
/// 字体表、样式表、图片、脚注、页眉页脚、域指令等整组丢弃。
/// </summary>
internal static class RtfToTextConverter
{
    public const string EngineName = "内置 RTF 解析";

    private const string NewLine = "\r\n";

    /// <summary>整组丢弃的目的地：这些组里的内容不出现在 BC 的文本里。</summary>
    private static readonly HashSet<string> Dropped = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "generator", "listtable", "listoverridetable",
        "rsidtbl", "revtbl", "filetbl", "xmlnstbl", "themedata", "colorschememapping", "datastore",
        "latentstyles", "header", "headerl", "headerr", "headerf", "footer", "footerl", "footerr",
        "footerf", "footnote", "annotation", "atnid", "atnauthor", "pict", "object", "objdata",
        "private", "xe", "txe", "rxe", "bkmkstart", "bkmkend", "fldinst", "docvar", "shppict",
        "nonshppict", "panose", "shpinst"
    };

    /// <summary>BC 把符号控制字换成正文字符，其余未知控制字连词一起丢掉。</summary>
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["bullet"] = "•",
        ["endash"] = "–",
        ["emdash"] = "—",
        ["lquote"] = "‘",
        ["rquote"] = "’",
        ["ldblquote"] = "“",
        ["rdblquote"] = "”"
    };

    private static readonly Dictionary<int, Encoding> Encodings = [];
    private static int? _ansiCodePage;

    static RtfToTextConverter() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private struct State
    {
        /// <summary><c>\ucN</c>：<c>\uN</c> 后面有几个回退字符要跳过。</summary>
        public int Fallback;
        /// <summary><c>\ansicpgN</c>；0 表示没有写，按系统 ANSI 解码。</summary>
        public int CodePage;
        /// <summary>当前 <c>\uN</c> 还没跳完的回退字符数。</summary>
        public int PendingSkip;
    }

    /// <summary>输出侧的收尾状态：BC 会合并表格行后的空行，也会裁掉表格构造留下的尾巴。</summary>
    private sealed class Output
    {
        public readonly StringBuilder Text = new();
        /// <summary>刚结束一个表格行：紧随其后的换行命令不再另起一行。</summary>
        public bool AfterRow;
        /// <summary>最后一个控制字是 <c>\cell</c>：EOF 时它带的制表符要裁掉。</summary>
        public bool LastCell;
        /// <summary>最后一个控制字是 <c>\row</c>：EOF 时它带的换行要裁掉。</summary>
        public bool LastRow;

        public void Cell()
        {
            Text.Append('\t');
            AfterRow = false;
            LastCell = true;
            LastRow = false;
        }

        public void Tab()
        {
            Text.Append('\t');
            AfterRow = false;
            LastCell = false;
            LastRow = false;
        }

        /// <summary>换行命令：表格行之后的第一个换行被吃掉，避免表格和下一段之间凭空多一行。</summary>
        public void NewParagraph()
        {
            if (!AfterRow) Text.Append(NewLine);
            AfterRow = false;
            LastCell = false;
            LastRow = false;
        }

        public void Row()
        {
            if (AfterRow)
            {
                // 连续的 \row 只收一次行。
                LastCell = false;
                LastRow = true;
                return;
            }
            // 行尾空单元格不留制表符。
            if (LastCell && Text.Length > 0 && Text[^1] == '\t') Text.Length--;
            Text.Append(NewLine);
            AfterRow = true;
            LastCell = false;
            LastRow = true;
        }

        public void Literal(char value)
        {
            Text.Append(value);
            AfterRow = false;
            LastCell = false;
            LastRow = false;
        }

        public void Literal(string value)
        {
            Text.Append(value);
            AfterRow = false;
            LastCell = false;
            LastRow = false;
        }

        /// <summary>末尾收尾：最后一个单元格的制表符、最后一个表格行的换行都裁掉。</summary>
        public string Finish()
        {
            if (LastCell && Text.Length > 0 && Text[^1] == '\t') Text.Length--;
            else if (LastRow && Text.Length >= 2 && Text[^2] == '\r' && Text[^1] == '\n') Text.Length -= 2;
            return Text.ToString();
        }
    }

    public static string Convert(string path, CancellationToken token) => Convert(File.ReadAllBytes(path), token);

    public static string Convert(byte[] data, CancellationToken token)
    {
        // 不是 RTF 的文件 BC 当普通文本原样输出。
        if (data.Length < 5 || data[0] != (byte)'{' || data[1] != (byte)'\\' ||
            data[2] != (byte)'r' || data[3] != (byte)'t' || data[4] != (byte)'f')
            return DecodePlain(data);

        var output = new Output();
        // 正文里的字节先攒着，遇到控制字再整段用当时的代码页解码：多字节字符不会被切在转义边界上。
        var run = new List<byte>(256);
        var state = new State { Fallback = 1 };
        var stack = new Stack<State>();
        var depth = 0;
        var skipDepth = -1;
        var index = 0;
        while (index < data.Length)
        {
            if ((index & 0xFFF) == 0) token.ThrowIfCancellationRequested();
            var value = data[index];
            if (value == (byte)'{')
            {
                Flush(output, run, state);
                depth++;
                stack.Push(state);
                // 已经在丢弃的组里就不要再记：嵌套组的结束标签会把外层的状态清掉。
                if (skipDepth < 0 && IsDroppedGroup(data, index)) skipDepth = depth;
                index++;
                continue;
            }
            if (value == (byte)'}')
            {
                Flush(output, run, state);
                if (depth == skipDepth) skipDepth = -1;
                if (stack.Count > 0) state = stack.Pop();
                depth--;
                index++;
                continue;
            }
            if (skipDepth >= 0)
            {
                // 被丢掉的组合里写坏十六进制转义，BC 同样整份转换失败，所以这里仍然检查。
                if (value == (byte)'\\' && index + 1 < data.Length && data[index + 1] == (byte)'\'')
                {
                    HexByte(data, index + 2);
                    index += 4;
                    continue;
                }
                index++;
                continue;
            }
            if (value == (byte)'\\')
            {
                index = Control(data, index, output, run, ref state);
                continue;
            }
            // 源文件里的裸 CR/LF 不占位。
            if (value == (byte)'\r' || value == (byte)'\n') { index++; continue; }
            if (state.PendingSkip > 0) { state.PendingSkip--; index++; continue; }
            run.Add(value);
            index++;
        }
        Flush(output, run, state);
        return output.Finish();
    }

    /// <summary>解析一个控制字或控制符号，返回下一个要读的位置。</summary>
    private static int Control(byte[] data, int index, Output output, List<byte> run, ref State state)
    {
        var next = index + 1;
        if (next >= data.Length) return next;
        var value = data[next];
        switch (value)
        {
            case (byte)'\\' or (byte)'{' or (byte)'}':
                Flush(output, run, state);
                output.Literal((char)value);
                return next + 1;
            // \'hh：当前代码页里的一个字节，同一个字符的多个字节仍在同一个缓冲里。
            case (byte)'\'':
                var hex = HexByte(data, next + 1);
                if (state.PendingSkip > 0) state.PendingSkip--;
                else run.Add(hex);
                return next + 3;
            case (byte)'~':
                Flush(output, run, state);
                output.Literal('\u00a0');
                return next + 1;
            case (byte)'_':
                Flush(output, run, state);
                output.Literal('-');
                return next + 1;
            // \- 是不换行连字符的占位，\* 是"不认识就跳过"的标记，都不出字符。
            case (byte)'-' or (byte)'*':
                return next + 1;
            // 反斜杠后直接换行：一个硬换行。
            case (byte)'\r' or (byte)'\n':
                Flush(output, run, state);
                output.NewParagraph();
                return value == (byte)'\r' && next + 1 < data.Length && data[next + 1] == (byte)'\n' ? next + 2 : next + 1;
        }
        if (!IsLetter(value))
        {
            Flush(output, run, state);
            output.Literal((char)value);
            return next + 1;
        }

        var end = next;
        while (end < data.Length && IsLetter(data[end])) end++;
        var word = Encoding.ASCII.GetString(data, next, end - next);
        var parameter = 0;
        if (end < data.Length && (data[end] == (byte)'-' || IsDigit(data[end])))
        {
            var negative = data[end] == (byte)'-';
            if (negative) end++;
            var start = end;
            while (end < data.Length && IsDigit(data[end])) end++;
            if (end > start && int.TryParse(Encoding.ASCII.GetString(data, start, end - start),
                    System.Globalization.NumberStyles.None, null, out var parsed))
                parameter = negative ? -parsed : parsed;
        }
        // 控制字后的一个空格只是分隔符。
        if (end < data.Length && data[end] == (byte)' ') end++;
        Flush(output, run, state);

        switch (word)
        {
            case "par" or "line":
                output.NewParagraph();
                break;
            case "row":
                output.Row();
                break;
            case "tab":
                output.Tab();
                break;
            case "cell":
                output.Cell();
                break;
            case "u":
                var code = parameter < 0 ? parameter + 0x10000 : parameter;
                // 代理对分两次给出，直接相邻追加就是一个完整字符。
                if (code is > 0 and <= 0xFFFF) output.Literal((char)code);
                state.PendingSkip = state.Fallback;
                break;
            case "uc":
                state.Fallback = Math.Max(0, parameter);
                break;
            case "ansicpg":
                state.CodePage = parameter;
                break;
            default:
                // \sect、\page 和所有未知控制字都不出字符。
                if (Symbols.TryGetValue(word, out var symbol)) output.Literal(symbol);
                break;
        }
        return end;
    }

    private static bool IsDroppedGroup(byte[] data, int index)
    {
        var next = index + 1;
        if (next + 1 >= data.Length || data[next] != (byte)'\\') return false;
        next++;
        // {\* ...}：不认识的目的地一律整组跳过。
        if (data[next] == (byte)'*') return true;
        if (!IsLetter(data[next])) return false;
        var end = next;
        while (end < data.Length && IsLetter(data[end])) end++;
        return Dropped.Contains(Encoding.ASCII.GetString(data, next, end - next));
    }

    private static bool IsLetter(byte value) => value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    /// <summary>
    /// 读 <c>\'</c> 后面的十六进制字节。BC 遇到没写全的十六进制会整份转换失败（输出空文本），
    /// 这里改成抛异常，界面明确提示文件损坏，而不是悄悄比出"没有差异"。
    /// </summary>
    private static byte HexByte(byte[] data, int index)
    {
        if (index >= data.Length) throw InvalidEscape();
        var high = Digit(data[index]);
        if (high < 0) throw InvalidEscape();
        // 文件正好在单个十六进制数字后结束：BC 按这一个数字取值。
        if (index + 1 >= data.Length) return (byte)high;
        var low = Digit(data[index + 1]);
        if (low < 0) throw InvalidEscape();
        return (byte)(high * 16 + low);
    }

    private static InvalidDataException InvalidEscape() =>
        new("RTF 的 \\'hh 十六进制转义不完整，文件可能已损坏。");
    private static int Digit(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        _ => -1
    };

    private static void Flush(Output output, List<byte> run, State state)
    {
        if (run.Count == 0) return;
        output.Literal(Decoder(state.CodePage).GetString(run.ToArray()));
        run.Clear();
    }

    /// <summary>按 RTF 的 <c>\ansicpg</c> 解码；没写就用系统 ANSI，和 BC 在本机的表现一致。</summary>
    private static Encoding Decoder(int codePage)
    {
        lock (Encodings)
        {
            if (Encodings.TryGetValue(codePage, out var cached)) return cached;
            Encoding encoding;
            try
            {
                // 解不出来的字节 BC 写成 "?"，不抛异常。
                encoding = System.Text.Encoding.GetEncoding(
                    codePage == 0 ? AnsiCodePage() : codePage,
                    EncoderFallback.ReplacementFallback, new DecoderReplacementFallback("?"));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                encoding = new UTF8Encoding(false);
            }
            Encodings[codePage] = encoding;
            return encoding;
        }
    }

    /// <summary>系统 ANSI 代码页：BC 用 Windows 当前 ANSI 解码没有 <c>\ansicpg</c> 的文件。</summary>
    private static int AnsiCodePage()
    {
        if (_ansiCodePage is { } cached) return cached;
        var codePage = 0;
        try { codePage = System.Text.Encoding.GetEncoding(0).CodePage; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        if (codePage <= 0) codePage = 1252;
        _ansiCodePage = codePage;
        return codePage;
    }

    private static string DecodePlain(byte[] data)
    {
        try { return new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException) { return Decoder(0).GetString(data); }
    }
}
