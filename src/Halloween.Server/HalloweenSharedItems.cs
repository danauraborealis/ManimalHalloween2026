using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;

namespace Manimal.Halloween2026.Server;

// the event's own items come from WTT-ContentBackport, which names them in english only. upstream's other
// languages go over that here, after every mod has added its text.
[Injectable(TypePriority = OnLoadOrder.PostLoad + 3)]
public sealed class HalloweenSharedItems(
    HalloweenConfig config,
    JsonUtil jsonUtil,
    TemplateTable templateTable,
    LocaleTable localeTable,
    ISptLogger<HalloweenSharedItems> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var path = config.DbPath("content", "sharedItems.json");
        var shared = jsonUtil.DeserializeFromFile<Shared>(path)
            ?? throw new InvalidDataException($"[Halloween2026] content file is missing or empty: {path}");

        var missing = shared.Items.Where(id => !templateTable.Items.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            logger.Error($"[Halloween2026] {missing.Count} of {shared.Items.Count} event items are not installed ({string.Join(", ", missing)}). "
                + $"They come with WTT-ContentBackport {ModIdentity.ContentBackportVersion} or newer, and the event quests cannot be finished without them");
        }

        var languages = 0;
        foreach (var (code, lazyLocale) in localeTable.Global)
        {
            if (!shared.Locales.TryGetValue(code, out var rows))
            {
                continue;
            }

            languages++;
            lazyLocale.AddTransformer(data =>
            {
                if (data is null)
                {
                    return data;
                }

                foreach (var (key, text) in rows)
                {
                    // a real translation from the item's own mod is left alone
                    if (!data.TryGetValue(key, out var current) || current == shared.English.GetValueOrDefault(key))
                    {
                        data[key] = text;
                    }
                }

                return data;
            });
        }

        logger.Info($"[Halloween2026] {shared.Items.Count - missing.Count} of {shared.Items.Count} event items come from WTT-ContentBackport, translated here into {languages} languages");
        return Task.CompletedTask;
    }

    private sealed class Shared
    {
        [JsonPropertyName("items")] public List<MongoId> Items { get; init; } = [];
        [JsonPropertyName("english")] public Dictionary<string, string> English { get; init; } = [];
        [JsonPropertyName("locales")] public Dictionary<string, Dictionary<string, string>> Locales { get; init; } = [];
    }
}
