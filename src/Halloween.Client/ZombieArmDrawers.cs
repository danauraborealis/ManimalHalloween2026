using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Halloween2026.Client
{
    // the six office desks whose bloodied drawer hides a zombie arm. retail drives each through a
    // trigger graph the rip drops; what is left in the layer is the desk, the arm and one animator
    // with three endings. so the same steps are done here: an Open prompt on the handle, a roll,
    // the animator's ending, and what the ending does (nothing, a blast, or the wallet's loot).
    internal static class ZombieArmDrawers
    {
        internal const string RootName = "INTERACTIVE_Table_ShapeP_Randomize_Zombie_Arm";

        internal const int Wallet = 1, Finger = 2, Blast = 3;

        private static int _wallet = 30, _finger = 28, _blast = 42;
        private static readonly List<int[]> ItemCount = new List<int[]>();
        private static readonly List<KeyValuePair<string, int>> Items = new List<KeyValuePair<string, int>>();
        private static readonly Dictionary<string, int[]> Stacks = new Dictionary<string, int[]>();
        private static bool _dataLoaded;

        internal static void Spawn()
        {
            try
            {
                var scene = SceneManager.GetSceneByName(LabsLayer.SceneName);
                if (!LabsLayer.Loaded || !scene.IsValid() || !scene.isLoaded)
                {
                    return;
                }

                LoadData();
                var ready = 0;
                var roots = scene.GetRootGameObjects();
                for (var r = 0; r < roots.Length; r++)
                {
                    var all = roots[r].GetComponentsInChildren<Transform>(true);
                    for (var i = 0; i < all.Length; i++)
                    {
                        if (all[i].name.StartsWith(RootName, StringComparison.Ordinal) && Arm(all[i]))
                        {
                            ready++;
                        }
                    }
                }

                Plugin.Log.LogInfo($"[Halloween2026] {ready} zombie-arm drawers ready (wallet {_wallet}, finger {_finger}, grenade {_blast})");
                if (ready > 0)
                {
                    Preload();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] setting up the zombie-arm drawers failed: {e}");
            }
        }

        private static bool Arm(Transform desk)
        {
            var animator = desk.GetComponent<Animator>();
            if (!animator || desk.GetComponent<ZombieArmDrawer>())
            {
                return false;
            }

            Collider handle = null;
            Transform blast = null, wallet = null;
            var parts = desk.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.name == "Logic" && !handle)
                {
                    handle = part.GetComponent<Collider>();
                }
                else if (part.name == "Explosion_VFX")
                {
                    blast = part;
                }
                else if (part.name == "item_wallet")
                {
                    wallet = part;
                }
            }

            if (!handle)
            {
                Plugin.Log.LogWarning($"[Halloween2026] drawer at {desk.position} has no handle collider, it stays shut");
                return false;
            }

            // the layer build may have parked it; its resting clip is what hides the grenade and the blast
            animator.enabled = true;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            var drawer = desk.gameObject.AddComponent<ZombieArmDrawer>();
            drawer.Animator = animator;
            drawer.Handle = handle;
            drawer.BlastPoint = blast;
            drawer.WalletPoint = wallet;
            EventHandles.Attach(handle, "Open", drawer.Open);
            return true;
        }

        private static void LoadData()
        {
            if (_dataLoaded)
            {
                return;
            }

            _dataLoaded = true;
            var path = Path.Combine(Plugin.PluginDir, "plugin-data", "labs", "drawers.json");
            if (!File.Exists(path))
            {
                return;
            }

            var file = JObject.Parse(File.ReadAllText(path));
            var outcomes = file["outcomes"];
            if (outcomes != null)
            {
                _wallet = Math.Max(0, (int?)outcomes["wallet"] ?? _wallet);
                _finger = Math.Max(0, (int?)outcomes["finger"] ?? _finger);
                _blast = Math.Max(0, (int?)outcomes["grenade"] ?? _blast);
            }

            foreach (var row in file["itemCount"] ?? new JArray())
            {
                ItemCount.Add(new[] { (int)row[0], (int)row[1] });
            }

            foreach (var row in file["items"] ?? new JArray())
            {
                Items.Add(new KeyValuePair<string, int>((string)row[0], (int)row[1]));
            }

            var stacks = file["stacks"] as JObject;
            if (stacks != null)
            {
                foreach (var pair in stacks)
                {
                    Stacks[pair.Key] = new[] { (int)pair.Value[0], (int)pair.Value[1] };
                }
            }
        }

        // the game only loads the bundles of items that exist at raid start; these are conjured later
        private static void Preload()
        {
            try
            {
                var factory = Singleton<ItemFactory>.Instance;
                var pool = Singleton<ObjectsFactory>.Instance;
                var keys = new List<ResourceKey>();
                var seen = new HashSet<string>();
                for (var i = 0; i < Items.Count; i++)
                {
                    var item = factory.CreateItem(factory.NextId, Items[i].Key, null);
                    if (item == null)
                    {
                        continue;
                    }

                    foreach (var key in item.Template.AllResources)
                    {
                        if (key != null && seen.Add(key.path))
                        {
                            keys.Add(key);
                        }
                    }
                }

                if (keys.Count == 0)
                {
                    return;
                }

                var load = pool.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid, ObjectsFactory.AssemblyType.Local,
                    keys.ToArray(), Diz.Jobs.JobYieldPriority.General, null, pool.PoolsCancellationToken);
                load.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Halloween2026] preloading the drawer wallet's items failed, they may not appear: {e.Message}");
            }
        }

        internal static int Roll()
        {
            var total = _wallet + _finger + _blast;
            if (total <= 0)
            {
                return Finger;
            }

            var roll = UnityEngine.Random.Range(0, total);
            return roll < _wallet ? Wallet : roll < _wallet + _finger ? Finger : Blast;
        }

        internal static void DropLoot(Vector3 position, Player opener)
        {
            try
            {
                var world = Singleton<GameWorld>.Instance;
                var factory = Singleton<ItemFactory>.Instance;
                var count = Pick(ItemCount);
                var dropped = new List<string>();
                for (var i = 0; i < count; i++)
                {
                    var tpl = PickItem();
                    var item = tpl == null ? null : factory.CreateItem(factory.NextId, tpl, null);
                    if (item == null)
                    {
                        continue;
                    }

                    if (Stacks.TryGetValue(tpl, out var range))
                    {
                        item.StackObjectsCount = Mathf.Clamp(UnityEngine.Random.Range(range[0], range[1] + 1), 1, item.StackMaxSize);
                    }

                    item.SpawnedInSession = true;
                    var at = position + new Vector3(UnityEngine.Random.Range(-0.05f, 0.05f), 0.05f + 0.04f * i, UnityEngine.Random.Range(-0.05f, 0.05f));
                    if (world.ThrowItem(item, opener, at, Quaternion.identity, Vector3.zero, Vector3.zero, true, true, 0f))
                    {
                        dropped.Add(item.ShortName.Localized(null));
                    }
                }

                Plugin.Log.LogInfo($"[Halloween2026] drawer wallet: {(dropped.Count == 0 ? "nothing" : string.Join(", ", dropped.ToArray()))}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] the drawer wallet's loot failed: {e}");
            }
        }

        private static int Pick(List<int[]> rows)
        {
            var total = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                total += rows[i][1];
            }

            if (total <= 0)
            {
                return 1;
            }

            var roll = UnityEngine.Random.Range(0, total);
            for (var i = 0; i < rows.Count; i++)
            {
                roll -= rows[i][1];
                if (roll < 0)
                {
                    return rows[i][0];
                }
            }

            return rows[0][0];
        }

        private static string PickItem()
        {
            var total = 0;
            for (var i = 0; i < Items.Count; i++)
            {
                total += Items[i].Value;
            }

            if (total <= 0)
            {
                return null;
            }

            var roll = UnityEngine.Random.Range(0, total);
            for (var i = 0; i < Items.Count; i++)
            {
                roll -= Items[i].Value;
                if (roll < 0)
                {
                    return Items[i].Key;
                }
            }

            return Items[0].Key;
        }
    }

    internal sealed class ZombieArmDrawer : MonoBehaviour
    {
        // the clips' own timing: the hand is out by 3.3 s, the grenade goes at 3.0 s
        private const float WalletSeconds = 3.3f;
        private const float BlastSeconds = 3f;

        internal Animator Animator;
        internal Collider Handle;
        internal Transform BlastPoint;
        internal Transform WalletPoint;

        private int _outcome;
        private float _opened;
        private bool _done;
        private Player _opener;

        internal void Open(Player player)
        {
            if (_outcome != 0)
            {
                return;
            }

            _opener = player;
            _opened = Time.time;
            _outcome = ZombieArmDrawers.Roll();
            if (Handle)
            {
                Handle.enabled = false;
            }

            Animator.SetBool(_outcome == ZombieArmDrawers.Wallet ? "IsOpen_01" : _outcome == ZombieArmDrawers.Finger ? "IsOpen_02" : "IsOpen_03", true);
            var at = Handle ? Handle.transform.position : transform.position;
            LayerSounds.PlayStep(ZombieArmDrawers.RootName, "Open_01", at);
            // each ending has its own clip on the same cue as its animation: the empty hand, the finger, the grenade's pin and lever
            LayerSounds.PlayStep(ZombieArmDrawers.RootName,
                _outcome == ZombieArmDrawers.Wallet ? "Open_Table_01" : _outcome == ZombieArmDrawers.Finger ? "Open_Table_02" : "Open_Table_03", at);
            Plugin.Log.LogInfo("[Halloween2026] drawer opened: "
                + (_outcome == ZombieArmDrawers.Wallet ? "wallet" : _outcome == ZombieArmDrawers.Finger ? "finger" : "grenade"));
        }

        private void Update()
        {
            if (_outcome == 0 || _done)
            {
                return;
            }

            var elapsed = Time.time - _opened;
            if (_outcome == ZombieArmDrawers.Finger)
            {
                _done = true;
            }
            else if (_outcome == ZombieArmDrawers.Blast && elapsed >= BlastSeconds)
            {
                _done = true;
                var at = BlastPoint ? BlastPoint.position : transform.position;
                LayerSounds.PlayStep(ZombieArmDrawers.RootName, "Explosion", at);
                EventHandles.Explode(at, _opener);
            }
            else if (_outcome == ZombieArmDrawers.Wallet && elapsed >= WalletSeconds)
            {
                _done = true;
                ZombieArmDrawers.DropLoot(WalletPoint ? WalletPoint.position : transform.position, _opener);
            }
        }
    }
}
