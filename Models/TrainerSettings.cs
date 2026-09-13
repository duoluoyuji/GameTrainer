using System.IO;

namespace GameTrainer.Models;

public class TrainerSettings
{
    public string DownloadDirectory { get; set; } = string.Empty;
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoCheckUpdate { get; set; } = true;
    public bool AutoLaunchWithGame { get; set; } = true;
    public string Theme { get; set; } = "Dark";
    public bool IsListView { get; set; } = false;

    public string GetEffectiveDownloadDir()
    {
        if (!string.IsNullOrWhiteSpace(DownloadDirectory) && Directory.Exists(DownloadDirectory))
            return DownloadDirectory;

        var defaultDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "修改器");
        Directory.CreateDirectory(defaultDir);
        return defaultDir;
    }
}
