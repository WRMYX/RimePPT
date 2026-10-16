using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace RimePPT.Controls;

/// <summary>Preserves command order without a scrolling or touch-panning surface.</summary>
public sealed class ToolbarWrapPanel : Panel
{
    public Orientation Orientation { get; set; }
    private double _primaryLimit = double.PositiveInfinity;
    public double PrimaryLimit
    {
        get => _primaryLimit;
        set { if (_primaryLimit == value) return; _primaryLimit = value; InvalidateMeasure(); }
    }
    private sealed class Line
    {
        public readonly List<UIElement> Items = new();
        public double Length, Breadth;
    }
    private List<Line> BuildLines(bool measure)
    {
        bool vertical = Orientation == Orientation.Vertical;
        var lines = new List<Line>();
        var line = new Line();
        foreach (UIElement child in Children)
        {
            if (measure) child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            double length = vertical ? size.Height : size.Width;
            double breadth = vertical ? size.Width : size.Height;
            if (line.Items.Count > 0 && line.Length + length > _primaryLimit)
            { lines.Add(line); line = new Line(); }
            line.Items.Add(child); line.Length += length; line.Breadth = Math.Max(line.Breadth, breadth);
        }
        if (line.Items.Count > 0) lines.Add(line);
        return lines;
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        double length = 0, breadth = 0;
        foreach (var line in BuildLines(true)) { length = Math.Max(length, line.Length); breadth += line.Breadth; }
        return Orientation == Orientation.Vertical ? new Size(breadth, length) : new Size(length, breadth);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        bool vertical = Orientation == Orientation.Vertical;
        var lines = BuildLines(false);
        double measuredBreadth = 0;
        foreach (var line in lines) measuredBreadth += line.Breadth;
        double cross = vertical ? finalSize.Width : finalSize.Height;
        double extra = lines.Count == 0 ? 0 : Math.Max(0, cross - measuredBreadth) / lines.Count;
        double secondary = 0;
        foreach (var line in lines)
        {
            double breadth = line.Breadth + extra;
            double primary = 0;
            foreach (var child in line.Items)
            {
                var size = child.DesiredSize;
                // Every button fills its own row/column; content remains centered by ToolbarButton.
                child.Arrange(vertical ? new Rect(secondary, primary, breadth, size.Height)
                    : new Rect(primary, secondary, size.Width, breadth));
                primary += vertical ? size.Height : size.Width;
            }
            secondary += breadth;
        }
        return finalSize;
    }
}
