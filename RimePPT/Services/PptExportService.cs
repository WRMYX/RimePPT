using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace RimePPT.Services
{
    /// <summary>
    /// 使用 PowerPoint COM 自动化将 .pptx 各页导出为 PNG 图片
    /// </summary>
    public class PptExportService
    {
        private string? _tempFolder;

        /// <summary>
        /// 导出 PPTX 全部页面为 PNG，返回图片路径列表（按页序）
        /// </summary>
        public async Task<List<string>> ExportSlidesAsync(string pptxPath)
        {
            return await Task.Run(() => ExportSlides(pptxPath));
        }

        private List<string> ExportSlides(string pptxPath)
        {
            _tempFolder = Path.Combine(Path.GetTempPath(), "RimePPT", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempFolder);

            dynamic? pptApp = null;
            dynamic? presentation = null;

            try
            {
                var pptType = Type.GetTypeFromProgID("PowerPoint.Application")
                    ?? throw new InvalidOperationException("未检测到 Microsoft PowerPoint，请确保已安装 Office。");

                pptApp = Activator.CreateInstance(pptType)
                    ?? throw new InvalidOperationException("无法启动 PowerPoint 进程。");

                pptApp.Visible = false;

                presentation = pptApp.Presentations.Open(
                    pptxPath,
                    ReadOnly: true,
                    Untitled: false,
                    WithWindow: false);

                int slideCount = presentation.Slides.Count;
                var paths = new List<string>(slideCount);

                for (int i = 1; i <= slideCount; i++)
                {
                    string outputPath = Path.Combine(_tempFolder, $"slide_{i:D4}.png");
                    // 导出为 PNG，宽高 1920×1080
                    presentation.Slides[i].Export(outputPath, "PNG", 1920, 1080);
                    paths.Add(outputPath);
                }

                return paths;
            }
            finally
            {
                try { presentation?.Close(); } catch { }
                try { pptApp?.Quit(); } catch { }
                if (presentation != null) Marshal.ReleaseComObject(presentation);
                if (pptApp != null) Marshal.ReleaseComObject(pptApp);
            }
        }

        /// <summary>
        /// 清理临时文件夹（程序退出时调用）
        /// </summary>
        public void Cleanup()
        {
            if (_tempFolder != null && Directory.Exists(_tempFolder))
            {
                try { Directory.Delete(_tempFolder, recursive: true); } catch { }
            }
        }
    }
}
