using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using RimePPT.Core;
using RimePPT.Core.Ink;

var view = new InkViewport(1920, 1080);
var tests = new List<(string Name, Action Run)>();
void Test(string name, Action run) => tests.Add((name, run));
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
InkSample S(float x, float y, ulong t = 1) => new(new(x, y), t, InkDevice.Touch);
InkInputSession Session() => new() { Viewport = view };
InkToolSnapshot Pen(int slide = 1) => new(InkTool.Pen, slide, new byte[] { 255, 232, 17, 35 }, 4);
StrokeData Stroke(params Vector2[] points) => new() { Dots = points.Select(view.Normalize).ToList(), ThicknessDips = 4 };
List<InkSample> Circle(int count, float rx = 200, float ry = 200) => Enumerable.Range(0, count + 1)
    .Select(i => S(960 + rx * MathF.Cos(i * MathF.Tau / count), 540 + ry * MathF.Sin(i * MathF.Tau / count), (ulong)i + 1)).ToList();
float Error(List<Vector2> points) => points.Zip(points.Skip(1)).SelectMany(pair => Enumerable.Range(0, 9)
    .Select(i => MathF.Abs(Vector2.Distance(Vector2.Lerp(pair.First, pair.Second, i / 8f), new(960, 540)) - 200))).Max();

Test("pinned launchers migrate after existing commands", () => {
    var s = new AppSettings();
    s.QuickLaunchEntries.Add(new() { Id = "app", Name = "App", PinnedLayout = ToolbarLayout.LeftRail });
    var order = s.GetToolbarItemOrder(ToolbarLayout.LeftRail);
    Check(order.Last() == "launcher:app", "legacy launcher must append");
    Check(order.Take(order.Count - 1).SequenceEqual(s.GetToolbarCommands(ToolbarLayout.LeftRail).Select(AppSettings.CommandKey)), "commands preserved");
});
Test("mixed toolbar order survives JSON and is independent per position", () => {
    var s = new AppSettings();
    s.QuickLaunchEntries.Add(new() { Id = "app", Name = "App", Path = "app.exe", PinnedLayout = ToolbarLayout.LeftRail });
    var right = s.GetToolbarItemOrder(ToolbarLayout.RightRail).ToArray();
    var mixed = s.GetToolbarItemOrder(ToolbarLayout.LeftRail).ToList();
    mixed.Remove("launcher:app"); mixed.Insert(1, "launcher:app");
    s.SetToolbarItemOrder(ToolbarLayout.LeftRail, mixed);
    var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;
    Check(loaded.GetToolbarItemOrder(ToolbarLayout.LeftRail).SequenceEqual(mixed), "mixed order roundtrip");
    Check(loaded.GetToolbarItemOrder(ToolbarLayout.RightRail).SequenceEqual(right), "other rail unchanged");
});
Test("removing pinned item unpins without deleting launcher", () => {
    var s = new AppSettings();
    s.QuickLaunchEntries.Add(new() { Id = "app", Name = "App", PinnedLayout = ToolbarLayout.LeftRail });
    s.SetToolbarItemOrder(ToolbarLayout.LeftRail, s.GetToolbarCommands(ToolbarLayout.LeftRail).Select(AppSettings.CommandKey));
    Check(s.QuickLaunchEntries.Count == 1 && s.QuickLaunchEntries[0].PinnedLayout is null, "unpin only");
});
Test("mixed order discards stale duplicate keys without phantom commands", () => {
    var s = new AppSettings();
    s.SetToolbarCommands(ToolbarLayout.BottomRight, new[] { ToolbarCommand.Eraser });
    s.ToolbarItemOrderByLayout[ToolbarLayout.BottomRight] = new() { "launcher:deleted", "command:Eraser", "command:Eraser", "command:unknown" };
    Check(s.GetToolbarItemOrder(ToolbarLayout.BottomRight).SequenceEqual(new[] { "command:Eraser" }), "normalize keys");
    s.SetToolbarItemOrder(ToolbarLayout.BottomRight, new[] { "command:unknown" });
    Check(s.GetToolbarCommands(ToolbarLayout.BottomRight).Count == 0, "invalid key does not introduce Prev");
});
Test("new command added after mixed order remains visible", () => {
    var s = new AppSettings();
    s.SetToolbarCommands(ToolbarLayout.BottomRight, new[] { ToolbarCommand.Eraser });
    s.GetToolbarItemOrder(ToolbarLayout.BottomRight);
    s.SetToolbarCommands(ToolbarLayout.BottomRight, new[] { ToolbarCommand.Eraser, ToolbarCommand.Annotate });
    Check(s.GetToolbarItemOrder(ToolbarLayout.BottomRight).SequenceEqual(new[] { "command:Eraser", "command:Annotate" }), "append missing tool");
});

List<Vector2> Poly(Vector2[] corners, int samples=24) => corners.Zip(corners.Skip(1)).SelectMany(edge=>Enumerable.Range(0,samples).Select(i=>Vector2.Lerp(edge.First,edge.Second,i/(float)samples))).Concat(new[]{corners[^1]}).ToList();
Test("smart triangle rotates scales and preserves metadata", () => {
    foreach(float size in new[]{40f,120f,300f}) foreach(float angle in new[]{0f,.5f,1.3f}) {
        var matrix=Matrix3x2.CreateRotation(angle)*Matrix3x2.CreateTranslation(500,400);
        var path=Poly(new[]{new Vector2(0,0),new Vector2(size,20),new Vector2(size*.3f,size),Vector2.Zero});
        var input=Stroke(path.Select(x=>Vector2.Transform(x,matrix)).ToArray());
        var shape=SmartShapeRecognizer.Recognize(input,view);
        Check(shape is not null && shape.Dots.Count==4,$"triangle {size} {angle}");
        Check(shape!.Id==input.Id && shape.ThicknessDips==input.ThicknessDips && shape.Argb.SequenceEqual(input.Argb),"metadata");
    }
});
Test("smart rectangle supports rotation jitter and midpoint start", () => {
    var path=Poly(new[]{new Vector2(60,0),new Vector2(160,0),new Vector2(160,90),new Vector2(0,90),Vector2.Zero,new Vector2(60,0)});
    var points=path.Select((x,i)=>Vector2.Transform(x+new Vector2(MathF.Sin(i)*1.5f,MathF.Cos(i)*1.5f),Matrix3x2.CreateRotation(.7f))+new Vector2(600,400)).ToArray();
    var shape=SmartShapeRecognizer.Recognize(Stroke(points),view);
    Check(shape is not null && shape.Dots.Count==5,"rotated noisy rectangle");
});
Test("smart circle accepts sparse dense jitter and closure gap", () => {
    foreach(int count in new[]{16,64,250}) {
        var path=Enumerable.Range(0,count).Select(i=> {float a=i/(float)(count-1)*MathF.Tau*.98f;float r=100+MathF.Sin(i*2.3f)*3;return new Vector2(600,400)+r*new Vector2(MathF.Cos(a),MathF.Sin(a));}).ToArray();
        var shape=SmartShapeRecognizer.Recognize(Stroke(path),view);
        Check(shape is not null && shape.Dots.Count==65,"circle "+count);
    }
});
Test("smart line tolerates wobble but rejects backtracking", () => {
    var path=Enumerable.Range(0,80).Select(i=>new Vector2(300+i*3,300+MathF.Sin(i*.7f)*4)).ToArray();
    Check(SmartShapeRecognizer.Recognize(Stroke(path),view)?.Dots.Count==2,"wobbly line");
    Check(SmartShapeRecognizer.Recognize(Stroke(Poly(new[]{new Vector2(100,100),new Vector2(400,100),new Vector2(150,100),new Vector2(420,100)}).ToArray()),view) is null,"backtracking");
});
Test("smart shapes reject arcs open letters and tiny marks", () => {
    var arc=Enumerable.Range(0,60).Select(i=>new Vector2(500,400)+100*new Vector2(MathF.Cos(i*MathF.PI/59),MathF.Sin(i*MathF.PI/59))).ToArray();
    Check(SmartShapeRecognizer.Recognize(Stroke(arc),view) is null,"open arc");
    Check(SmartShapeRecognizer.Recognize(Stroke(Poly(new[]{new Vector2(200,200),new Vector2(200,350),new Vector2(320,350)}).ToArray()),view) is null,"letter L");
    Check(SmartShapeRecognizer.Recognize(Stroke(new(100,100),new(102,103),new(105,104)),view) is null,"tiny mark");
});
Test("triangle accepts small closure gap and rounded corners", () => {
    var corners=new[]{new Vector2(100,100),new Vector2(320,120),new Vector2(180,300)};
    var path=new List<Vector2>();
    for(int k=0;k<3;k++) {
        var c=corners[k];var prev=corners[(k+2)%3];var next=corners[(k+1)%3];
        var a=Vector2.Lerp(c,prev,.08f);var b=Vector2.Lerp(c,next,.08f);
        for(int j=0;j<8;j++){float t=j/8f;path.Add((1-t)*(1-t)*a+2*t*(1-t)*c+t*t*b);}
        path.AddRange(Poly(new[]{b,Vector2.Lerp(next,c,.08f)},20));
    }
    path.Add(path[0]+new Vector2(5,3));
    Check(SmartShapeRecognizer.Recognize(Stroke(path.ToArray()),view)?.Dots.Count==4,"rounded triangle gap");
});
Test("recognized triangle keeps independent color data and undo replay", () => {
    var input=Stroke(Poly(new[]{new Vector2(200,200),new Vector2(380,230),new Vector2(270,420),new Vector2(200,200)}).ToArray());
    input.SlideIndex=1;
    var shape=SmartShapeRecognizer.Recognize(input,view)!;
    Check(shape is not null && !ReferenceEquals(shape.Argb,input.Argb),"color must clone");
    var pages=new Dictionary<int,List<StrokeData>>{{1,new()}};
    var history=new InkHistory(pages);var before=history.Snapshot(1);pages[1].Add(shape!);history.Record(1,before);
    Check(history.Undo(1) && pages[1].Count==0,"undo recognized stroke");
    Check(history.Redo(1) && pages[1][0].Dots.Count==4,"redo recognized stroke");
    var loaded=JsonSerializer.Deserialize<StrokeData>(JsonSerializer.Serialize(shape))!;
    Check(loaded.Dots.Count==4 && loaded.Id==input.Id,"saved replay geometry");
});

Test("hold tolerance distinguishes contact devices", () => {
    Check(SmartShapeRecognizer.HoldTolerance(InkDevice.Touch)>SmartShapeRecognizer.HoldTolerance(InkDevice.Pen),"touch jitter");
    Check(SmartShapeRecognizer.HoldTolerance(InkDevice.Pen)>SmartShapeRecognizer.HoldTolerance(InkDevice.Mouse),"pen jitter");
});

Test("new user toolbar defaults preserve five independent positions", () => {
    var s = new AppSettings(); s.Validate();
    foreach (var layout in Enum.GetValues<ToolbarLayout>())
        Check(s.GetToolbarCommands(layout).SequenceEqual(ToolbarConfiguration.Defaults(layout)), "default " + layout);
    Check(s.GetToolbarCommands(ToolbarLayout.LeftRail).SequenceEqual(new[] {
        ToolbarCommand.Prev, ToolbarCommand.Next, ToolbarCommand.Annotate, ToolbarCommand.Eraser,
        ToolbarCommand.Tools, ToolbarCommand.ExitShow }), "new rail order");
});
Test("toolbar legacy global visibility migrates without adding bottom navigation tools", () => {
    var s = new AppSettings { VisibleToolbarCommands = new() { ToolbarCommand.Eraser, ToolbarCommand.Next, ToolbarCommand.Whiteboard } };
    s.Validate();
    Check(s.GetToolbarCommands(ToolbarLayout.LeftRail).SequenceEqual(new[] { ToolbarCommand.Next, ToolbarCommand.Eraser, ToolbarCommand.Whiteboard }), "rail migration");
    Check(s.GetToolbarCommands(ToolbarLayout.BottomCenter).SequenceEqual(new[] { ToolbarCommand.Eraser, ToolbarCommand.Whiteboard }), "center migration");
    Check(s.GetToolbarCommands(ToolbarLayout.BottomLeft).SequenceEqual(new[] { ToolbarCommand.Next }), "bottom migration");
});
Test("toolbar changes and order are independent per position", () => {
    var s = new AppSettings(); var right = s.GetToolbarCommands(ToolbarLayout.RightRail).ToArray();
    s.SetToolbarCommands(ToolbarLayout.LeftRail, new[] { ToolbarCommand.Tools, ToolbarCommand.Annotate, ToolbarCommand.Prev });
    Check(s.GetToolbarCommands(ToolbarLayout.LeftRail).SequenceEqual(new[] { ToolbarCommand.Tools, ToolbarCommand.Annotate, ToolbarCommand.Prev }), "order");
    Check(s.GetToolbarCommands(ToolbarLayout.RightRail).SequenceEqual(right), "right modified");
    s.SetToolbarCommands(ToolbarLayout.BottomRight, new[] { ToolbarCommand.Whiteboard });
    Check(s.GetToolbarCommands(ToolbarLayout.BottomLeft).SequenceEqual(ToolbarConfiguration.Defaults(ToolbarLayout.BottomLeft)), "opposite bottom modified");
});
Test("toolbar empty configuration survives validation and JSON roundtrip", () => {
    var s = new AppSettings(); s.SetToolbarCommands(ToolbarLayout.LeftRail, Array.Empty<ToolbarCommand>());
    var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!; restored.Validate();
    Check(restored.GetToolbarCommands(ToolbarLayout.LeftRail).Count == 0, "empty overwritten");
});
Test("toolbar malformed values and duplicates normalize without losing order", () => {
    var s = new AppSettings { ToolbarCommandsByLayout = new() {
        [ToolbarLayout.LeftRail] = new() { ToolbarCommand.Tools, (ToolbarCommand)900, ToolbarCommand.Tools, ToolbarCommand.Next },
        [(ToolbarLayout)99] = new() { ToolbarCommand.Next } } };
    s.Validate();
    Check(s.GetToolbarCommands(ToolbarLayout.LeftRail).SequenceEqual(new[] { ToolbarCommand.Tools, ToolbarCommand.Next }), "order normalized wrong");
    Check(s.ToolbarCommandsByLayout!.Count == 5 && s.GetToolbarCommands(ToolbarLayout.RightRail).Count == 6, "partial repair");
});
Test("toolbar restoring one default retains other layout and non-toolbar settings", () => {
    var s = new AppSettings { PenColor = "purple", InkReplayDurationMs = 1700, VisibleToolbarCommands = new() };
    s.SetToolbarCommands(ToolbarLayout.RightRail, new[] { ToolbarCommand.Export, ToolbarCommand.Eraser });
    s.SetToolbarCommands(ToolbarLayout.LeftRail, ToolbarConfiguration.Defaults(ToolbarLayout.LeftRail));
    var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!; restored.Validate();
    Check(restored.GetToolbarCommands(ToolbarLayout.LeftRail).Count == 6, "legacy global blocked restore");
    Check(restored.GetToolbarCommands(ToolbarLayout.RightRail).SequenceEqual(new[] { ToolbarCommand.Export, ToolbarCommand.Eraser }), "other layout lost");
    Check(restored.PenColor == "purple" && restored.InkReplayDurationMs == 1700, "other settings lost");
});

Test("new defaults enable all positions and replay without experiments", () => {
    var s = new AppSettings(); s.Validate();
    Check(s.InkBackend == InkBackend.Rime && s.RunAtStartup && s.InkPageAnimation == InkPageAnimationMode.Replay, "new defaults");
    Check(s.GetEnabledToolbarLayouts().Length == 5 && !s.DeveloperModeEnabled && !s.ExitSeparatorEnabled && !s.SeparateExitToolbarEnabled, "zones or experiments");
});
Test("existing sparse configuration retains historical defaults and rail navigation", () => {
    var s = AppSettings.FromExistingJson("{}");
    Check(!s.RunAtStartup && s.GetEnabledToolbarLayouts().Length == 2 && s.InkPageAnimation == InkPageAnimationMode.Fade, "legacy defaults changed");
    Check(s.GetToolbarCommands(ToolbarLayout.LeftRail).Contains(ToolbarCommand.Pages) && s.GetToolbarCommands(ToolbarLayout.LeftRail).Count == 9, "legacy commands changed");
});
Test("existing explicit choices survive migration", () => {
    var original = new AppSettings { InkBackend = InkBackend.Native, RunAtStartup = false, InkPageAnimation = InkPageAnimationMode.None, ShowBottomCenter = false };
    original.SetToolbarCommands(ToolbarLayout.LeftRail, new[] { ToolbarCommand.Pages, ToolbarCommand.Redo });
    var s = AppSettings.FromExistingJson(JsonSerializer.Serialize(original));
    Check(s.InkBackend == InkBackend.Native && !s.RunAtStartup && s.InkPageAnimation == InkPageAnimationMode.None && !s.ShowBottomCenter, "choices overwritten");
    Check(s.GetToolbarCommands(ToolbarLayout.LeftRail).SequenceEqual(new[] { ToolbarCommand.Pages, ToolbarCommand.Redo }), "order overwritten");
});
Test("pen reset clears drawing choices without changing other settings", () => {
    var s = new AppSettings { PenColor = "custom", CustomPenArgb = "FF001122", PenThickness = 18, PenShape = InkShape.Rectangle,
        PenLineStyle = InkLineStyle.Wave, InkBackend = InkBackend.Native, EraserHeightDip = 120 };
    s.ResetPenDefaults();
    Check(s.PenColor == "red" && s.CustomPenArgb is null && s.PenThickness == 4 && s.PenShape == InkShape.Freehand && s.PenLineStyle == InkLineStyle.Solid, "reset incomplete");
    Check(s.InkBackend == InkBackend.Native && s.EraserHeightDip == 120 && s.RunAtStartup, "unrelated settings changed");
});
Test("developer unlock and launcher pinning survive restart with exclusive experiments", () => {
    var s = new AppSettings { DeveloperModeEnabled = true, SeparateExitToolbarEnabled = true,
        QuickLaunchEntries = new() { new() { Name = "课堂", Path = "example.pptx", PinnedLayout = ToolbarLayout.RightRail } } };
    var restored = AppSettings.FromExistingJson(JsonSerializer.Serialize(s));
    Check(restored.DeveloperModeEnabled && restored.SeparateExitToolbarEnabled && !restored.ExitSeparatorEnabled, "developer state lost");
    Check(restored.QuickLaunchEntries[0].PinnedLayout == ToolbarLayout.RightRail && restored.QuickLaunchEntries[0].Name == "课堂", "pin lost");
    restored.DeveloperModeEnabled = false; restored.Validate();
    Check(!restored.SeparateExitToolbarEnabled && !restored.ExitSeparatorEnabled, "locked experiments active");
});
Test("single dot commits once", () => {
    var s = Session(); Check(s.Begin(1, S(100, 100), Pen()), "begin");
    Check(s.End(1, null)?.Dots.Count == 1 && s.PointerId is null && s.End(1, null) is null, "dot or double commit");
});
Test("second contact ignored", () => {
    var s = Session(); s.Begin(1, S(100, 100), Pen()); Check(!s.Begin(2, S(200, 200), Pen()), "second begin");
    s.Move(2, new[] { S(300, 300, 2) }); Check(s.End(2, null) is null && s.PointerId == 1, "second ended first");
    Check(s.End(1, null)?.Dots.Count == 1, "second moved first");
});
Test("final sample and immutable tool snapshot", () => {
    var s = Session(); var pen = Pen(3); s.Begin(1, S(100, 100), pen); pen.Argb[1] = 0;
    var stroke = s.End(1, S(180, 160, 2))!;
    Check(stroke.SlideIndex == 3 && stroke.Argb[1] == 232, "snapshot");
    Check(Vector2.Distance(view.ToDip(stroke.Dots[^1]), new(180, 160)) < .001f, "tail");
});
Test("cancel/restart and old/duplicate samples", () => {
    var s = Session(); s.Begin(1, S(10, 10), Pen()); s.Cancel(); Check(s.Begin(2, S(100, 100, 5), Pen(2)), "stuck");
    s.Move(2, new[] { S(500, 500, 3), S(100, 100, 6), S(150, 150, 7) });
    Check(s.SampleCount == 2 && s.LastTimestamp == 7, "sample filtering");
});
Test("incremental curve equals final shape at 8/16/64/240 samples", () => {
    foreach (int count in new[] { 8, 16, 64, 240 }) {
        var samples = Circle(count); var s = Session(); s.Begin(1, samples[0], Pen());
        foreach (var sample in samples.Skip(1)) s.Move(1, new[] { sample });
        var before = s.Preview!.Dots.Select(view.ToDip).ToArray(); var final = s.End(1, samples[^1])!;
        var expected = StrokeGeometry.BuildCurve(samples);
        Check(final.Dots.Count == expected.Count && before.Length == expected.Count, "point count");
        for (int i = 0; i < expected.Count; i++) Check(Vector2.Distance(before[i], expected[i]) < .001f && Vector2.Distance(view.ToDip(final.Dots[i]), expected[i]) < .001f, "release jump");
    }
});
Test("sparse circle improves on original straight infill", () => {
    var samples = Circle(16); var original = BaselineAlgorithms.Curve(samples, view); var curve = StrokeGeometry.BuildCurve(samples);
    float oldError = Error(original), error = Error(curve);
    Console.WriteLine($"  circle16 error: baseline={oldError:F3}, new={error:F3} DIP; points={original.Count}/{curve.Count}");
    Check(error < oldError * .75f && error < 3, "not improved");
});
Test("dense circles and ellipse interpolate samples", () => {
    foreach (var samples in new[] { Circle(64), Circle(120), Circle(16, 300, 80) }) {
        var curve = StrokeGeometry.BuildCurve(samples);
        Check(curve[0] == samples[0].Position && curve[^1] == samples[^1].Position, "endpoints");
        foreach (var sample in samples) Check(curve.Contains(sample.Position), "sample lost");
        Check(curve.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y)), "nonfinite");
    }
});
Test("duplicates, line and sharp reversal remain bounded", () => {
    var curve = StrokeGeometry.BuildCurve(new[] { S(100, 100), S(100, 100, 2), S(500, 100, 3), S(100, 100, 4) });
    Check(curve.Count < 200 && curve.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y)), "unbounded");
    var line = StrokeGeometry.BuildCurve(new[] { S(10, 20), S(100, 20, 2) }); Check(line.Count == 2, "line changed");
});
Test("eraser clips line without interior sample", () => {
    var original = Stroke(new Vector2(100, 500), new(900, 500)); var strokes = new List<StrokeData> { original };
    Check(new StrokeEraser().EraseSweep(strokes, new(new(500, 500), new(500, 500)), view).Changed, "crossing missed");
    Check(strokes.Count == 2 && strokes.All(s => s.Id != original.Id && s.Dots.Count == 2), "split/ids");
    Check(Math.Abs(view.ToDip(strokes[0].Dots[^1]).X - 470) < .01 && Math.Abs(view.ToDip(strokes[1].Dots[0]).X - 530) < .01, "boundary/radius");
});
Test("fast eraser sweep has no gap", () => {
    var strokes = new List<StrokeData> { Stroke(new Vector2(500, 100), new(500, 900)) };
    new StrokeEraser().EraseSweep(strokes, new(new(200, 500), new(800, 500)), view);
    Check(strokes.Count == 2 && view.ToDip(strokes[0].Dots[^1]).Y < 500 && view.ToDip(strokes[1].Dots[0]).Y > 500, "gap");
});
Test("diagonal sweep excludes bounding box corners", () => {
    var dot = Stroke(new Vector2(220, 780)); var strokes = new List<StrokeData> { dot };
    Check(!new StrokeEraser().EraseSweep(strokes, new(new(200, 200), new(800, 800)), view).Changed && ReferenceEquals(strokes[0], dot), "overerase");
});
Test("dot erases and unrelated stroke is unchanged", () => {
    var dot = Stroke(new Vector2(500, 500)); var untouched = Stroke(new Vector2(100, 100), new(200, 100)); var strokes = new List<StrokeData> { dot, untouched };
    Check(new StrokeEraser().EraseSweep(strokes, new(new(500, 500), new(500, 500)), view).Changed, "dot missed");
    Check(strokes.Count == 1 && ReferenceEquals(strokes[0], untouched), "unrelated copied");
});
Test("DPI does not change logical eraser size", () => {
    var a = new List<StrokeData> { Stroke(new Vector2(100, 500), new(900, 500)) }; var b = new List<StrokeData> { Stroke(new Vector2(100, 500), new(900, 500)) };
    new StrokeEraser().EraseSweep(a, new(new(500, 500), new(500, 500)), view); new StrokeEraser().EraseSweep(b, new(new(500, 500), new(500, 500)), view with { Scale = 2 });
    Check(a.Count == b.Count && a[0].Dots[^1].X == b[0].Dots[^1].X, "DPI mismatch");
});
Test("legacy JSON and empty document round trip", () => {
    var old = JsonSerializer.Deserialize<InkDocument>("{\"ContentHash\":\"abc\",\"Slides\":{\"1\":[{\"SlideIndex\":1,\"Argb\":\"/+gRIw==\",\"ThicknessDips\":4,\"Dots\":[{\"X\":0.1,\"Y\":0.2},{\"X\":0.3,\"Y\":0.4}]}]}}")!;
    Check(old.Slides[1][0].Dots[1].X == .3, "legacy"); old.Slides[1].Clear();
    var restored = JsonSerializer.Deserialize<InkDocument>(JsonSerializer.Serialize(old))!; Check(restored.Slides[1].Count == 0 && restored.ContentHash == "abc", "empty");
});
Test("repeated erasure is idempotent at clipped boundaries", () => {
    var strokes = new List<StrokeData> { Stroke(new Vector2(100, 500), new(900, 500)) }; var eraser = new StrokeEraser();
    var sweep = new EraserSweep(new(500, 500), new(500, 500));
    Check(eraser.EraseSweep(strokes, sweep, view).Changed, "first missed");
    var ids = strokes.Select(s => s.Id).ToArray();
    Check(!eraser.EraseSweep(strokes, sweep, view).Changed && ids.SequenceEqual(strokes.Select(s => s.Id)), "repeated change");
});
Test("long stroke commits without losing samples", () => {
    var samples = Circle(2400); var s = Session(); s.Begin(1, samples[0], Pen());
    s.Move(1, samples.Skip(1).ToArray()); var stroke = s.End(1, null)!;
    Check(stroke.Dots.Count >= samples.Count && stroke.Dots.All(d => double.IsFinite(d.X) && double.IsFinite(d.Y)), "long stroke lost");
});
Test("invalid viewport and nonfinite sample rejected", () => {
    var s = new InkInputSession(); Check(!s.Begin(1, S(10, 10), Pen()), "zero viewport"); s.Viewport = view;
    Check(!s.Begin(1, S(float.NaN, 100), Pen()), "NaN accepted");
    Check(s.Begin(1, S(100, 100), Pen()), "restart rejected");
    s.Move(1, new[] { S(float.PositiveInfinity, 200, 2) }); Check(s.SampleCount == 1, "infinity accepted");
});
Test("erasure preserves page color and thickness", () => {
    var stroke = Stroke(new Vector2(100, 500), new(900, 500)); stroke.SlideIndex = 5; stroke.ThicknessDips = 12;
    var strokes = new List<StrokeData> { stroke }; new StrokeEraser().EraseSweep(strokes, new(new(500, 500), new(500, 500)), view);
    Check(strokes.Count == 2 && strokes.All(s => s.SlideIndex == 5 && s.ThicknessDips == 12 && s.Argb.SequenceEqual(stroke.Argb)), "style lost");
});
ClassroomTests.Register(Test, Check, view);
int failures = 0;
Test("ten contacts cross without mixing", () => {
    var manager = new InkContactManager();
    for (uint id=1; id<=10; id++) Check(manager.Begin(id,S(id*30,100),Pen(),view), "begin contact");
    for (uint id=1; id<=10; id++) manager.Find(id)!.Move(id,new[] { S(500-id*30,400,2) });
    var strokes = new List<StrokeData>();
    for (uint id=10; id>=1; id--) { var stroke=manager.End(id,null)!; Check(Vector2.Distance(view.ToDip(stroke.Dots[0]),new(id*30,100))<.01f,"mixed start"); strokes.Add(stroke); }
    Check(manager.Count==0 && strokes.Select(x=>x.Id).Distinct().Count()==10,"contact cleanup");
});
Test("ending one contact preserves others and accepts restart", () => {
    var m=new InkContactManager(); m.Begin(1,S(10,10),Pen(),view); m.Begin(2,S(20,20),Pen(),view);
    Check(m.End(1,null) is not null && m.Find(2)?.PointerId==2,"ended neighbour");
    Check(m.Begin(1,S(50,50),Pen(),view) && m.End(99,null) is null,"restart");
    foreach(var id in m.Ids()) m.End(id,null); Check(m.Count==0,"finish all");
});
Test("history is per-page and preserves ids", () => {
    var pages=new Dictionary<int,List<StrokeData>>();var h=new InkHistory(pages); var a=Stroke(new Vector2(10,10));
    var before=h.Snapshot(1); pages[1]=new(){a};h.Record(1,before);
    pages[2]=new(){Stroke(new Vector2(100,100))};
    Check(h.Undo(1)&&pages[1].Count==0&&pages[2].Count==1,"page isolation");
    Check(h.Redo(1)&&ReferenceEquals(pages[1][0],a),"id restored");
});
Test("clear can undo and new edit drops redo", () => {
    var a=Stroke(new Vector2(10,10));var pages=new Dictionary<int,List<StrokeData>>{{1,new(){a}}};var h=new InkHistory(pages);
    Check(h.Clear(1)&&!h.Clear(1),"clear transaction"); Check(h.Undo(1)&&pages[1][0].Id==a.Id,"restore clear");
    var before=h.Snapshot(1);pages[1].Add(Stroke(new Vector2(80,80)));h.Record(1,before); Check(!h.CanRedo(1),"stale redo");
});
Test("erase fragments undo to original stroke", () => {
    var a=Stroke(new Vector2(100,100),new(600,100));var pages=new Dictionary<int,List<StrokeData>>{{1,new(){a}}};var h=new InkHistory(pages);
    var before=h.Snapshot(1);new StrokeEraser().EraseSweep(pages[1],new(new(300,100),new(300,100),40,40),view);h.Record(1,before);
    Check(pages[1].Count==2&&h.Undo(1)&&ReferenceEquals(pages[1][0],a),"fragment restore");
});
Test("history count budget discards oldest edits", () => {
    var pages=new Dictionary<int,List<StrokeData>>();var h=new InkHistory(pages);
    for(int i=0;i<105;i++){var before=h.Snapshot(1);pages[1]=new(){Stroke(new Vector2(i,10))};h.Record(1,before);}
    int count=0;while(h.Undo(1))count++;Check(count==100,"history cap");Check(h.RetainedBytes()<64L*1024*1024,"byte cap");
});
Test("adjusted eraser respects narrow width", () => {
    var a=Stroke(new Vector2(120,100));var list=new List<StrokeData>{a};var eraser=new StrokeEraser();
    Check(!eraser.EraseSweep(list,new(new(100,100),new(100,100),16,16),view).Changed,"narrow erased far point");
    Check(eraser.EraseSweep(list,new(new(100,100),new(100,100),56,72),view).Changed,"default did not erase");
});

Test("fade has complementary opacity and exact endpoints", () => {
    var animation = new InkPageTransition(InkPageAnimationMode.Fade, 240, 1000, true, true);
    var first = animation.At(0); var middle = animation.At(120); var last = animation.At(240);
    Check(first.OutgoingOpacity == 1 && first.IncomingOpacity == 0 && !first.IsComplete, "first frame");
    Check(Math.Abs(middle.IncomingOpacity - .5f) < .0001 && middle.IncomingOpacity + middle.OutgoingOpacity == 1, "crossfade");
    Check(last.OutgoingOpacity == 0 && last.IncomingOpacity == 1 && last.IsComplete, "end frame");
});
Test("replay and outgoing fade use independent durations", () => {
    var animation = new InkPageTransition(InkPageAnimationMode.Replay, 200, 1000, true, true);
    Check(animation.At(200).OutgoingOpacity == 0 && Math.Abs(animation.At(200).ReplayProgress - .2) < .0001 && !animation.At(200).IsComplete, "independent clocks");
    Check(animation.At(1000).IsComplete && animation.At(1000).ReplayProgress == 1, "replay finish");
    Check(new InkPageTransition(InkPageAnimationMode.Replay, 200, 4000, true, false).At(200).IsComplete, "empty page must not wait for replay");
});
Test("disabled or empty animation settles immediately and invalid durations are bounded", () => {
    Check(new InkPageTransition(InkPageAnimationMode.None, 240, 1000, true, true).At(0).IsComplete, "disabled");
    Check(new InkPageTransition(InkPageAnimationMode.Fade, 240, 1000, false, false).At(0).IsComplete, "empty");
    var corrupt = new InkPageTransition(InkPageAnimationMode.Replay, double.NaN, double.PositiveInfinity, true, true);
    Check(double.IsFinite(corrupt.At(double.NaN).ReplayProgress) && corrupt.At(1000).IsComplete, "nonfinite duration");
});
Test("replay follows arc length through uneven samples and original direction", () => {
    var line = Stroke(new(100, 100), new(110, 100), new(500, 100));
    var plan = new InkReplayPlan(new[] { line }, view);
    var frame = plan.At(.5);
    Check(frame.CompletedStrokes == 0 && frame.VisiblePoints == 2 && frame.Tail is { } p && Vector2.Distance(p, new(300, 100)) < .01, "point-count speed or reversed direction");
    Check(plan.At(0).PartialStroke is null && plan.At(1).CompletedStrokes == 1, "replay endpoints");
});
Test("replay preserves stroke order, dots, saved data and page history", () => {
    var first = Stroke(new(600, 100), new(200, 100)); var second = Stroke(new(100, 400), new(500, 400));
    var strokes = new[] { first, second }; string original = JsonSerializer.Serialize(strokes);
    var plan = new InkReplayPlan(strokes, view);
    Check(ReferenceEquals(plan.At(.25).PartialStroke, first) && plan.At(.25).Tail is { } p && Math.Abs(p.X - 400) < .01, "first stroke direction");
    Check(plan.At(.75).CompletedStrokes == 1 && ReferenceEquals(plan.At(.75).PartialStroke, second), "second stroke sequence");
    for (int i = 0; i <= 100; i++) plan.At(i / 100d);
    Check(original == JsonSerializer.Serialize(strokes), "animation mutated saved ink");
    var dots = new InkReplayPlan(new[] { new StrokeData(), Stroke(new Vector2(10, 10)), Stroke(new(20, 20), new(20, 20)) }, view);
    Check(dots.At(.25).PartialStroke?.Dots.Count == 1 && dots.At(1).CompletedStrokes == 3, "dot/empty handling");
});
Test("replay path scales with viewport and clamps progress", () => {
    var stroke = Stroke(new(100, 100), new(500, 100)); var larger = new InkViewport(view.WidthDip * 2, view.HeightDip * 2, 2);
    var plan = new InkReplayPlan(new[] { stroke }, larger);
    Check(plan.At(.5).Tail is { } p && Vector2.Distance(p, new(600, 200)) < .01, "DPI/viewport");
    Check(plan.At(-1).PartialStroke is null && plan.At(2).CompletedStrokes == 1 && plan.At(double.NaN).PartialStroke is null, "progress bounds");
});

Test("direct shapes replace intermediate samples and preserve source", () => {
    var input = Stroke(new(100,100), new(300,400), new(500,300));
    string original = JsonSerializer.Serialize(input);
    var rectangle = InkDrawing.Apply(input, view, new(InkLineStyle.Dash, InkShape.Rectangle));
    Check(rectangle.Dots.Count == 5 && rectangle.LineStyle == InkLineStyle.Dash, "rectangle metadata");
    Check(Vector2.Distance(view.ToDip(rectangle.Dots[0]), view.ToDip(rectangle.Dots[^1])) < .01, "closed rectangle");
    var line = InkDrawing.Apply(input, view, new(InkLineStyle.Solid, InkShape.Line));
    Check(line.Dots.Count == 2 && Vector2.Distance(view.ToDip(line.Dots[^1]), new(500,300)) < .01, "line endpoint");
    Check(original == JsonSerializer.Serialize(input), "source mutation");
});
Test("ellipse triangle arrow and zero length shapes have finite geometry", () => {
    foreach (var shape in new[] { InkShape.Ellipse, InkShape.Triangle, InkShape.Arrow }) {
        var result = InkDrawing.Apply(Stroke(new(500,300),new(100,100)),view,new(InkLineStyle.Solid,shape));
        Check(result.Dots.Count >= 4 && result.Dots.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)), "finite shape");
        var zero = InkDrawing.Apply(Stroke(new(100,100),new(100,100)),view,new(InkLineStyle.Solid,shape));
        Check(zero.Dots.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)), "zero length");
    }
});
Test("wave geometry persists and replay reaches original saved path", () => {
    var wave = InkDrawing.Apply(Stroke(new(100,100),new(500,100)),view,new(InkLineStyle.Wave,InkShape.Freehand));
    Check(wave.Dots.Count > 20 && wave.Dots.Any(p => Math.Abs(view.ToDip(p).Y - 100) > 1), "wave displacement");
    var restored = JsonSerializer.Deserialize<StrokeData>(JsonSerializer.Serialize(wave))!;
    Check(restored.LineStyle == InkLineStyle.Wave && restored.Dots.Count == wave.Dots.Count, "round trip");
    Check(new InkReplayPlan(new[] { restored }, view).At(1).CompletedStrokes == 1, "replay completion");
});
Test("old ink defaults to solid and erasing keeps selected style", () => {
    Check(JsonSerializer.Deserialize<StrokeData>("{\"Dots\":[]}")!.LineStyle == InkLineStyle.Solid, "legacy default");
    var line = InkDrawing.Apply(Stroke(new(100,100),new(500,100)),view,new(InkLineStyle.DashDot,InkShape.Line));
    var page = new List<StrokeData> { line };
    new StrokeEraser().EraseSweep(page,new(new(300,80),new(300,120),20,20),view);
    Check(page.Count == 2 && page.All(p => p.LineStyle == InkLineStyle.DashDot), "fragment style");
});

string ReleaseJson(string tag = "v1.2.3", string asset = "RimePPT-1.2.3.0-Portable-x64.zip", string digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bool draft = false, bool prerelease = false, string? url = null) => JsonSerializer.Serialize(new {
    tag_name = tag, draft, prerelease, body = "Update notes", published_at = "2026-10-06T01:00:00Z",
    assets = new[] { new { name = asset, size = 100, digest, browser_download_url = url ?? $"https://github.com/WRMYX/RimePPT/releases/download/{tag}/{asset}" } }
});
Test("GitHub update versions compare numerically", () => {
    Check(GitHubRelease.ParseVersion("v1.10.0") > GitHubRelease.ParseVersion("v1.9.9"), "numeric comparison");
    foreach (string tag in new[] { "v1.2.3.1", "v0.1.2", "v1.65536.0", "v1.02.3", "1.2.3", "v1.2.3-beta" }) {
        bool rejected = false; try { GitHubRelease.ParseVersion(tag); } catch { rejected = true; }
        Check(rejected, "invalid tag " + tag);
    }
});
Test("GitHub update selects architecture and installed channel", () => {
    var portable = GitHubRelease.Parse(ReleaseJson(), GitHubUpdateChannel.Portable, "x64");
    Check(portable.Asset is not null && portable.Version == new Version(1,2,3,0), "portable matched");
    Check(GitHubRelease.Parse(ReleaseJson(), GitHubUpdateChannel.Portable, "arm64").Asset is null, "no wrong architecture");
    Check(GitHubRelease.Parse(ReleaseJson(), GitHubUpdateChannel.Msix, "x64").Asset is null, "no wrong channel");
    var msix = GitHubRelease.Parse(ReleaseJson(asset: "RimePPT-1.2.3.0-MSIX-with-certificate-x64.zip"), GitHubUpdateChannel.Msix, "x64");
    Check(msix.Asset is not null, "signed msix matched");
    Check(GitHubRelease.Parse(ReleaseJson(), GitHubUpdateChannel.Store, "x64").Asset is null, "store not updated via github");
});
Test("GitHub update rejects drafts prereleases bad hash and foreign URL", () => {
    foreach (string json in new[] { ReleaseJson(draft:true), ReleaseJson(prerelease:true), ReleaseJson(digest:""), ReleaseJson(url:"https://example.com/file.zip"), ReleaseJson(url:"http://github.com/WRMYX/RimePPT/releases/download/v1.2.3/a.zip") }) {
        bool rejected=false; try { GitHubRelease.Parse(json,GitHubUpdateChannel.Portable,"x64"); } catch { rejected=true; }
        Check(rejected,"unsafe release rejected");
    }
});
Test("GitHub archive blocks traversal absolute and drive paths", () => {
    string root=Path.Combine(Path.GetTempPath(),"rimeppt-update-test");
    Check(GitHubRelease.SafeEntryPath(root,"RimePPT/Assets/icon.ico").StartsWith(root),"normal path");
    foreach(string path in new[]{"../outside.exe","RimePPT/../../outside.exe","C:/outside.exe","/outside.exe","RimePPT\\..\\outside.exe","RimePPT/file:stream","./file"}) {
        bool rejected=false;try{GitHubRelease.SafeEntryPath(root,path);}catch{rejected=true;}
        Check(rejected,"unsafe archive path "+path);
    }
});
Test("GitHub startup checks are opt in for new and existing settings", () => {
    Check(!new AppSettings().CheckGitHubUpdatesOnStartup,"new default off");
    Check(!JsonSerializer.Deserialize<AppSettings>("{}")!.CheckGitHubUpdatesOnStartup,"old default off");
});

GitHubUpdateTests.Register(Test, Check);
foreach (var test in tests) { try { test.Run(); Console.WriteLine("PASS " + test.Name); } catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + ex.Message); } }
if (failures > 0) return 1;
var page = Enumerable.Range(0, 500).Select(i => new StrokeData { Dots = Enumerable.Range(0, 400).Select(j => new StrokeData.Dot { X = .1 + j / 500d, Y = .1 + i / 1500d }).ToList() }).ToList();
var eraser = new StrokeEraser(); var sweep = new EraserSweep(new(50, 1000), new(80, 1000));
BaselineAlgorithms.EraseNoHit(page, sweep.To, view); eraser.EraseSweep(page, sweep, view);
object Measure(string name, Action action) {
    var timings = new List<double>(); var allocations = new List<long>();
    for (int trial = 0; trial < 5; trial++) {
        long allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++) action(); watch.Stop();
        timings.Add(watch.Elapsed.TotalMilliseconds / 100); allocations.Add((GC.GetAllocatedBytesForCurrentThread() - allocated) / 100);
    }
    timings.Sort(); allocations.Sort(); Console.WriteLine($"BENCH {name}: median {timings[2]:F4} ms/move, {allocations[2]} bytes/move");
    return new { name, medianMs = timings[2], medianBytes = allocations[2] };
}
var benchmark = new[] { Measure("original-no-hit-erase", () => BaselineAlgorithms.EraseNoHit(page, sweep.To, view)), Measure("new-no-hit-erase", () => eraser.EraseSweep(page, sweep, view)) };
Console.WriteLine($"RESULT {tests.Count} tests passed; CPU benchmark excludes Win2D/GPU and physical touch latency.");
if (args.Length > 0) File.WriteAllText(args[0], JsonSerializer.Serialize(new { passed = tests.Count, benchmark }, new JsonSerializerOptions { WriteIndented = true }));
return 0;

