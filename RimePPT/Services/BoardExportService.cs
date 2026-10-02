using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using RimePPT.Core;
using RimePPT.Core.Ink;
using RimePPT.Windows;
using Windows.Foundation;

namespace RimePPT.Services;

internal static class BoardExportService
{
    internal static async Task<string> ExportAsync(IPresentationController controller, Dictionary<int, List<StrokeData>> snapshot, InkViewport viewport, string parent)
    {
        if (!viewport.IsValid) viewport = new(1920, 1080);
        int width = 1920, height = Math.Clamp((int)Math.Round(width * viewport.HeightDip / viewport.WidthDip), 240, 4096);
        string folder = Path.Combine(parent, "RimePPT-板书-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        var temporary = new List<string>(); var pages = new List<string>();
        try
        {
            var device = CanvasDevice.GetSharedDevice();
            foreach (var entry in snapshot.OrderBy(p => p.Key))
            {
                if (entry.Value.Count == 0) continue;
                string backgroundPath = Path.Combine(folder, $".background-{entry.Key}.png");
                temporary.Add(backgroundPath);
                CanvasBitmap? background = null;
                try
                {
                    if (controller is not DebugPresentationController)
                    {
                        await controller.ExportSlideThumbnailAsync(entry.Key, backgroundPath, width, CancellationToken.None);
                        background = await CanvasBitmap.LoadAsync(device, backgroundPath);
                    }
                    var strokes = entry.Value.Select(s => new StrokeData { Id = s.Id, SlideIndex = s.SlideIndex, Argb = s.Argb, Dots = s.Dots, ThicknessDips = s.ThicknessDips * width / viewport.WidthDip }).ToArray();
                    using var target = new CanvasRenderTarget(device, width, height, 96);
                    using var renderer = new InkRenderer(device, () => strokes, () => Array.Empty<StrokeData>());
                    using (var draw = target.CreateDrawingSession())
                    {
                        draw.Clear(background is null ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
                        if (background is not null)
                        {
                            float scale = Math.Min(width / (float)background.Size.Width, height / (float)background.Size.Height);
                            var size = new Vector2((float)background.Size.Width, (float)background.Size.Height) * scale;
                            draw.DrawImage(background, new Rect((width - size.X) / 2, (height - size.Y) / 2, size.X, size.Y));
                        }
                        renderer.Draw(draw, new(width, height));
                    }
                    await target.SaveAsync(Path.Combine(folder, $"第{entry.Key:D3}页.png"), CanvasBitmapFileFormat.Png);
                    string jpeg = Path.Combine(folder, $".page-{entry.Key}.jpg"); temporary.Add(jpeg);
                    await target.SaveAsync(jpeg, CanvasBitmapFileFormat.Jpeg, 0.95f); pages.Add(jpeg);
                }
                finally { background?.Dispose(); }
            }
            if (pages.Count == 0) throw new InvalidOperationException("当前没有可导出的板书。");
            await Task.Run(() => BoardPdfWriter.Write(Path.Combine(folder, "课堂板书.pdf"), pages, width, height));
            return folder;
        }
        finally
        {
            foreach (string file in temporary)
                try { if (File.Exists(file)) File.Delete(file); }
                catch (IOException ex) { CrashReporter.Report(ex, "board-export-temp"); }
                catch (UnauthorizedAccessException ex) { CrashReporter.Report(ex, "board-export-temp"); }
        }
    }
}
