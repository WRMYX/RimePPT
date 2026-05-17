using Windows.Foundation;
using Windows.UI;

namespace RimePPT.Models
{
    /// <summary>
    /// 代表一条手写笔画
    /// </summary>
    public class StrokeData
    {
        public List<Point> Points { get; set; } = new();
        public Color StrokeColor { get; set; } = Colors.Red;
        public double Thickness { get; set; } = 3.0;
    }
}
