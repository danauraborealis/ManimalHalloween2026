using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;

namespace Manimal.Halloween2026.Server;

public sealed class QuestLootSet
{
    [JsonPropertyName("forced")] public List<Spawnpoint> Forced { get; set; } = [];
    [JsonPropertyName("spawnpoints")] public List<Spawnpoint> Spawnpoints { get; set; } = [];
}

// where the blood samples, the vaccine batch and the secret-exit items lie.
// "laboratory@halloween2026" is not installed here: InfectedLabs adds it to the variant only.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.Preload + 31)]
public sealed class HalloweenQuestLoot(
    HalloweenConfig config,
    JsonUtil jsonUtil,
    LocationTable locationTable,
    TemplateTable templateTable,
    ISptLogger<HalloweenQuestLoot> logger) : IOnLoad
{
    public const string LabsVariantKey = "laboratory@halloween2026";

    private Dictionary<string, QuestLootSet> _sets = new(StringComparer.OrdinalIgnoreCase);

    public QuestLootSet? LabsVariant => _sets.GetValueOrDefault(LabsVariantKey);

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var path = config.DbPath("questLoot", "questLoot.json");
        var sets = jsonUtil.DeserializeFromFile<Dictionary<string, QuestLootSet>>(path)
            ?? throw new InvalidDataException($"[Halloween2026] quest loot is missing or empty: {path}");
        _sets = new Dictionary<string, QuestLootSet>(sets, StringComparer.OrdinalIgnoreCase);

        foreach (var set in _sets.Values)
        {
            set.Forced.RemoveAll(point => !Installed(point));
            set.Spawnpoints.RemoveAll(point => !Installed(point));
        }

        if (!config.Settings.QuestsEnabled)
        {
            return Task.CompletedTask;
        }

        var maps = 0;
        foreach (var (map, set) in _sets)
        {
            if (map.Contains('@'))
            {
                continue;
            }

            var location = locationTable.GetLocation(map);
            if (location?.LooseLoot is null)
            {
                logger.Warning($"[Halloween2026] no loose loot table for '{map}', its quest items will not spawn");
                continue;
            }

            location.LooseLoot.AddTransformer(loot => loot is null ? loot : Add(loot, set));
            maps++;
        }

        logger.Info($"[Halloween2026] quest item points prepared for {maps} maps");
        return Task.CompletedTask;
    }

    internal static LooseLoot Add(LooseLoot loot, QuestLootSet set)
    {
        loot.SpawnpointsForced = Merge(loot.SpawnpointsForced, set.Forced);
        loot.Spawnpoints = Merge(loot.Spawnpoints, set.Spawnpoints);
        return loot;
    }

    // keyed on Template.Id so a second pass, or another mod shipping the same point, adds nothing
    internal static List<Spawnpoint> Merge(IEnumerable<Spawnpoint>? existing, List<Spawnpoint> extra)
    {
        var merged = (existing ?? []).ToList();
        var ids = merged.Select(point => point.Template?.Id).ToHashSet();
        foreach (var point in extra)
        {
            if (ids.Add(point.Template?.Id))
            {
                merged.Add(point);
            }
        }

        return merged;
    }

    private bool Installed(Spawnpoint point)
    {
        var items = point.Template?.Items;
        return items is not null && items.All(item => templateTable.Items.ContainsKey(item.Template));
    }
}
