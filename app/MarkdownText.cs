using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace DshSessionHud;

/// <summary>
/// 把 Markdown 渲染进 <see cref="RichTextBlock"/> 的附加属性。
///
/// 用法：
///     &lt;RichTextBlock local:MarkdownText.Source="{Binding LastText}" /&gt;
///
/// 之所以用附加属性而不是自定义控件：XAML 里直接绑字符串，文本一变就重新解析，
/// 不需要额外的 DataTemplate / 依赖属性包装。
/// 解析在 MarkdownParser.cs（纯 C#，有单元测试）；这个文件只负责把结果画出来。
/// </summary>
public static class MarkdownText
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached(
            "Source",
            typeof(string),
            typeof(MarkdownText),
            new PropertyMetadata(null, OnSourceChanged));

    public static string? GetSource(DependencyObject element) => (string?)element.GetValue(SourceProperty);

    public static void SetSource(DependencyObject element, string? value) => element.SetValue(SourceProperty, value);

    /// <summary>行内代码用的等宽字体；和「当前工作」那块保持一致。</summary>
    private static readonly FontFamily MonoFont = new("Cascadia Mono,Consolas,Courier New");

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBlock target) return;

        target.Blocks.Clear();
        var runs = MarkdownParser.Parse(e.NewValue as string);
        if (runs.Count == 0) return;

        var paragraph = new Paragraph();
        foreach (var run in runs)
        {
            var inline = BuildInline(run);
            if (inline is not null) paragraph.Inlines.Add(inline);
        }
        target.Blocks.Add(paragraph);
    }

    private static Inline? BuildInline(MdRun run)
    {
        if (run.Kind == MdKind.LineBreak) return new LineBreak();
        if (run.Text.Length == 0) return null;

        // 链接：自己处理点击。桌面应用里 Hyperlink 的默认 NavigateUri 行为不一定生效。
        if (run.Url is not null)
        {
            var hyperlink = new Hyperlink();
            hyperlink.Inlines.Add(new Run { Text = run.Text });
            string url = run.Url;
            hyperlink.Click += (_, _) => OpenUrl(url);
            return hyperlink;
        }

        Inline inline = new Run { Text = run.Text };

        // 从内到外依次包装，保证「粗 + 斜 + 码 + 删除线」的组合样式都能叠加生效。
        if (run.Style.HasFlag(MdStyle.Code))
        {
            inline = new Span { FontFamily = MonoFont, Inlines = { inline } };
        }
        if (run.Style.HasFlag(MdStyle.Italic))
        {
            inline = new Italic { Inlines = { inline } };
        }
        if (run.Style.HasFlag(MdStyle.Bold))
        {
            inline = new Bold { Inlines = { inline } };
        }
        if (run.Style.HasFlag(MdStyle.Strike))
        {
            inline = new Span { TextDecorations = TextDecorations.Strikethrough, Inlines = { inline } };
        }
        return inline;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了，HUD 不该因为点了个链接而出问题。
        }
    }
}
