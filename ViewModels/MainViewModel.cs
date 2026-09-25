using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameTrainer.Models;
using GameTrainer.Services;

namespace GameTrainer.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly TrainerDataService _dataService;
    private readonly TrainerDownloadService _downloadService;
    private readonly FlingScraperService _scraper;
    private readonly LocalGameScannerService _scannerService;

    private readonly DispatcherTimer _searchDebounceTimer;
    private DispatcherTimer? _statusTimer;
    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _currentSection = "local"; // local, hot, new, downloaded, search

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isSearchResultEmpty;

    [ObservableProperty]
    private bool _isListView;

    [ObservableProperty]
    private int _installedGamesCount;

    [ObservableProperty]
    private bool _isScanningLocalGames;

    [ObservableProperty]
    private string _scanProgressMessage = string.Empty;

    [ObservableProperty]
    private bool _hasUnmatchedGames;

    [ObservableProperty]
    private bool _isUnmatchedExpanded;

    [ObservableProperty]
    private ObservableCollection<DetectedGame> _unmatchedGameItems = new();

    [ObservableProperty]
    private ObservableCollection<TrainerItem> _displayedTrainers = new();

    private List<TrainerItem> _allLocalList = new();
    private List<TrainerItem> _localList = new();
    private List<TrainerItem> _hotList = new();
    private List<TrainerItem> _newList = new();
    private List<DetectedGame> _unmatchedGames = new();

    public MainViewModel(
        TrainerDataService dataService,
        TrainerDownloadService downloadService,
        FlingScraperService scraper,
        LocalGameScannerService scannerService)
    {
        _dataService = dataService;
        _downloadService = downloadService;
        _scraper = scraper;
        _scannerService = scannerService;

        _isListView = _dataService.Settings.IsListView;

        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            PerformSearch();
        };

        InitializeData();
    }

    public bool IsLocalTab => CurrentSection == "local";
    public bool IsHotTab => CurrentSection == "hot";
    public bool IsNewTab => CurrentSection == "new";
    public bool IsDownloadedTab => CurrentSection == "downloaded";
    public bool IsCardView => !IsListView;
    public bool ShowUnmatchedSection => IsLocalTab && HasUnmatchedGames;

    partial void OnCurrentSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsLocalTab));
        OnPropertyChanged(nameof(IsHotTab));
        OnPropertyChanged(nameof(IsNewTab));
        OnPropertyChanged(nameof(IsDownloadedTab));
        OnPropertyChanged(nameof(ShowUnmatchedSection));
    }

    partial void OnHasUnmatchedGamesChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowUnmatchedSection));
    }

    partial void OnIsListViewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCardView));
        _dataService.Settings.IsListView = value;
        _dataService.SaveSettings();
    }

    [RelayCommand]
    private void SetViewMode(string mode)
    {
        IsListView = mode == "list";
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }

    public void TriggerImmediateSearch()
    {
        _searchDebounceTimer.Stop();
        PerformSearch();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounceTimer.Stop();
        _searchCts?.Cancel();

        // 当用户清空输入框或全部删除时，零延迟立刻返回热门推荐，绝不卡顿
        if (string.IsNullOrWhiteSpace(value))
        {
            if (CurrentSection == "search")
            {
                CurrentSection = "hot";
                UpdateDisplayedList();
            }
            return;
        }

        _searchDebounceTimer.Start();
    }

    partial void OnStatusMessageChanged(string value)
    {
        _statusTimer?.Stop();
        if (string.IsNullOrEmpty(value)) return;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) => { _statusTimer?.Stop(); StatusMessage = string.Empty; };
        _statusTimer.Start();
    }

    private void InitializeData()
    {
        // 1. 本地极速秒开（载入基础离线库）
        _allLocalList = _dataService.LoadBuiltinData();
        _hotList = _allLocalList.Where(x => x.IsHot).Take(40).ToList();
        _newList = _allLocalList.Take(30).ToList();

        // 默认首先展示本机游戏分类
        CurrentSection = "local";
        UpdateDisplayedList();

        // 2. 自动启动本机已安装游戏探测与在线智能探针
        _ = ScanLocalGamesAsync();

        // 3. 后台异步同步官网最新海报与更新（断网不报错、不卡界面）
        _ = Task.Run(async () =>
        {
            try
            {
                var (onlineHot, onlineNewest) = await _dataService.FetchOnlineUpdatesAsync();
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (onlineHot.Count > 0)
                    {
                        var existingUrls = new HashSet<string>(onlineHot.Select(x => x.PageUrl), StringComparer.OrdinalIgnoreCase);
                        _hotList = onlineHot.Concat(_hotList.Where(x => !existingUrls.Contains(x.PageUrl))).Take(50).ToList();
                    }
                    if (onlineNewest.Count > 0)
                    {
                        _newList = onlineNewest;
                    }

                    // 只有用户当前正停留在热门或最新时才温和刷新，绝不篡改其他视图
                    if (CurrentSection is "hot" or "new")
                    {
                        UpdateDisplayedList();
                    }
                });
            }
            catch { }
        });
    }

    [RelayCommand]
    private void SelectSection(string section)
    {
        _searchDebounceTimer.Stop();
        CurrentSection = section;
        IsSearchResultEmpty = false;

        // 如果从搜索状态切回分类，清空搜索框但不要再触发搜索
        if (!string.IsNullOrEmpty(SearchText))
        {
            SearchText = string.Empty;
        }

        UpdateDisplayedList();
    }

    private void PerformSearch()
    {
        var text = SearchText?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            IsSearchResultEmpty = false;
            // 只有当之前处于搜索状态时，清空搜索框才自动切回热门推荐
            if (CurrentSection == "search")
            {
                CurrentSection = "hot";
                UpdateDisplayedList();
            }
            return;
        }

        CurrentSection = "search";

        // 取消上一轮未完成的后台网络搜索任务，避免并发打架
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        // 1. 离线/本地极速搜索（内存毫秒级；单次渲染最多限制前 50 条，消除 WrapPanel 几百个卡片创建造成的界面卡死）
        var localResults = _dataService.SearchLocal(text);
        var displayResults = localResults.Take(50).ToList();
        DisplayedTrainers = new ObservableCollection<TrainerItem>(displayResults);
        IsSearchResultEmpty = localResults.Count == 0;
        
        if (localResults.Count > 50)
        {
            StatusMessage = $"找到 {localResults.Count} 个修改器（已展示最匹配的前 50 个）";
        }
        else
        {
            StatusMessage = localResults.Count == 0 ? "未找到相关修改器，正在联网匹配..." : $"找到 {localResults.Count} 个修改器";
        }

        // 2. 同时异步向风灵官网搜索（如果能联网则补充）
        Task.Run(async () =>
        {
            try
            {
                if (ct.IsCancellationRequested) return;

                var onlineResults = await _scraper.SearchOnlineSmartAsync(
                    text, 
                    _dataService.ResolveEnglishName,
                    _dataService.FindExistingTrainer);

                if (ct.IsCancellationRequested) return;

                if (onlineResults.Count > 0)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        if (SearchText?.Trim() == text)
                        {
                            var currentUrls = new HashSet<string>(DisplayedTrainers.Select(x => x.PageUrl), StringComparer.OrdinalIgnoreCase);
                            int addedCount = 0;
                            foreach (var item in onlineResults)
                            {
                                if (DisplayedTrainers.Count >= 60) break; // 保护上限

                                // 必须同时满足：未在当前列表中且严格匹配用户搜索词
                                if (!currentUrls.Contains(item.PageUrl) && _dataService.IsTrainerMatchQuery(item, text))
                                {
                                    DisplayedTrainers.Add(item);
                                    currentUrls.Add(item.PageUrl);
                                    addedCount++;
                                }
                            }
                            if (addedCount > 0)
                            {
                                _dataService.SyncDownloadedState(DisplayedTrainers);
                            }
                            IsSearchResultEmpty = DisplayedTrainers.Count == 0;
                            StatusMessage = $"共找到 {DisplayedTrainers.Count} 个修改器";
                        }
                    });
                }
                else if (localResults.Count == 0)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        if (SearchText?.Trim() == text && DisplayedTrainers.Count == 0)
                        {
                            StatusMessage = "未找到相关修改器，可尝试输入英文原名重试";
                        }
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }, ct);
    }

    private void UpdateDisplayedList()
    {
        List<TrainerItem> target;
        switch (CurrentSection)
        {
            case "local":
                target = _localList;
                _dataService.SyncDownloadedState(target);
                break;
            case "hot":
                target = _hotList;
                _dataService.SyncDownloadedState(target);
                break;
            case "new":
                target = _newList;
                _dataService.SyncDownloadedState(target);
                break;
            case "downloaded":
                target = _dataService.GetDownloadedTrainers();
                break;
            case "search":
                return;
            default:
                target = _localList.Count > 0 ? _localList : _hotList;
                _dataService.SyncDownloadedState(target);
                break;
        }

        DisplayedTrainers = new ObservableCollection<TrainerItem>(target);
        IsSearchResultEmpty = target.Count == 0;
    }

    [RelayCommand]
    private void ToggleUnmatchedExpanded()
    {
        IsUnmatchedExpanded = !IsUnmatchedExpanded;
    }

    [RelayCommand]
    private async Task RescanLocalGamesAsync()
    {
        await ScanLocalGamesAsync();
    }

    public async Task ScanLocalGamesAsync()
    {
        IsScanningLocalGames = true;
        ScanProgressMessage = "正在全盘扫描 Steam / Epic / GOG 与本地已安装游戏...";

        try
        {
            var detected = await _scannerService.ScanAllGamesAsync();

            var matchedTrainers = new List<TrainerItem>();
            var unmatched = new List<DetectedGame>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Phase 1: 毫秒级离线快速对齐
            foreach (var game in detected)
            {
                var trainer = _dataService.FindTrainerForDetectedGame(game.Name, game.InstallPath);
                if (trainer != null)
                {
                    trainer.IsLocalInstalled = true;
                    if (!string.IsNullOrEmpty(game.InstallPath))
                    {
                        trainer.InstalledGamePath = game.InstallPath;
                    }
                    if (seenUrls.Add(trainer.PageUrl))
                    {
                        matchedTrainers.Add(trainer);
                    }
                    game.HasTrainer = true;
                }
                else
                {
                    unmatched.Add(game);
                }
            }

            _localList = matchedTrainers.ToList();
            InstalledGamesCount = _localList.Count;

            Application.Current.Dispatcher.Invoke(() =>
            {
                _dataService.SyncDownloadedState(_localList);
                if (CurrentSection == "local")
                {
                    UpdateDisplayedList();
                }
                if (_localList.Count > 0)
                {
                    StatusMessage = $"已检测到本机 {_localList.Count} 款游戏并匹配修改器";
                }
            });

            // Phase 2: 后台在线探针（对离线库未命中的新游戏，向官网发起检索）
            if (unmatched.Count > 0)
            {
                ScanProgressMessage = $"正在为 {unmatched.Count} 款游戏联网探测风灵官网最新修改器...";

                var candidates = unmatched
                    .Where(g => !string.IsNullOrWhiteSpace(g.Name) && g.Name.Length >= 3)
                    .Take(15)
                    .ToList();

                var sem = new SemaphoreSlim(2);
                var probeTasks = candidates.Select(async candidate =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var onlineHits = await _scraper.SearchOnlineSmartAsync(
                            candidate.Name,
                            _dataService.ResolveEnglishName,
                            _dataService.FindExistingTrainer);

                        if (onlineHits.Count > 0)
                        {
                            var best = onlineHits[0];
                            best.IsLocalInstalled = true;
                            best.InstalledGamePath = candidate.InstallPath;
                            var merged = _dataService.MergeOrAdd(best);

                            candidate.HasTrainer = true;
                            lock (matchedTrainers)
                            {
                                if (seenUrls.Add(merged.PageUrl))
                                {
                                    matchedTrainers.Insert(0, merged);
                                }
                            }

                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                _localList = matchedTrainers.ToList();
                                InstalledGamesCount = _localList.Count;
                                _dataService.SyncDownloadedState(_localList);
                                if (CurrentSection == "local")
                                {
                                    UpdateDisplayedList();
                                }
                                StatusMessage = $"新发现修改器：{merged.GameName}";
                            });
                        }
                        else
                        {
                            candidate.Note = "风灵官方暂未收录该游戏修改器";
                        }
                    }
                    catch { }
                    finally
                    {
                        sem.Release();
                    }
                });

                await Task.WhenAll(probeTasks);
            }

            _unmatchedGames = unmatched.Where(x => !x.HasTrainer).ToList();
            Application.Current.Dispatcher.Invoke(() =>
            {
                UnmatchedGameItems = new ObservableCollection<DetectedGame>(_unmatchedGames);
                HasUnmatchedGames = _unmatchedGames.Count > 0;
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描本机游戏时出错：{ex.Message}";
        }
        finally
        {
            IsScanningLocalGames = false;
            ScanProgressMessage = string.Empty;
        }
    }

    [RelayCommand]
    private async Task DownloadAsync(TrainerItem item)
    {
        if (item == null || item.IsDownloading) return;

        if (item.IsDownloaded && File.Exists(item.LocalPath))
        {
            StatusMessage = "该修改器已下载，直接启动即可";
            return;
        }

        item.IsDownloading = true;
        item.DownloadProgress = 0;
        StatusMessage = $"正在准备下载 {item.GameName}...";

        try
        {
            if (string.IsNullOrWhiteSpace(item.DownloadUrl))
            {
                StatusMessage = "正在解析官网下载地址...";
                var url = await _scraper.ResolveDownloadUrlAsync(item.PageUrl, item.OriginalName);
                if (string.IsNullOrWhiteSpace(url))
                    throw new InvalidOperationException("未能获取到下载链接，可能官网网络暂时波动");
                item.DownloadUrl = url;
            }

            var downloadDir = _dataService.Settings.GetEffectiveDownloadDir();
            StatusMessage = $"正在下载 {item.GameName}...";

            var localExe = await _downloadService.DownloadAsync(item, downloadDir, progress =>
            {
                Application.Current.Dispatcher.Invoke(() => item.DownloadProgress = progress);
            });

            item.LocalPath = localExe;
            item.IsDownloaded = true;

            _dataService.InvalidateDownloadCache();
            _dataService.SyncDownloadedState(DisplayedTrainers);
            _dataService.SyncDownloadedState(_localList);
            StatusMessage = $"下载完成：{Path.GetFileName(localExe)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"下载失败：{ex.Message}";
        }
        finally
        {
            item.IsDownloading = false;
        }
    }

    [RelayCommand]
    private void LaunchTrainer(TrainerItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.LocalPath) || !File.Exists(item.LocalPath))
        {
            StatusMessage = "未找到修改器主程序，可能已被杀毒软件误删或移动";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.LocalPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(item.LocalPath)
            });
            StatusMessage = $"已启动：{Path.GetFileName(item.LocalPath)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"启动失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteTrainer(TrainerItem item)
    {
        if (item == null) return;

        var res = MessageBox.Show(
            $"确定删除「{item.GameName}」的修改器文件吗？\n删除后可随时重新下载。",
            "删除修改器",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res != MessageBoxResult.Yes) return;

        try
        {
            if (!string.IsNullOrEmpty(item.LocalPath))
            {
                var dir = Path.GetDirectoryName(item.LocalPath);
                var root = _dataService.Settings.GetEffectiveDownloadDir();
                if (!string.IsNullOrEmpty(dir) && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
                else if (File.Exists(item.LocalPath))
                {
                    File.Delete(item.LocalPath);
                }
            }

            item.IsDownloaded = false;
            item.LocalPath = string.Empty;
            item.Options.Clear();

            _dataService.InvalidateDownloadCache();

            if (CurrentSection == "downloaded")
            {
                DisplayedTrainers.Remove(item);
                IsSearchResultEmpty = DisplayedTrainers.Count == 0;
            }

            // 同步其他视图中的同款修改器状态
            _dataService.SyncDownloadedState(_localList);
            _dataService.SyncDownloadedState(_hotList);
            _dataService.SyncDownloadedState(_newList);
            if (CurrentSection != "downloaded")
            {
                _dataService.SyncDownloadedState(DisplayedTrainers);
            }

            StatusMessage = $"已删除「{item.GameName}」修改器";
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除出错：{ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        var dir = _dataService.Settings.GetEffectiveDownloadDir();
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task AddDefenderExclusionAsync()
    {
        var dir = _dataService.Settings.GetEffectiveDownloadDir();
        StatusMessage = "正在配置 Windows Defender 排除项...";
        var (success, msg, openSettings) = await DefenderWhitelistService.AddExclusionPathAsync(dir);
        if (!success)
        {
            try { Clipboard.SetText(dir); } catch { }
        }
        StatusMessage = success ? "已成功添加白名单！" : "防篡改保护限制，请在安全中心手动确认添加";
        MessageBox.Show(msg, "Windows Defender 白名单配置", MessageBoxButton.OK,
            success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    [RelayCommand]
    private void OpenTrainerOfficialPage(TrainerItem item)
    {
        if (item != null && !string.IsNullOrWhiteSpace(item.PageUrl))
        {
            Process.Start(new ProcessStartInfo(item.PageUrl) { UseShellExecute = true });
        }
    }
}
