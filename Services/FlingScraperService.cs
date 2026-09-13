using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using GameTrainer.Models;

namespace GameTrainer.Services;

public class FlingScraperService
{
    private readonly IHttpClientProvider _httpProvider;

    public FlingScraperService(IHttpClientProvider httpProvider)
    {
        _httpProvider = httpProvider;
    }

    /// <summary>获取热门修改器列表</summary>
    public async Task<List<TrainerItem>> GetHotTrainersAsync(int count = 15)
    {
        var result = new List<TrainerItem>();
        try
        {
            var html = await _httpProvider.SendWithProxyRetryAsync(
                "fling-home",
                TimeSpan.FromSeconds(15),
                client => client.GetStringAsync("https://flingtrainer.com/"));

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var items = doc.DocumentNode.SelectNodes("//div[contains(@class,'popular-posts')]//ul[contains(@class,'wpp-list')]/li");
            if (items != null)
            {
                foreach (var li in items.Take(count))
                {
                    var titleLink = li.SelectSingleNode(".//a[contains(@class,'wpp-post-title')]");
                    var imgNode = li.SelectSingleNode(".//img[contains(@class,'wpp-thumbnail')]");

                    var rawName = WebUtility.HtmlDecode(titleLink?.InnerText.Trim() ?? "").Replace('\u2019', '\'');
                    if (string.IsNullOrWhiteSpace(rawName)) continue;

                    var pageUrl = titleLink?.GetAttributeValue("href", "") ?? "";
                    var coverUrl = imgNode?.GetAttributeValue("src", "") ?? "";

                    result.Add(new TrainerItem
                    {
                        GameName = StripTrainerSuffix(rawName),
                        OriginalName = StripTrainerSuffix(rawName),
                        CoverUrl = coverUrl,
                        PageUrl = pageUrl,
                        IsHot = true
                    });
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>获取最新发布修改器列表</summary>
    public async Task<List<TrainerItem>> GetNewReleasesAsync(int count = 15)
    {
        var result = new List<TrainerItem>();
        try
        {
            var html = await _httpProvider.SendWithProxyRetryAsync(
                "fling-home",
                TimeSpan.FromSeconds(15),
                client => client.GetStringAsync("https://flingtrainer.com/"));

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var items = doc.DocumentNode.SelectNodes("//div[@id='rpwe_widget-4']//ul[contains(@class,'rpwe-ul')]/li");
            if (items != null)
            {
                foreach (var item in items.Take(count))
                {
                    var titleLink = item.SelectSingleNode(".//h3[contains(@class,'rpwe-title')]/a");
                    var imgNode = item.SelectSingleNode(".//a[contains(@class,'rpwe-img')]//img");

                    var rawName = WebUtility.HtmlDecode(titleLink?.InnerText.Trim() ?? "").Replace('\u2019', '\'');
                    if (string.IsNullOrWhiteSpace(rawName)) continue;

                    var pageUrl = titleLink?.GetAttributeValue("href", "") ?? "";
                    var coverUrl = imgNode?.GetAttributeValue("src", "") ?? "";

                    result.Add(new TrainerItem
                    {
                        GameName = StripTrainerSuffix(rawName),
                        OriginalName = StripTrainerSuffix(rawName),
                        CoverUrl = coverUrl,
                        PageUrl = pageUrl,
                        IsNew = true
                    });
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>从修改器详情页解析真实下载直链。若页面404或未获取到链接，自动按游戏英文名去风灵官网搜索补齐最新文章。</summary>
    public async Task<string?> ResolveDownloadUrlAsync(string pageUrl, string? fallbackGameName = null)
    {
        string? href = null;

        if (!string.IsNullOrWhiteSpace(pageUrl))
        {
            try
            {
                var html = await _httpProvider.SendWithProxyRetryAsync(
                    "fling-page",
                    TimeSpan.FromSeconds(15),
                    client => client.GetStringAsync(pageUrl));

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var linkNode = doc.DocumentNode.SelectSingleNode("//a[contains(@class,'attachment-link')]");
                if (linkNode != null)
                {
                    href = linkNode.GetAttributeValue("href", "");
                }
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(href)) return href;

        // 若原网址失效或未解析到链接，尝试通过游戏英文原名搜索风灵官网定位最新有效文章
        if (!string.IsNullOrWhiteSpace(fallbackGameName))
        {
            try
            {
                var searchHits = await SearchOnlineAsync(fallbackGameName);
                var matched = searchHits.FirstOrDefault(h => IsFlingResultRelevant(fallbackGameName, h.OriginalName));
                if (matched != null && !string.IsNullOrWhiteSpace(matched.PageUrl) && !string.Equals(matched.PageUrl, pageUrl, StringComparison.OrdinalIgnoreCase))
                {
                    return await ResolveDownloadUrlAsync(matched.PageUrl, null);
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>在线搜索修改器</summary>
    public async Task<List<TrainerItem>> SearchOnlineAsync(string query)
    {
        var result = new List<TrainerItem>();
        if (string.IsNullOrWhiteSpace(query)) return result;

        var trimmed = query.Trim();
        // 过滤无意义的极短搜索词，避免触发风灵博客全站最新文章倒排
        if (trimmed.Length < 2 && !trimmed.Any(c => c > 0x2E80)) return result;

        try
        {
            var url = $"https://flingtrainer.com/?s={Uri.EscapeDataString(trimmed)}";
            var html = await _httpProvider.SendWithProxyRetryAsync(
                "fling-search",
                TimeSpan.FromSeconds(15),
                client => client.GetStringAsync(url));

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var articles = doc.DocumentNode.SelectNodes("//article[contains(@class,'post-standard')]");
            if (articles != null)
            {
                foreach (var article in articles.Take(12))
                {
                    var titleNode = article.SelectSingleNode(".//h2[contains(@class,'post-title')]/a");
                    var imgNode = article.SelectSingleNode(".//img[contains(@class,'wp-post-image')]");

                    var rawName = WebUtility.HtmlDecode(titleNode?.InnerText.Trim() ?? "").Replace('\u2019', '\'');
                    if (string.IsNullOrWhiteSpace(rawName)) continue;

                    var pageUrl = titleNode?.GetAttributeValue("href", "") ?? "";
                    var coverUrl = imgNode?.GetAttributeValue("src", "") ?? "";

                    result.Add(new TrainerItem
                    {
                        GameName = StripTrainerSuffix(rawName),
                        OriginalName = StripTrainerSuffix(rawName),
                        PageUrl = pageUrl,
                        CoverUrl = coverUrl
                    });
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>智能搜索：支持中英文与中文简写。优先查本地，必要时通过 Steam 商店解析英文名再去风灵官网搜索。</summary>
    public async Task<List<TrainerItem>> SearchOnlineSmartAsync(
        string query, 
        Func<string, string?>? localEnglishResolver = null,
        Func<string, TrainerItem?>? localItemMatcher = null)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return new List<TrainerItem>();

        var hasChinese = trimmed.Any(c => c > 0x2E80);
        if (!hasChinese)
        {
            // 英文搜索：直接向官网搜索并进行严格相关度校验
            var direct = await SearchOnlineAsync(trimmed);
            var filtered = new List<TrainerItem>();
            foreach (var item in direct)
            {
                if (!IsFlingResultRelevant(trimmed, item.OriginalName)) continue;

                // 本地若已有此修改器，优先使用经过验证的准确中文名与高清封面
                var local = localItemMatcher?.Invoke(item.OriginalName);
                if (local != null)
                {
                    item.GameName = local.GameName;
                    if (!string.IsNullOrEmpty(local.CoverUrl)) item.CoverUrl = local.CoverUrl;
                    item.Aliases = local.Aliases;
                    item.IsHot = local.IsHot;
                }
                filtered.Add(item);
            }
            return filtered;
        }

        var result = new List<TrainerItem>();

        // 1. 先尝试本地快速词典/别名库匹配英文名
        var localEn = localEnglishResolver?.Invoke(trimmed);
        if (!string.IsNullOrWhiteSpace(localEn))
        {
            var fromLocalEn = await SearchOnlineAsync(localEn);
            foreach (var item in fromLocalEn)
            {
                if (!IsFlingResultRelevant(localEn, item.OriginalName)) continue;

                var local = localItemMatcher?.Invoke(item.OriginalName);
                if (local != null)
                {
                    item.GameName = local.GameName;
                    if (!string.IsNullOrEmpty(local.CoverUrl)) item.CoverUrl = local.CoverUrl;
                }
                else
                {
                    item.GameName = trimmed; // 优先友好展示用户搜的中文
                }
                result.Add(item);
            }
            if (result.Count > 0) return result;
        }

        // 2. 本地未命中具体游戏（可能输入了未收录的新游戏），通过 Steam 商店候选解析对应英文名
        var candidates = await ResolveChineseCandidatesAsync(trimmed);
        foreach (var (appId, zhName) in candidates)
        {
            var en = await ResolveEnglishNameAsync(appId);
            if (string.IsNullOrWhiteSpace(en) || en.Length < 2) continue;

            var list = await SearchOnlineAsync(en);

            foreach (var trainer in list)
            {
                // 核心防污染门禁：必须确保风灵官网返回的结果与当前查询目标游戏真实相关！
                // 彻底杜绝将不相关游戏（如返回的 WordPress 热门列表）打上 zhName 标签！
                if (!IsFlingResultRelevant(en, trainer.OriginalName)) continue;

                var local = localItemMatcher?.Invoke(trainer.OriginalName);
                if (local != null)
                {
                    trainer.GameName = local.GameName;
                    if (!string.IsNullOrEmpty(local.CoverUrl)) trainer.CoverUrl = local.CoverUrl;
                }
                else if (!string.IsNullOrWhiteSpace(zhName))
                {
                    trainer.GameName = zhName;
                }

                if (result.All(r => !string.Equals(r.PageUrl, trainer.PageUrl, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(trainer);
                }
            }
            if (result.Count >= 10) break;
        }

        return result;
    }

    private async Task<List<(int AppId, string ZhName)>> ResolveChineseCandidatesAsync(string query)
    {
        var result = new List<(int AppId, string ZhName)>();
        var attempts = new List<string> { query };
        var normalized = NormalizeChinese(query);
        if (normalized != query) attempts.Add(normalized);

        foreach (var term in attempts)
        {
            try
            {
                var url = "https://store.steampowered.com/api/storesearch/?term=" +
                          Uri.EscapeDataString(term) + "&l=schinese&cc=cn";
                var json = await _httpProvider.SendWithProxyRetryAsync(
                    "trainer-steam-search",
                    TimeSpan.FromSeconds(15),
                    client => client.GetStringAsync(url));
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray().Take(6))
                    {
                        var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var id = item.TryGetProperty("id", out var i) && i.TryGetInt32(out var ai) ? ai : 0;
                        if (!string.IsNullOrWhiteSpace(name) && id > 0 && result.All(c => c.AppId != id))
                            result.Add((id, name));
                    }
                }
                if (result.Count > 0) break;
            }
            catch { }
        }
        return result;
    }

    private async Task<string?> ResolveEnglishNameAsync(int appId)
    {
        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english";
            var json = await _httpProvider.SendWithProxyRetryAsync(
                "trainer-en-name",
                TimeSpan.FromSeconds(15),
                client => client.GetStringAsync(url));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(appId.ToString(), out var node) &&
                node.TryGetProperty("success", out var ok) && ok.GetBoolean() &&
                node.TryGetProperty("data", out var data) &&
                data.TryGetProperty("name", out var name))
            {
                var en = name.GetString();
                if (!string.IsNullOrWhiteSpace(en)) return en;
            }
        }
        catch { }

        try
        {
            var html = await _httpProvider.SendWithProxyRetryAsync(
                "trainer-en-title",
                TimeSpan.FromSeconds(15),
                client => client.GetStringAsync($"https://store.steampowered.com/app/{appId}/?l=english&cc=cn"));
            var m = Regex.Match(html, "<title>(.*?)</title>", RegexOptions.Singleline);
            if (m.Success)
            {
                var title = WebUtility.HtmlDecode(m.Groups[1].Value);
                title = Regex.Replace(title, "\\s+on Steam\\s*$", "", RegexOptions.IgnoreCase);
                title = Regex.Replace(title, "^Save \\d+% on ", "", RegexOptions.IgnoreCase);
                if (!string.IsNullOrWhiteSpace(title) &&
                    !title.Contains("Welcome to Steam", StringComparison.OrdinalIgnoreCase))
                    return title;
            }
        }
        catch { }
        return null;
    }

    private static readonly HashSet<string> RelevanceStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "and", "in", "to", "for", "on", "with", "at", "by", "from",
        "trainer", "plus", "edition", "remastered", "remake", "hd", "deluxe", "v1", "v2"
    };

    /// <summary>
    /// 校验风灵官网返回的结果标题是否与目标游戏真实相关。
    /// 彻底防止 WordPress 泛搜索（如搜索包含 'The' 或 'World' 的游戏时返回不相关的热门文章）导致的串味污染。
    /// </summary>
    public static bool IsFlingResultRelevant(string expectedGame, string actualFlingTitle)
    {
        if (string.IsNullOrWhiteSpace(expectedGame) || string.IsNullOrWhiteSpace(actualFlingTitle))
            return false;

        var expTokens = Tokenize(expectedGame);
        var actTokens = Tokenize(actualFlingTitle);

        if (expTokens.Count == 0 || actTokens.Count == 0)
            return false;

        var actSet = new HashSet<string>(actTokens, StringComparer.OrdinalIgnoreCase);

        // 如果期望名称只有 1 个特征词（如 "Valheim" 或 "Wukong"），必须完全包含
        if (expTokens.Count == 1)
        {
            return actSet.Contains(expTokens[0]) || actTokens.Any(at => at.Contains(expTokens[0], StringComparison.OrdinalIgnoreCase));
        }

        // 统计特征词命中数
        int matched = expTokens.Count(t => actSet.Contains(t) || actTokens.Any(at => at.Contains(t, StringComparison.OrdinalIgnoreCase)));
        double ratio = (double)matched / expTokens.Count;

        // 匹配规则：
        // 1. 2 个特征词至少命中 1 个并且包含
        // 2. 3 个及以上特征词至少命中 2 个，且命中率 >= 40%
        return matched >= 2 || (expTokens.Count == 2 && matched >= 1) || ratio >= 0.5;
    }

    private static List<string> Tokenize(string text)
    {
        var clean = new string(text.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        return clean.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !RelevanceStopWords.Contains(w))
            .ToList();
    }

    private static string NormalizeChinese(string s) =>
        new(s.Where(c => !char.IsWhiteSpace(c) && c != '：' && c != ':' && c != '《' && c != '》' && c != '“' && c != '”').ToArray());

    private static string StripTrainerSuffix(string name)
    {
        var suffix = " Trainer";
        return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^suffix.Length]
            : name;
    }
}
