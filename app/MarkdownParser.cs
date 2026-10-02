namespace DshSessionHud;

/// <summary>行内 Markdown 的样式位。用位标志而不是嵌套节点，因为组合只有"粗+斜+删+码"这几种。</summary>
[Flags]
public enum MdStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Strike = 4,
    Code = 8,
}

public enum MdKind
{
    Text,
    /// <summary>换行。</summary>
    LineBreak,
}

public sealed record MdRun(MdKind Kind, string Text, MdStyle Style, string? Url = null);

/// <summary>
/// 极简 Markdown 解析器 —— 只覆盖模型输出里**真正会出现**的那几种标记，不做完整 CommonMark。
/// 没有 HTML、没有表格、没有嵌套列表。
///
/// 支持：**粗** / __粗__ / *斜* / _斜_ / ***粗斜*** / `行内代码` / ~~删除线~~ /
///       [文字](链接) / \转义 / 标题(#) / 无序列表(- * +) / 引用(&gt;)
///
/// 这个文件**故意不依赖任何 WinUI 类型**，所以可以被 test/markdown.test 直接编译进去做单元测试
/// （渲染那一半在 MarkdownText.cs）。
///
/// 刻意实现的"不要误伤"规则，每一条都有对应测试：
///   * 开标记后面不能是空白、闭标记前面不能是空白 —— 否则 "3 * 4 = 12" 会被吃成斜体；
///   * `_` 前后还要求是词边界 —— 否则 "snake_case_name" 会被吃成斜体；
///   * 找不到闭标记时整段按字面量输出 —— 插件会把长文本截断，未闭合的 ** 很常见，
///     这种时候必须原样显示，不能把后半段整段加粗。
/// </summary>
public static class MarkdownParser
{
    public static IReadOnlyList<MdRun> Parse(string? input)
    {
        var runs = new List<MdRun>();
        if (string.IsNullOrEmpty(input)) return runs;

        // 统一换行，再按行处理块级标记。
        var normalized = input.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) runs.Add(new MdRun(MdKind.LineBreak, "\n", MdStyle.None));
            ParseLine(lines[i], runs);
        }
        return runs;
    }

    private static void ParseLine(string line, List<MdRun> runs)
    {
        var style = MdStyle.None;
        string text = line;

        // 标题：'#' 到 '######' + 空格 → 整行加粗（标题里的行内标记仍然照常解析）
        int indent = 0;
        while (indent < text.Length && indent < 3 && text[indent] == ' ') indent++;
        int hashes = 0;
        while (indent + hashes < text.Length && text[indent + hashes] == '#') hashes++;
        if (hashes is >= 1 and <= 6 && indent + hashes < text.Length && text[indent + hashes] == ' ')
        {
            style |= MdStyle.Bold;
            text = text[(indent + hashes + 1)..];
        }
        else if (text.Length >= 2 && text[0] == '>' && text[1] == ' ')
        {
            text = "▎ " + text[2..];
        }
        else if (text.Length >= 2 && (text[0] == '-' || text[0] == '*' || text[0] == '+') && text[1] == ' ')
        {
            text = "• " + text[2..];
        }

        if (text.Length == 0) return;
        ParseInline(text, 0, text.Length, style, runs);
    }

    private static void ParseInline(string s, int start, int end, MdStyle style, List<MdRun> runs)
    {
        int i = start;
        int textStart = start;

        while (i < end)
        {
            char c = s[i];

            // ---- 反斜杠转义 ----
            if (c == '\\' && i + 1 < end && IsEscapable(s[i + 1]))
            {
                Append(runs, s, textStart, i, style);
                runs.Add(new MdRun(MdKind.Text, s[i + 1].ToString(), style));
                i += 2;
                textStart = i;
                continue;
            }

            // ---- 行内代码 ----
            if (c == '`')
            {
                int close = FindDelimiter(s, i + 1, end, '`');
                if (close > i + 1)
                {
                    Append(runs, s, textStart, i, style);
                    runs.Add(new MdRun(MdKind.Text, s[(i + 1)..close], style | MdStyle.Code));
                    i = close + 1;
                    textStart = i;
                    continue;
                }
            }

            // ---- 链接 [文字](地址) ----
            if (c == '[' && TryParseLink(s, i, end, out int afterLink, out string label, out string url))
            {
                Append(runs, s, textStart, i, style);
                runs.Add(new MdRun(MdKind.Text, label, style, url));
                i = afterLink;
                textStart = i;
                continue;
            }

            // ---- 强调：*** / ** / * 与 ___ / __ / _ ----
            if (c is '*' or '_')
            {
                bool matched = false;
                int runLength = CountRun(s, i, end, c);
                for (int len = Math.Min(3, runLength); len >= 1; len--)
                {
                    if (!IsValidOpener(s, i, len, c)) continue;
                    int close = FindCloser(s, i + len, end, c, len);
                    if (close < 0) continue;

                    var inner = style;
                    if (len >= 2) inner |= MdStyle.Bold;
                    if (len != 2) inner |= MdStyle.Italic;

                    Append(runs, s, textStart, i, style);
                    ParseInline(s, i + len, close, inner, runs);
                    i = close + len;
                    textStart = i;
                    matched = true;
                    break;
                }
                if (matched) continue;
            }

            // ---- 删除线 ----
            if (c == '~' && i + 1 < end && s[i + 1] == '~')
            {
                int close = FindCloser(s, i + 2, end, '~', 2);
                if (close >= 0)
                {
                    Append(runs, s, textStart, i, style);
                    ParseInline(s, i + 2, close, style | MdStyle.Strike, runs);
                    i = close + 2;
                    textStart = i;
                    continue;
                }
            }

            i++;
        }

        Append(runs, s, textStart, end, style);
    }

    private static void Append(List<MdRun> runs, string s, int from, int to, MdStyle style)
    {
        if (to > from) runs.Add(new MdRun(MdKind.Text, s[from..to], style));
    }

    private static bool IsEscapable(char c)
        => c is '*' or '_' or '`' or '~' or '[' or ']' or '(' or ')' or '\\' or '#' or '>' or '-';

    private static int CountRun(string s, int i, int end, char c)
    {
        int n = 0;
        while (i + n < end && s[i + n] == c) n++;
        return n;
    }

    private static int FindDelimiter(string s, int from, int end, char c)
    {
        for (int j = from; j < end; j++)
        {
            if (s[j] == '\\') { j++; continue; }
            if (s[j] == c) return j;
        }
        return -1;
    }

    /// <summary>开标记：后面必须紧跟非空白；`_` 还要求前面不是字母数字（避免 snake_case）。</summary>
    private static bool IsValidOpener(string s, int i, int len, char c)
    {
        int after = i + len;
        if (after >= s.Length) return false;
        if (char.IsWhiteSpace(s[after])) return false;

        if (c == '_' && i > 0 && char.IsLetterOrDigit(s[i - 1])) return false;
        return true;
    }

    /// <summary>找闭标记：本身不被转义、前面不是空白；`_` 还要求后面不是字母数字。</summary>
    private static int FindCloser(string s, int from, int end, char c, int len)
    {
        for (int j = from; j + len <= end; j++)
        {
            if (s[j] == '\\') { j++; continue; }
            if (s[j] != c) continue;
            if (CountRun(s, j, end, c) < len) continue;
            if (j == from) continue;                       // 空内容（** **）不算一对
            if (char.IsWhiteSpace(s[j - 1])) continue;     // 闭标记前不能是空白

            if (c == '_')
            {
                int after = j + len;
                if (after < s.Length && char.IsLetterOrDigit(s[after])) continue;
            }
            return j;
        }
        return -1;
    }

    private static bool TryParseLink(string s, int i, int end, out int next, out string label, out string url)
    {
        next = i;
        label = string.Empty;
        url = string.Empty;

        int close = i + 1;
        int depth = 0;
        for (; close < end; close++)
        {
            if (s[close] == '\\') { close++; continue; }
            if (s[close] == '[') depth++;
            else if (s[close] == ']')
            {
                if (depth == 0) break;
                depth--;
            }
        }
        if (close >= end) return false;
        if (close == i + 1) return false;                       // 空标签不处理
        if (close + 1 >= end || s[close + 1] != '(') return false;

        int paren = close + 1;
        int closeParen = s.IndexOf(')', paren + 1);
        if (closeParen < 0 || closeParen > end) return false;

        string candidate = s[(paren + 1)..closeParen].Trim();
        // 只认真正能打开的地址，其它一律按字面量显示（免得把 [x](y) 这种普通文本变成链接）。
        if (!IsSafeUrl(candidate)) return false;

        label = s[(i + 1)..close];
        url = candidate;
        next = closeParen + 1;
        return true;
    }

    private static bool IsSafeUrl(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
}
