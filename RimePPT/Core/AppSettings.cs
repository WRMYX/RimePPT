using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RimePPT.Core.Ink;

namespace RimePPT.Core
{
    /// <summary>笔迹预设颜色（名称 → ARGB 四字节）。</summary>
    public static class PenPalette
    {
        public static readonly (string Name, byte[] Argb)[] Presets =
        {
            ("red", new byte[] { 0xFF, 0xE8, 0x11, 0x23 }),
            ("blue", new byte[] { 0xFF, 0x00, 0x78, 0xD4 }),
            ("green", new byte[] { 0xFF, 0x10, 0x7C, 0x10 }),
            ("orange", new byte[] { 0xFF, 0xF7, 0x63, 0x0C }),
            ("purple", new byte[] { 0xFF, 0x8B, 0x5C, 0xF6 }),
            ("black", new byte[] { 0xFF, 0x1B, 0x1B, 0x1B }),
        };

        public static byte[] GetArgb(string name)
        {
            foreach (var (name2, argb) in Presets)
            {
                if (string.Equals(name2, name, StringComparison.OrdinalIgnoreCase))
                {
                    return argb;
                }
            }
            return Presets[0].Argb;
        }
    }

    /// <summary>
    /// 应用设置：JSON 持久化于 %LOCALAPPDATA%\RimePPT\settings.json，
    /// 静态单例 + SettingsChanged 通知（在 UI 线程触发）。
    /// </summary>
    public sealed class AppSettings
    {
        private static readonly object Lock = new();
        private static AppSettings? _instance;

        public static AppSettings Instance => _instance ??= Load();

        /// <summary>任何设置变更并保存后触发（改属性后需手动调用 Save()）。</summary>
        public static event EventHandler? SettingsChanged;

        public string Theme { get; set; } = "auto"; // light / dark / auto（跟随系统）
        public bool ShowToolbarText { get; set; } = true;
        public int EdgeMargin { get; set; } = 6;
        public string PenColor { get; set; } = "red";
        public double PenThickness { get; set; } = 4;
        public InkBackend InkBackend { get; set; } = InkBackend.Native;
        public string? CustomPenArgb { get; set; }
        public double EraserWidthDip { get; set; } = 56;
        public double EraserHeightDip { get; set; } = 72;
        public InkPageAnimationMode InkPageAnimation { get; set; } = InkPageAnimationMode.Fade;
        public int InkFadeDurationMs { get; set; } = 240;
        public int InkReplayDurationMs { get; set; } = 1000;
        public byte[] GetPenArgb()
        {
            if (PenColor == "custom" && CustomPenArgb is { Length: 8 } text)
                try { return Convert.FromHexString(text); } catch (FormatException) { }
            return (byte[])PenPalette.GetArgb(PenColor).Clone();
        }
        public void Validate()
        {
            if (!Enum.IsDefined(InkBackend)) InkBackend = InkBackend.Native;
            if (!Enum.IsDefined(InkPageAnimation)) InkPageAnimation = InkPageAnimationMode.Fade;
            InkFadeDurationMs = Math.Clamp(InkFadeDurationMs, 100, 1000);
            InkReplayDurationMs = Math.Clamp(InkReplayDurationMs, 300, 4000);
            PenThickness = double.IsFinite(PenThickness) ? Math.Clamp(PenThickness, 2, 20) : 4;
            EraserWidthDip = double.IsFinite(EraserWidthDip) ? Math.Clamp(EraserWidthDip, 16, 160) : 56;
            EraserHeightDip = double.IsFinite(EraserHeightDip) ? Math.Clamp(EraserHeightDip, 16, 160) : 72;
            if (Theme is not ("auto" or "light" or "dark")) Theme = "auto";
        }
        public bool RunAtStartup { get; set; }
        public bool AutoShowOverlay { get; set; } = true;

        // 工具条五区显示开关：默认保持历史行为（仅左右两条侧栏）。
        // 旧 JSON 缺字段时反序列化落到属性初始化器默认值，正好是期望行为，无需迁移
        public bool ShowLeftRail { get; set; } = true;
        public bool ShowRightRail { get; set; } = true;
        public bool ShowBottomLeft { get; set; } = false;
        public bool ShowBottomCenter { get; set; } = false;
        public bool ShowBottomRight { get; set; } = false;

        // 可空且无初始化值：JSON 缺该字段时保持 null（属性初始化器对"缺失字段"
        // 同样生效，不能用来表达默认），据此做一次性迁移
        public int? SettingsVersion { get; set; }

        /// <summary>按固定顺序返回启用的工具条布局（真实放映 / 预览 / 重建共用）。</summary>
        public ToolbarLayout[] GetEnabledToolbarLayouts()
        {
            var layouts = new List<ToolbarLayout>();
            if (ShowLeftRail) { layouts.Add(ToolbarLayout.LeftRail); }
            if (ShowRightRail) { layouts.Add(ToolbarLayout.RightRail); }
            if (ShowBottomLeft) { layouts.Add(ToolbarLayout.BottomLeft); }
            if (ShowBottomCenter) { layouts.Add(ToolbarLayout.BottomCenter); }
            if (ShowBottomRight) { layouts.Add(ToolbarLayout.BottomRight); }
            return layouts.ToArray();
        }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RimePPT", "settings.json");

        public static AppSettings Load()
        {
            lock (Lock)
            {
                if (_instance is not null)
                {
                    return _instance;
                }

                try
                {
                    if (File.Exists(FilePath))
                    {
                        string json = File.ReadAllText(FilePath);
                        _instance = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                        using var document = JsonDocument.Parse(json);
                        if (!document.RootElement.TryGetProperty("InkBackend", out _)) _instance.InkBackend = InkBackend.Rime;
                    }
                }
                catch
                {
                    // 损坏设置文件按默认处理
                }

                _instance ??= new AppSettings();

                _instance.Validate();
                _instance.SettingsVersion = 3;

                return _instance;
            }
        }

        public void Save()
        {
            Persist();
            NotifyChanged();
        }

        public void NotifyChanged()
        {
            Validate();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        internal void Persist()
        {
            Validate();
            lock (Lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch
                {
                }
            }

        }
    }
}
