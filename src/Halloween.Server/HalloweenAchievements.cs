using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using WttCommonFacade = WTTServerCommonLib.WTTServerCommonLib;

namespace Manimal.Halloween2026.Server;

// Shields Up!, Triumph of Science and Groovy, through WTT CommonLib like Scourge's.
// the first two are quest rewards (in quests.json); Groovy is its own 300-kill counter.
[Injectable(TypePriority = OnLoadOrder.GameCallbacks - 1)]
public sealed class HalloweenAchievements(
    HalloweenConfig config,
    WttCommonFacade commonLib,
    TemplateTable templateTable,
    JsonUtil jsonUtil,
    HalloweenAchievementRewardPatch rewardPatch,
    ISptLogger<HalloweenAchievements> logger) : IOnLoad
{
    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (!config.Settings.QuestsEnabled)
        {
            return;
        }

        var path = config.DbPath("CustomAchievements", "Achievements", "halloween2026.json");
        var ours = jsonUtil.DeserializeFromFile<List<Achievement>>(path)
            ?? throw new InvalidDataException($"[Halloween2026] achievement data is missing or invalid: {path}");

        // commonlib appends the whole folder, so it is all or nothing
        if (ours.Any(achievement => templateTable.Achievements.Any(existing => existing.Id == achievement.Id)))
        {
            logger.Warning("[Halloween2026] an event achievement is already registered, leaving the catalog as it is");
            return;
        }

        await commonLib.CustomAchievementService.CreateCustomAchievements(Assembly.GetExecutingAssembly());

        var registered = ours.Count(achievement => templateTable.Achievements.Any(existing => existing.Id == achievement.Id));
        if (registered != ours.Count)
        {
            throw new InvalidOperationException($"[Halloween2026] expected {ours.Count} achievements after the CommonLib load, found {registered}");
        }

        logger.Info($"[Halloween2026] registered {registered} event achievements");

        // without it the two quest-unlocked achievements never hand over their poster
        try
        {
            if (rewardPatch.Arm(ours))
            {
                rewardPatch.Enable();
            }
            else
            {
                logger.Warning("[Halloween2026] achievement item rewards cannot be mailed on this SPT build, the poster will not arrive");
            }
        }
        catch (Exception e)
        {
            logger.Warning($"[Halloween2026] achievement item rewards will not be mailed: {e.Message}");
        }
    }
}
