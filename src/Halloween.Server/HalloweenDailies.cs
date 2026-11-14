using System.Reflection;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace Manimal.Halloween2026.Server;

// the exchange dailies are ordinary quests. this forgets a completed one a day later, just before the
// profile goes to the client, so it is on offer again at the next login or raid end.
[Injectable(InjectionType.Singleton)]
public sealed class HalloweenDailies : AbstractPatch
{
    private static HalloweenDailies? _instance;

    private readonly SaveServer _saveServer;
    private readonly ISptLogger<HalloweenDailies> _logger;
    private readonly HashSet<MongoId> _quests = [];
    private double _repeatSeconds = 24 * 3600;
    private bool _enabled;

    public HalloweenDailies(SaveServer saveServer, HalloweenConfig config, JsonUtil jsonUtil, ISptLogger<HalloweenDailies> logger)
        : base($"{ModIdentity.Guid}.dailies")
    {
        _saveServer = saveServer;
        _logger = logger;
        _instance = this;

        var path = config.DbPath("content", "dailies.json");
        var file = File.Exists(path) ? jsonUtil.DeserializeFromFile<DailyFile>(path) : null;
        if (file is not null)
        {
            _repeatSeconds = file.RepeatHours * 3600;
            foreach (var id in file.Quests)
            {
                _quests.Add(new MongoId(id));
            }
        }
    }

    internal void EnableOnce()
    {
        if (_enabled || _quests.Count == 0)
        {
            return;
        }

        Enable();
        _enabled = true;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(ProfileController).GetMethod(
                   nameof(ProfileController.GetCompleteProfile),
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                   binder: null,
                   types: [typeof(MongoId)],
                   modifiers: null)
               ?? throw new MissingMethodException("[Halloween2026] ProfileController.GetCompleteProfile(MongoId) is unavailable.");
    }

    [PatchPrefix]
    private static void Prefix(MongoId sessionId)
    {
        try
        {
            _instance?.Reopen(sessionId);
        }
        catch (Exception e)
        {
            _instance?._logger.Warning($"[Halloween2026] re-opening the exchange dailies failed: {e.Message}");
        }
    }

    private void Reopen(MongoId sessionId)
    {
        var pmc = _saveServer.GetProfile(sessionId).CharacterData?.PmcData;
        if (pmc?.Quests is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var due = pmc.Quests
            .Where(quest => _quests.Contains(quest.QId)
                && quest.Status == QuestStatusEnum.Success
                && quest.StatusTimers.TryGetValue(QuestStatusEnum.Success, out var done)
                && now - done >= _repeatSeconds)
            .ToList();
        foreach (var quest in due)
        {
            pmc.Quests.Remove(quest);
            // hand-over progress is kept per condition; left behind, the next run would start already done
            foreach (var counter in (pmc.TaskConditionCounters ?? []).Where(pair => pair.Value.SourceId == quest.QId).ToList())
            {
                pmc.TaskConditionCounters!.Remove(counter.Key);
            }
        }
    }

    private sealed class DailyFile
    {
        [JsonPropertyName("repeatHours")] public double RepeatHours { get; init; } = 24;
        [JsonPropertyName("quests")] public List<string> Quests { get; init; } = [];
    }
}
