using System;
using System.Reflection;
using Audio.AmbientSubsystem;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Halloween2026.Client
{
    // what else follows from the power being out: nothing electrical hums or announces, and the
    // keycard locks are dead. retail's event scene switches its transformer hums off the same way.
    internal static class LabsPowerDown
    {
        // native loops that need no power
        private static readonly string[] Kept = { "wind", "waterpipe" };

        // with the event's own ambience running, every native sound goes: it has its own winds and tones
        internal static void Apply(bool eventAmbience)
        {
            if (Plugin.LabsLightsOut.Value || eventAmbience)
            {
                Silence(eventAmbience);
            }

            if (Plugin.LabsDoorsUnlocked.Value)
            {
                UnlockKeycardDoors();
            }
        }

        // the map's own placed sounds: the general bed, machine loops, the announcer speakers
        private static void Silence(bool all)
        {
            try
            {
                var muted = 0;
                var kept = 0;
                for (var s = 0; s < SceneManager.sceneCount; s++)
                {
                    var scene = SceneManager.GetSceneAt(s);
                    if (!scene.isLoaded || !scene.name.StartsWith(LabsLayer.NativePrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var roots = scene.GetRootGameObjects();
                    for (var r = 0; r < roots.Length; r++)
                    {
                        var sources = roots[r].GetComponentsInChildren<AudioSource>(true);
                        for (var i = 0; i < sources.Length; i++)
                        {
                            var source = sources[i];
                            if (!all)
                            {
                                // the game's ambient players (its halloween tones among them) pick clips at runtime
                                if (!source.clip || !source.playOnAwake || source.GetComponent<BaseAmbientSoundPlayer>())
                                {
                                    continue;
                                }

                                if (IsKept(source.clip.name))
                                {
                                    kept++;
                                    continue;
                                }
                            }

                            // mute, not disable: the ambient system's distance culling restarts its sources
                            source.mute = true;
                            source.Stop();
                            muted++;
                        }
                    }
                }

                Plugin.Log.LogInfo(all
                    ? $"[Halloween2026] Labs power is out: all {muted} of the map's own sound sources muted, the event's ambience takes over"
                    : $"[Halloween2026] Labs power is out: {muted} placed sounds muted, {kept} left (wind, pipes)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] silencing the Labs machine sounds failed: {e}");
            }
        }

        private static bool IsKept(string clip)
        {
            for (var i = 0; i < Kept.Length; i++)
            {
                if (clip.IndexOf(Kept[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static void UnlockKeycardDoors()
        {
            try
            {
                var relock = AccessTools.Field(typeof(KeycardDoor), "_lockOnShut");
                var doors = UnityEngine.Object.FindObjectsOfType<KeycardDoor>();
                var unlocked = 0;
                for (var i = 0; i < doors.Length; i++)
                {
                    var door = doors[i];
                    if (!door)
                    {
                        continue;
                    }

                    // some relock every time they shut
                    if (relock != null)
                    {
                        relock.SetValue(door, false);
                    }

                    if (door.DoorState != EDoorState.Locked)
                    {
                        continue;
                    }

                    door.DoorState = EDoorState.Shut;
                    door.CurrentAngle = door.GetAngle(EDoorState.Shut);
                    unlocked++;
                }

                Plugin.Log.LogInfo($"[Halloween2026] {unlocked} of {doors.Length} keycard doors unlocked");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] unlocking the keycard doors failed: {e}");
            }
        }
    }
}
