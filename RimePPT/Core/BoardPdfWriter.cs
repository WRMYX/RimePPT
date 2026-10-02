using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RimePPT.Core;

/// <summary>生成只包含课堂页面图像的 PDF，不依赖外部服务或打印机。</summary>
public static class BoardPdfWriter
{
    public static void Write(string destination, IReadOnlyList<string> jpegPaths, int width, int height)
    {
        if (jpegPaths.Count == 0 || width <= 0 || height <= 0) throw new ArgumentException("没有可导出的页面。");
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
        var offsets = new List<long> { 0 };
        void Text(string value) { var bytes = Encoding.ASCII.GetBytes(value); output.Write(bytes); }
        void Begin(int id) { offsets.Add(output.Position); Text($"{id} 0 obj\n"); }
        Text("%PDF-1.4\n");
        Begin(1); Text("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Begin(2); Text($"<< /Type /Pages /Count {jpegPaths.Count} /Kids [");
        for (int i = 0; i < jpegPaths.Count; i++) Text($"{3 + i * 3} 0 R ");
        Text("] >>\nendobj\n");
        double pageHeight = 720d * height / width;
        string h = pageHeight.ToString("0.###", CultureInfo.InvariantCulture);
        for (int i = 0; i < jpegPaths.Count; i++)
        {
            int page = 3 + i * 3, image = page + 1, content = page + 2;
            Begin(page); Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 720 {h}] /Resources << /XObject << /Image {image} 0 R >> >> /Contents {content} 0 R >>\nendobj\n");
            var jpeg = File.ReadAllBytes(jpegPaths[i]);
            Begin(image); Text($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
            output.Write(jpeg); Text("\nendstream\nendobj\n");
            string commands = $"q\n720 0 0 {h} 0 0 cm\n/Image Do\nQ\n";
            Begin(content); Text($"<< /Length {Encoding.ASCII.GetByteCount(commands)} >>\nstream\n{commands}endstream\nendobj\n");
        }
        long crossReference = output.Position;
        Text($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        for (int i = 1; i < offsets.Count; i++) Text(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{crossReference}\n%%EOF\n");
    }
}
