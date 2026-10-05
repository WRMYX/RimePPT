using System;
using System.Collections.Generic;
using System.Linq;

namespace RimePPT.Core;

public static class ToolbarConfiguration
{
    public static readonly (ToolbarCommand Command, string Glyph, string Label)[] Commands =
    {
        (ToolbarCommand.Prev,"\uE76B","上一页"), (ToolbarCommand.Next,"\uE76C","下一页"),
        (ToolbarCommand.Annotate,"\uE70F","批注"), (ToolbarCommand.Eraser,"\uE75C","橡皮"),
        (ToolbarCommand.Pages,"\uE8A5","页面导航"), (ToolbarCommand.Undo,"\uE7A7","撤销"),
        (ToolbarCommand.Redo,"\uE7A6","重做"), (ToolbarCommand.Tools,"\uE90F","工具菜单"),
        (ToolbarCommand.ExitShow,"\uE711","退出放映"), (ToolbarCommand.Spotlight,"\uE7B3","聚光与放大"),
        (ToolbarCommand.Blackout,"\uE708","黑屏"), (ToolbarCommand.Timer,"\uE916","计时器"),
        (ToolbarCommand.Export,"\uE896","导出板书"), (ToolbarCommand.Whiteboard,"\uE70F","独立画板"),
        (ToolbarCommand.QuickLaunch,"\uE8A7","快捷启动")
    };

    public static List<ToolbarCommand> Defaults(ToolbarLayout layout) => layout switch
    {
        ToolbarLayout.LeftRail or ToolbarLayout.RightRail => new() { ToolbarCommand.Prev, ToolbarCommand.Next,
            ToolbarCommand.Annotate, ToolbarCommand.Eraser, ToolbarCommand.Tools, ToolbarCommand.ExitShow },
        ToolbarLayout.BottomCenter => new() { ToolbarCommand.Annotate, ToolbarCommand.Eraser,
            ToolbarCommand.Undo, ToolbarCommand.Redo, ToolbarCommand.Tools, ToolbarCommand.ExitShow },
        _ => new() { ToolbarCommand.Prev, ToolbarCommand.Pages, ToolbarCommand.Next }
    };

    // 旧版底部中栏也允许显示这些附加功能，迁移时保留用户的全局选择。
    public static IEnumerable<ToolbarCommand> LegacyCandidates(ToolbarLayout layout) => layout switch
    {
        ToolbarLayout.LeftRail or ToolbarLayout.RightRail => Commands.Select(x => x.Command),
        ToolbarLayout.BottomCenter => Defaults(layout).Concat(Commands.Skip(9).Select(x => x.Command)),
        _ => Defaults(layout)
    };

    public static List<ToolbarCommand> Normalize(IEnumerable<ToolbarCommand>? commands) =>
        commands?.Where(x => Commands.Any(info => info.Command == x)).Distinct().ToList() ?? new();
}
