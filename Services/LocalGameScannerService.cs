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
        "artbook", "sdk", "toolkit", "benchmark", "playtest", "directx",
        "redistributable", "microsoft", "windows", "runtime", "package", "driver",
        "controller config", "steam controller", "visual c++", "framework", ".net",
        "google", "nvidia", "realtek", "intel", "python", "node.js", "git", "7-zip",
        "everything", "wps office", "telegram", "antigravity", "traework", "mumu",
        "purpl", "wegame", "ubisoft connect", "steam", "riot vanguard", "anticheat"
    };

    public Task<List<DetectedGame>> ScanAllGamesAsync()
    {
        return Task.Run(() =>
        {
            var result = new Dictionary<string, DetectedGame>(StringComparer.OrdinalIgnoreCase);

            try { ScanSteam(result); } catch { }
            try { ScanEpic(result); } catch { }
            try { ScanGOG(result); } catch { }
            try { ScanRegistry(result); } catch { }
            try { ScanCommonFolders(result); } catch { }

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
        var lower = name.ToLowerInvariant();
        return IgnoredKeywords.Any(k => lower.Contains(k));
    }

    private static string CleanGameName(string name)
    {
        name = name.Trim();
        // 去除常见的结尾杂质，如 (Playtest), Soundtrack, Dedicated Server
        name = Regex.Replace(name, @"\s*(\((Playtest|Demo|Beta|Server)\)|Soundtrack|Dedicated Server|Original Soundtrack|Artbook)$", "", RegexOptions.IgnoreCase).Trim();
        return name;
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
                            if (nameMatch.Success)
                            {
                                var gName = nameMatch.Groups[1].Value;
                                var gDir = dirMatch.Success ? Path.Combine(steamApps, "common", dirMatch.Groups[1].Value) : "";
                                AddGame(result, gName, gDir, "Steam");
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // 1.2 读取 common 目录下的各个游戏文件夹（补充扫描手动拷贝或识别不全的）
                var commonDir = Path.Combine(steamApps, "common");
                if (Directory.Exists(commonDir))
                {
                    try
                    {
                        foreach (var sub in Directory.GetDirectories(commonDir))
                        {
                            var folderName = Path.GetFileName(sub);
                            AddGame(result, folderName, sub, "Steam");
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
                    if (!string.IsNullOrEmpty(name))
                    {
                        AddGame(result, name, path ?? "", "Epic Games");
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
                    if (!string.IsNullOrEmpty(name))
                    {
                        AddGame(result, name, path ?? "", "GOG");
                    }
                }
            }
            catch { }
        }
    }

    private void ScanRegistry(Dictionary<string, DetectedGame> result)
    {
        var uninstallKeys = new[]
        {
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
        };

        foreach (var (root, path) in uninstallKeys)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key == null) continue;

                foreach (var subName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(subName);
                        if (sub == null) continue;

                        var sysComp = sub.GetValue("SystemComponent");
                        if (sysComp is int sysInt && sysInt == 1) continue;

                        var name = sub.GetValue("DisplayName")?.ToString();
                        var installLoc = sub.GetValue("InstallLocation")?.ToString();

                        if (!string.IsNullOrEmpty(name))
                        {
                            // 仅当安装目录中存在可执行文件且非系统组件时
                            if (!string.IsNullOrEmpty(installLoc) && Directory.Exists(installLoc))
                            {
                                AddGame(result, name, installLoc, "Windows");
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private void ScanCommonFolders(Dictionary<string, DetectedGame> result)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;

            var candidates = new[]
            {
                Path.Combine(drive.RootDirectory.FullName, "Games"),
                Path.Combine(drive.RootDirectory.FullName, "Game"),
                Path.Combine(drive.RootDirectory.FullName, "单机游戏"),
                Path.Combine(drive.RootDirectory.FullName, "SteamLibrary", "steamapps", "common")
            };

            foreach (var cand in candidates)
            {
                if (Directory.Exists(cand))
                {
                    try
                    {
                        foreach (var sub in Directory.GetDirectories(cand))
                        {
                            var folderName = Path.GetFileName(sub);
                            // 确认目录下包含可执行文件
                            if (Directory.GetFiles(sub, "*.exe", SearchOption.TopDirectoryOnly).Any() ||
                                Directory.GetFiles(sub, "*.exe", SearchOption.AllDirectories).Take(1).Any())
                            {
                                AddGame(result, folderName, sub, "本地目录");
                            }
                        }
                    }
                    catch { }
                }
            }
        }
    }
}
