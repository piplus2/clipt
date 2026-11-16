using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Clipt.UI;

/// <summary>Attached properties that render a TextBlock with search terms emphasised.</summary>
public static class Highlighter
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Highlighter), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(Highlighter), new PropertyMetadata(null, OnChanged));

    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);
    public static void SetText(DependencyObject o, string? v) => o.SetValue(TextProperty, v);
    public static string? GetQuery(DependencyObject o) => (string?)o.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject o, string? v) => o.SetValue(QueryProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        var text = GetText(tb) ?? "";
        var terms = (GetQuery(tb) ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        tb.Inlines.Clear();
        if (terms.Length == 0 || text.Length == 0)
        {
            tb.Inlines.Add(new Run(text));
            return;
        }

        // Collect match ranges.
        var ranges = new List<(int start, int end)>();
        foreach (var term in terms)
        {
            int i = 0;
            while ((i = text.IndexOf(term, i, StringComparison.CurrentCultureIgnoreCase)) >= 0)
            {
                ranges.Add((i, i + term.Length));
                i += term.Length;
            }
        }
        if (ranges.Count == 0)
        {
            tb.Inlines.Add(new Run(text));
            return;
        }

        ranges.Sort((a, b) => a.start.CompareTo(b.start));
        var merged = new List<(int start, int end)>();
        foreach (var r in ranges)
        {
            if (merged.Count > 0 && r.start <= merged[^1].end)
                merged[^1] = (merged[^1].start, Math.Max(merged[^1].end, r.end));
            else merged.Add(r);
        }

        // If the first hit is far into the line, scroll the text so it's visible.
        int offset = 0;
        if (merged[0].start > 32)
        {
            offset = merged[0].start - 16;
            tb.Inlines.Add(new Run("…"));
        }

        int pos = offset;
        foreach (var (start, end) in merged)
        {
            if (end <= offset) continue;
            int s = Math.Max(start, offset);
            if (s > pos) tb.Inlines.Add(new Run(text[pos..s]));
            var hit = new Run(text[s..end]) { FontWeight = FontWeights.SemiBold };
            hit.SetResourceReference(TextElement.ForegroundProperty, "Fg.Highlight");
            tb.Inlines.Add(hit);
            pos = end;
        }
        if (pos < text.Length) tb.Inlines.Add(new Run(text[pos..]));
    }
}
