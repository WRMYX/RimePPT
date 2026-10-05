using System;
using System.Collections.Generic;

namespace RimePPT.Core.Ink
{
    /// <summary>
    /// 单笔画：点为相对屏幕的 0–1 归一化坐标（与分辨率/DPI 无关，便于跨会话还原）。
    /// </summary>
    public sealed class StrokeData
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>归属幻灯片页码（从 1 开始）。</summary>
        public int SlideIndex { get; set; }

        /// <summary>ARGB 四字节。</summary>
        public byte[] Argb { get; set; } = { 0xFF, 0xE8, 0x11, 0x23 };

        public float ThicknessDips { get; set; } = 4f;
        public InkLineStyle LineStyle { get; set; }

        public List<Dot> Dots { get; set; } = new();

        public sealed class Dot
        {
            public double X { get; set; }
            public double Y { get; set; }
        }
    }
}
