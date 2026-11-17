using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Clipt.UI;

/// <summary>Attached properties that render a TextBlock with search terms emphasised.</summary>
public sealed class Highlighter : AvaloniaObject
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<Highlighter, TextBlock, string?>("Text");

    public static readonly AttachedProperty<string?> QueryProperty =
        AvaloniaProperty.RegisterAttached<Highlighter, TextBlock, string?>("Query");

    public static string? GetText(TextBlock o) => o.GetValue(TextProperty);
    public static void SetText(TextBlock o, string? v) => o.SetValue(TextProperty, v);
    public static string? GetQuery(TextBlock o) => o.GetValue(QueryProperty);
    public static void SetQuery(TextBlock o, string? v) => o.SetValue(QueryProperty, v);

    static Highlighter()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((tb, _) => Render(tb));
        QueryProperty.Changed.AddClassHandler<TextBlock>((tb, _) => Render(tb));
    }

    private static void Render(TextBlock tb)
    {
        var text = GetText(tb) ?? "";
        var terms = (GetQuery(tb) ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var inlines = new InlineCollection();
        tb.Inlines = inlines;
        if (terms.Length == 0 || text.Length == 0)
        {
            inlines.Add(new Run(text));
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
            inlines.Add(new Run(text));
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
            inlines.Add(new Run("…"));
        }

        var highlight = tb.TryFindResource("Fg.Highlight", tb.ActualThemeVariant, out var res) && res is IBrush b ? b : null;
        int pos = offset;
        foreach (var (start, end) in merged)
        {
            if (end <= offset) continue;
            int s = Math.Max(start, offset);
            if (s > pos) inlines.Add(new Run(text[pos..s]));
            var hit = new Run(text[s..end]) { FontWeight = FontWeight.SemiBold };
            if (highlight is not null) hit.Foreground = highlight;
            inlines.Add(hit);
            pos = end;
        }
        if (pos < text.Length) inlines.Add(new Run(text[pos..]));
    }
}
