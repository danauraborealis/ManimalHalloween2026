using System;
using System.Collections.Generic;
using System.IO;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Halloween2026.Client
{
    // the room the event dressed on Lighthouse: the ground-floor office of water treatment building 2, with
    // a dead Black Division operator, a comms post and a radio. retail keeps it in the map's normal scenes;
    // neither the 4.1 map nor a rip of 1.1.5 has it, so it rides in as an additive layer over either
    // (tools/lighthouse). the layer is never unloaded from here: the game unloads every scene at raid end.
    internal static class LighthouseRoom
    {
        private const string BundleFile = "manimal_halloween2026_lighthouse.bundle";
        private const string SceneName = "Halloween2026_Lighthouse_Layer";
        private const string NativePrefix = "Lighthouse_";
        // a reworked Lighthouse loads its own copies of the map's scenes under this suffix
        private const string ReworkScene = "Lighthouse_Water_filter_facility_02_ML";
        private const string CorpseName = "Corpse_BD_01";
        private const string PmcBody = "6582e6d7b14c3f72eb071420";

#pragma warning disable 0649 // filled by json
        private sealed class RoomFile
        {
            [JsonProperty("hide")] public Dictionary<string, List<LabsLayer.Hidden>> Hide;
        }
#pragma warning restore 0649

        private static AssetBundle _bundle;
        private static bool _bundleFailed;
        private static bool _requested;
        private static bool _loaded;

        private static string DataDir => Path.Combine(Plugin.PluginDir, "plugin-data", "lighthouse");

        internal static void Install()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                if (scene.name == SceneName)
                {
                    _loaded = true;
                    LabsLayer.RebindShaders(scene, "Lighthouse room");
                    LayerSounds.Collect(scene);
                    return;
                }

                // the first of the map's scenes: early enough that the layer is in before the raid starts
                if (_requested || !Plugin.LighthouseCommsRoom.Value || !scene.name.StartsWith(NativePrefix, StringComparison.Ordinal))
                {
                    return;
                }

                _requested = true;
                if (!EnsureBundle())
                {
                    return;
                }

                SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
                Plugin.Log.LogInfo("[Halloween2026] loading the Lighthouse comms room");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] Lighthouse room scene handling failed: {e}");
            }
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            if (scene.name == SceneName || scene.name.StartsWith(NativePrefix, StringComparison.Ordinal))
            {
                _loaded = false;
                _requested = false;
            }
        }

        private static bool EnsureBundle()
        {
            if (_bundle)
            {
                return true;
            }

            if (_bundleFailed)
            {
                return false;
            }

            var path = Path.Combine(DataDir, BundleFile);
            if (!File.Exists(path))
            {
                _bundleFailed = true;
                Plugin.Log.LogWarning($"[Halloween2026] {path} is missing, the Lighthouse comms room stays as the map has it");
                return false;
            }

            _bundle = AssetBundle.LoadFromFile(path);
            if (!_bundle)
            {
                _bundleFailed = true;
                Plugin.Log.LogError($"[Halloween2026] AssetBundle.LoadFromFile failed for {path}");
                return false;
            }

            return true;
        }

        internal static void RaidStarted(GameWorld world)
        {
            try
            {
                var scene = SceneManager.GetSceneByName(SceneName);
                if (!_loaded || !scene.IsValid() || !scene.isLoaded)
                {
                    if (_requested && !_bundleFailed)
                    {
                        Plugin.Log.LogWarning("[Halloween2026] the Lighthouse comms room was not loaded by raid start, the room stays as the map has it");
                    }

                    return;
                }

                var rework = SceneManager.GetSceneByName(ReworkScene).isLoaded;
                HideReplaced(rework ? "rework" : "native");
                PlaceBody(world, scene);
                LabsAmbience.Start(scene, "lighthouse");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] setting up the Lighthouse comms room failed: {e}");
            }
        }

        // the props retail moved or took out of the room; the layer carries them where retail has them
        private static void HideReplaced(string baseMap)
        {
            var path = Path.Combine(DataDir, "room.json");
            if (!File.Exists(path))
            {
                return;
            }

            var file = JsonConvert.DeserializeObject<RoomFile>(File.ReadAllText(path));
            if (file == null || file.Hide == null || !file.Hide.TryGetValue(baseMap, out var rows) || rows == null)
            {
                return;
            }

            var hidden = LabsLayer.Hide(rows);
            Plugin.Log.LogInfo($"[Halloween2026] Lighthouse comms room over the {baseMap} map: hid {hidden} of {rows.Count} props it replaces");
        }

        // the body's container script did not survive the rip, and its template is a 1.3 one. a PMC body
        // of this map lends its container, placed on the layer's corpse so the look-at ray finds it through
        // the corpse's own collider; what it holds is rolled from upstream's table for the real template.
        private static void PlaceBody(GameWorld world, Scene scene)
        {
            Transform corpse = null;
            var roots = scene.GetRootGameObjects();
            for (var r = 0; r < roots.Length && !corpse; r++)
            {
                var all = roots[r].GetComponentsInChildren<Transform>(true);
                for (var i = 0; i < all.Length; i++)
                {
                    if (all[i].name == CorpseName)
                    {
                        corpse = all[i];
                        break;
                    }
                }
            }

            var path = Path.Combine(DataDir, "corpse.json");
            if (!corpse || !File.Exists(path))
            {
                Plugin.Log.LogWarning("[Halloween2026] the Black Division body or its loot table is missing, it stays a prop");
                return;
            }

            var table = JObject.Parse(File.ReadAllText(path));
            var id = (string)table["container"];
            var template = (string)table["template"] ?? PmcBody;

            LootableContainer source = null, spare = null;
            var containers = UnityEngine.Object.FindObjectsOfType<LootableContainer>(true);
            for (var i = 0; i < containers.Length; i++)
            {
                var candidate = containers[i];
                if (!candidate || candidate.gameObject.scene == scene)
                {
                    continue;
                }

                if (candidate.Id == id)
                {
                    // some other mod already brought the real one
                    Plugin.Log.LogInfo("[Halloween2026] the Black Division body is already a container on this map, left alone");
                    return;
                }

                var item = candidate.ItemOwner != null ? candidate.ItemOwner.RootItem : null;
                if (!source && item != null && item.StringTemplateId == template)
                {
                    source = candidate;
                }
                else if (!spare && candidate.name.IndexOf("corpse", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // a body the server gave no loot this raid still has the container script
                    spare = candidate;
                }
            }

            if (!source)
            {
                source = spare;
            }

            if (!source)
            {
                Plugin.Log.LogWarning("[Halloween2026] no PMC body on this map to lend the Black Division body a container, it stays a prop");
                return;
            }

            Transform collider = null;
            for (var i = 0; i < corpse.childCount; i++)
            {
                if (corpse.GetChild(i).name.EndsWith("_LOOTABLE", StringComparison.Ordinal))
                {
                    collider = corpse.GetChild(i);
                }
            }

            var copy = UnityEngine.Object.Instantiate(source.gameObject, corpse.position, corpse.rotation, corpse);
            copy.name = "Halloween2026_" + id;
            copy.SetActive(true);

            // only the container is wanted: its own shape lies where the lender's body lay
            var own = copy.GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < own.Length; i++)
            {
                own[i].enabled = false;
            }

            var renderers = copy.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                renderers[i].forceRenderingOff = true;
            }

            var container = copy.GetComponent<LootableContainer>();
            if (!container)
            {
                UnityEngine.Object.Destroy(copy);
                return;
            }

            if (collider)
            {
                collider.SetParent(copy.transform, true);
                collider.gameObject.layer = source.gameObject.layer;
                var shape = collider.GetComponent<Collider>();
                if (shape)
                {
                    shape.enabled = true;
                }
            }

            container.enabled = true;
            container.Id = id;
            if (world.World != null)
            {
                world.RegisterWorldInteractionObject(container);
            }

            var factory = Singleton<ItemFactory>.Instance;
            var body = factory.CreateItem(factory.NextId, template, null);
            if (body == null)
            {
                UnityEngine.Object.Destroy(copy);
                return;
            }

            LootItem.CreateLootContainer(container, body, body.ShortName.Localized(null), world, null);
            var held = Fill(body as CompoundItem, table);
            Plugin.Log.LogInfo($"[Halloween2026] Black Division body placed ({(collider ? "its own collider" : "no collider of its own")}), holding: "
                + (held.Count == 0 ? "nothing" : string.Join(", ", held.ToArray())));
        }

        private static List<string> Fill(CompoundItem body, JObject table)
        {
            var held = new List<string>();
            if (body == null || body.Grids == null || body.Grids.Length == 0)
            {
                return held;
            }

            var counts = new List<int[]>();
            foreach (var row in table["itemCount"] ?? new JArray())
            {
                counts.Add(new[] { (int)row[0], (int)row[1] });
            }

            var items = new List<KeyValuePair<string, int>>();
            foreach (var row in table["items"] ?? new JArray())
            {
                items.Add(new KeyValuePair<string, int>((string)row[0], (int)row[1]));
            }

            var stacks = table["stacks"] as JObject;
            var factory = Singleton<ItemFactory>.Instance;
            var keys = new List<ResourceKey>();
            var count = Weighted(counts, c => c[1], c => c[0], 1);
            for (var i = 0; i < count && items.Count > 0; i++)
            {
                var tpl = Weighted(items, p => p.Value, p => p.Key, null);
                var item = tpl == null ? null : factory.CreateItem(factory.NextId, tpl, null);
                if (item == null)
                {
                    continue;
                }

                if (item.StackMaxSize > 1)
                {
                    var range = stacks != null && stacks[tpl] != null ? new[] { (int)stacks[tpl][0], (int)stacks[tpl][1] } : new[] { 1, 20 };
                    item.StackObjectsCount = Mathf.Clamp(UnityEngine.Random.Range(range[0], range[1] + 1), 1, item.StackMaxSize);
                }

                item.SpawnedInSession = true;
                if (!body.Grids[0].AddAnywhere(item, EErrorHandlingType.Ignore).Succeeded)
                {
                    continue;
                }

                held.Add(item.ShortName.Localized(null));
                foreach (var key in item.Template.AllResources)
                {
                    if (key != null)
                    {
                        keys.Add(key);
                    }
                }
            }

            // the game only loaded the bundles of items that existed when the raid was built
            if (keys.Count > 0)
            {
                try
                {
                    var pool = Singleton<ObjectsFactory>.Instance;
                    var load = pool.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid, ObjectsFactory.AssemblyType.Local,
                        keys.ToArray(), Diz.Jobs.JobYieldPriority.General, null, pool.PoolsCancellationToken);
                    load.ContinueWith(task => { _ = task.Exception; }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Halloween2026] preloading the body's items failed, they may not show when taken out: {e.Message}");
                }
            }

            return held;
        }

        private static TOut Weighted<TRow, TOut>(List<TRow> rows, Func<TRow, int> weight, Func<TRow, TOut> value, TOut fallback)
        {
            var total = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                total += weight(rows[i]);
            }

            if (total <= 0)
            {
                return fallback;
            }

            var roll = UnityEngine.Random.Range(0, total);
            for (var i = 0; i < rows.Count; i++)
            {
                roll -= weight(rows[i]);
                if (roll < 0)
                {
                    return value(rows[i]);
                }
            }

            return fallback;
        }
    }
}
