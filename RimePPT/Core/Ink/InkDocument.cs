using System;
using System.Collections.Generic;

namespace RimePPT.Core.Ink
{
    /// <summary>
    /// 单个演示文稿的墨迹档案：以课件内容 SHA-256 为主键，
    /// PathHint 仅作跨位置匹配的辅助线索。
    /// </summary>
    public sealed class InkDocument
    {
        public string ContentHash { get; set; } = string.Empty;

        /// <summary>保存时的课件完整路径（可能已失效，仅作提示与兜底匹配）。</summary>
        public string PathHint { get; set; } = string.Empty;

        public DateTime SavedAtUtc { get; set; }

        /// <summary>幻灯片页码（从 1 开始）→ 该页笔画集合。</summary>
        public Dictionary<int, List<StrokeData>> Slides { get; set; } = new();
    }
}
