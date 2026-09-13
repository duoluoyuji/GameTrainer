using System.Text.Json.Serialization;

namespace GameTrainer.Models;

public class CheatOption
{
    public string KeyName { get; set; } = string.Empty;
    public string Modifiers { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayKey => string.IsNullOrEmpty(Modifiers)
        ? KeyName
        : $"{Modifiers}+{KeyName}";

    [JsonIgnore]
    public string DisplayText => $"{DisplayKey} - {Description}";
}
