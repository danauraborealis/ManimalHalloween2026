using System.Reflection;
using System.Text.Json;
using SPTarkov.DI.Annotations;

namespace Manimal.Halloween2026.Server;

public sealed class HalloweenSettings
{
    public bool QuestsEnabled { get; set; } = true;
    // false = the quest line is always offered, like the labs tile itself
    public bool QuestsOnlyDuringHalloween { get; set; }
    public bool Hide2024EventQuests { get; set; } = true;
    public bool DisableBuiltInHalloweenEvent { get; set; }
    public bool InfectedLabsEnabled { get; set; } = true;
    public bool InfectedFactoryEnabled { get; set; } = true;
    // false keeps Labs' normal exits open beside the two event ones
    public bool LabsEventExtracts { get; set; } = true;
    public bool OperationalTasksEnabled { get; set; } = true;
    public ZombieSettings Zombies { get; set; } = new();
}

[Injectable(InjectionType.Singleton)]
public sealed class HalloweenConfig
{
    public string ModPath { get; } = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
        ?? throw new InvalidOperationException("[Halloween2026] server assembly location is unavailable.");

    public HalloweenSettings Settings { get; }

    public HalloweenConfig()
    {
        var path = Path.Combine(ModPath, "config.json");
        Settings = File.Exists(path)
            ? JsonSerializer.Deserialize<HalloweenSettings>(File.ReadAllText(path), new JsonSerializerOptions
              {
                  PropertyNameCaseInsensitive = true,
                  ReadCommentHandling = JsonCommentHandling.Skip,
                  AllowTrailingCommas = true,
              }) ?? new HalloweenSettings()
            : new HalloweenSettings();
    }

    public string DbPath(params string[] parts) => Path.Combine([ModPath, "db", .. parts]);
}
