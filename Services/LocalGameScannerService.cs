using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GameTrainer.Models;
using Microsoft.Win32;

namespace GameTrainer.Services;

public class LocalGameScannerService
{
    private static readonly string[] IgnoredKeywords = new[]
    {
        "steamworks", "common redist", "wallpaper engine", "dedicated server", "soundtrack",
        "artbook", "sdk", "toolkit", "benchmark", "playtest", "directx", "demo",
        "redistributable", "microsoft", "windows", "runtime", "package", "driver",
        "controller config", "steam controller", "visual c++", "framework", ".net",
        "google", "nvidia", "realtek", "intel", "python", "node.js", "git", "7-zip",
        "everything", "wps office", "telegram", "antigravity", "traework", "mumu",
        "purpl", "wegame", "ubisoft connect", "steam", "riot vanguard", "anticheat",
        "360", "baidu", "doubao", "nutstore", "gamepp", "netdisk", "accelerator",
        "quark", "迅雷", "网盘", "输入法", "安全卫士", "杀毒", "管家", "控制台"
    };

    public Task<List<DetectedGame>> ScanAllGamesAsync()
    {
        return Task.Run(() =>
        {
            var result = new Dictionary<string, DetectedGame>(StringComparer.OrdinalIgnoreCase);

            try { ScanSteam(result); } catch { }
            try { ScanEpic(result); } catch { }
            try { ScanGOG(result); } catch { }
            try { ScanUbisoft(result); } catch { }
            try { ScanCommonGameFolders(result); } catch { }

            return result.Values.OrderBy(x => x.Name).ToList();
        });
    }

    private void AddGame(Dictionary<string, DetectedGame> dict, string name, string path, string source)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var cleanName = CleanGameName(name);
        if (string.IsNullOrWhiteSpace(cleanName)) return;

        if (IsIgnored(cleanName)) return;

        var key = cleanName.ToLowerInvariant();
        if (!dict.ContainsKey(key))
        {
            dict[key] = new DetectedGame
            {
                Name = cleanName,
                InstallPath = path ?? string.Empty,
                Source = source
            };
        }
    }

    private static bool IsIgnored(string name)
    {
        var lower = name.ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
        return IgnoredKeywords.Any(k => lower.Contains(k));
    }

    private static string CleanGameName(string name)
    {
        name = name.Trim();
        // 去除常见的结尾杂质，如 (Playtest), Soundtrack, Dedicated Server
        name = Regex.Replace(name, @"\s*(\((Playtest|Demo|Beta|Server)\)|Soundtrack|Dedicated Server|Original Soundtrack|Artbook)$", "", RegexOptions.IgnoreCase).Trim();
        return name;
    }

    /// <summary>
    /// 严格校验目录是否为真实有效的单机游戏安装目录（必须存在可执行程序，且排除空目录、卸载器、崩溃收集器等杂质）
    /// </summary>
    public static bool IsValidGameDirectory(string? dirPath, out string? mainExePath)
    {
        mainExePath = null;
        if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath))
            return false;

        try
        {
            var dir = new DirectoryInfo(dirPath);
            // 目录如果没有任何文件和子目录，直接判定为假（彻底解决像 StellarBlade 这类 0 字节空目录误报问题）
            if (!dir.EnumerateFileSystemInfos().Any())
                return false;

            var exeFiles = new List<FileInfo>();
            try
            {
                exeFiles.AddRange(dir.EnumerateFiles("*.exe", SearchOption.TopDirectoryOnly));
                foreach (var sub in dir.EnumerateDirectories().Take(8))
                {
                    try
                    {
                        exeFiles.AddRange(sub.EnumerateFiles("*.exe", SearchOption.TopDirectoryOnly));
                        if (sub.Name.Equals("binaries", StringComparison.OrdinalIgnoreCase) ||
                            sub.Name.Equals("game", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var sub2 in sub.EnumerateDirectories().Take(5))
                            {
                                exeFiles.AddRange(sub2.EnumerateFiles("*.exe", SearchOption.TopDirectoryOnly));
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            if (exeFiles.Count == 0)
                return false;

            // 排除卸载器、崩溃报告、安装程序、反作弊等工具 exe
            var gameExes = exeFiles.Where(f =>
            {
                var name = f.Name.ToLowerInvariant();
                if (f.Length < 2 * 1024 * 1024)
                    return false;

                if (name.StartsWith("unins") || name.Contains("crash") || name.Contains("setup") ||
                    name.Contains("redist") || name.Contains("vcredist") || name.Contains("dxsetup") ||
                    name.Contains("report") || name.Contains("helper") || name.Contains("patch") ||
                    name.Contains("anticheat") || name.Contains("battleye") || name.Contains("easyanticheat") ||
                    name.Contains("install") || name.Contains("update"))
                    return false;

                return true;
            }).OrderByDescending(f => f.Length).ToList();

            if (gameExes.Count > 0)
            {
                mainExePath = gameExes[0].FullName;
                return true;
            }
        }
        catch { }

        return false;
    }

    private void ScanSteam(Dictionary<string, DetectedGame> result)
    {
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 读取注册表中的 Steam 路径
        try
        {
            var p1 = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath")?.ToString();
            if (!string.IsNullOrEmpty(p1) && Directory.Exists(p1)) steamRoots.Add(p1);

            var p2 = Registry.LocalMachine.OpenSubKey(@"Software\Valve\Steam")?.GetValue("InstallPath")?.ToString();
            if (!string.IsNullOrEmpty(p2) && Directory.Exists(p2)) steamRoots.Add(p2);

            var p3 = Registry.LocalMachine.OpenSubKey(@"Software\Wow6432Node\Valve\Steam")?.GetValue("InstallPath")?.ToString();
            if (!string.IsNullOrEmpty(p3) && Directory.Exists(p3)) steamRoots.Add(p3);
        }
        catch { }

        // 常见 Steam 默认路径补充
        var defaults = new[] { @"C:\Program Files (x86)\Steam", @"C:\Steam", @"D:\Steam", @"E:\Steam", @"F:\Steam" };
        foreach (var d in defaults)
        {
            if (Directory.Exists(d)) steamRoots.Add(d);
        }

        foreach (var root in steamRoots)
        {
            var libVdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            var libraryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };

            if (File.Exists(libVdf))
            {
                try
                {
                    var text = File.ReadAllText(libVdf);
                    var matches = Regex.Matches(text, @"""path""\s+""([^""]+)""");
                    foreach (Match m in matches)
                    {
                        var lib = m.Groups[1].Value.Replace(@"\\", @"\");
                        if (Directory.Exists(lib))
                        {
                            libraryPaths.Add(lib);
                        }
                    }
                }
                catch { }
            }

            foreach (var lib in libraryPaths)
            {
                var steamApps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(steamApps)) continue;

                // 1.1 读取 appmanifest_*.acf
                try
                {
                    foreach (var acf in Directory.GetFiles(steamApps, "appmanifest_*.acf"))
                    {
                        try
                        {
                            var acfContent = File.ReadAllText(acf);
                            var nameMatch = Regex.Match(acfContent, @"""name""\s+""([^""]+)""");
                            var dirMatch = Regex.Match(acfContent, @"""installdir""\s+""([^""]+)""");
                            var stateMatch = Regex.Match(acfContent, @"""StateFlags""\s+""([^""]+)""");

                            // 校验 StateFlags（4 表示完全下载安装完毕）
                            if (stateMatch.Success && stateMatch.Groups[1].Value != "4")
                                continue;

                            if (nameMatch.Success)
                            {
                                var gName = nameMatch.Groups[1].Value;
                                var gDir = dirMatch.Success ? Path.Combine(steamApps, "common", dirMatch.Groups[1].Value) : "";

                                // 严格校验该目录是否真实存在有效游戏可执行文件
                                if (IsValidGameDirectory(gDir, out var mainExe))
                                {
                                    AddGame(result, gName, gDir, "Steam");
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // 1.2 读取 common 目录下的各个游戏文件夹（补充扫描手动放置或未写清单的完整单机游戏）
                var commonDir = Path.Combine(steamApps, "common");
                if (Directory.Exists(commonDir))
                {
                    try
                    {
                        foreach (var sub in Directory.GetDirectories(commonDir))
                        {
                            // 空目录（如 0 字节残留文件夹）或无有效可执行文件的文件夹一律严加过滤，绝不采纳！
                            if (IsValidGameDirectory(sub, out var mainExe))
                            {
                                var folderName = Path.GetFileName(sub);
                                AddGame(result, folderName, sub, "Steam");
                            }
                        }
                    }
                    catch { }
                }
            }
        }
    }

    private void ScanEpic(Dictionary<string, DetectedGame> result)
    {
        var epicManifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(epicManifests)) return;

        foreach (var itemFile in Directory.GetFiles(epicManifests, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(itemFile));
                var root = doc.RootElement;
                if (root.TryGetProperty("DisplayName", out var dn) &&
                    root.TryGetProperty("InstallLocation", out var il))
                {
                    var name = dn.GetString();
                    var path = il.GetString();
                    if (!string.IsNullOrEmpty(name) && IsValidGameDirectory(path, out _))
                    {
                        AddGame(result, name, path!, "Epic Games");
                    }
                }
            }
            catch { }
        }
    }

    private void ScanGOG(Dictionary<string, DetectedGame> result)
    {
        var keys = new[] { @"Software\GOG.com\Games", @"Software\WOW6432Node\GOG.com\Games" };
        foreach (var k in keys)
        {
            try
            {
                using var sub = Registry.LocalMachine.OpenSubKey(k);
                if (sub == null) continue;
                foreach (var gameSubKeyName in sub.GetSubKeyNames())
                {
                    using var gKey = sub.OpenSubKey(gameSubKeyName);
                    var name = gKey?.GetValue("gameName")?.ToString();
                    var path = gKey?.GetValue("path")?.ToString();
                    if (!string.IsNullOrEmpty(name) && IsValidGameDirectory(path, out _))
                    {
                        AddGame(result, name, path!, "GOG");
                    }
                }
            }
            catch { }
        }
    }

    private void ScanUbisoft(Dictionary<string, DetectedGame> result)
    {
        var ubiKeys = new[] { @"Software\Ubisoft\Launcher\Installs", @"Software\WOW6432Node\Ubisoft\Launcher\Installs" };
        foreach (var k in ubiKeys)
        {
            try
            {
                using var sub = Registry.LocalMachine.OpenSubKey(k);
                if (sub == null) continue;
                foreach (var gameId in sub.GetSubKeyNames())
                {
                    using var gKey = sub.OpenSubKey(gameId);
                    var path = gKey?.GetValue("InstallDir")?.ToString();
                    if (!string.IsNullOrEmpty(path) && IsValidGameDirectory(path, out _))
                    {
                        var folderName = Path.GetFileName(path.TrimEnd('\\', '/'));
                        AddGame(result, folderName, path, "Ubisoft");
                    }
                }
            }
            catch { }
        }
    }

    private void ScanCommonGameFolders(Dictionary<string, DetectedGame> result)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;

            var candidates = new[]
            {
                Path.Combine(drive.RootDirectory.FullName, "Games"),
                Path.Combine(drive.RootDirectory.FullName, "Game"),
                Path.Combine(drive.RootDirectory.FullName, "单机游戏")
            };

            foreach (var cand in candidates)
            {
                if (Directory.Exists(cand))
                {
                    try
                    {
                        foreach (var sub in Directory.GetDirectories(cand))
                        {
                            // 严密校验是否包含真实单机游戏可执行程序
                            if (IsValidGameDirectory(sub, out _))
                            {
                                var folderName = Path.GetFileName(sub);
                                AddGame(result, folderName, sub, "本地磁盘");
                            }
                        }
                    }
                    catch { }
                }
            }
        }
    }
}
