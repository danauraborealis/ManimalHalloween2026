using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Halloween2026.Client
{
    // the event's dressing (gore, corpses, clutter, lamps) as one additive scene over native Labs.
    // built from the retail event scenes minus retail normal Labs: see tools/labs.
    internal static class LabsLayer
    {
        private const string BundleFile = "manimal_halloween2026_labs.bundle";
        internal const string SceneName = "Halloween2026_Labs_Layer";
        internal const string NativePrefix = "Laboratory_";

#pragma warning disable 0649 // filled by json
        internal sealed class Hidden
        {
            [JsonProperty("scene")] public string Scene;
            [JsonProperty("name")] public string Name;
            [JsonProperty("position")] public float[] Position;
        }

        private sealed class LayerFile
        {
            [JsonProperty("hide")] public List<Hidden> Hide;
        }
#pragma warning restore 0649

        private static AssetBundle _bundle;
        private static bool _bundleTried;
        private static bool _requested;

        internal static bool Loaded { get; private set; }

        internal static void Install()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            Application.quitting += OnQuit;
        }

        // quitting mid-raid destroys the materials before the decals, and each decal then throws on its way out
        private static void OnQuit()
        {
            try
            {
                var scene = SceneManager.GetSceneByName(SceneName);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    return;
                }

                var roots = scene.GetRootGameObjects();
                for (var r = 0; r < roots.Length; r++)
                {
                    var decals = roots[r].GetComponentsInChildren<StaticDeferredDecal>(false);
                    for (var i = 0; i < decals.Length; i++)
                    {
                        decals[i].enabled = false;
                    }
                }
            }
            catch (Exception)
            {
                // nothing to save at quit
            }
        }

        private static string DataDir => Path.Combine(Plugin.PluginDir, "plugin-data", "labs");

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                if (scene.name == SceneName)
                {
                    Loaded = true;
                    RebindShaders(scene, "Labs layer");
                    LayerSounds.Collect(scene);
                    LabsLayerLights.Attach(scene);
                    return;
                }

                // the first native Labs scene of a Halloween raid: MapVariants decided before the preset began
                if (_requested || !InfectedLabs.Active || !scene.name.StartsWith(NativePrefix, StringComparison.Ordinal))
                {
                    return;
                }

                _requested = true;
                LabsExtracts.Prepare(scene);
                if (!EnsureBundle())
                {
                    return;
                }

                SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
                Plugin.Log.LogInfo("[Halloween2026] loading the Labs event layer");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] Labs layer scene handling failed: {e}");
            }
        }

        // state only. the game unloads every loaded scene by name at raid end, ours included, and a scene
        // unloaded from here first would make that call throw and hang the return to the menu
        private static void OnSceneUnloaded(Scene scene)
        {
            try
            {
                if (scene.name == SceneName)
                {
                    Loaded = false;
                    _requested = false;
                    return;
                }

                if (scene.name.StartsWith(NativePrefix, StringComparison.Ordinal))
                {
                    Loaded = false;
                    _requested = false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] Labs layer unload failed: {e}");
            }
        }

        private static bool EnsureBundle()
        {
            if (_bundle)
            {
                return true;
            }

            if (_bundleTried)
            {
                return false;
            }

            // only a failure is final: a bundle the game unloaded between raids gets loaded again
            var path = Path.Combine(DataDir, BundleFile);
            if (!File.Exists(path))
            {
                _bundleTried = true;
                Plugin.Log.LogWarning($"[Halloween2026] {path} is missing, infected Labs loads without the event dressing");
                return false;
            }

            _bundle = AssetBundle.LoadFromFile(path);
            if (!_bundle)
            {
                _bundleTried = true;
                Plugin.Log.LogError($"[Halloween2026] AssetBundle.LoadFromFile failed for {path}");
                return false;
            }

            return true;
        }

        // the bundle holds ripped stand-in shaders under the game's names; the real ones come from the
        // game's own shaders bundle, by identity, since a name lookup can return some other mod's copy
        internal static void RebindShaders(Scene scene, string label)
        {
            var game = new Dictionary<string, Shader>(StringComparer.Ordinal);
            foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (bundle && bundle.name.IndexOf("shaders", StringComparison.OrdinalIgnoreCase) >= 0
                    && !bundle.name.StartsWith("manimal_halloween2026", StringComparison.OrdinalIgnoreCase))
                {
                    var shaders = bundle.LoadAllAssets<Shader>();
                    for (var i = 0; i < shaders.Length; i++)
                    {
                        if (shaders[i] && !game.ContainsKey(shaders[i].name))
                        {
                            game[shaders[i].name] = shaders[i];
                        }
                    }
                }
            }

            var seen = new HashSet<Material>();
            var missing = new HashSet<string>();
            var rebound = 0;
            var roots = scene.GetRootGameObjects();
            for (var r = 0; r < roots.Length; r++)
            {
                // decals draw through their component, not a renderer
                var decals = roots[r].GetComponentsInChildren<StaticDeferredDecal>(true);
                var renderers = roots[r].GetComponentsInChildren<Renderer>(true);
                for (var i = 0; i < renderers.Length + decals.Length; i++)
                {
                    var materials = i < renderers.Length
                        ? renderers[i].sharedMaterials
                        : new[] { decals[i - renderers.Length].DecalMaterial };
                    for (var m = 0; m < materials.Length; m++)
                    {
                        var material = materials[m];
                        if (!material || !material.shader || !seen.Add(material))
                        {
                            continue;
                        }

                        var name = material.shader.name;
                        if (!game.TryGetValue(name, out var real) || !real)
                        {
                            real = Shader.Find(name);
                        }

                        if (!real)
                        {
                            missing.Add(name);
                        }
                        else if (real != material.shader)
                        {
                            // the render queue rides on the material, and a shader swap resets it
                            var queue = material.renderQueue;
                            material.shader = real;
                            material.renderQueue = queue;
                            rebound++;
                        }
                    }
                }
            }

            Plugin.Log.LogInfo($"[Halloween2026] {label} loaded: {seen.Count} materials, {rebound} rebound to game shaders ({game.Count} in the shaders bundle)");
            if (missing.Count > 0)
            {
                Plugin.Log.LogWarning($"[Halloween2026] no game shader for: {string.Join(", ", new List<string>(missing).ToArray())}");
            }
        }

        // native props the event took away (pallets, a few chairs and stands), found by name and place.
        // deactivated, never destroyed: culling registers objects in Start.
        internal static void HideRemovedNatives()
        {
            if (!Loaded)
            {
                return;
            }

            var path = Path.Combine(DataDir, "labs-layer.json");
            if (!File.Exists(path))
            {
                return;
            }

            var file = JsonConvert.DeserializeObject<LayerFile>(File.ReadAllText(path));
            if (file == null || file.Hide == null || file.Hide.Count == 0)
            {
                return;
            }

            var hidden = Hide(file.Hide);
            Plugin.Log.LogInfo($"[Halloween2026] hid {hidden} of {file.Hide.Count} native props the event removes");
        }

        // each row names a prop by scene, name and place; how many were found and switched off
        internal static int Hide(List<Hidden> rows)
        {
            var byScene = new Dictionary<string, List<Hidden>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rows.Count; i++)
            {
                if (!byScene.TryGetValue(rows[i].Scene, out var list))
                {
                    byScene[rows[i].Scene] = list = new List<Hidden>();
                }

                list.Add(rows[i]);
            }

            var hidden = 0;
            for (var s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!byScene.TryGetValue(scene.name, out var wanted))
                {
                    continue;
                }

                var names = new Dictionary<string, List<Hidden>>(StringComparer.Ordinal);
                for (var i = 0; i < wanted.Count; i++)
                {
                    if (!names.TryGetValue(wanted[i].Name, out var list))
                    {
                        names[wanted[i].Name] = list = new List<Hidden>();
                    }

                    list.Add(wanted[i]);
                }

                var roots = scene.GetRootGameObjects();
                for (var r = 0; r < roots.Length; r++)
                {
                    var transforms = roots[r].GetComponentsInChildren<Transform>(true);
                    for (var i = 0; i < transforms.Length; i++)
                    {
                        var transform = transforms[i];
                        if (!names.TryGetValue(transform.name, out var candidates))
                        {
                            continue;
                        }

                        var position = transform.position;
                        for (var c = 0; c < candidates.Count; c++)
                        {
                            var p = candidates[c].Position;
                            if (Mathf.Abs(position.x - p[0]) < 0.05f && Mathf.Abs(position.y - p[1]) < 0.05f && Mathf.Abs(position.z - p[2]) < 0.05f)
                            {
                                transform.gameObject.SetActive(false);
                                hidden++;
                                break;
                            }
                        }
                    }
                }
            }

            return hidden;
        }
    }
}
