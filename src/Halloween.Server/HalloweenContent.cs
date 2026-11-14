using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Items;
using SPTarkov.Server.Core.Utils;

namespace Manimal.Halloween2026.Server;

// event items no installed mod provides (none today: WTT-ContentBackport has them all, see HalloweenSharedItems)
// and the event's interface text, before anything that validates quests or loot against the item table
[Injectable(TypePriority = OnLoadOrder.Preload + 30)]
public sealed class HalloweenContent(
    HalloweenConfig config,
    JsonUtil jsonUtil,
    TemplateTable templateTable,
    LocaleTable localeTable,
    ItemBaseClassService itemBaseClassService,
    SeasonalEventConfig seasonalEventConfig,
    ISptLogger<HalloweenContent> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var items = Read<Dictionary<MongoId, TemplateItem>>("items.json");
        var added = new List<MongoId>();
        foreach (var (id, item) in items)
        {
            if (templateTable.Items.ContainsKey(id))
            {
                logger.Info($"[Halloween2026] item '{id}' is already installed, keeping it");
                continue;
            }

            if (!templateTable.Items.ContainsKey(item.Parent))
            {
                logger.Warning($"[Halloween2026] item '{id}' skipped, its parent '{item.Parent}' is not in this database");
                continue;
            }

            templateTable.Items[id] = item;
            added.Add(id);
        }

        foreach (var entry in Read<List<HandbookItem>>("handbook.json"))
        {
            if (templateTable.Items.ContainsKey(entry.Id) && templateTable.Handbook.Items.All(existing => existing.Id != entry.Id))
            {
                templateTable.Handbook.Items.Add(entry);
            }
        }

        foreach (var (id, price) in Read<Dictionary<MongoId, double>>("prices.json"))
        {
            templateTable.Prices.TryAdd(id, price);
        }

        AddLocales(localeTable, Read<Dictionary<string, Dictionary<string, string>>>("locales.json"));

        foreach (var id in added)
        {
            itemBaseClassService.AddItemToCache(id);
        }

        HangablePosters(added);

        if (config.Settings.DisableBuiltInHalloweenEvent)
        {
            var disabled = 0;
            foreach (var seasonalEvent in seasonalEventConfig.Events)
            {
                if (seasonalEvent.Type == SeasonalEventType.Halloween && seasonalEvent.Enabled)
                {
                    seasonalEvent.Enabled = false;
                    disabled++;
                }
            }

            logger.Info($"[Halloween2026] turned off {disabled} built-in Halloween event window(s)");
        }

        if (items.Count > 0)
        {
            logger.Info($"[Halloween2026] {added.Count} of {items.Count} event items added");
        }

        return Task.CompletedTask;
    }

    // the hideout's poster slots list every flyer they take by id, not by class
    private void HangablePosters(List<MongoId> added)
    {
        var posters = added.Where(id => templateTable.Items[id].Parent == Flyer).ToList();
        if (posters.Count == 0)
        {
            return;
        }

        var slots = 0;
        foreach (var item in templateTable.Items.Values)
        {
            foreach (var slot in item.Properties?.Slots ?? [])
            {
                if (slot.Name is null || !slot.Name.StartsWith("Poster_", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var filter in slot.Properties?.Filters ?? [])
                {
                    if (filter.Filter is null)
                    {
                        continue;
                    }

                    foreach (var poster in posters)
                    {
                        filter.Filter.Add(poster);
                    }

                    slots++;
                }
            }
        }

        logger.Info($"[Halloween2026] {posters.Count} event poster(s) accepted by {slots} hideout poster slots");
    }

    private static readonly MongoId Flyer = new("6759673c76e93d8eb20b2080");

    // missing languages fall back to english, and nothing already present is overwritten
    internal static void AddLocales(LocaleTable localeTable, Dictionary<string, Dictionary<string, string>> locales)
    {
        if (!locales.TryGetValue("en", out var english))
        {
            return;
        }

        foreach (var (code, lazyLocale) in localeTable.Global)
        {
            var localized = locales.GetValueOrDefault(code) ?? english;
            lazyLocale.AddTransformer(data =>
            {
                if (data is null)
                {
                    return data;
                }

                foreach (var (key, value) in english)
                {
                    data.TryAdd(key, localized.GetValueOrDefault(key) ?? value);
                }

                return data;
            });
        }
    }

    private T Read<T>(string file)
    {
        var path = config.DbPath("content", file);
        return jsonUtil.DeserializeFromFile<T>(path)
            ?? throw new InvalidDataException($"[Halloween2026] content file is missing or empty: {path}");
    }
}
