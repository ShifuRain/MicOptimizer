using System.Windows;
using System.Windows.Media;

namespace MicOptimizer.App.Controls;

/// <summary>Vertical segmented level meter, like the LED ladder on outboard gear. Value is in dBFS.</summary>
public sealed class Meter : FrameworkElement
{
    private const int Segments = 28;
    private const double FloorDb = -70, HotDb = -6;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Meter),
        new FrameworkPropertyMetadata(FloorDb, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var ink = (Brush)Application.Current.FindResource("InkBrush");
        var hot = (Brush)Application.Current.FindResource("AccentBrush");
        var off = (Brush)Application.Current.FindResource("LineBrush");

        double gap = 2;
        double segH = (ActualHeight - gap * (Segments - 1)) / Segments;
        if (segH <= 0) return;

        for (int i = 0; i < Segments; i++)
        {
            // Segment i covers up to this level; i = 0 is the bottom one.
            double topDb = FloorDb + (i + 1) * (0 - FloorDb) / Segments;
            bool lit = Value >= topDb - (0 - FloorDb) / Segments / 2;
            double y = ActualHeight - (i + 1) * segH - i * gap;
            var brush = lit ? (topDb > HotDb ? hot : ink) : off;
            dc.PushOpacity(lit ? 1 : 0.55);
            dc.DrawRoundedRectangle(brush, null, new Rect(0, y, ActualWidth, segH), 1, 1);
            dc.Pop();
        }
    }
}
