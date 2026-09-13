using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using GameTrainer.Models;

namespace GameTrainer.Services;

public class TrainerDownloadService
{
    private readonly IHttpClientProvider _httpProvider;

    public TrainerDownloadService(IHttpClientProvider httpProvider)
    {
        _httpProvider = httpProvider;
    }

    public async Task<string> DownloadAsync(TrainerItem item, string targetFolder, Action<double>? progressCallback = null)
    {
        if (string.IsNullOrWhiteSpace(item.DownloadUrl))
            throw new InvalidOperationException("下载地址为空");

        var cleanGameName = SanitizeFileName(string.IsNullOrWhiteSpace(item.GameName) ? item.OriginalName : item.GameName);
        var dir = Path.Combine(targetFolder, cleanGameName);
        Directory.CreateDirectory(dir);

        var client = _httpProvider.GetClient("trainer-dl", TimeSpan.FromMinutes(20));

        var req = new HttpRequestMessage(HttpMethod.Get, item.DownloadUrl);
        if (!string.IsNullOrWhiteSpace(item.PageUrl))
        {
            req.Headers.Referrer = new Uri(item.PageUrl);
        }

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        // 优先从响应头中提取文件名
        var headerFileName = resp.Content.Headers.ContentDisposition?.FileNameStar
                             ?? resp.Content.Headers.ContentDisposition?.FileName;

        string fileName;
        if (!string.IsNullOrWhiteSpace(headerFileName))
        {
            fileName = SanitizeFileName(headerFileName.Trim('"', '\'', ' '));
        }
        else
        {
            fileName = $"{cleanGameName}.zip";
        }

        var tempDest = Path.Combine(dir, fileName);

        var total = resp.Content.Headers.ContentLength ?? -1;
        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(tempDest))
        {
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n));
                read += n;
                if (total > 0 && progressCallback != null)
                {
                    progressCallback(read * 100.0 / total);
                }
            }
        }

        // 根据文件二进制头（Magic Number）智能识别类型并重命名/解压
        var finalExePath = ProcessDownloadedFile(tempDest, dir, cleanGameName);

        // 写入元数据
        try
        {
            var metaPath = Path.Combine(dir, "trainer_meta.json");
            var meta = new
            {
                game_name = item.GameName,
                original_name = item.OriginalName,
                page_url = item.PageUrl,
                exe_path = finalExePath,
                download_time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }

        item.LocalPath = finalExePath;
        item.IsDownloaded = true;
        return finalExePath;
    }

    /// <summary>根据二进制文件头（Magic Number）识别文件真实类型并处理</summary>
    public static string ProcessDownloadedFile(string filePath, string targetDir, string defaultName)
    {
        if (!File.Exists(filePath)) return filePath;

        byte[] header = new byte[4];
        using (var fs = File.OpenRead(filePath))
        {
            fs.Read(header, 0, header.Length);
        }

        bool isZip = header.Length >= 4 && header[0] == 0x50 && header[1] == 0x4B; // PK..
        bool isExe = header.Length >= 2 && header[0] == 0x4D && header[1] == 0x5A; // MZ..

        if (isZip)
        {
            var zipPath = filePath;
            if (!zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                zipPath = Path.Combine(targetDir, $"{defaultName}.zip");
                try { File.Move(filePath, zipPath, true); } catch { }
            }

            try
            {
                ZipFile.ExtractToDirectory(zipPath, targetDir, true);
                var exe = Directory.GetFiles(targetDir, "*.exe", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).Contains("setup", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => Path.GetFileName(f).Contains("trainer", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(f => Path.GetFileName(f).StartsWith("start_protected", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Contains("bypass", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (exe != null)
                {
                    try { File.Delete(zipPath); } catch { }
                    return exe;
                }
            }
            catch { }

            return zipPath;
        }

        if (isExe)
        {
            var exePath = filePath;
            if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                exePath = Path.Combine(targetDir, $"{defaultName} Trainer.exe");
                try { File.Move(filePath, exePath, true); } catch { }
            }
            return exePath;
        }

        return filePath;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat((name ?? "Trainer").Select(c => invalid.Contains(c) ? '_' : c)).Trim();
    }
}
