using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using RimePPT.Core;

namespace RimePPT.Services;

/// <summary>随程序编译的原生矢量配图，保持缩放清晰并跟随工具栏主题。</summary>
internal static class OnboardingIllustrations
{
    internal static UIElement Create(ToolbarCommand command)
    {
        string detail = command switch
        {
            ToolbarCommand.Annotate => "<Path Data='M 72,72 Q 110,64 150,73 T 207,70' Stroke='{ThemeResource AccentFillColorDefaultBrush}' StrokeThickness='5' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/><FontIcon Glyph='&#xE70F;' FontSize='32' Canvas.Left='200' Canvas.Top='49' Foreground='{ThemeResource AccentFillColorDefaultBrush}'/>",
            ToolbarCommand.Eraser => "<Path Data='M 70,73 L 116,73 M 164,73 L 204,73' Stroke='{ThemeResource AccentFillColorDefaultBrush}' StrokeThickness='5' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/><FontIcon Glyph='&#xE75C;' FontSize='36' Canvas.Left='126' Canvas.Top='53' Foreground='{ThemeResource AccentFillColorDefaultBrush}'/>",
            ToolbarCommand.Tools => "<FontIcon Glyph='&#xE708;' FontSize='25' Canvas.Left='72' Canvas.Top='48'/><FontIcon Glyph='&#xE7F4;' FontSize='25' Canvas.Left='126' Canvas.Top='48'/><FontIcon Glyph='&#xE916;' FontSize='25' Canvas.Left='180' Canvas.Top='48' Foreground='{ThemeResource AccentFillColorDefaultBrush}'/>",
            ToolbarCommand.ExitShow => "<FontIcon Glyph='&#xE8BB;' FontSize='28' Canvas.Left='89' Canvas.Top='46'/><FontIcon Glyph='&#xE74E;' FontSize='30' Canvas.Left='159' Canvas.Top='44' Foreground='{ThemeResource AccentFillColorDefaultBrush}'/>",
            ToolbarCommand.Undo => "<FontIcon Glyph='&#xE7A7;' FontSize='37' Canvas.Left='121' Canvas.Top='40' Foreground='{ThemeResource AccentFillColorDefaultBrush}'/>",
            ToolbarCommand.Redo => "<FontIcon Glyph='&#xE7A6;' FontSize='37' Canvas.Left='121' Canvas.Top='40' Foreground='{ThemeResource AccentFillColorDefaultBrush}'/>",
            ToolbarCommand.Pages => "<Border Width='38' Height='30' Canvas.Left='74' Canvas.Top='43' CornerRadius='3' Background='{ThemeResource AccentFillColorDefaultBrush}'/><Border Width='38' Height='30' Canvas.Left='121' Canvas.Top='43' CornerRadius='3' Background='{ThemeResource SubtleFillColorSecondaryBrush}' BorderBrush='{ThemeResource TextFillColorSecondaryBrush}' BorderThickness='1'/><Border Width='38' Height='30' Canvas.Left='168' Canvas.Top='43' CornerRadius='3' Background='{ThemeResource SubtleFillColorSecondaryBrush}' BorderBrush='{ThemeResource TextFillColorSecondaryBrush}' BorderThickness='1'/>",
            _ => $"<FontIcon Glyph='&#x{(command == ToolbarCommand.Prev ? "E72B" : "E72A")};' FontSize='36' Canvas.Left='122' Canvas.Top='42' Foreground='{{ThemeResource AccentFillColorDefaultBrush}}'/>",
        };
        return (UIElement)XamlReader.Load($$"""
            <Viewbox xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="280" Height="116" Stretch="Uniform" IsHitTestVisible="False">
                <Canvas Width="280" Height="116" Background="{ThemeResource SubtleFillColorSecondaryBrush}">
                    <Border Width="204" Height="86" Canvas.Left="38" Canvas.Top="12" CornerRadius="8" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1"/>
                    <Rectangle Width="64" Height="5" Canvas.Left="58" Canvas.Top="25" RadiusX="2" RadiusY="2" Fill="{ThemeResource TextFillColorSecondaryBrush}" Opacity="0.4"/>
                    {{detail}}
                    <Rectangle Width="80" Height="3" Canvas.Left="100" Canvas.Top="105" RadiusX="2" RadiusY="2" Fill="{ThemeResource AccentFillColorDefaultBrush}" Opacity="0.6"/>
                </Canvas>
            </Viewbox>
            """);
    }
}
