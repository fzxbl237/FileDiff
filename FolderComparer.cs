using System.Runtime.InteropServices;

namespace FileDiff;

/// <summary>文件夹比较的判定方式，对应 Beyond Compare 脚本里的 <c>criteria</c>。</summary>
internal enum FolderCriteria
{
    /// <summary>BC 的默认值：时间差超过 3 秒看新旧，否则看大小。内容不比对。</summary>
    SizeAndTimestamp,
    /// <summary>逐字节比内容，时间不参与（BC 的 binary/crc）。</summary>
    Content,
    /// <summary>只比大小。</summary>
    Size,
    /// <summary>只比修改时间。</summary>
    Timestamp
}

/// <summary>BC 报告里的状态符号：=、&lt;&gt;、&gt;&gt;、&lt;&lt;，以及只在单侧存在的孤儿项。</summary>
internal enum FolderStatus
{
    Same,
    Different,
    LeftNewer,
    RightNewer,
    LeftOnly,
    RightOnly
}

/// <summary>BC 报告的显示方式，对应 <c>options:display-all</c> / <c>display-mismatches</c> / <c>display-no-orphans</c>。</summary>
internal enum FolderDisplay
{
    All,
    /// <summary>只留有差异的文件，连它的上层目录一起留。</summary>
    Differences,
    /// <summary>去掉单侧存在的项，连整棵只有一侧的目录一起去掉。</summary>
    NoOrphans
}

/// <summary>
/// BC 报告里一行在某侧的呈现：较新一侧标红、较旧一侧标灰、单侧存在标紫、相同为黑。
/// 和 <see cref="FolderStatus"/> 不同，这里已经分到了左右两边。
/// </summary>
internal enum FolderSide
{
    /// <summary>这一侧没有这一项，整格留空。</summary>
    Absent,
    Same,
    Different,
    Newer,
    Older,
    Orphan
}

/// <summary>文件夹比较里的一行：一个文件或一个目录，两侧各带文件名、大小和修改时间。</summary>
internal sealed record FolderEntry(
    string RelativePath, string Name, int Depth, bool IsFolder,
    string? LeftPath, string? RightPath,
    long LeftSize, long RightSize, DateTime? LeftTime, DateTime? RightTime,
    FolderStatus Status)
{
    /// <summary>BC 在名字后加的属性后缀，例如 <c> (sh)</c>：系统、隐藏、只读。</summary>
    public string LeftAttributes { get; init; } = "";

    public string RightAttributes { get; init; } = "";

    public bool HasLeft => LeftPath is not null;

    public bool HasRight => RightPath is not null;

    public bool IsChanged => Status != FolderStatus.Same;

    public bool IsOrphan => Status is FolderStatus.LeftOnly or FolderStatus.RightOnly;

    /// <summary>两侧都是文件才能点开做文本比较，目录和孤儿项不行。</summary>
    public bool IsComparable => !IsFolder && LeftPath is not null && RightPath is not null;

    /// <summary>BC 中间的符号：=、&lt;&gt;，以及指向"该往哪边复制"的 &gt;&gt; / &lt;&lt;。</summary>
    public string Symbol => Status switch
    {
        FolderStatus.Same => "=",
        FolderStatus.Different => "<>",
        FolderStatus.LeftNewer or FolderStatus.LeftOnly => ">>",
        _ => "<<"
    };

    /// <summary>两侧各自的呈现：较新的一侧红、较旧的一侧灰，孤儿只有存在的那一侧有内容。</summary>
    public (FolderSide Left, FolderSide Right) Sides => Status switch
    {
        FolderStatus.Same => (FolderSide.Same, FolderSide.Same),
        FolderStatus.Different => (FolderSide.Different, FolderSide.Different),
        FolderStatus.LeftNewer => (FolderSide.Newer, FolderSide.Older),
        FolderStatus.RightNewer => (FolderSide.Older, FolderSide.Newer),
        FolderStatus.LeftOnly => (FolderSide.Orphan, FolderSide.Absent),
        _ => (FolderSide.Absent, FolderSide.Orphan)
    };
}

internal sealed record FolderComparison(IReadOnlyList<FolderEntry> Entries, FolderCriteria Criteria)
{
    public IEnumerable<FolderEntry> Files => Entries.Where(entry => !entry.IsFolder);

    public int Count(FolderStatus status) => Files.Count(entry => entry.Status == status);

    public int Changed => Files.Count(entry => entry.IsChanged);

    public int Folders => Entries.Count(entry => entry.IsFolder);

    /// <summary>按 BC 的三种显示方式筛行；父目录总是跟着子项保留，树不会断掉。</summary>
    public IReadOnlyList<FolderEntry> Filter(FolderDisplay display)
    {
        if (display == FolderDisplay.All) return Entries;
        if (display == FolderDisplay.Differences)
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Entries)
            {
                if (entry.IsChanged) AddWithAncestors(keep, entry.RelativePath);
            }
            return Entries.Where(entry => keep.Contains(entry.RelativePath)).ToList();
        }
        // 无孤儿：只有一侧存在的项连同它的整个子树一起隐藏。
        var result = new List<FolderEntry>();
        var skipDepth = -1;
        foreach (var entry in Entries)
        {
            if (skipDepth >= 0)
            {
                if (entry.Depth > skipDepth) continue;
                skipDepth = -1;
            }
            if (entry.IsOrphan)
            {
                skipDepth = entry.Depth;
                continue;
            }
            result.Add(entry);
        }
        return result;
    }

    private static void AddWithAncestors(HashSet<string> keep, string relative)
    {
        for (var path = relative;;)
        {
            keep.Add(path);
            var cut = path.LastIndexOfAny(['\\', '/']);
            if (cut <= 0) return;
            path = path[..cut];
        }
    }
}

/// <summary>
/// 文件夹比较，规则取自对 Beyond Compare 5 的实测：名字大小写不敏感地配对，目录排在文件前面，
/// 同层按名字做自然排序（<c>a2</c> 在 <c>a10</c> 前，中文和重音字符一起参与）；
/// 默认判定是"时间差不足 3 秒就比大小，超过就看谁新"，目录行的大小是整棵子树的字节数。
/// </summary>
internal static class FolderComparer
{
    /// <summary>BC 的时间容忍度：差 2.99 秒算相同，差满 3 秒就算新旧。</summary>
    public static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(3);

    private const int ReportEvery = 256;

    public static FolderComparison Compare(string leftRoot, string rightRoot, FolderCriteria criteria,
        IProgress<string>? progress, CancellationToken token)
    {
        var rows = new List<FolderEntry>();
        Walk(new DirectoryInfo(leftRoot), new DirectoryInfo(rightRoot), "", 0, criteria, rows, progress, token);
        token.ThrowIfCancellationRequested();
        return new FolderComparison(rows, criteria);
    }

    /// <summary>递归比较一层目录：先按 BC 的顺序生成这一层和下面各层的行，再返回子树的大小和状态。</summary>
    private static (FolderStatus Status, long LeftSize, long RightSize) Walk(
        DirectoryInfo? left, DirectoryInfo? right, string relative, int depth,
        FolderCriteria criteria, List<FolderEntry> rows, IProgress<string>? progress, CancellationToken token)
    {
        var keys = new Dictionary<Key, (FileSystemInfo? Left, FileSystemInfo? Right)>(KeyComparer.Instance);
        foreach (var item in Read(left, token)) keys[new Key(item.Name, IsDirectory(item))] = (item, null);
        foreach (var item in Read(right, token))
        {
            var key = new Key(item.Name, IsDirectory(item));
            keys[key] = keys.TryGetValue(key, out var existing) ? (existing.Left, item) : (null, item);
        }

        var ordered = keys.Keys.ToList();
        ordered.Sort(KeyOrder.Instance);

        var leftSize = 0L;
        var rightSize = 0L;
        var same = true;
        foreach (var key in ordered)
        {
            token.ThrowIfCancellationRequested();
            var pair = keys[key];
            var child = relative.Length == 0 ? key.Name : Path.Combine(relative, key.Name);
            if (key.IsFolder)
            {
                // 目录行要排在它的子项前面，所以先占位，等子树算完再填进去。
                var at = rows.Count;
                var sub = Walk(pair.Left as DirectoryInfo, pair.Right as DirectoryInfo, child, depth + 1,
                    criteria, rows, progress, token);
                // 只有一侧存在时目录自己的状态就是孤儿，和里面有多少内容无关。
                var status = pair.Left is null ? FolderStatus.RightOnly
                    : pair.Right is null ? FolderStatus.LeftOnly
                    : sub.Status;
                rows.Insert(at, Entry(child, key.Name, depth, true, pair.Left, pair.Right, sub.LeftSize, sub.RightSize, status));
                leftSize += sub.LeftSize;
                rightSize += sub.RightSize;
                if (status != FolderStatus.Same) same = false;
            }
            else
            {
                var leftFile = pair.Left as FileInfo;
                var rightFile = pair.Right as FileInfo;
                var status = leftFile is null ? FolderStatus.RightOnly
                    : rightFile is null ? FolderStatus.LeftOnly
                    : CompareFiles(leftFile, rightFile, criteria, token);
                rows.Add(Entry(child, key.Name, depth, false, leftFile, rightFile,
                    leftFile?.Length ?? 0, rightFile?.Length ?? 0, status));
                leftSize += leftFile?.Length ?? 0;
                rightSize += rightFile?.Length ?? 0;
                if (status != FolderStatus.Same) same = false;
            }
            if (progress is not null && rows.Count % ReportEvery == 0) progress.Report($"正在比较文件夹… 已检查 {rows.Count} 项");
        }
        return (same ? FolderStatus.Same : FolderStatus.Different, leftSize, rightSize);
    }

    private static FolderStatus CompareFiles(FileInfo left, FileInfo right, FolderCriteria criteria, CancellationToken token)
    {
        var sizeSame = left.Length == right.Length;
        var delta = right.LastWriteTimeUtc - left.LastWriteTimeUtc;
        var timeSame = delta.Duration() < TimeTolerance;
        // 时间不同时先看新旧，BC 的默认和 timestamp 都是这个顺序。
        FolderStatus Newer() => delta > TimeSpan.Zero ? FolderStatus.RightNewer : FolderStatus.LeftNewer;
        return criteria switch
        {
            FolderCriteria.Content => sizeSame && SameContent(left.FullName, right.FullName, token)
                ? FolderStatus.Same : FolderStatus.Different,
            FolderCriteria.Size => sizeSame ? FolderStatus.Same : FolderStatus.Different,
            FolderCriteria.Timestamp => timeSame ? FolderStatus.Same : Newer(),
            _ => timeSame ? sizeSame ? FolderStatus.Same : FolderStatus.Different : Newer()
        };
    }

    /// <summary>逐字节比内容；大小不同直接判不同，省得读整份文件。</summary>
    private static bool SameContent(string left, string right, CancellationToken token)
    {
        const int size = 64 * 1024;
        using var a = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var b = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bufferA = new byte[size];
        var bufferB = new byte[size];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var readA = a.ReadAtLeast(bufferA, size, throwOnEndOfStream: false);
            var readB = b.ReadAtLeast(bufferB, size, throwOnEndOfStream: false);
            if (readA != readB) return false;
            if (readA == 0) return true;
            if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB))) return false;
        }
    }

    private static FolderEntry Entry(string relative, string name, int depth, bool folder,
        FileSystemInfo? left, FileSystemInfo? right, long leftSize, long rightSize, FolderStatus status) => new(
            relative, name, depth, folder, left?.FullName, right?.FullName,
            left is null ? 0 : leftSize, right is null ? 0 : rightSize,
            left?.LastWriteTime, right?.LastWriteTime, status)
    {
        LeftAttributes = left is null ? "" : AttributeSuffix(left.Attributes),
        RightAttributes = right is null ? "" : AttributeSuffix(right.Attributes)
    };

    /// <summary>BC 的属性后缀顺序实测是系统、隐藏、只读，例如 <c> (sh)</c>、<c> (shr)</c>。</summary>
    private static string AttributeSuffix(FileAttributes attributes)
    {
        var text = (attributes.HasFlag(FileAttributes.System) ? "s" : "")
            + (attributes.HasFlag(FileAttributes.Hidden) ? "h" : "")
            + (attributes.HasFlag(FileAttributes.ReadOnly) ? "r" : "");
        return text.Length == 0 ? "" : $" ({text})";
    }

    private static List<FileSystemInfo> Read(DirectoryInfo? directory, CancellationToken token)
    {
        if (directory is null) return [];
        var items = new List<FileSystemInfo>();
        try
        {
            // 没有权限的子目录 BC 也只是跳过，这里照做。
            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                items.Add(item);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return items;
    }

    private static bool IsDirectory(FileSystemInfo item) => (item.Attributes & FileAttributes.Directory) != 0;

    /// <summary>配对用的键：名字（大小写不敏感）加"是不是目录"，同名的一文件一目录算两项。</summary>
    private readonly record struct Key(string Name, bool IsFolder);

    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(Key x, Key y) =>
            x.IsFolder == y.IsFolder && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(Key key) => HashCode.Combine(key.Name.ToUpperInvariant(), key.IsFolder);
    }

    /// <summary>目录先、文件后；同组按名字自然排序。</summary>
    private sealed class KeyOrder : IComparer<Key>
    {
        public static readonly KeyOrder Instance = new();

        public int Compare(Key x, Key y) => x.IsFolder != y.IsFolder
            ? x.IsFolder ? -1 : 1
            : NaturalSort.Compare(x.Name, y.Name);
    }
}

/// <summary>
/// BC 的排序是 windows 资源管理器那套自然排序：不区分大小写，数字按数值比。
/// 直接借 shell 的 StrCmpLogicalW，和 BC 实测出来的顺序逐项一致；取不到时退回自己写的近似实现。
/// </summary>
internal static partial class NaturalSort
{
    private static readonly bool Available = Probe();

    public static int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        var result = Available ? Logical(left, right) : Fallback(left, right);
        // 只有大小写不同时仍然要有稳定的先后，保证两次比较的列表顺序一致。
        return result != 0 ? result : string.CompareOrdinal(left, right);
    }

    private static bool Probe()
    {
        try { Logical("a", "a"); return true; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string left, string right);

    private static int Logical(string left, string right) => StrCmpLogicalW(left, right);

    /// <summary>逐段比较：连续数字当整数，其余按不区分大小写的字符序。</summary>
    private static int Fallback(string left, string right)
    {
        var a = 0;
        var b = 0;
        while (a < left.Length && b < right.Length)
        {
            if (char.IsAsciiDigit(left[a]) && char.IsAsciiDigit(right[b]))
            {
                var startA = a;
                var startB = b;
                while (a < left.Length && char.IsAsciiDigit(left[a])) a++;
                while (b < right.Length && char.IsAsciiDigit(right[b])) b++;
                var numberA = left.AsSpan(startA, a - startA).TrimStart('0');
                var numberB = right.AsSpan(startB, b - startB).TrimStart('0');
                if (numberA.Length != numberB.Length) return numberA.Length - numberB.Length;
                var digits = numberA.SequenceCompareTo(numberB);
                if (digits != 0) return digits;
                continue;
            }
            var difference = char.ToUpperInvariant(left[a]) - char.ToUpperInvariant(right[b]);
            if (difference != 0) return difference;
            a++;
            b++;
        }
        return (left.Length - a) - (right.Length - b);
    }
}
