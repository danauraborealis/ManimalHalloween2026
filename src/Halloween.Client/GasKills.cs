using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using EFT;
using EFT.Quests;
using HarmonyLib;
using Newtonsoft.Json;

namespace Manimal.Halloween2026.Client
{
    // First Results and The Final Push only count infected that have been inside the right vaccine gas.
    // retail does that with a field the 4.1 kill condition lacks, so the kill in progress is remembered
    // here and the two conditions ask ManimalCSGas whether that victim was dosed.
    internal static class GasKills
    {
        private const string ApiType = "Manimal.CSGas.Api.Gas";

        // kill condition id -> buff the victim must carry (plugin-data/kill-buffs.json)
        private static Dictionary<string, string> _required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static MethodInfo _wasGassed;
        private static bool _looked;
        private static bool _warned;
        private static string _victim;

        internal static void Install(Harmony harmony)
        {
            try
            {
                var path = Path.Combine(Plugin.PluginDir, "plugin-data", "kill-buffs.json");
                if (File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        _required = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
                    }
                }

                if (_required.Count == 0)
                {
                    return;
                }

                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                var kill = AccessTools.Method(typeof(BaseStatisticsManager), nameof(BaseStatisticsManager.OnEnemyKill));
                var test = AccessTools.Method(typeof(ConditionHitProgressChecker), nameof(ConditionHitProgressChecker.Test), new[] { typeof(object) });
                if (kill == null || test == null)
                {
                    Plugin.Log.LogError("[Halloween2026] kill hooks not found, gassed-kill objectives count any infected kill");
                    return;
                }

                harmony.Patch(kill, new HarmonyMethod(typeof(GasKills).GetMethod(nameof(KillBegins), flags)),
                    finalizer: new HarmonyMethod(typeof(GasKills).GetMethod(nameof(KillEnds), flags)));
                harmony.Patch(test, postfix: new HarmonyMethod(typeof(GasKills).GetMethod(nameof(KillTested), flags)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] gassed-kill objectives could not be wired up: {e}");
            }
        }

        private static void KillBegins(string playerProfileId) => _victim = playerProfileId;

        private static void KillEnds() => _victim = null;

        private static void KillTested(ConditionHitProgressChecker __instance, ref bool __result)
        {
            if (!__result || _victim == null || __instance.Condition == null)
            {
                return;
            }

            if (_required.TryGetValue(__instance.Condition.id.ToString(), out var buff))
            {
                __result = WasGassed(_victim, buff);
            }
        }

        // without ManimalCSGas 2.0.1 nobody can be dosed, so the objective falls back to any infected kill
        private static bool WasGassed(string profileId, string buff)
        {
            if (!_looked)
            {
                _looked = true;
                var api = AccessTools.TypeByName(ApiType);
                _wasGassed = api == null ? null : AccessTools.Method(api, "WasGassed", new[] { typeof(string), typeof(string) });
            }

            if (_wasGassed == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Log.LogWarning("[Halloween2026] ManimalCSGas 2.0.1 or later is not installed: gassed-kill objectives count any infected kill");
                }

                return true;
            }

            try
            {
                return (bool)_wasGassed.Invoke(null, new object[] { profileId, buff });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Halloween2026] asking ManimalCSGas about a kill failed: {e.Message}");
                return true;
            }
        }
    }
}
