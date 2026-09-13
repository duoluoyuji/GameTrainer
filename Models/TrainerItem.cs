using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GameTrainer.Models;

public partial class TrainerItem : ObservableObject
{
    [ObservableProperty]
    private string _gameName = string.Empty;

    [ObservableProperty]
    private string _originalName = string.Empty;

    [ObservableProperty]
    private string _coverUrl = string.Empty;

    [ObservableProperty]
    private string _pageUrl = string.Empty;

    [ObservableProperty]
    private string _downloadUrl = string.Empty;

    [ObservableProperty]
    private string _updateDate = string.Empty;

    [ObservableProperty]
    private string _localPath = string.Empty;

    [ObservableProperty]
    private bool _isDownloaded;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private bool _isHot;

    [ObservableProperty]
    private bool _isNew;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private ObservableCollection<CheatOption> _options = new();

    public List<string> Aliases { get; set; } = new();

    [JsonIgnore]
    public bool HasOptions => Options.Count > 0;
}
