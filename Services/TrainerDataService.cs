using System.IO;
using System.Text.Json;
using GameTrainer.Models;

namespace GameTrainer.Services;

public class TrainerDataService
{
    private readonly FlingScraperService _scraper;
    private readonly List<TrainerItem> _allTrainers = new();
    private readonly object _lock = new();

    private TrainerSettings _settings = new();
    public TrainerSettings Settings => _settings;

    public TrainerDataService(FlingScraperService scraper)
    {
        _scraper = scraper;
        LoadSettings();
    }

    public void LoadSettings()
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                _settings = JsonSerializer.Deserialize<TrainerSettings>(json) ?? new TrainerSettings();
            }
        }
        catch { }
    }

    public void SaveSettings()
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }

    /// <summary>初始加载：从本地内置数据库快速加载，0网络延迟秒开</summary>
    public List<TrainerItem> LoadBuiltinData()
    {
        lock (_lock)
        {
            _allTrainers.Clear();
            var jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "builtin_trainers.json");
            string? json = null;

            if (File.Exists(jsonPath))
            {
                try { json = File.ReadAllText(jsonPath); } catch { }
            }

            if (string.IsNullOrEmpty(json))
            {
                try
                {
                    using var stream = typeof(TrainerDataService).Assembly.GetManifestResourceStream("GameTrainer.Data.builtin_trainers.json");
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream);
                        json = reader.ReadToEnd();
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        var zh = elem.TryGetProperty("GameName", out var zn) ? zn.GetString() ?? "" : "";
                        var en = elem.TryGetProperty("OriginalName", out var on) ? on.GetString() ?? "" : "";
                        var page = elem.TryGetProperty("PageUrl", out var pu) ? pu.GetString() ?? "" : "";
                        var cover = elem.TryGetProperty("CoverUrl", out var cu) ? cu.GetString() ?? "" : "";
                        var isHot = elem.TryGetProperty("IsHot", out var ih) && ih.GetBoolean();
                        var aliases = new List<string>();
                        if (elem.TryGetProperty("Aliases", out var al) && al.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var a in al.EnumerateArray())
                            {
                                var str = a.GetString();
                                if (!string.IsNullOrWhiteSpace(str)) aliases.Add(str);
                            }
                        }

                        _allTrainers.Add(new TrainerItem
                        {
                            GameName = zh,
                            OriginalName = en,
                            PageUrl = page,
                            CoverUrl = cover,
                            IsHot = isHot,
                            Aliases = aliases
                        });
                    }
                }
                catch { }
            }

            SyncDownloadedState(_allTrainers);
            return _allTrainers.ToList();
        }
    }

    private List<TrainerItem>? _downloadedCache;
    private Dictionary<string, string>? _downloadMapCache;
    private readonly object _downloadCacheLock = new();

    /// <summary>使已下载缓存失效，当下完新修改器或删除修改器后调用</summary>
    public void InvalidateDownloadCache()
    {
        lock (_downloadCacheLock)
        {
            _downloadedCache = null;
            _downloadMapCache = null;
        }
    }

    /// <summary>扫描已下载目录并同步标记状态（带内存高速缓存，彻底消除搜索与输入时的扫盘卡顿）</summary>
    public void SyncDownloadedState(IEnumerable<TrainerItem> items)
    {
        Dictionary<string, string> downloadMap;
        lock (_downloadCacheLock)
        {
            if (_downloadMapCache == null)
            {
                GetDownloadedTrainers();
            }
            downloadMap = _downloadMapCache ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var item in items)
        {
            var keyZh = NormalizeName(item.GameName);
            var keyEn = NormalizeName(item.OriginalName);

            if (downloadMap.TryGetValue(keyZh, out var path) || downloadMap.TryGetValue(keyEn, out path))
            {
                item.IsDownloaded = true;
                item.LocalPath = path;
            }
            else
            {
                item.IsDownloaded = false;
                item.LocalPath = string.Empty;
            }
        }
    }

    /// <summary>获取所有本地已下载的修改器</summary>
    public List<TrainerItem> GetDownloadedTrainers(bool forceRefresh = false)
    {
        lock (_downloadCacheLock)
        {
            if (!forceRefresh && _downloadedCache != null)
            {
                return _downloadedCache.ToList();
            }

            var list = new List<TrainerItem>();
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rootDir = _settings.GetEffectiveDownloadDir();
            if (!Directory.Exists(rootDir))
            {
                _downloadedCache = list;
                _downloadMapCache = map;
                return list;
            }

            try
            {
                foreach (var subDir in Directory.GetDirectories(rootDir))
                {
                    var metaPath = Path.Combine(subDir, "trainer_meta.json");
                    string? gameName = null;
                    string? originalName = null;
                    string? pageUrl = null;
                    string? exePath = null;

                    if (File.Exists(metaPath))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                            if (doc.RootElement.TryGetProperty("game_name", out var gn)) gameName = gn.GetString();
                            if (doc.RootElement.TryGetProperty("original_name", out var on)) originalName = on.GetString();
                            if (doc.RootElement.TryGetProperty("page_url", out var pu)) pageUrl = pu.GetString();
                            if (doc.RootElement.TryGetProperty("exe_path", out var ep)) exePath = ep.GetString();
                        }
                        catch { }
                    }

                    // 验证 exePath 是否存在且确实是以 .exe 结尾的有效可执行文件
                    bool validExe = !string.IsNullOrEmpty(exePath) && File.Exists(exePath) && exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

                    if (!validExe)
                    {
                        // 1. 如果已记录路径存在但缺少 .exe 扩展名（如 Fling 下载未命名的哈希文件），先尝试直接修复它
                        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                        {
                            var healed = TrainerDownloadService.ProcessDownloadedFile(exePath, subDir, gameName ?? Path.GetFileName(subDir));
                            if (healed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(healed))
                            {
                                exePath = healed;
                                validExe = true;
                            }
                        }
                    }

                    if (!validExe)
                    {
                        // 2. 检查目录下是否已有解压出的 .exe
                        exePath = Directory.GetFiles(subDir, "*.exe", SearchOption.AllDirectories)
                            .Where(f => !Path.GetFileName(f).Contains("setup", StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(f => Path.GetFileName(f).Contains("trainer", StringComparison.OrdinalIgnoreCase))
                            .ThenBy(f => Path.GetFileName(f).StartsWith("start_protected", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Contains("bypass", StringComparison.OrdinalIgnoreCase))
                            .FirstOrDefault();
                        validExe = !string.IsNullOrEmpty(exePath) && File.Exists(exePath);
                    }

                    if (!validExe)
                    {
                        // 3. 扫描目录下的非 json/tmp 文件，通过二进制特征码（PE/Zip）自愈修复
                        var nonJsonFiles = Directory.GetFiles(subDir, "*", SearchOption.AllDirectories)
                            .Where(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        foreach (var rawFile in nonJsonFiles)
                        {
                            var healed = TrainerDownloadService.ProcessDownloadedFile(rawFile, subDir, gameName ?? Path.GetFileName(subDir));
                            if (healed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(healed))
                            {
                                exePath = healed;
                                validExe = true;
                                break;
                            }
                        }
                    }

                    if (validExe && !string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        // 更新元数据文件，确保以后都能秒级识别正确路径
                        try
                        {
                            var meta = new
                            {
                                game_name = gameName ?? Path.GetFileName(subDir),
                                original_name = originalName ?? Path.GetFileName(subDir),
                                page_url = pageUrl ?? string.Empty,
                                exe_path = exePath,
                                download_time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                            };
                            File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
                        }
                        catch { }

                        var item = new TrainerItem
                        {
                            GameName = gameName ?? Path.GetFileName(subDir),
                            OriginalName = originalName ?? Path.GetFileName(subDir),
                            PageUrl = pageUrl ?? string.Empty,
                            LocalPath = exePath,
                            IsDownloaded = true
                        };

                        // 从总库补齐封面海报与热门标识
                        var matched = _allTrainers.FirstOrDefault(t =>
                            (!string.IsNullOrEmpty(item.OriginalName) && t.OriginalName.Equals(item.OriginalName, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrEmpty(item.GameName) && t.GameName.Equals(item.GameName, StringComparison.OrdinalIgnoreCase)));
                        if (matched != null)
                        {
                            item.CoverUrl = matched.CoverUrl;
                            item.IsHot = matched.IsHot;
                        }

                        // 解析其快捷键
                        var (_, options) = TrainerExeParser.Parse(exePath);
                        if (options.Count > 0)
                        {
                            item.Options = new System.Collections.ObjectModel.ObservableCollection<CheatOption>(options);
                        }

                        list.Add(item);

                        var key1 = NormalizeName(item.GameName);
                        if (!string.IsNullOrEmpty(key1)) map[key1] = exePath;
                        var key2 = NormalizeName(item.OriginalName);
                        if (!string.IsNullOrEmpty(key2)) map[key2] = exePath;
                    }
                }
            }
            catch { }

            _downloadedCache = list;
            _downloadMapCache = map;
            return list;
        }
    }

    /// <summary>根据输入的中文或别名快速反查对应的游戏英文原名</summary>
    public string? ResolveEnglishName(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var rawQ = query.Trim();
        var qNorm = NormalizeName(rawQ);

        lock (_lock)
        {
            var match = _allTrainers.FirstOrDefault(t =>
                string.Equals(t.GameName, rawQ, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.OriginalName, rawQ, StringComparison.OrdinalIgnoreCase) ||
                t.Aliases.Any(a => string.Equals(a, rawQ, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(qNorm) && (
                    NormalizeName(t.GameName) == qNorm ||
                    NormalizeName(t.OriginalName) == qNorm ||
                    t.Aliases.Any(a => NormalizeName(a) == qNorm)
                )));

            return match?.OriginalName;
        }
    }

    /// <summary>根据英文原名、中文名或页面链接，在本地总库中查找已有记录（用于补齐中文名及官方封面）</summary>
    public TrainerItem? FindExistingTrainer(string? nameOrUrl)
    {
        if (string.IsNullOrWhiteSpace(nameOrUrl)) return null;
        var q = nameOrUrl.Trim();
        var qNorm = NormalizeName(q);

        lock (_lock)
        {
            return _allTrainers.FirstOrDefault(t =>
                (!string.IsNullOrEmpty(t.PageUrl) && string.Equals(t.PageUrl, q, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(t.OriginalName, q, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.GameName, q, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(qNorm) && (
                    NormalizeName(t.OriginalName) == qNorm ||
                    NormalizeName(t.GameName) == qNorm
                )));
        }
    }

    /// <summary>严格验证某个修改器条目是否确实与用户的搜索词相关，杜绝跨游戏串味污染</summary>
    public bool IsTrainerMatchQuery(TrainerItem item, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        var qNorm = NormalizeName(q);

        // 1. 中文名匹配
        if (!string.IsNullOrEmpty(item.GameName))
        {
            if (item.GameName.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(qNorm) && NormalizeName(item.GameName).Contains(qNorm)) return true;
        }

        // 2. 英文原名匹配
        if (!string.IsNullOrEmpty(item.OriginalName))
        {
            if (item.OriginalName.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(qNorm) && NormalizeName(item.OriginalName).Contains(qNorm)) return true;
        }

        // 3. 别名匹配
        if (item.Aliases != null && item.Aliases.Count > 0)
        {
            if (item.Aliases.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase))) return true;
            if (!string.IsNullOrEmpty(qNorm) && item.Aliases.Any(a => NormalizeName(a).Contains(qNorm))) return true;
        }

        return false;
    }

    /// <summary>本地极速模糊搜索（支持中文原名、常用别名、中英文简称及无标点模糊匹配）</summary>
    public List<TrainerItem> SearchLocal(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _allTrainers.ToList();

        var rawQ = query.Trim();
        var qNorm = NormalizeName(rawQ);

        lock (_lock)
        {
            var scored = new List<(TrainerItem Item, int Score)>();

            foreach (var t in _allTrainers)
            {
                int score = 0;

                // 1. 完全匹配（中文名、英文名、别名）
                if (t.GameName.Equals(rawQ, StringComparison.OrdinalIgnoreCase) ||
                    t.OriginalName.Equals(rawQ, StringComparison.OrdinalIgnoreCase) ||
                    t.Aliases.Any(a => a.Equals(rawQ, StringComparison.OrdinalIgnoreCase)))
                {
                    score = 1000;
                }
                // 2. 标点符号与空格归一化后完全匹配（例如：黑神话悟空 == 黑神话：悟空）
                else if (!string.IsNullOrEmpty(qNorm) && (
                    NormalizeName(t.GameName) == qNorm ||
                    NormalizeName(t.OriginalName) == qNorm ||
                    t.Aliases.Any(a => NormalizeName(a) == qNorm)))
                {
                    score = 900;
                }
                // 3. 前缀匹配
                else if (t.GameName.StartsWith(rawQ, StringComparison.OrdinalIgnoreCase) ||
                         t.OriginalName.StartsWith(rawQ, StringComparison.OrdinalIgnoreCase) ||
                         t.Aliases.Any(a => a.StartsWith(rawQ, StringComparison.OrdinalIgnoreCase)))
                {
                    score = 700;
                }
                // 4. 归一化前缀匹配
                else if (!string.IsNullOrEmpty(qNorm) && (
                         NormalizeName(t.GameName).StartsWith(qNorm) ||
                         NormalizeName(t.OriginalName).StartsWith(qNorm) ||
                         t.Aliases.Any(a => NormalizeName(a).StartsWith(qNorm))))
                {
                    score = 600;
                }
                // 5. 包含子串
                else if (t.GameName.Contains(rawQ, StringComparison.OrdinalIgnoreCase) ||
                         t.OriginalName.Contains(rawQ, StringComparison.OrdinalIgnoreCase) ||
                         t.Aliases.Any(a => a.Contains(rawQ, StringComparison.OrdinalIgnoreCase)))
                {
                    score = 400;
                }
                // 6. 归一化包含匹配（去掉空格、标点符号后包含）
                else if (!string.IsNullOrEmpty(qNorm) && (
                         NormalizeName(t.GameName).Contains(qNorm) ||
                         NormalizeName(t.OriginalName).Contains(qNorm) ||
                         t.Aliases.Any(a => NormalizeName(a).Contains(qNorm))))
                {
                    score = 300;
                }

                if (score > 0)
                {
                    if (t.IsHot) score += 50;
                    scored.Add((t, score));
                }
            }

            var results = scored.OrderByDescending(s => s.Score).Select(s => s.Item).ToList();
            SyncDownloadedState(results);
            return results;
        }
    }

    /// <summary>后台尝试在线增量同步热门与最新修改器</summary>
    public async Task<(List<TrainerItem> hot, List<TrainerItem> newest)> FetchOnlineUpdatesAsync()
    {
        var rawHot = await _scraper.GetHotTrainersAsync(15);
        var rawNewest = await _scraper.GetNewReleasesAsync(15);

        lock (_lock)
        {
            var hot = new List<TrainerItem>();
            foreach (var item in rawHot)
            {
                hot.Add(MergeOrAdd(item));
            }

            var newest = new List<TrainerItem>();
            foreach (var item in rawNewest)
            {
                newest.Add(MergeOrAdd(item));
            }

            SyncDownloadedState(_allTrainers);
            SyncDownloadedState(hot);
            SyncDownloadedState(newest);
            return (hot, newest);
        }
    }

    private TrainerItem MergeOrAdd(TrainerItem item)
    {
        var existing = _allTrainers.FirstOrDefault(t =>
            (!string.IsNullOrEmpty(item.PageUrl) && t.PageUrl.Equals(item.PageUrl, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(item.OriginalName) && t.OriginalName.Equals(item.OriginalName, StringComparison.OrdinalIgnoreCase)));

        if (existing != null)
        {
            if (item.IsHot) existing.IsHot = true;
            if (item.IsNew) existing.IsNew = true;
            // 只有当现有条目完全没有封面时，才采纳在线拉取到的封面；若已有官方封面绝不替换
            if (string.IsNullOrEmpty(existing.CoverUrl) && !string.IsNullOrEmpty(item.CoverUrl))
            {
                existing.CoverUrl = item.CoverUrl;
            }
            return existing;
        }
        else
        {
            _allTrainers.Insert(0, item);
            return item;
        }
    }

    private static string NormalizeName(string name)
    {
        return new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
