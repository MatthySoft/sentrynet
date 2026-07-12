using System.Windows;
using System.Windows.Media;
using SentryNet.Models;

namespace SentryNet.Controls;

/// <summary>
/// 60-second down/up rate sparkline for one process row. Down and up share one
/// vertical scale (per-row max) so their relative magnitude reads at a glance.
/// Re-renders when the row's HistoryVersion bumps (once per tick).
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(ProcessRow), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(long), typeof(Sparkline),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public ProcessRow? Row
    {
        get => (ProcessRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public long Version
    {
        get => (long)GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    static readonly Brush DownBrush, UpBrush, DownFill, UpFill, BaselineBrush;
    static readonly Pen DownPen, UpPen, BaselinePen;

    static Sparkline()
    {
        DownBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)));
        UpBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23)));
        DownFill = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0x3F, 0xB9, 0x50)));
        UpFill = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0xF5, 0xA6, 0x23)));
        BaselineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x42)));
        DownPen = FreezePen(new Pen(DownBrush, 1.4));
        UpPen = FreezePen(new Pen(UpBrush, 1.4));
        BaselinePen = FreezePen(new Pen(BaselineBrush, 1.0));
    }

    static Brush Freeze(Brush b) { b.Freeze(); return b; }
    static Pen FreezePen(Pen p)
    {
        p.LineJoin = PenLineJoin.Round;
        p.StartLineCap = PenLineCap.Round;
        p.EndLineCap = PenLineCap.Round;
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        // Transparent hit-test surface so the row hover still works over the cell.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        double baseY = h - 1;
        dc.DrawLine(BaselinePen, new Point(0, baseY), new Point(w, baseY));

        var row = Row;
        if (row == null) return;

        int n = ProcessRow.HistoryLen;
        int head = row.HistoryHead; // oldest sample
        double max = 0;
        for (int i = 0; i < n; i++)
        {
            if (row.DownHistory[i] > max) max = row.DownHistory[i];
            if (row.UpHistory[i] > max) max = row.UpHistory[i];
        }
        if (max <= 0) return;

        double usable = h - 3; // headroom so the peak's line isn't clipped
        DrawSeries(dc, row.UpHistory, head, n, w, baseY, usable, max, UpPen, UpFill);
        DrawSeries(dc, row.DownHistory, head, n, w, baseY, usable, max, DownPen, DownFill);
    }

    static void DrawSeries(DrawingContext dc, double[] data, int head, int n,
                           double w, double baseY, double usable, double max,
                           Pen pen, Brush fill)
    {
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lg = line.Open())
        using (var ag = area.Open())
        {
            ag.BeginFigure(new Point(0, baseY), isFilled: true, isClosed: true);
            for (int i = 0; i < n; i++)
            {
                double v = data[(head + i) % n];
                double x = i * w / (n - 1);
                double y = baseY - v / max * usable;
                var p = new Point(x, y);
                if (i == 0) lg.BeginFigure(p, isFilled: false, isClosed: false);
                else lg.LineTo(p, isStroked: true, isSmoothJoin: false);
                ag.LineTo(p, isStroked: false, isSmoothJoin: false);
            }
            ag.LineTo(new Point(w, baseY), isStroked: false, isSmoothJoin: false);
        }
        line.Freeze();
        area.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, pen, line);
    }
}
