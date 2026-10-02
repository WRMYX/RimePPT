using System.Numerics;
using System.Text;
using System.Text.Json;
using RimePPT.Core;
using RimePPT.Core.Ink;
using RimePPT.Services;

internal static class ClassroomTests
{
    internal static void Register(Action<string, Action> test, Action<bool, string> check, InkViewport view)
    {
        StrokeData Stroke(IEnumerable<Vector2> points) => new() { SlideIndex = 3, ThicknessDips = 6, Dots = points.Select(view.Normalize).ToList() };
        test("smart line preserves endpoints, style and original ink", () =>
        {
            var source = Stroke(Enumerable.Range(0, 30).Select(i => new Vector2(100 + 10 * i, 200 + MathF.Sin(i) * 2)));
            var shape = SmartShapeRecognizer.Recognize(source, view);
            check(shape is { Dots.Count: 2 } && shape.Id == source.Id && shape.SlideIndex == 3 && shape.ThicknessDips == 6, "line or metadata");
            check(source.Dots.Count == 30 && shape!.Dots[0].X == source.Dots[0].X && shape.Dots[^1].Y == source.Dots[^1].Y, "original changed");
        });
        test("smart circle respects clockwise and counterclockwise writing", () =>
        {
            foreach (int direction in new[] { -1, 1 })
            {
                var shape = SmartShapeRecognizer.Recognize(Stroke(Enumerable.Range(0, 81).Select(i => new Vector2(500 + 100 * MathF.Cos(direction * i * MathF.Tau / 80), 400 + 100 * MathF.Sin(direction * i * MathF.Tau / 80)))), view);
                check(shape is { Dots.Count: 65 }, "circle not recognized");
                check(direction * (view.ToDip(shape!.Dots[1]).Y - view.ToDip(shape.Dots[0]).Y) > 0, "direction changed");
            }
        });
        test("smart rectangle closes four corners", () =>
        {
            var corners = new[] { new Vector2(200, 200), new Vector2(500, 200), new Vector2(500, 400), new Vector2(200, 400), new Vector2(200, 200) };
            var points = Enumerable.Range(0, 4).SelectMany(edge => Enumerable.Range(0, 20).Select(i => Vector2.Lerp(corners[edge], corners[edge + 1], i / 20f))).Append(corners[0]);
            var result = SmartShapeRecognizer.Recognize(Stroke(points), view);
            check(result is { Dots.Count: 5 } && Vector2.Distance(view.ToDip(result.Dots[0]), view.ToDip(result.Dots[^1])) < 0.01, "rectangle failed");
        });
        test("smart shapes reject short line, ellipse, open arc and invalid input", () =>
        {
            var shortLine = Stroke(Enumerable.Range(0, 10).Select(i => new Vector2(100 + i, 100)));
            var ellipse = Stroke(Enumerable.Range(0, 81).Select(i => new Vector2(500 + 180 * MathF.Cos(i * MathF.Tau / 80), 400 + 60 * MathF.Sin(i * MathF.Tau / 80))));
            var arc = Stroke(Enumerable.Range(0, 61).Select(i => new Vector2(500 + 100 * MathF.Cos(i * MathF.Tau / 80), 400 + 100 * MathF.Sin(i * MathF.Tau / 80))));
            var invalid = Stroke(Enumerable.Repeat(new Vector2(100, 100), 10)); invalid.Dots[2].X = double.NaN;
            foreach (var source in new[] { shortLine, ellipse, arc, invalid }) check(SmartShapeRecognizer.Recognize(source, view) is null, "unreliable shape accepted");
        });
        test("smart shapes recognize the actual smoothed input preview", () =>
        {
            var corners = new[] { new Vector2(200, 200), new Vector2(500, 200), new Vector2(500, 400), new Vector2(200, 400), new Vector2(200, 200) };
            var rectangle = Enumerable.Range(0, 4).SelectMany(edge => Enumerable.Range(0, 20).Select(i => Vector2.Lerp(corners[edge], corners[edge + 1], i / 20f))).Append(corners[0]);
            var circle = Enumerable.Range(0, 81).Select(i => new Vector2(500 + 100 * MathF.Cos(i * MathF.Tau / 80), 400 + 100 * MathF.Sin(i * MathF.Tau / 80)));
            foreach (var points in new[] { rectangle, circle })
            {
                var samples = points.Select((p, i) => new InkSample(p, (ulong)i + 1, InkDevice.Touch)).ToArray();
                var session = new InkInputSession { Viewport = view }; session.Begin(1, samples[0], new InkToolSnapshot(InkTool.Pen, 1, new byte[] { 255, 0, 0, 255 }, 4));
                session.Move(1, samples.Skip(1).ToArray());
                check(SmartShapeRecognizer.Recognize(session.Preview!, view) != null, "smoothed preview rejected");
            }
        });
        test("ClassWidgets fresh lesson and break are healthy, stale and invalid data are not", () =>
        {
            string file = Path.Combine(Path.GetTempPath(), "rimeppt-connection-test-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                check(!ClassWidgetsCourseReader.ReadStatus(file).Connected, "missing accepted");
                void Write(string kind, bool active, DateTimeOffset updated, int version = 1) => File.WriteAllText(file, JsonSerializer.Serialize(new { version, active, entryType = kind, subjectName = kind == "class" ? "数学" : null, updatedAtUtc = updated }));
                Write("class", true, DateTimeOffset.UtcNow); var status = ClassWidgetsCourseReader.ReadStatus(file);
                check(status.Connected && status.Course == "数学", "fresh lesson failed");
                Write("break", true, DateTimeOffset.UtcNow); check(ClassWidgetsCourseReader.ReadStatus(file).Connected, "break incorrectly failed");
                Write("class", true, DateTimeOffset.UtcNow.AddMinutes(-2)); check(!ClassWidgetsCourseReader.ReadStatus(file).Connected, "stale accepted");
                Write("class", false, DateTimeOffset.UtcNow); check(!ClassWidgetsCourseReader.ReadStatus(file).Connected, "inactive accepted");
                Write("class", true, DateTimeOffset.UtcNow, 2); check(!ClassWidgetsCourseReader.ReadStatus(file).Connected, "bad version accepted");
                File.WriteAllText(file, "{broken"); check(!ClassWidgetsCourseReader.ReadStatus(file).Connected, "bad JSON accepted");
                File.WriteAllText(file, "{}"); check(!ClassWidgetsCourseReader.ReadStatus(file).Connected, "missing fields accepted");
            }
            finally { if (File.Exists(file)) File.Delete(file); }
        });
        test("board PDF cross references and page order are valid, refuses overwrite", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "rimeppt-pdf-test-" + Guid.NewGuid().ToString("N"));
            string jpeg = root + ".jpg", pdf = root + ".pdf";
            try
            {
                File.WriteAllBytes(jpeg, new byte[] { 255, 216, 255, 217 }); // Stream structure test; rendering is checked separately with exported JPEGs.
                BoardPdfWriter.Write(pdf, new[] { jpeg, jpeg }, 1920, 1080);
                var bytes = File.ReadAllBytes(pdf); string data = Encoding.ASCII.GetString(bytes);
                check(data.Contains("/Count 2 /Kids [3 0 R 6 0 R") && data.Contains("/MediaBox [0 0 720 405]"), "pages/aspect");
                int start = data.IndexOf("xref\n"); check(start > 0 && data.EndsWith($"startxref\n{start}\n%%EOF\n"), "startxref");
                var lines = data[start..].Split('\n'); int count = int.Parse(lines[1].Split(' ')[1]);
                for (int i = 1; i < count; i++) { int offset = int.Parse(lines[2 + i][..10]); check(data[offset..].StartsWith($"{i} 0 obj\n"), "object offset"); }
                bool refused = false; try { BoardPdfWriter.Write(pdf, new[] { jpeg }, 1920, 1080); } catch (IOException) { refused = true; }
                check(refused, "overwrote existing PDF");
            }
            finally { if (File.Exists(jpeg)) File.Delete(jpeg); if (File.Exists(pdf)) File.Delete(pdf); }
        });
    }
}
