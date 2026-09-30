using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using RimePPT.Core.Ink;

namespace RimePPT.Core
{
    /// <summary>
    /// 侧车墨迹库：每份课件一个 JSON 文件，存于 %LOCALAPPDATA%\RimePPT\Ink\，
    /// 文件名 = 课件内容 SHA-256。查找先按内容哈希，未命中再按保存时的路径兜底。
    /// </summary>
    public static class InkStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
        };

        private static string Directory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RimePPT", "Ink");

        public static async Task<InkDocument?> TryLoadAsync(string pptxPath)
        {
            try
            {
                var hash = await HashFileAsync(pptxPath);

                // 主键：内容哈希
                var primary = Path.Combine(Directory, hash + ".ink.json");
                var doc = await TryReadAsync(primary);
                if (doc is not null)
                {
                    return doc;
                }

                // 兜底：保存时的路径匹配（课件被移动/改名，内容未变的情况由主键覆盖；
                // 这里处理的是"内容变了但路径相同"之外的少见路径复用场景）
                if (System.IO.Directory.Exists(Directory))
                {
                    foreach (var file in System.IO.Directory.GetFiles(Directory, "*.ink.json"))
                    {
                        doc = await TryReadAsync(file);
                        if (doc is not null &&
                            string.Equals(doc.PathHint, pptxPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return doc;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"tryload failed: {ex.Message}");
            }

            return null;
        }

        public static async Task SaveAsync(InkDocument document)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path.Combine(Directory, document.ContentHash + ".ink.json");

                // 先写临时文件再替换，避免写一半崩溃留下损坏文件
                var temp = path + ".tmp";
                await using (var stream = File.Create(temp))
                {
                    await JsonSerializer.SerializeAsync(stream, document, JsonOptions);
                }
                File.Move(temp, path, overwrite: true);
                Log($"saved: {path} slides={document.Slides.Count}");
            }
            catch (Exception ex)
            {
                Log($"save failed: {ex.Message}");
            }
        }

        /// <summary>删除某课件的墨迹档案（按内容哈希）。</summary>
        public static Task DeleteAsync(string contentHash)
        {
            try
            {
                var path = Path.Combine(Directory, contentHash + ".ink.json");
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Log($"deleted: {path}");
                }
            }
            catch (Exception ex)
            {
                Log($"delete failed: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        public static async Task<string> HashFileAsync(string path)
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static async Task<InkDocument?> TryReadAsync(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                await using var stream = File.OpenRead(path);
                return await JsonSerializer.DeserializeAsync<InkDocument>(stream, JsonOptions);
            }
            catch (Exception ex)
            {
                // 损坏文件改名隔离，不阻断查找
                Log($"corrupt, quarantining: {path} ({ex.Message})");
                try
                {
                    File.Move(path, path + ".corrupt", overwrite: true);
                }
                catch
                {
                }
                return null;
            }
        }

        private static void Log(string message)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "rimeppt_app.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} [ink] {message}\r\n");
            }
            catch
            {
            }
        }
    }
}
