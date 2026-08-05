using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using D365EntitySqlGenerator.ViewModels;

namespace D365EntitySqlGenerator.Views;

public partial class MainWindow : Window
{
    private int _lastMatchOffset = -1;
    private TextRange? _highlight;
    private FlowDocument? _highlightDoc;
    private static readonly Brush HighlightBrush = FrozenHighlight();

    private static Brush FrozenHighlight()
    {
        var b = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xC1, 0x07)); // translucent amber
        b.Freeze();
        return b;
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        // Ctrl+F (ApplicationCommands.Find default gesture) opens the find bar.
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => ShowFind()));
    }

    // ---- Find bar --------------------------------------------------------------------------------

    private void ShowFind()
    {
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void FindClose_Click(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = Visibility.Collapsed;
        ClearHighlight();
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _lastMatchOffset = -1;   // restart the search on new text
        Find(forward: true, fromCurrent: false);
    }

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                FindClose_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Enter:
                Find(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0, fromCurrent: true);
                e.Handled = true;
                break;
            case Key.F3:
                Find(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0, fromCurrent: true);
                e.Handled = true;
                break;
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => Find(forward: true, fromCurrent: true);
    private void FindPrev_Click(object sender, RoutedEventArgs e) => Find(forward: false, fromCurrent: true);

    private void Find(bool forward, bool fromCurrent)
    {
        var query = FindBox.Text;
        if (query.Length == 0)
        {
            FindStatus.Text = "";
            ClearHighlight();
            return;
        }

        var (full, runs) = BuildTextMap();
        if (full.Length == 0) { FindStatus.Text = "0/0"; return; }

        const StringComparison cmp = StringComparison.OrdinalIgnoreCase;
        int idx;
        if (forward)
        {
            var start = fromCurrent && _lastMatchOffset >= 0 ? _lastMatchOffset + 1 : 0;
            if (start > full.Length) start = 0;
            idx = full.IndexOf(query, start, cmp);
            if (idx < 0) idx = full.IndexOf(query, 0, cmp);   // wrap
        }
        else
        {
            var start = _lastMatchOffset > 0 ? _lastMatchOffset - 1 : full.Length - 1;
            idx = start >= 0 ? full.LastIndexOf(query, Math.Min(start, full.Length - 1), cmp) : -1;
            if (idx < 0) idx = full.LastIndexOf(query, full.Length - 1, cmp);   // wrap
        }

        if (idx < 0)
        {
            ClearHighlight();
            FindStatus.Text = "No matches";
            return;
        }

        _lastMatchOffset = idx;
        HighlightMatch(runs, idx, query.Length);

        // status: which match of how many
        int total = CountOccurrences(full, query, cmp);
        int number = CountOccurrences(full[..idx], query, cmp) + 1;
        FindStatus.Text = $"{number}/{total}";
    }

    /// <summary>Highlight the match with an inline background (part of the document flow, so it
    /// scrolls with the text), then bring it into view.</summary>
    private void HighlightMatch(List<(int off, TextPointer ptr, int len)> runs, int offset, int length)
    {
        var s = OffsetToPointer(runs, offset);
        var e = OffsetToPointer(runs, offset + length);
        if (s == null || e == null) return;

        ClearHighlight();
        _highlight = new TextRange(s, e);
        _highlightDoc = SqlBox.Document;
        _highlight.ApplyPropertyValue(TextElement.BackgroundProperty, HighlightBrush);

        var rect = s.GetCharacterRect(LogicalDirection.Forward);
        if (rect.Top < 0 || rect.Bottom > SqlBox.ViewportHeight)
            SqlBox.ScrollToVerticalOffset(SqlBox.VerticalOffset + rect.Top - SqlBox.ViewportHeight / 3);
    }

    private void ClearHighlight()
    {
        if (_highlight != null && ReferenceEquals(_highlightDoc, SqlBox.Document))
        {
            try { _highlight.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent); }
            catch { /* document changed underneath us */ }
        }
        _highlight = null;
        _highlightDoc = null;
    }

    /// <summary>Concatenate all text runs (highlighting splits text into many Runs) with a map
    /// from character offset back to a TextPointer, so multi-token matches can be located.</summary>
    private (string full, List<(int off, TextPointer ptr, int len)> runs) BuildTextMap()
    {
        var sb = new StringBuilder();
        var runs = new List<(int off, TextPointer ptr, int len)>();
        for (var p = SqlBox.Document.ContentStart; p != null; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (p.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) continue;
            var text = p.GetTextInRun(LogicalDirection.Forward);
            runs.Add((sb.Length, p, text.Length));
            sb.Append(text);
        }
        return (sb.ToString(), runs);
    }

    private static TextPointer? OffsetToPointer(List<(int off, TextPointer ptr, int len)> runs, int offset)
    {
        foreach (var r in runs)
            if (offset >= r.off && offset <= r.off + r.len)
                return r.ptr.GetPositionAtOffset(offset - r.off);
        return runs.Count > 0 ? runs[^1].ptr.GetPositionAtOffset(runs[^1].len) : null;
    }

    private static int CountOccurrences(string text, string query, StringComparison cmp)
    {
        if (query.Length == 0) return 0;
        int count = 0, i = 0;
        while ((i = text.IndexOf(query, i, cmp)) >= 0) { count++; i += query.Length; }
        return count;
    }
}
