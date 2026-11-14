using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;

namespace Manimal.Halloween2026.Client
{
    // what the look-at ray finds on a set piece's button or handle
    internal sealed class EventHandle : InteractableObject
    {
        internal string Label = "Open";
        internal Action<Player> Pressed;
    }

    // the event's set pieces are driven by trigger graphs the rip drops. each gets a handle of ours on
    // its switch collider, and the game is told what action that handle offers.
    internal static class EventHandles
    {
        private const string F1Tpl = "5710c24ad2720bc3458b45a3";

        internal static void Install(Harmony harmony)
        {
            try
            {
                // the dispatcher throws on a type it doesnt know, so ours is answered before it runs
                var target = AccessTools.Method(typeof(InteractionContextHelper), "GetAvailableActions",
                    new[] { typeof(GamePlayerOwner), typeof(IInteractive) });
                if (target == null)
                {
                    Plugin.Log.LogError("[Halloween2026] GetAvailableActions(IInteractive) was not found, the event's drawers and elevators cannot be used");
                    return;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(typeof(EventHandles).GetMethod(nameof(Actions), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] patching the interaction dispatcher failed: {e}");
            }
        }

        private static bool Actions(ref AvailableInteractionState __result, GamePlayerOwner owner, IInteractive interactive)
        {
            var handle = interactive as EventHandle;
            if (!handle)
            {
                return true;
            }

            __result = new AvailableInteractionState();
            var pressed = handle.Pressed;
            if (pressed != null && handle.enabled)
            {
                var action = new InteractionAction { Name = handle.Label.Localized(null), Action = () => pressed(owner.Player) };
                __result.Actions = new List<InteractionAction> { action };
            }

            return false;
        }

        internal static EventHandle Attach(Collider collider, string label, Action<Player> pressed)
        {
            collider.enabled = true;
            var handle = collider.gameObject.GetComponent<EventHandle>();
            if (!handle)
            {
                handle = collider.gameObject.AddComponent<EventHandle>();
            }

            handle.Label = label;
            handle.Pressed = pressed;
            return handle;
        }

        // the game's own grenade blast: damage and fragments. sound and flash are the set piece's own.
        internal static void Explode(Vector3 position, Player opener)
        {
            try
            {
                var world = Singleton<GameWorld>.Instance;
                var factory = Singleton<ItemFactory>.Instance;
                var grenade = factory.CreateItem(factory.NextId, F1Tpl, null);
                var explosive = grenade as IExplosiveItem;
                if (explosive == null)
                {
                    Plugin.Log.LogWarning("[Halloween2026] the F-1 is not an explosive item on this build, the grenade is a dud");
                    return;
                }

                Grenade.Explosion(null, explosive, position, opener ? opener.ProfileId : null, world.SharedBallisticsCalculator,
                    grenade, Vector3.up * 0.08f, false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] a set piece's grenade failed: {e}");
            }
        }
    }

    // the layer carries one silent source per clip. set pieces play copies of them, by retail's table in
    // plugin-data/labs/sounds.json; the ambience borrows the clips.
    internal static class LayerSounds
    {
        private const string Holder = "Halloween2026_Sounds";
        private const string MixerGroup = "TechnicalSounds";

        private struct Row
        {
            public string Trigger;
            public string Clip;
            public float Volume;
        }

        private static readonly Dictionary<string, AudioSource> Templates = new Dictionary<string, AudioSource>();
        private static readonly Dictionary<string, List<Row>> Pieces = new Dictionary<string, List<Row>>();
        private static AudioMixerGroup _group;
        private static bool _groupLooked;
        private static bool _tableLoaded;

        internal static void Collect(Scene scene)
        {
            Templates.Clear();
            _group = null;
            _groupLooked = false;
            var roots = scene.GetRootGameObjects();
            for (var r = 0; r < roots.Length; r++)
            {
                if (roots[r].name != Holder)
                {
                    continue;
                }

                var sources = roots[r].GetComponentsInChildren<AudioSource>(true);
                for (var i = 0; i < sources.Length; i++)
                {
                    Templates[sources[i].gameObject.name] = sources[i];
                }
            }

            LoadTable();
        }

        private static void LoadTable()
        {
            if (_tableLoaded)
            {
                return;
            }

            _tableLoaded = true;
            var path = Path.Combine(Plugin.PluginDir, "plugin-data", "labs", "sounds.json");
            if (!File.Exists(path))
            {
                return;
            }

            foreach (var piece in JObject.Parse(File.ReadAllText(path))["pieces"] ?? new JArray())
            {
                var name = (string)piece["name"];
                if (name == null || Pieces.ContainsKey(name))
                {
                    continue;
                }

                var rows = new List<Row>();
                foreach (var sound in piece["sounds"] ?? new JArray())
                {
                    rows.Add(new Row { Trigger = (string)sound["trigger"], Clip = (string)sound["clip"], Volume = (float)sound["volume"] });
                }

                Pieces[name] = rows;
            }
        }

        // every sound retail plays on that step of that set piece
        internal static void PlayStep(string piece, string trigger, Vector3 position)
        {
            // pieces of one kind share a table; their scene names differ only by a counter
            List<Row> rows = null;
            foreach (var pair in Pieces)
            {
                if (piece.StartsWith(pair.Key, StringComparison.Ordinal))
                {
                    rows = pair.Value;
                    break;
                }
            }

            for (var i = 0; rows != null && i < rows.Count; i++)
            {
                if (rows[i].Trigger == trigger)
                {
                    Play(rows[i].Clip, position, rows[i].Volume);
                }
            }
        }

        private static void Play(string clip, Vector3 position, float volume)
        {
            try
            {
                if (!Templates.TryGetValue(clip, out var template) || !template || !template.clip)
                {
                    return;
                }

                var copy = UnityEngine.Object.Instantiate(template.gameObject, position, Quaternion.identity, template.transform.parent);
                var source = copy.GetComponent<AudioSource>();
                source.enabled = true;
                source.volume = Mathf.Clamp01(volume);
                // without a group the sound skips the game's volume slider and its effects
                source.outputAudioMixerGroup = Group();
                source.Play();
                UnityEngine.Object.Destroy(copy, template.clip.length + 0.5f);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Halloween2026] sound '{clip}' failed: {e.Message}");
            }
        }

        private static AudioMixerGroup Group()
        {
            if (_groupLooked)
            {
                return _group;
            }

            _groupLooked = true;
            _group = FindGroup(MixerGroup);
            if (!_group)
            {
                Plugin.Log.LogWarning($"[Halloween2026] no '{MixerGroup}' mixer group loaded, set piece sounds play unmixed");
            }

            return _group;
        }

        internal static AudioMixerGroup FindGroup(string name)
        {
            var groups = Resources.FindObjectsOfTypeAll<AudioMixerGroup>();
            for (var i = 0; i < groups.Length; i++)
            {
                if (groups[i] && groups[i].name == name)
                {
                    return groups[i];
                }
            }

            return null;
        }

        internal static AudioClip Clip(string name)
        {
            return name != null && Templates.TryGetValue(name, out var template) && template ? template.clip : null;
        }
    }
}
