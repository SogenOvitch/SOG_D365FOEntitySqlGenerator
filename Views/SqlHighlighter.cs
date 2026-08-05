using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace D365EntitySqlGenerator.Views;

/// <summary>
/// Attached property that renders SQL text into a read-only RichTextBox with SSMS/VS-dark colours.
/// Dependency-free: a small regex tokenizer builds coloured Runs. Set via SqlHighlighter.Text.
/// </summary>
public static class SqlHighlighter
{
    private static readonly Brush Keyword = Frozen(0x56, 0x9C, 0xD6); // blue
    private static readonly Brush Comment = Frozen(0x6A, 0x99, 0x55); // green
    private static readonly Brush StringLit = Frozen(0xCE, 0x91, 0x78); // orange
    private static readonly Brush Number = Frozen(0xB5, 0xCE, 0xA8); // pale green
    private static readonly Brush Param = Frozen(0x9C, 0xDC, 0xFE); // light blue
    private static readonly Brush Punct = Frozen(0xD4, 0xD4, 0xD4); // light grey
    private static readonly Brush Default = Frozen(0xDC, 0xDC, 0xDC);

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "AS", "ON", "IN", "IS", "NULL",
        "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "JOIN", "UNION", "ALL",
        "GROUP", "ORDER", "BY", "HAVING", "DISTINCT", "TOP", "EXISTS", "BETWEEN", "LIKE",
        "CASE", "WHEN", "THEN", "ELSE", "END", "ASC", "DESC", "WITH",
    };

    // token order matters: comments & strings first, then params, numbers, words, punctuation.
    private static readonly Regex Tokenizer = new(
        @"(?<comment>--[^\r\n]*)" +
        @"|(?<string>'(?:[^']|'')*')" +
        @"|(?<param>@\w+)" +
        @"|(?<number>\b\d+(?:\.\d+)?\b)" +
        @"|(?<word>[A-Za-z_][A-Za-z0-9_]*)" +
        @"|(?<punct>[(),.=<>*+\-/;])" +
        @"|(?<ws>\s+)" +
        @"|(?<other>.)",
        RegexOptions.Compiled);

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(SqlHighlighter),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public static void SetText(DependencyObject o, string value) => o.SetValue(TextProperty, value);
    public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox rtb) return;
        var text = e.NewValue as string ?? "";

        const double fontSize = 12.5;
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = fontSize,
            PagePadding = new Thickness(0),
        };
        var para = new Paragraph { Margin = new Thickness(0) };

        int maxLen = 0;
        foreach (var line in text.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.Length > maxLen) maxLen = l.Length;
            AppendLine(para, l);
            para.Inlines.Add(new LineBreak());
        }

        // Size the page to the longest line so the horizontal scrollbar matches the content
        // (Consolas glyph advance ≈ 0.55·em) rather than extending far past it.
        doc.PageWidth = maxLen * fontSize * 0.55 + 24;

        doc.Blocks.Add(para);
        rtb.Document = doc;
    }

    private static void AppendLine(Paragraph para, string line)
    {
        foreach (Match m in Tokenizer.Matches(line))
        {
            Brush brush;
            if (m.Groups["comment"].Success) brush = Comment;
            else if (m.Groups["string"].Success) brush = StringLit;
            else if (m.Groups["param"].Success) brush = Param;
            else if (m.Groups["number"].Success) brush = Number;
            else if (m.Groups["punct"].Success) brush = Punct;
            else if (m.Groups["word"].Success) brush = Keywords.Contains(m.Value) ? Keyword : Default;
            else brush = Default;

            para.Inlines.Add(new Run(m.Value) { Foreground = brush });
        }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
