namespace RimePPT.Core;
public sealed class QuickLaunchEntry
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
    public ToolbarLayout? PinnedLayout { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}
