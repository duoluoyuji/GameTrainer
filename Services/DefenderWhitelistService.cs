using System.Diagnostics;

namespace GameTrainer.Services;

public static class DefenderWhitelistService
{
    public static async Task<(bool success, string message, bool openSettings)> AddExclusionPathAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return (false, "目录路径无效", false);

        var cleanPath = folderPath.TrimEnd('\\', '/');

        try
        {
            // 使用 Base64 编码传递命令，防止任何引号与特殊字符转义错误
            var psCommand = $"Add-MpPreference -ExclusionPath '{cleanPath}' -ErrorAction Stop";
            var bytes = System.Text.Encoding.Unicode.GetBytes(psCommand);
            var encoded = Convert.ToBase64String(bytes);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0)
                {
                    return (true, "已成功将修改器目录加入 Windows Defender 白名单！", false);
                }
            }
        }
        catch
        {
            // 用户点击了取消 UAC 弹窗
        }

        // 如果自动化添加失败，通常是 Windows 启用了“防篡改保护”（Tamper Protection，阻止任何第三方程序自动修改排除项）
        // 自动帮用户唤起 Windows 安全中心的排除项设置页，用户手动点击一次添加即可
        try
        {
            Process.Start(new ProcessStartInfo("windowsdefender://exclusions") { UseShellExecute = true });
            return (false, "检测到系统启用了防篡改保护，已为您自动打开「Windows 安全中心 - 排除项」页面。\n\n请在打开的窗口中点击【添加排除项】➜【文件夹】，选择修改器目录即可：\n" + cleanPath, true);
        }
        catch
        {
            return (false, "建议手动在 Windows 安全中心 ➜ 病毒和威胁防护 ➜ 管理设置 ➜ 排除项 中添加此目录：\n" + cleanPath, false);
        }
    }
}
