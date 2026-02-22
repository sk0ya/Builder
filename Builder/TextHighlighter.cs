using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Builder;

public static class TextHighlighter
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(TextHighlighter),
            new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty QueryProperty =
        DependencyProperty.RegisterAttached("Query", typeof(string), typeof(TextHighlighter),
            new PropertyMetadata(string.Empty, OnChanged));

    public static string GetText(TextBlock tb) => (string)tb.GetValue(TextProperty);
    public static void SetText(TextBlock tb, string value) => tb.SetValue(TextProperty, value);

    public static string GetQuery(TextBlock tb) => (string)tb.GetValue(QueryProperty);
    public static void SetQuery(TextBlock tb, string value) => tb.SetValue(QueryProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;

        var text = GetText(tb) ?? string.Empty;
        var query = GetQuery(tb) ?? string.Empty;

        tb.Inlines.Clear();

        if (string.IsNullOrEmpty(query))
        {
            tb.Inlines.Add(new Run(text));
            return;
        }

        Brush highlightBrush = Brushes.Orange;
        if (Application.Current?.Resources["ThemeAccent"] is SolidColorBrush accent)
        {
            var c = accent.Color;
            highlightBrush = new SolidColorBrush(Color.FromArgb(90, c.R, c.G, c.B));
        }

        int start = 0;
        while (start < text.Length)
        {
            int idx = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                tb.Inlines.Add(new Run(text[start..]));
                break;
            }

            if (idx > start)
                tb.Inlines.Add(new Run(text[start..idx]));

            tb.Inlines.Add(new Run(text[idx..(idx + query.Length)])
            {
                Background = highlightBrush,
            });

            start = idx + query.Length;
        }
    }
}
