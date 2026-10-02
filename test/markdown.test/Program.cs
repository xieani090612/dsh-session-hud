using DshSessionHud;

// MarkdownParser 的离线单元测试。
//
// 这个解析器的价值全在「边界情况」上 —— 正常加粗谁都能写对，
// 难的是不要让 "3 * 4 = 12" / "snake_case_name" / 被截断的 "**xxx" 变形。
// 所以下面每一组断言都对应一条具体的误伤风险。
//
// 运行：
//     dotnet run --project test/markdown.test

int passed = 0;
var failures = new List<string>();

void Check(string name, bool ok, string? detail = null)
{
    if (ok)
    {
        passed++;
        Console.WriteLine($"  ok   {name}");
    }
    else
    {
        failures.Add($"{name}{(detail is null ? "" : "  -> " + detail)}");
        Console.WriteLine($"  FAIL {name}{(detail is null ? "" : "  -> " + detail)}");
    }
}

/// 把 runs 还原成「文本 + 样式」的可读形式，方便断言和排错。
string Dump(IReadOnlyList<MdRun> runs)
    => string.Join(" | ", runs.Select(r =>
        r.Kind == MdKind.LineBreak
            ? "<br>"
            : $"{r.Text}[{Describe(r.Style)}{(r.Url is null ? "" : " url=" + r.Url)}]"));

static string Describe(MdStyle s)
{
    if (s == MdStyle.None) return "plain";
    var parts = new List<string>();
    if (s.HasFlag(MdStyle.Bold)) parts.Add("B");
    if (s.HasFlag(MdStyle.Italic)) parts.Add("I");
    if (s.HasFlag(MdStyle.Strike)) parts.Add("S");
    if (s.HasFlag(MdStyle.Code)) parts.Add("C");
    return string.Join("+", parts);
}

/// 断言：把所有 run 拼起来等于预期文本，且每个 run 都是预期样式。
/// 不要求「只有一个 run」—— 转义（\*、\_）会合法地切出多个 run。
void CheckSingle(string name, string input, string expectText, MdStyle expectStyle)
{
    var runs = MarkdownParser.Parse(input);
    string joined = string.Concat(runs.Select(r => r.Text));
    bool ok = joined == expectText && runs.All(r => r.Style == expectStyle);
    Check(name, ok, Dump(runs));
}

Console.WriteLine("== 基本行内标记 ==");
CheckSingle("**粗体** 只留文字并加粗", "**111**", "111", MdStyle.Bold);
CheckSingle("__粗体__", "__111__", "111", MdStyle.Bold);
CheckSingle("*斜体*", "*111*", "111", MdStyle.Italic);
CheckSingle("_斜体_", "_111_", "111", MdStyle.Italic);
CheckSingle("***粗+斜***", "***111***", "111", MdStyle.Bold | MdStyle.Italic);
CheckSingle("`行内代码`", "`code`", "code", MdStyle.Code);
CheckSingle("~~删除线~~", "~~111~~", "111", MdStyle.Strike);
CheckSingle("普通文本原样", "普通文本", "普通文本", MdStyle.None);

var mixed = MarkdownParser.Parse("渲染 **加粗** 与普通文字");
Check("句中加粗切成三段",
    Dump(mixed) == "渲染 [plain] | 加粗[B] |  与普通文字[plain]",
    Dump(mixed));

var combo = MarkdownParser.Parse("**粗 _粗里的斜_ 尾**");
Check("粗体里嵌斜体",
    combo.Count == 3
    && combo[0].Text == "粗 " && combo[0].Style == MdStyle.Bold
    && combo[1].Text == "粗里的斜" && combo[1].Style == (MdStyle.Bold | MdStyle.Italic)
    && combo[2].Text == " 尾" && combo[2].Style == MdStyle.Bold,
    Dump(combo));

Console.WriteLine("\n== 不要误伤（重点）==");
CheckSingle("* 当乘号用不变斜体", "3 * 4 = 12", "3 * 4 = 12", MdStyle.None);
CheckSingle("snake_case 不变斜体", "snake_case_name", "snake_case_name", MdStyle.None);
CheckSingle("未闭合的 ** 原样显示", "这是 **没有闭合", "这是 **没有闭合", MdStyle.None);
CheckSingle("未闭合的 * 原样显示", "a * b", "a * b", MdStyle.None);
CheckSingle("单独的 ** 原样显示", "**", "**", MdStyle.None);
CheckSingle("反斜杠转义星号", @"\*不是斜体\*", "*不是斜体*", MdStyle.None);
CheckSingle("反斜杠转义下划线", @"\_不是斜体\_", "_不是斜体_", MdStyle.None);
CheckSingle("代码里的星号不解析", "`a * b`", "a * b", MdStyle.Code);
CheckSingle("非安全地址按字面量", "[x](javascript:alert(1))", "[x](javascript:alert(1))", MdStyle.None);

Console.WriteLine("\n== 链接 ==");
var link = MarkdownParser.Parse("见 [官网](https://www.deepseek.com) 说明");
Check("链接带 URL 且前后文字保留",
    link.Count == 3
    && link[0].Text == "见 " && link[0].Url is null
    && link[1].Text == "官网" && link[1].Url == "https://www.deepseek.com"
    && link[2].Text == " 说明"
    && !link[2].Text.Contains(')'),
    Dump(link));

CheckSingle("mailto 也算安全", "[写信](mailto:a@b.com)", "写信", MdStyle.None);

Console.WriteLine("\n== 块级标记 ==");
CheckSingle("标题去掉 # 并整行加粗", "# 一级标题", "一级标题", MdStyle.Bold);
CheckSingle("六级标题", "###### 六级", "六级", MdStyle.Bold);
CheckSingle("####### 七个 # 不算标题", "####### 七个", "####### 七个", MdStyle.None);
CheckSingle("# 后面没空格不算标题", "#hashtag", "#hashtag", MdStyle.None);
CheckSingle("无序列表转圆点", "- 第一项", "• 第一项", MdStyle.None);
CheckSingle("* 开头的列表转圆点", "* 第一项", "• 第一项", MdStyle.None);
CheckSingle("引用加竖线", "> 引用内容", "▎ 引用内容", MdStyle.None);

var lines = MarkdownParser.Parse("## 标题\n- 一 **要点**\n- 二 `code`");
Check("多行产生 LineBreak 且各行标记独立",
    lines.Count(r => r.Kind == MdKind.LineBreak) == 2
    && lines.Any(r => r.Text == "标题" && r.Style == MdStyle.Bold)
    && lines.Any(r => r.Text == "要点" && r.Style == MdStyle.Bold)
    && lines.Any(r => r.Text == "code" && r.Style == MdStyle.Code)
    && lines.Any(r => r.Text == "• 一 "),
    Dump(lines));

Console.WriteLine("\n== 退化输入 ==");
Check("空串返回空", MarkdownParser.Parse("").Count == 0);
Check("null 返回空", MarkdownParser.Parse(null).Count == 0);
Check("单个字符", Dump(MarkdownParser.Parse("a")) == "a[plain]");
Check("CRLF 归一化成 LineBreak",
    MarkdownParser.Parse("a\r\nb").Count(r => r.Kind == MdKind.LineBreak) == 1);
try
{
    MarkdownParser.Parse("***");
    Check("纯标记不抛异常", true);
}
catch (Exception ex)
{
    Check("纯标记不抛异常", false, ex.Message);
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine($"全部通过 ✅  ({passed} 项)");
    return 0;
}

Console.WriteLine($"失败 {failures.Count} 项 / 共 {passed + failures.Count} 项：");
foreach (var f in failures) Console.WriteLine("  - " + f);
return 1;
