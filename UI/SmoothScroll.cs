using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Clipt.UI;

/// <summary>
/// Mouse-wheel scrolling for pixel-scrolled lists: each notch moves by the user's Windows
/// "lines to scroll" setting measured in rows (3 rows by default) and eases into place.
/// Precision touchpads send small deltas, which scroll proportionally.
/// </summary>
public static class SmoothScroll
{
    private sealed class State
    {
        public double Target;
        public bool Running;
        public int StuckFrames;
        public double LastOffset = double.NaN;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    public static void Enable(ScrollViewer viewer, Func<double> rowHeight)
    {
        viewer.PreviewMouseWheel += (_, e) => OnWheel(viewer, e, rowHeight);
    }

    private static void OnWheel(ScrollViewer sv, MouseWheelEventArgs e, Func<double> rowHeight)
    {
        if (sv.ScrollableHeight <= 0) return;
        e.Handled = true;

        var st = States.GetValue(sv, _ => new State());
        if (!st.Running) st.Target = sv.VerticalOffset;

        int lines = SystemParameters.WheelScrollLines;
        double step = lines < 0 ? sv.ViewportHeight : Math.Max(1, lines) * rowHeight();
        st.Target = Math.Clamp(st.Target - e.Delta / 120.0 * step, 0, sv.ScrollableHeight);

        if (st.Running) return;
        st.Running = true;
        st.StuckFrames = 0;
        st.LastOffset = double.NaN;

        void Tick(object? sender, EventArgs args)
        {
            double current = sv.VerticalOffset;
            double diff = st.Target - current;
            // Stop when we've arrived, or if layout refuses to move (e.g. the list shrank under us).
            st.StuckFrames = current == st.LastOffset ? st.StuckFrames + 1 : 0;
            st.LastOffset = current;
            if (Math.Abs(diff) < 0.5 || st.StuckFrames > 10 || !sv.IsVisible)
            {
                if (Math.Abs(diff) < 0.5) sv.ScrollToVerticalOffset(st.Target);
                st.Running = false;
                CompositionTarget.Rendering -= Tick;
                return;
            }
            sv.ScrollToVerticalOffset(current + diff * 0.3);
        }
        CompositionTarget.Rendering += Tick;
    }
}
