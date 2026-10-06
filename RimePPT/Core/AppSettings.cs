using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public static bool IsFreshInstallation { get; private set; }

        public static AppSettings Instance => _instance ??= Load();

        /// <summary>任何设置变更并保存后触发（改属性后需手动调用 Save()）。</summary>
        public static event EventHandler? SettingsChanged;

        public string Theme { get; set; } = "auto"; // light / dark / auto（跟随系统）
        public bool ShowToolbarText { get; set; } = true;
        public bool CheckGitHubUpdatesOnStartup { get; set; } = false;
        public string GitHubDownloadSourceId { get; set; } = "direct";
        public List<string> GitHubCustomDownloadSources { get; set; } = new();
        public string GitHubCustomDownloadSource { get; set; } = "";
        public int EdgeMargin { get; set; } = 6;
        public string PenColor { get; set; } = "red";
        public double PenThickness { get; set; } = 4;
        public bool SmartShapesEnabled { get; set; } = true;
        public InkLineStyle PenLineStyle { get; set; }
        public InkShape PenShape { get; set; }
        public HashSet<ToolbarCommand>? VisibleToolbarCommands { get; set; }
        public Dictionary<ToolbarLayout, List<ToolbarCommand>>? ToolbarCommandsByLayout { get; set; }
        public Dictionary<ToolbarLayout, List<string>> ToolbarItemOrderByLayout { get; set; } = new();
        public static string CommandKey(ToolbarCommand command) => "command:" + command;
        public IReadOnlyList<string> GetToolbarItemOrder(ToolbarLayout layout)
        {
            var available = GetToolbarCommands(layout).Select(CommandKey)
                .Concat(QuickLaunchEntries.Where(x => x.PinnedLayout == layout).Select(x => "launcher:" + x.Id)).ToList();
            ToolbarItemOrderByLayout ??= new();
            var previous = ToolbarItemOrderByLayout.TryGetValue(layout, out var order) ? order : new List<string>();
            var normalized = (previous ?? new()).Where(available.Contains).Concat(available).Distinct().ToList();
            ToolbarItemOrderByLayout[layout] = normalized;
            return normalized.AsReadOnly();
        }
        public void SetToolbarItemOrder(ToolbarLayout layout, IEnumerable<string> keys)
        {
            var selected = keys.Distinct().ToList();
            var commands = GetToolbarCommands(layout).Where(x => selected.Contains(CommandKey(x))).ToList();
            SetToolbarCommands(layout, commands.OrderBy(c => selected.IndexOf(CommandKey(c))));
            foreach (var entry in QuickLaunchEntries.Where(x => x.PinnedLayout == layout))
                if (!selected.Contains("launcher:" + entry.Id)) entry.PinnedLayout = null;
            ToolbarItemOrderByLayout ??= new();
            ToolbarItemOrderByLayout[layout] = selected;
            GetToolbarItemOrder(layout);
        }
        public HashSet<string>? VisibleToolItems { get; set; }
        public List<QuickLaunchEntry> QuickLaunchEntries { get; set; } = new();
        public bool IsToolbarVisible(ToolbarCommand command) => VisibleToolbarCommands?.Contains(command) ?? command <= ToolbarCommand.Redo;
        public bool IsToolVisible(string key) => VisibleToolItems?.Contains(key) ?? true;
        public IReadOnlyList<ToolbarCommand> GetToolbarCommands(ToolbarLayout layout)
        {
            NormalizeToolbarConfiguration();
            return ToolbarCommandsByLayout![layout].AsReadOnly();
        }
        public void SetToolbarCommands(ToolbarLayout layout, IEnumerable<ToolbarCommand> commands)
        {
            if (!Enum.IsDefined(layout)) throw new ArgumentOutOfRangeException(nameof(layout));
            NormalizeToolbarConfiguration();
            ToolbarCommandsByLayout![layout] = ToolbarConfiguration.Normalize(commands);
        }
        private void NormalizeToolbarConfiguration()
        {
            bool migrate = ToolbarCommandsByLayout is null;
            ToolbarCommandsByLayout ??= new();
            foreach (ToolbarLayout layout in Enum.GetValues<ToolbarLayout>())
            {
                if (ToolbarCommandsByLayout.TryGetValue(layout, out var commands))
                    ToolbarCommandsByLayout[layout] = ToolbarConfiguration.Normalize(commands);
                else ToolbarCommandsByLayout[layout] = migrate && VisibleToolbarCommands is not null
                    ? ToolbarConfiguration.Normalize(ToolbarConfiguration.LegacyCandidates(layout).Where(IsToolbarVisible))
                    : ToolbarConfiguration.Defaults(layout);
            }
            foreach (var layout in ToolbarCommandsByLayout.Keys.Where(x => !Enum.IsDefined(x)).ToArray())
                ToolbarCommandsByLayout.Remove(layout);
        }
        public InkBackend InkBackend { get; set; } = InkBackend.Rime;
        public bool DeveloperModeEnabled { get; set; }
        public bool ExitSeparatorEnabled { get; set; }
        public bool SeparateExitToolbarEnabled { get; set; }
        public void ResetPenDefaults()
        {
            PenColor = "red"; CustomPenArgb = null; PenThickness = 4;
            PenLineStyle = InkLineStyle.Solid; PenShape = InkShape.Freehand;
        }
        public string? CustomPenArgb { get; set; }
        public double EraserWidthDip { get; set; } = 56;
        public double EraserHeightDip { get; set; } = 72;
        // 保留旧宽高字段兼容已有设置；新版只调整整体高度，宽度按 SVG 比例联动。
        [System.Text.Json.Serialization.JsonIgnore]
        public double EraserSizeDip
        {
            get => EraserHeightDip;
            set { EraserHeightDip = value; EraserWidthDip = value * 56 / 72; }
        }
        public InkPageAnimationMode InkPageAnimation { get; set; } = InkPageAnimationMode.Replay;
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
            NormalizeToolbarConfiguration();
            if (!Enum.IsDefined(PenLineStyle)) PenLineStyle = InkLineStyle.Solid;
            if (!Enum.IsDefined(PenShape)) PenShape = InkShape.Freehand;
            QuickLaunchEntries ??= new();
            var ids = new HashSet<string>();
            foreach (var entry in QuickLaunchEntries)
            {
                if (string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id)) { entry.Id = Guid.NewGuid().ToString("N"); ids.Add(entry.Id); }
                if (entry.PinnedLayout is { } position && !Enum.IsDefined(position)) entry.PinnedLayout = null;
            }
            if (!DeveloperModeEnabled) { ExitSeparatorEnabled = false; SeparateExitToolbarEnabled = false; }
            if (SeparateExitToolbarEnabled) ExitSeparatorEnabled = false;
            if (!Enum.IsDefined(InkBackend)) InkBackend = InkBackend.Native;
            if (!Enum.IsDefined(InkPageAnimation)) InkPageAnimation = InkPageAnimationMode.Fade;
            InkFadeDurationMs = Math.Clamp(InkFadeDurationMs, 100, 1000);
            InkReplayDurationMs = Math.Clamp(InkReplayDurationMs, 300, 4000);
            PenThickness = double.IsFinite(PenThickness) ? Math.Clamp(PenThickness, 2, 20) : 4;
            EraserWidthDip = double.IsFinite(EraserWidthDip) ? Math.Clamp(EraserWidthDip, 16, 160) : 56;
            EraserSizeDip = double.IsFinite(EraserHeightDip) ? Math.Clamp(EraserHeightDip, 24, 160) : 72;
            if (Theme is not ("auto" or "light" or "dark")) Theme = "auto";
        }
        public bool RunAtStartup { get; set; } = true;
        public bool AutoShowOverlay { get; set; } = true;

        // 工具条五区显示开关：默认保持历史行为（仅左右两条侧栏）。
        // 旧 JSON 缺字段时反序列化落到属性初始化器默认值，正好是期望行为，无需迁移
        public bool ShowLeftRail { get; set; } = true;
        public bool ShowRightRail { get; set; } = true;
        public bool ShowBottomLeft { get; set; } = true;
        public bool ShowBottomCenter { get; set; } = true;
        public bool ShowBottomRight { get; set; } = true;

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
                        _instance = FromExistingJson(json);
                    }
                }
                catch
                {
                    // 损坏设置文件按默认处理
                }

                if (_instance is null)
                {
                    IsFreshInstallation = !File.Exists(FilePath);
                    _instance = new AppSettings();
                    _instance.ToolbarCommandsByLayout = Enum.GetValues<ToolbarLayout>()
                        .ToDictionary(x => x, ToolbarConfiguration.Defaults);
                }

                _instance.Validate();
                _instance.SettingsVersion = 4;

                return _instance;
            }
        }

        public void Save()
        {
            Persist();
            NotifyChanged();
        }
        internal static AppSettings FromExistingJson(string json)
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? throw new JsonException("设置不能为空");
            using var document = JsonDocument.Parse(json);
            var existing = document.RootElement;
            if (!existing.TryGetProperty("RunAtStartup", out _)) settings.RunAtStartup = false;
            if (!existing.TryGetProperty("InkPageAnimation", out _)) settings.InkPageAnimation = InkPageAnimationMode.Fade;
            if (!existing.TryGetProperty("ShowBottomLeft", out _)) settings.ShowBottomLeft = false;
            if (!existing.TryGetProperty("ShowBottomCenter", out _)) settings.ShowBottomCenter = false;
            if (!existing.TryGetProperty("ShowBottomRight", out _)) settings.ShowBottomRight = false;
            if (!existing.TryGetProperty("InkBackend", out _)) settings.InkBackend = InkBackend.Rime;
            if (settings.ToolbarCommandsByLayout is null)
                settings.ToolbarCommandsByLayout = Enum.GetValues<ToolbarLayout>().ToDictionary(x => x,
                    x => ToolbarConfiguration.Normalize(ToolbarConfiguration.LegacyCandidates(x).Where(settings.IsToolbarVisible)));
            settings.Validate(); return settings;
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
