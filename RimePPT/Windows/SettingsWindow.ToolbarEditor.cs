using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RimePPT.Core;

namespace RimePPT.Windows;

public sealed partial class SettingsWindow
{
    private readonly ComboBox _toolbarPosition = new() { Header = "编辑位置", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListView _toolbarOrder = new() { SelectionMode = ListViewSelectionMode.None,
        CanReorderItems = true, CanDragItems = true, AllowDrop = true, IsSwipeEnabled = true,
        ReorderMode = ListViewReorderMode.Enabled,
        HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _toolbarAvailable = new() { Header = "可添加功能", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _toolbarEmpty = new() { Text = "此位置没有按钮，工具栏将隐藏。", TextWrapping = TextWrapping.Wrap };
    private ObservableCollection<ToolbarEditorItem> _toolbarRows = new();
    private bool _toolbarDragHandle;
    private ToolbarLayout _dragLayout;
    private ObservableCollection<ToolbarEditorItem>? _dragRows;
    private readonly string _toolbarDragFormat = "RimePPT.ToolbarOrder." + Guid.NewGuid().ToString("N");
    private string? _dragCommand;
    private string[] _dragOriginalOrder = Array.Empty<string>();
    private int _dragInsertionIndex = -1;
    private ToolbarLayout EditingLayout => (ToolbarLayout)Math.Max(0, _toolbarPosition.SelectedIndex);

    private void InitializeToolbarEditor(StackPanel host)
    {
        _toolbarOrder.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(@"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <Grid ColumnSpacing='8' MinHeight='48'>
    <Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
    <Border Tag='toolbar-drag-handle' MinWidth='40' MinHeight='40' Background='Transparent' AutomationProperties.Name='{Binding DragName}' ToolTipService.ToolTip='拖动调整顺序'><FontIcon Glyph='&#xE700;' FontSize='16'/></Border>
    <StackPanel Grid.Column='1' Orientation='Horizontal' Spacing='8' VerticalAlignment='Center'><FontIcon Glyph='{Binding Glyph}' FontSize='18'/><TextBlock Text='{Binding Label}' VerticalAlignment='Center'/></StackPanel>
    <Button Grid.Column='2' Tag='previous' MinWidth='40' MinHeight='40' ToolTipService.ToolTip='前移' IsEnabled='{Binding CanMovePrevious}' AutomationProperties.Name='{Binding PreviousName}'><FontIcon Glyph='&#xE74A;' FontSize='14'/></Button>
    <Button Grid.Column='3' Tag='next' MinWidth='40' MinHeight='40' ToolTipService.ToolTip='后移' IsEnabled='{Binding CanMoveNext}' AutomationProperties.Name='{Binding NextName}'><FontIcon Glyph='&#xE74B;' FontSize='14'/></Button>
    <Button Grid.Column='4' Tag='remove' MinWidth='40' MinHeight='40' ToolTipService.ToolTip='移除' AutomationProperties.Name='{Binding RemoveName}'><FontIcon Glyph='&#xE738;' FontSize='14'/></Button>
  </Grid>
</DataTemplate>");
        var itemStyle = new Style(typeof(ListViewItem));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _toolbarOrder.ItemContainerStyle = itemStyle;
        _toolbarOrder.ContainerContentChanging += (_, e) =>
        {
            if (!e.InRecycleQueue) e.RegisterUpdateCallback(1, (_, update) => WireToolbarRow(update.ItemContainer));
        };
        host.Children.Add(new TextBlock { Text = "每个位置独立设置。拖动左侧手柄排序，或使用前移／后移。侧栏从上到下，底栏从左到右。", TextWrapping = TextWrapping.Wrap });
        foreach (string label in new[] { "左侧工具栏", "右侧工具栏", "底部左侧", "底部中间", "底部右侧" }) _toolbarPosition.Items.Add(label);
        _toolbarPosition.SelectedIndex = 0;
        _toolbarPosition.SelectionChanged += (_, _) => RefreshToolbarEditor();
        AutomationProperties.SetName(_toolbarPosition, "编辑工具栏位置");
        AutomationProperties.SetName(_toolbarOrder, "已显示功能顺序");
        AutomationProperties.SetName(_toolbarAvailable, "可添加功能");
        host.Children.Add(_toolbarPosition);
        host.Children.Add(new TextBlock { Text = "已显示功能", FontSize = 18 });
        host.Children.Add(_toolbarEmpty);
        host.Children.Add(_toolbarOrder);
        _toolbarOrder.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            _toolbarDragHandle = false;
            var source = e.OriginalSource as DependencyObject;
            while (source is not null && !ReferenceEquals(source, _toolbarOrder))
            {
                if (source is FrameworkElement { Tag: "toolbar-drag-handle" }) { _toolbarDragHandle = true; break; }
                source = VisualTreeHelper.GetParent(source);
            }
        }), true);
        _toolbarOrder.DragItemsStarting += (_, e) =>
        {
            e.Cancel = !_toolbarDragHandle;
            CrashReporter.Log($"toolbar reorder start: handle={_toolbarDragHandle}, count={_toolbarRows.Count}");
            _dragLayout = EditingLayout; _dragRows = _toolbarRows;
            _dragCommand = e.Items.OfType<ToolbarEditorItem>().FirstOrDefault()?.Key;
            _dragOriginalOrder = _toolbarRows.Select(x => x.Key).ToArray();
            _dragInsertionIndex = -1;
            if (!e.Cancel)
            {
                e.Data.RequestedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                e.Data.SetData(_toolbarDragFormat, true);
            }
        };
        _toolbarOrder.DragOver += (_, e) =>
        {
            if (_dragRows is null || !ReferenceEquals(_dragRows, _toolbarRows) || !e.DataView.Contains(_toolbarDragFormat)) return;
            e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            _dragInsertionIndex = _toolbarRows.Count;
            for (int i = 0; i < _toolbarRows.Count; i++)
            {
                if (_toolbarOrder.ContainerFromIndex(i) is FrameworkElement item && e.GetPosition(item).Y < item.ActualHeight / 2)
                { _dragInsertionIndex = i; break; }
            }
        };
        _toolbarOrder.DragItemsCompleted += (_, e) =>
        {
            CrashReporter.Log($"toolbar reorder completed: {e.DropResult}, count={_dragRows?.Count}");
            var rows = _dragRows; var layout = _dragLayout;
            if (e.DropResult == global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move &&
                rows is not null && _dragCommand is { } command && _dragInsertionIndex >= 0 &&
                rows.Select(x => x.Key).SequenceEqual(_dragOriginalOrder))
            {
                // 部分运行时只报告 Move；集合尚未换位时按同一原生落点补齐。
                int source = rows.ToList().FindIndex(x => x.Key == command);
                if (source >= 0)
                {
                    int destination = Math.Clamp(_dragInsertionIndex - (source < _dragInsertionIndex ? 1 : 0), 0, rows.Count - 1);
                    if (destination != source) rows.Move(source, destination);
                }
            }
            _dragRows = null; _toolbarDragHandle = false;
            _dragCommand = null; _dragInsertionIndex = -1;
            if (rows is null || e.DropResult != global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move) return;
            // 等待 ListView 完成集合重排，再持久化；切换编辑位置不会写错目标。
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed) return;
                AppSettings.Instance.SetToolbarItemOrder(layout, rows.Select(x => x.Key));
                AppSettings.Instance.Save();
                if (EditingLayout == layout) RefreshToolbarEditor();
            });
        };
        var actions = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        actions.Children.Add(_toolbarAvailable);
        var add = new Button { Content = "添加到此工具栏", MinHeight = 40 };
        add.Click += (_, _) =>
        {
            if (_toolbarAvailable.SelectedItem is not ComboBoxItem { Tag: ToolbarCommand command }) return;
            var commands = AppSettings.Instance.GetToolbarCommands(EditingLayout).ToList();
            if (!commands.Contains(command)) commands.Add(command);
            SaveToolbarOrder(commands);
        };
        Grid.SetColumn(add, 1); add.VerticalAlignment = VerticalAlignment.Bottom;
        actions.Children.Add(add); host.Children.Add(actions);
        var reset = new Button { Content = "恢复此位置默认配置", MinHeight = 40 };
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Click += (_, _) =>
        {
            AppSettings.Instance.SetToolbarCommands(EditingLayout, ToolbarConfiguration.Defaults(EditingLayout));
            var keys = AppSettings.Instance.GetToolbarCommands(EditingLayout).Select(AppSettings.CommandKey)
                .Concat(AppSettings.Instance.QuickLaunchEntries.Where(x => x.PinnedLayout == EditingLayout).Select(x => "launcher:" + x.Id));
            AppSettings.Instance.SetToolbarItemOrder(EditingLayout, keys);
            AppSettings.Instance.Save(); RefreshToolbarEditor();
        };
        host.Children.Add(reset);
        RefreshToolbarEditor();
    }

    private void SaveToolbarOrder(System.Collections.Generic.IEnumerable<ToolbarCommand> commands)
    {
        AppSettings.Instance.SetToolbarCommands(EditingLayout, commands);
        AppSettings.Instance.Save();
        RefreshToolbarEditor();
    }

    private void RefreshToolbarEditor()
    {
        var commands = AppSettings.Instance.GetToolbarCommands(EditingLayout);
        var order = AppSettings.Instance.GetToolbarItemOrder(EditingLayout);
        _toolbarRows = new();
        for (int i = 0; i < order.Count; i++)
        {
            var key = order[i];
            var entry = AppSettings.Instance.QuickLaunchEntries.FirstOrDefault(x => key == "launcher:" + x.Id);
            var info = ToolbarConfiguration.Commands.FirstOrDefault(x => AppSettings.CommandKey(x.Command) == key);
            _toolbarRows.Add(new ToolbarEditorItem { Key = key, Command = entry is null ? info.Command : ToolbarCommand.QuickLaunch,
                Glyph = entry is null ? info.Glyph : "\uE8A7", Label = entry is null ? info.Label : entry.Name,
                CanMovePrevious = i > 0, CanMoveNext = i < order.Count - 1 });
        }
        _toolbarOrder.ItemsSource = _toolbarRows;
        _toolbarEmpty.Visibility = order.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _toolbarAvailable.Items.Clear();
        foreach (var info in ToolbarConfiguration.Commands.Where(x => !commands.Contains(x.Command) &&
            !(EditingLayout is ToolbarLayout.LeftRail or ToolbarLayout.RightRail && x.Command == ToolbarCommand.Pages)))
            _toolbarAvailable.Items.Add(new ComboBoxItem { Content = info.Label, Tag = info.Command });
        _toolbarAvailable.SelectedIndex = _toolbarAvailable.Items.Count > 0 ? 0 : -1;
        _toolbarAvailable.IsEnabled = _toolbarAvailable.Items.Count > 0;
    }

    private void OnToolbarEditorAction(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ToolbarEditorItem item, Tag: string action }) return;
        var list = AppSettings.Instance.GetToolbarItemOrder(EditingLayout).ToList();
        int index = list.IndexOf(item.Key);
        if (index < 0) return;
        list.RemoveAt(index);
        if (action != "remove") list.Insert(Math.Clamp(index + (action == "previous" ? -1 : 1), 0, list.Count), item.Key);
        AppSettings.Instance.SetToolbarItemOrder(EditingLayout, list);
        AppSettings.Instance.Save(); RefreshToolbarEditor(); RefreshLauncherRows();
    }

    private void WireToolbarRow(Microsoft.UI.Xaml.Controls.Primitives.SelectorItem container)
    {
        container.CanDrag = true;
        void Wire(DependencyObject node)
        {
            if (node is Button button)
            {
                button.Click -= OnToolbarEditorAction;
                button.Click += OnToolbarEditorAction;
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Wire(VisualTreeHelper.GetChild(node, i));
        }
        Wire(container);
    }
}

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class ToolbarEditorItem
{
    public string Key { get; set; } = "";
    public ToolbarCommand Command { get; set; }
    public string Glyph { get; set; } = "";
    public string Label { get; set; } = "";
    public bool CanMovePrevious { get; set; }
    public bool CanMoveNext { get; set; }
    public string DragName => Label + " 排序手柄";
    public string PreviousName => Label + " 前移";
    public string NextName => Label + " 后移";
    public string RemoveName => Label + " 移除";
}
