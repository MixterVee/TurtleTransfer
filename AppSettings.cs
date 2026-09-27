using System.Text.Json;

namespace CampTransfer;

public sealed class AppSettings
{
    public string LastDestination { get; set; } = "";
    public string SpeedLimitText { get; set; } = "2 Mbps";
    public string TransferOperation { get; set; } = "Copy";
    public string WhenFinishedAction { get; set; } = "Do nothing";
    public bool TransferAnimationEnabled { get; set; } = true;
    public List<string> RecentDestinations { get; set; } = [];
    public List<NamedDestination> NamedDestinations { get; set; } = [];

    private static readonly string AppDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CampTransfer");

    private static readonly string SettingsPath = Path.Combine(AppDirectory, "settings.json");
    private static readonly string QueuePath = Path.Combine(AppDirectory, "queue.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            settings.RecentDestinations ??= [];
            settings.NamedDestinations ??= [];
            settings.TransferOperation = NormalizeOperation(settings.TransferOperation);
            settings.LastDestination = PathHelpers.NormalizeDestinationPath(settings.LastDestination);
            settings.RecentDestinations = settings.RecentDestinations
                .Select(PathHelpers.NormalizeDestinationPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var destination in settings.NamedDestinations)
                destination.Path = PathHelpers.NormalizeDestinationPath(destination.Path);
            if (string.IsNullOrWhiteSpace(settings.WhenFinishedAction))
                settings.WhenFinishedAction = "Do nothing";
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDirectory);
        TransferOperation = NormalizeOperation(TransferOperation);
        LastDestination = PathHelpers.NormalizeDestinationPath(LastDestination);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static List<TransferItem> LoadQueue()
    {
        try
        {
            if (!File.Exists(QueuePath)) return [];
            var items = JsonSerializer.Deserialize<List<TransferItem>>(File.ReadAllText(QueuePath)) ?? [];
            foreach (var item in items)
            {
                item.Operation = NormalizeOperation(item.Operation);
                item.DestinationRoot = PathHelpers.NormalizeDestinationPath(item.DestinationRoot);
                item.ResetRuntimeState();
            }
            return items;
        }
        catch
        {
            return [];
        }
    }

    public static void SaveQueue(IEnumerable<TransferItem> items)
    {
        Directory.CreateDirectory(AppDirectory);
        foreach (var item in items)
            item.DestinationRoot = PathHelpers.NormalizeDestinationPath(item.DestinationRoot);
        File.WriteAllText(QueuePath, JsonSerializer.Serialize(items.ToList(), JsonOptions));
    }

    private static string NormalizeOperation(string? operation)
        => string.Equals(operation, "Move", StringComparison.OrdinalIgnoreCase) ? "Move" : "Copy";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
}

public sealed class NamedDestination
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    public override string ToString() => $"{Name} — {Path}";
}
