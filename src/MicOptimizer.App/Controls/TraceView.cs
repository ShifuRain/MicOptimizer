using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MicOptimizer.App.Controls;

/// <summary>
/// A rolling 10-second picture of the signal: the raw microphone as a grey silhouette, what is sent out as a
/// vermilion line, and a strip along the bottom marking when the gate was closed. It makes the processing visible:
/// whispers lifted, shouts held, typing erased.
/// </summary>
public sealed class TraceView : FrameworkElement
{
    private const int Points = 300;                 // 30 fps x 10 s
    private const double FloorDb = -70, TopPad = 22, BottomPad = 16;

    private readonly float[] _in = new float[Points];
    private readonly float[] _out = new float[Points];
    private readonly bool[] _gateOpen = new bool[Points];
    private int _head, _count;

    public TraceView()
    {
        SnapsToDevicePixels = true;
    }

    public void Push(float inDb, float outDb, bool gateOpen)
    {
        _in[_head] = inDb; _out[_head] = outDb; _gateOpen[_head] = gateOpen;
        _head = (_head + 1) % Points;
        if (_count < Points) _count++;
        InvalidateVisual();
    }

    private double Y(double db) =>
        TopPad + (ActualHeight - TopPad - BottomPad) * (1 - (Math.Clamp(db, FloorDb, 0) - FloorDb) / (0 - FloorDb));

    private double X(int age) => ActualWidth - age * ActualWidth / (Points - 1);   // age 0 = newest, at the right edge

    protected override void OnRender(DrawingContext dc)
    {
        var ink = (Brush)Application.Current.FindResource("InkBrush");
        var muted = (Brush)Application.Current.FindResource("InkMutedBrush");
        var line = (Brush)Application.Current.FindResource("LineBrush");
        var surface = (Brush)Application.Current.FindResource("SurfaceBrush");
        var accent = (Brush)Application.Current.FindResource("AccentBrush");
        var typeface = new Typeface(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        FormattedText Text(string s, Brush b) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10.5, b, dip);

        dc.DrawRectangle(surface, null, new Rect(0, 0, ActualWidth, ActualHeight));

        // Level grid
        var gridPen = new Pen(line, 1) { DashStyle = DashStyles.Dot };
        foreach (int db in new[] { 0, -6, -20, -40, -60 })
        {
            double y = Math.Round(Y(db)) + 0.5;
            dc.DrawLine(gridPen, new Point(0, y), new Point(ActualWidth, y));
            var label = Text(db == 0 ? "0 dB" : db.ToString(CultureInfo.InvariantCulture), muted);
            dc.DrawText(label, new Point(ActualWidth - label.Width - 6, y - label.Height - 1));
        }

        // Legend
        double lx = 12;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(120, 0x6A, 0x6D, 0x73)), null, new Rect(lx, 7, 12, 8));
        var l1 = Text("your mic", ink); dc.DrawText(l1, new Point(lx + 18, 4));
        double lx2 = lx + 18 + l1.Width + 18;
        dc.DrawRectangle(accent, null, new Rect(lx2, 10, 12, 2));
        var l2 = Text("sent out", ink); dc.DrawText(l2, new Point(lx2 + 18, 4));
        double lx3 = lx2 + 18 + l2.Width + 18;
        dc.DrawRectangle(line, null, new Rect(lx3, 7, 12, 8));
        dc.DrawText(Text("gate closed", ink), new Point(lx3 + 18, 4));

        if (_count < 2) return;

        // Index of the sample that is `age` steps old.
        int At(int age) => ((_head - 1 - age) % Points + Points) % Points;

        // Raw mic: filled silhouette
        var fill = new StreamGeometry();
        using (var ctx = fill.Open())
        {
            double baseY = Y(FloorDb);
            ctx.BeginFigure(new Point(X(_count - 1), baseY), true, true);
            for (int age = _count - 1; age >= 0; age--) ctx.LineTo(new Point(X(age), Y(_in[At(age)])), true, false);
            ctx.LineTo(new Point(X(0), baseY), true, false);
        }
        fill.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(120, 0x6A, 0x6D, 0x73)), null, fill);

        // Gate-closed strip along the bottom
        double stripTop = ActualHeight - BottomPad + 3;
        for (int age = _count - 1; age >= 0; age--)
        {
            if (_gateOpen[At(age)]) continue;
            int start = age;
            while (age > 0 && !_gateOpen[At(age - 1)]) age--;
            dc.DrawRectangle(line, null, new Rect(X(start), stripTop, Math.Max(2, X(age) - X(start) + 2), 8));
        }

        // Output line
        var outLine = new StreamGeometry();
        using (var ctx = outLine.Open())
        {
            ctx.BeginFigure(new Point(X(_count - 1), Y(_out[At(_count - 1)])), false, false);
            for (int age = _count - 2; age >= 0; age--) ctx.LineTo(new Point(X(age), Y(_out[At(age)])), true, true);
        }
        outLine.Freeze();
        dc.DrawGeometry(null, new Pen(accent, 1.6) { LineJoin = PenLineJoin.Round }, outLine);
    }
}
