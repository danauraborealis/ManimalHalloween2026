using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace Manimal.Halloween2026.Client
{
    [BepInPlugin(ModIdentity.Guid, ModIdentity.ClientName, ModIdentity.Version)]
    // hard: it also guarantees MapVariants' Awake ran, which is what makes registering from ours legal
    [BepInDependency(ModIdentity.MapVariantsGuid, ModIdentity.MapVariantsVersion)]
    // soft: only so its api type exists before GasKills looks for it
    [BepInDependency("com.manimal.csgas", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static string PluginDir;
        internal static ConfigEntry<bool> LabsLightsOut;
        internal static ConfigEntry<bool> LabsDoorsUnlocked;
        internal static ConfigEntry<bool> LabsEventAmbience;
        internal static ConfigEntry<bool> LighthouseCommsRoom;

        private void Awake()
        {
            Log = Logger;
            PluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            LabsLightsOut = Config.Bind("Infected Labs", "Lights out", true,
                "The Halloween Labs runs dark, as in the event: only the event's own lamps and your flashlight. The map's machine hums, general ambience and announcer go quiet with it.");
            LabsDoorsUnlocked = Config.Bind("Infected Labs", "Keycard doors unlocked", true,
                "With the power out, every keycard door on the Halloween Labs starts unlocked.");
            LabsEventAmbience = Config.Bind("Infected Labs", "Event ambience", true,
                "The Halloween Labs plays the event's own ambience (creaks, knocks, drips, distant screams, wind in the pipes) in place of the map's.");
            LighthouseCommsRoom = Config.Bind("Lighthouse", "Water treatment comms room", true,
                "The room the event dressed in the water treatment plant: the dead Black Division operator, the comms post and its radio.");
            var harmony = new HarmonyLib.Harmony(ModIdentity.Guid);
            EventRaid.Install(harmony);
            ScientistBodies.Install(harmony);
            LabsLayer.Install();
            LighthouseRoom.Install();
            GasKills.Install(harmony);
            LabsBlackout.Install(harmony);
            EventHandles.Install(harmony);
            ZombieVoices.Register();
            ZombieGore.Install(harmony, Config);
            Screamers.Install(Config);

            try
            {
                // short names alone: MapVariants composes "The Lab - Halloween 2026" itself
                var registered = MapVariantsApi.Register(
                    new[] { Api.Labs.LocationId },
                    "The Lab",
                    "Halloween 2026",
                    "Vanilla",
                    Tile("labs_halloween.png"),
                    Tile("labs_vanilla.png"),
                    InfectedLabs.OnVariant,
                    null);

                if (registered)
                {
                    MapVariantsApi.Subscribe(InfectedLabs.OnResolved);
                    Log.LogInfo("[Halloween2026] Labs registered with MapVariants");
                }
                else
                {
                    Log.LogError("[Halloween2026] MapVariants refused the registration, Labs has no Halloween tile. Look for a [MV] line above.");
                }
            }
            catch (Exception e)
            {
                Log.LogError($"[Halloween2026] registering with MapVariants failed: {e}");
            }
        }

        private void OnDestroy()
        {
            ZombieGore.Shutdown();
        }

        // absolute: MapVariants resolves a relative tile against its own folder
        private static string Tile(string file)
        {
            var path = Path.Combine(PluginDir, "plugin-data", "ui", file);
            return File.Exists(path) ? path : null;
        }
    }
}
