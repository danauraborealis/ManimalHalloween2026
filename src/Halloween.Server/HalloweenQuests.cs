using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Utils;
using Path = System.IO.Path;

namespace Manimal.Halloween2026.Server;

// the 13 quests, plus keeping spt's own 2024 zombie event quests out of the way
[Injectable(TypePriority = OnLoadOrder.GameCallbacks - 2)]
public sealed class HalloweenQuests(
    HalloweenConfig config,
    CustomQuestService customQuestService,
    TemplateTable templateTable,
    LocaleTable localeTable,
    QuestConfig questConfig,
    ImageRouter imageRouter,
    HalloweenDailies dailies,
    HalloweenOperationalTasks operationalTasks,
    HalloweenTaskAccessPatch taskAccess,
    HalloweenTaskRewardPatch taskRewards,
    HalloweenTaskChangePatch taskChanges,
    JsonUtil jsonUtil,
    ISptLogger<HalloweenQuests> logger) : IOnLoad
{
    // 1.3's RDA-68 / RDA-78 gas grenades need an item class and bundles 4.1 lacks
    private static readonly string[] GasGrenades = ["6a9854e0788619fe8502a6b7", "6a9854955358fa10380b8509"];
    private const string CsGasGrenade = "6a5d6a5f4ed8c025a0a2cff0";
    private static readonly string[] ItemConditions = ["FindItem", "HandoverItem", "LeaveItemAtLocation"];

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (!config.Settings.QuestsEnabled)
        {
            logger.Info("[Halloween2026] quests disabled by config");
            return Task.CompletedTask;
        }

        var path = config.DbPath("quests", "quests.json");
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException($"[Halloween2026] quest data is missing or invalid: {path}");

        var questNodes = root["quests"]!.AsObject();
        foreach (var (id, quest) in questNodes)
        {
            FitToInstalledItems(id, quest!.AsObject());
        }

        var document = jsonUtil.Deserialize<QuestDocument>(root.ToJsonString())
            ?? throw new InvalidDataException($"[Halloween2026] quest data did not deserialize: {path}");

        if (document.Quests.Count == 0)
        {
            throw new InvalidDataException($"[Halloween2026] no quests were read from {path}");
        }

        // CreateQuest rejects a language the live table lacks, and tushonka ships a few
        var english = document.Locales["en"];
        var locales = localeTable.Global.Keys.ToDictionary(code => code, code => document.Locales.GetValueOrDefault(code) ?? english);

        Hide2024EventQuests(document.Quests.Keys);

        var registered = 0;
        foreach (var (id, quest) in document.Quests)
        {
            if (templateTable.Quests.ContainsKey(quest.Id))
            {
                logger.Warning($"[Halloween2026] quest {id} already exists, keeping the existing one");
                continue;
            }

            RegisterImage(quest);
            var result = customQuestService.CreateQuest(new NewQuestDetails
            {
                NewQuest = quest,
                Locales = locales,
                LockedToSide = null,
            });
            if (!result.Success)
            {
                throw new InvalidOperationException($"[Halloween2026] could not register quest {id}: {string.Join("; ", result.Errors)}");
            }

            if (config.Settings.QuestsOnlyDuringHalloween)
            {
                questConfig.EventQuests[quest.Id] = new EventQuestData
                {
                    Name = id,
                    Season = SeasonalEventType.Halloween,
                    StartTimestamp = 0,
                    EndTimestamp = 0,
                    Yearly = true,
                };
            }

            registered++;
        }

        LinkStartedWith(document.StartedWith);
        dailies.EnableOnce();
        if (operationalTasks.Register())
        {
            taskAccess.Enable();
            taskRewards.Enable();
            taskChanges.Enable();
        }

        logger.Info($"[Halloween2026] registered {registered} of {document.Quests.Count} event quests");
        return Task.CompletedTask;
    }

    // rewards and item objectives that name an item this install lacks are swapped or dropped, never left dangling
    private void FitToInstalledItems(string questId, JsonObject quest)
    {
        foreach (var (status, rewards) in quest["rewards"]!.AsObject())
        {
            var list = rewards!.AsArray();
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var reward = list[i]!.AsObject();
                if (reward["type"]?.GetValue<string>() != "Item" || reward["items"] is not JsonArray items)
                {
                    continue;
                }

                var missing = false;
                foreach (var item in items)
                {
                    var resolved = Resolve(item!["_tpl"]!.GetValue<string>());
                    if (resolved is null)
                    {
                        missing = true;
                        break;
                    }

                    item["_tpl"] = resolved;
                }

                if (missing)
                {
                    logger.Warning($"[Halloween2026] quest {questId}: dropped a {status} reward whose items are not installed");
                    list.RemoveAt(i);
                }
            }
        }

        var removed = new HashSet<string>();
        var sections = quest["conditions"]!.AsObject();
        foreach (var (section, conditions) in sections)
        {
            var list = conditions!.AsArray();
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var condition = list[i]!.AsObject();
                if (!ItemConditions.Contains(condition["conditionType"]?.GetValue<string>()) || condition["target"] is not JsonArray targets)
                {
                    continue;
                }

                var kept = new JsonArray();
                foreach (var target in targets)
                {
                    var resolved = Resolve(target!.GetValue<string>());
                    if (resolved is not null && !kept.Any(existing => existing!.GetValue<string>() == resolved))
                    {
                        kept.Add(resolved);
                    }
                }

                if (kept.Count > 0)
                {
                    condition["target"] = kept;
                    continue;
                }

                logger.Warning($"[Halloween2026] quest {questId}: dropped a {section} item objective, its item is not installed");
                removed.Add(condition["id"]!.GetValue<string>());
                list.RemoveAt(i);
            }
        }

        // an objective that was shown only alongside a dropped one stands on its own now
        foreach (var (_, conditions) in sections)
        {
            foreach (var condition in conditions!.AsArray())
            {
                if (condition!["parentId"] is JsonValue parent && removed.Contains(parent.GetValue<string>()))
                {
                    condition["parentId"] = "";
                }
            }
        }
    }

    private string? Resolve(string tpl)
    {
        if (templateTable.Items.ContainsKey(new MongoId(tpl)))
        {
            return tpl;
        }

        return GasGrenades.Contains(tpl) && templateTable.Items.ContainsKey(new MongoId(CsGasGrenade)) ? CsGasGrenade : null;
    }

    // season None is what hides a quest for good: spt only shows those with ShowNonSeasonalEventQuests on
    private void Hide2024EventQuests(IEnumerable<string> ours)
    {
        if (!config.Settings.Hide2024EventQuests)
        {
            return;
        }

        var keep = ours.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hidden = new List<string>();
        foreach (var (id, data) in questConfig.EventQuests)
        {
            if (data.Season == SeasonalEventType.Halloween && !keep.Contains(id.ToString()))
            {
                data.Season = SeasonalEventType.None;
                data.Yearly = false;
                hidden.Add(data.Name);
            }
        }

        logger.Info($"[Halloween2026] hid {hidden.Count} earlier Halloween event quests: {string.Join(", ", hidden)}");
        if (questConfig.ShowNonSeasonalEventQuests)
        {
            logger.Warning("[Halloween2026] quest.json has showNonSeasonalEventQuests on, so the hidden 2024 quests are still offered");
        }
    }

    private void RegisterImage(Quest quest)
    {
        var image = quest.Image;
        if (string.IsNullOrWhiteSpace(image))
        {
            return;
        }

        var relative = image.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (relative.StartsWith("files" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            relative = relative[("files".Length + 1)..];
        }

        // shared icons that spt already ships are not in the package, and need no route
        var local = Path.Combine(config.ModPath, "images", relative);
        if (File.Exists(local))
        {
            imageRouter.AddRoute(Path.ChangeExtension(image, null), local);
        }
    }

    // only later 4.1.6 builds have this; without it the paired quest is accepted by hand
    private void LinkStartedWith(Dictionary<string, string> pairs)
    {
        var startedWith = questConfig.GetType().GetProperty("StartedWithQuests")?.GetValue(questConfig) as IDictionary<MongoId, MongoId>;
        if (startedWith is null)
        {
            logger.Info("[Halloween2026] this SPT build has no StartedWithQuests, paired quests are accepted separately");
            return;
        }

        foreach (var (starts, with) in pairs)
        {
            startedWith[new MongoId(starts)] = new MongoId(with);
        }
    }

    private sealed class QuestDocument
    {
        [JsonPropertyName("quests")]
        public Dictionary<string, Quest> Quests { get; init; } = new(StringComparer.Ordinal);

        [JsonPropertyName("locales")]
        public Dictionary<string, Dictionary<string, string>> Locales { get; init; } = new(StringComparer.Ordinal);

        [JsonPropertyName("startedWith")]
        public Dictionary<string, string> StartedWith { get; init; } = new(StringComparer.Ordinal);
    }
}
