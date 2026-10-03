using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace Findr;

public sealed class FileEntry
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public string Type { get; init; } = "";
    public string Size { get; init; } = "";
    public DateTime Modified { get; init; }
    public string Icon { get; init; } = "📄";
    public string? IconResource { get; init; }
    public string? IconPath { get; init; }
}

public sealed class BrowserTab
{
    public string Path { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string Title => Path == "PC://" ? "PC" : string.IsNullOrWhiteSpace(Path) || Path == "HOME://" || !Directory.Exists(Path) ? "ホーム" : new DirectoryInfo(Path).Name;
    public Stack<string> Back { get; } = new();
    public Stack<string> Forward { get; } = new();
}

public sealed class AppState
{
    public List<string> Favorites { get; set; } = [];
    public List<string> Recent { get; set; } = [];
    public Dictionary<string, string> FolderIcons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ViewModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class StateStore
{
    private static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Findr");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppState Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath)) ?? new AppState();
        }
        catch { }
        return new AppState();
    }

    public static void Save(AppState state)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, Options));
        }
        catch { }
    }
}
