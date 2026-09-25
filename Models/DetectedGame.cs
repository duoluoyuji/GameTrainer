namespace GameTrainer.Models;

public class DetectedGame
{
    public string Name { get; set; } = string.Empty;
    public string InstallPath { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty; // Steam, Epic, GOG, Registry, Folder
    public bool HasTrainer { get; set; }
    public string Note { get; set; } = string.Empty;
}
