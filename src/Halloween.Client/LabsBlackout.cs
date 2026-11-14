using System;
using System.Collections.Generic;
using Comfort.Common;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Manimal.Halloween2026.Client
{
    // lights out on the Halloween Labs. a port of the lights-out pass of Vultify's Blackout mod
    // (MIT, github.com/Vultify/Blackout), itself a port of the live blackout event: nothing is
    // disabled or destroyed, every light-ish thing in the map's light scene is muted in place.
    // the event layer's own lamps are a different scene and stay lit.
    internal static class LabsBlackout
    {
        private const string LightScene = "_LIGHT";
        private const string NativePrefix = "Laboratory_";
        private const string KeptGroup = "Doesnt_Turn_off";
        private const float SpecularKeep = 0.4f;

        private static readonly string[] EmissiveFloats = { "_EmissionPower", "_EmissionVisibility" };
        private static readonly string[] EmissiveColors = { "_EmissionColor", "_EmissiveColor", "_EmAnim1Color", "_EmAnim2Color" };
        private static readonly int SpecVals = Shader.PropertyToID("_SpecVals");

        internal static bool Active { get; private set; }

        internal static void Install(Harmony harmony)
        {
            try
            {
                // motion-sensor lamp groups write light state straight from their recorded values
                var target = AccessTools.Method(typeof(ControlledLampGroup), "SetLightState");
                if (target != null)
                {
                    harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(LabsBlackout), nameof(LampGroup))));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] lamp group patch failed, motion-sensor lights will relight: {e.Message}");
            }
        }

        private static void LampGroup(ref bool state)
        {
            if (Active)
            {
                state = false;
            }
        }

        internal static void Apply()
        {
            if (Active || !Plugin.LabsLightsOut.Value)
            {
                return;
            }

            Scene light = default;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.name.StartsWith(NativePrefix, StringComparison.Ordinal)
                    && scene.name.IndexOf(LightScene, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    light = scene;
                    break;
                }
            }

            if (!light.IsValid())
            {
                Plugin.Log.LogWarning("[Halloween2026] no Labs light scene is loaded, the lights stay on");
                return;
            }

            // lives in the light scene: unloading it restores every shared material we touched
            var host = new GameObject("Halloween2026_LabsBlackout");
            SceneManager.MoveGameObjectToScene(host, light);
            Active = true;
            host.AddComponent<Runner>().Begin(light);
        }

        private sealed class Runner : MonoBehaviour
        {
            private readonly List<CullingLightObject> _culled = new List<CullingLightObject>();
            private readonly List<CullingAdvancedLightObject> _culledAdvanced = new List<CullingAdvancedLightObject>();
            private readonly List<CullingLightObject> _asleep = new List<CullingLightObject>();
            private readonly List<CullingAdvancedLightObject> _asleepAdvanced = new List<CullingAdvancedLightObject>();
            private readonly List<Light> _stray = new List<Light>();
            private readonly List<BaseLight> _strayBase = new List<BaseLight>();
            private readonly Dictionary<Material, Dictionary<string, object>> _emissive = new Dictionary<Material, Dictionary<string, object>>();
            private readonly HashSet<Material> _emissiveKeyword = new HashSet<Material>();
            private readonly Dictionary<Material, Vector4> _specular = new Dictionary<Material, Vector4>();

            private LevelSettings _settings;
            private Color _sky, _equator, _ground, _nvSky, _nvEquator, _nvGround;
            private float _intensity, _nvIntensity;
            private AmbientLight[] _ambient;

            internal void Begin(Scene light)
            {
                try
                {
                    var counts = LightsOut(light);

                    // the map keeps its lamps in the light scene, but a mod's replacement room brings its own
                    var others = new List<string>();
                    for (var s = 0; s < SceneManager.sceneCount; s++)
                    {
                        var scene = SceneManager.GetSceneAt(s);
                        if (scene.isLoaded && scene != light && scene.name.StartsWith(NativePrefix, StringComparison.Ordinal) && HasLights(scene))
                        {
                            var more = LightsOut(scene);
                            for (var c = 0; c < counts.Length; c++)
                            {
                                counts[c] += more[c];
                            }

                            others.Add(scene.name);
                        }
                    }

                    if (others.Count > 0)
                    {
                        Plugin.Log.LogInfo($"[Halloween2026] lights also put out in: {string.Join(", ", others.ToArray())}");
                    }

                    var layer = LayerMaterials();
                    DimEmissive(layer);
                    DimSpecular();
                    TakeAmbient();
                    Plugin.Log.LogInfo($"[Halloween2026] Labs lights out: {_culled.Count + _culledAdvanced.Count} culled lamps, "
                        + $"{_stray.Count + _strayBase.Count} other lights, {counts[0]} volumetrics, {counts[1]} flares, {counts[2]} lit surfaces, "
                        + $"{_emissive.Count} emissive and {_specular.Count} specular materials dimmed");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[Halloween2026] Labs lights-out pass failed: {e}");
                }
            }

            private static bool HasLights(Scene scene)
            {
                var roots = scene.GetRootGameObjects();
                for (var r = 0; r < roots.Length; r++)
                {
                    if (roots[r].GetComponentInChildren<Light>(true) || roots[r].GetComponentInChildren<BaseLight>(true))
                    {
                        return true;
                    }
                }

                return false;
            }

            private int[] LightsOut(Scene light)
            {
                var wrapped = new HashSet<Light>();
                var wrappedBase = new HashSet<BaseLight>();
                var block = new MaterialPropertyBlock();
                var materials = new List<Material>(8);
                var counts = new int[3];
                var roots = light.GetRootGameObjects();
                for (var r = 0; r < roots.Length; r++)
                {
                    var root = roots[r];
                    if (root == gameObject)
                    {
                        continue;
                    }

                    var kept = new List<Transform>();
                    var transforms = root.GetComponentsInChildren<Transform>(true);
                    for (var i = 0; i < transforms.Length; i++)
                    {
                        if (transforms[i].name == KeptGroup)
                        {
                            kept.Add(transforms[i]);
                        }
                    }

                    // the fake-gi fill volumes that light rooms with no real lamps
                    if (root.name.IndexOf("Inverted_Shadow", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var fills = root.GetComponentsInChildren<Renderer>(true);
                        for (var i = 0; i < fills.Length; i++)
                        {
                            fills[i].enabled = false;
                        }
                    }

                    var culled = root.GetComponentsInChildren<CullingLightObject>(true);
                    for (var i = 0; i < culled.Length; i++)
                    {
                        if (Kept(culled[i].transform, kept))
                        {
                            if (culled[i]._light)
                            {
                                wrapped.Add(culled[i]._light);
                            }

                            continue;
                        }

                        culled[i].IntensityMultiplier = 0f;
                        _culled.Add(culled[i]);
                        if (culled[i]._light)
                        {
                            wrapped.Add(culled[i]._light);
                            culled[i]._light.enabled = false;
                        }

                        // its Awake undoes the mute when the game first activates it
                        if (!culled[i].gameObject.activeInHierarchy)
                        {
                            _asleep.Add(culled[i]);
                        }
                    }

                    var advanced = root.GetComponentsInChildren<CullingAdvancedLightObject>(true);
                    for (var i = 0; i < advanced.Length; i++)
                    {
                        if (Kept(advanced[i].transform, kept))
                        {
                            if (advanced[i]._light)
                            {
                                wrappedBase.Add(advanced[i]._light);
                            }

                            continue;
                        }

                        advanced[i].IntensityMultiplier = 0f;
                        _culledAdvanced.Add(advanced[i]);
                        if (advanced[i]._light)
                        {
                            wrappedBase.Add(advanced[i]._light);
                            advanced[i]._light.m_Intensity = 0f;
                        }

                        if (!advanced[i].gameObject.activeInHierarchy)
                        {
                            _asleepAdvanced.Add(advanced[i]);
                        }
                    }

                    var lights = root.GetComponentsInChildren<Light>(true);
                    for (var i = 0; i < lights.Length; i++)
                    {
                        if (!wrapped.Contains(lights[i]) && !Kept(lights[i].transform, kept))
                        {
                            lights[i].enabled = false;
                            _stray.Add(lights[i]);
                        }
                    }

                    var baseLights = root.GetComponentsInChildren<BaseLight>(true);
                    for (var i = 0; i < baseLights.Length; i++)
                    {
                        if (!wrappedBase.Contains(baseLights[i]) && !Kept(baseLights[i].transform, kept))
                        {
                            baseLights[i].m_Intensity = 0f;
                            _strayBase.Add(baseLights[i]);
                        }
                    }

                    var volumetrics = root.GetComponentsInChildren<VolumetricLight>(true);
                    for (var i = 0; i < volumetrics.Length; i++)
                    {
                        if (!Kept(volumetrics[i].transform, kept))
                        {
                            volumetrics[i].enabled = false;
                            counts[0]++;
                        }
                    }

                    var flares = root.GetComponentsInChildren<MultiFlare.FlareLight>(true);
                    for (var i = 0; i < flares.Length; i++)
                    {
                        if (!Kept(flares[i].transform, kept))
                        {
                            flares[i].SetAlpha(0f);
                            counts[1]++;
                        }
                    }

                    // a block overrides the shared material for this renderer only, and dies with the scene
                    var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
                    for (var i = 0; i < renderers.Length; i++)
                    {
                        if (Kept(renderers[i].transform, kept))
                        {
                            continue;
                        }

                        renderers[i].GetSharedMaterials(materials);
                        for (var slot = 0; slot < materials.Count; slot++)
                        {
                            var material = materials[slot];
                            if (!material)
                            {
                                continue;
                            }

                            block.Clear();
                            for (var p = 0; p < EmissiveFloats.Length; p++)
                            {
                                if (material.HasProperty(EmissiveFloats[p]))
                                {
                                    block.SetFloat(EmissiveFloats[p], 0f);
                                }
                            }

                            for (var p = 0; p < EmissiveColors.Length; p++)
                            {
                                if (material.HasProperty(EmissiveColors[p]))
                                {
                                    block.SetColor(EmissiveColors[p], Color.black);
                                }
                            }

                            if (!block.isEmpty)
                            {
                                renderers[i].SetPropertyBlock(block, slot);
                                counts[2]++;
                            }
                        }
                    }
                }

                return counts;
            }

            private static bool Kept(Transform transform, List<Transform> groups)
            {
                for (var i = 0; i < groups.Count; i++)
                {
                    if (transform.IsChildOf(groups[i]))
                    {
                        return true;
                    }
                }

                return false;
            }

            // the event layer keeps its glow: its lamps are the light the event leaves you
            private static HashSet<Material> LayerMaterials()
            {
                var set = new HashSet<Material>();
                var scene = SceneManager.GetSceneByName(LabsLayer.SceneName);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    return set;
                }

                var materials = new List<Material>(8);
                var roots = scene.GetRootGameObjects();
                for (var r = 0; r < roots.Length; r++)
                {
                    var renderers = roots[r].GetComponentsInChildren<Renderer>(true);
                    for (var i = 0; i < renderers.Length; i++)
                    {
                        renderers[i].GetSharedMaterials(materials);
                        for (var m = 0; m < materials.Count; m++)
                        {
                            if (materials[m])
                            {
                                set.Add(materials[m]);
                            }
                        }
                    }
                }

                return set;
            }

            // sky ceilings, fake windows, videowalls and signs sit in the area scenes, out of the light scene's reach
            private void DimEmissive(HashSet<Material> spare)
            {
                var all = Resources.FindObjectsOfTypeAll<Material>();
                for (var i = 0; i < all.Length; i++)
                {
                    var material = all[i];
                    if (!material || !material.shader || spare.Contains(material) || _emissive.ContainsKey(material))
                    {
                        continue;
                    }

                    var name = material.shader.name;
                    var standard = name == "Standard";
                    if (!standard && name.IndexOf("Emissive", StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    var keyword = standard && material.IsKeywordEnabled("_EMISSION");
                    if (standard && !keyword)
                    {
                        continue;
                    }

                    var saved = new Dictionary<string, object>();
                    for (var p = 0; p < EmissiveFloats.Length; p++)
                    {
                        if (material.HasProperty(EmissiveFloats[p]))
                        {
                            saved[EmissiveFloats[p]] = material.GetFloat(EmissiveFloats[p]);
                            material.SetFloat(EmissiveFloats[p], 0f);
                        }
                    }

                    for (var p = 0; p < EmissiveColors.Length; p++)
                    {
                        if (material.HasProperty(EmissiveColors[p]))
                        {
                            saved[EmissiveColors[p]] = material.GetColor(EmissiveColors[p]);
                            material.SetColor(EmissiveColors[p], Color.black);
                        }
                    }

                    if (saved.Count == 0 && !keyword)
                    {
                        continue;
                    }

                    if (keyword)
                    {
                        material.DisableKeyword("_EMISSION");
                        _emissiveKeyword.Add(material);
                    }

                    _emissive[material] = saved;
                }
            }

            // plain specular is all that still catches light at range; map scenes only, never gear or hands
            private void DimSpecular()
            {
                var renderers = new List<Renderer>(8192);
                var materials = new List<Material>(16);
                for (var s = 0; s < SceneManager.sceneCount; s++)
                {
                    var scene = SceneManager.GetSceneAt(s);
                    if (!scene.isLoaded || (!scene.name.StartsWith(NativePrefix, StringComparison.Ordinal) && scene.name != LabsLayer.SceneName))
                    {
                        continue;
                    }

                    var roots = scene.GetRootGameObjects();
                    for (var r = 0; r < roots.Length; r++)
                    {
                        roots[r].GetComponentsInChildren(true, renderers);
                        for (var i = 0; i < renderers.Count; i++)
                        {
                            if (!renderers[i])
                            {
                                continue;
                            }

                            renderers[i].GetSharedMaterials(materials);
                            for (var m = 0; m < materials.Count; m++)
                            {
                                // shared across renderers: scaling one twice compounds
                                var material = materials[m];
                                if (!material || _specular.ContainsKey(material) || !material.HasProperty(SpecVals))
                                {
                                    continue;
                                }

                                var spec = material.GetVector(SpecVals);
                                _specular[material] = spec;
                                material.SetVector(SpecVals, spec * SpecularKeep);
                            }
                        }
                    }
                }
            }

            // LevelSettings re-applies its serialized ambient every frame, so its fields are what must change
            private void TakeAmbient()
            {
                _settings = Singleton<LevelSettings>.Instance;
                if (_settings)
                {
                    _sky = _settings.SkyColor;
                    _equator = _settings.EquatorColor;
                    _ground = _settings.GroundColor;
                    _intensity = _settings.AmbientIntensity;
                    _nvSky = _settings.NightVisionSkyColor;
                    _nvEquator = _settings.NightVisionEquatorColor;
                    _nvGround = _settings.NightVisionGroundColor;
                    _nvIntensity = _settings.NightVisionAmbientIntensity;
                }

                _ambient = FindObjectsOfType<AmbientLight>();
            }

            private void LateUpdate()
            {
                for (var i = _asleep.Count - 1; i >= 0; i--)
                {
                    var cull = _asleep[i];
                    if (!cull)
                    {
                        _asleep.RemoveAt(i);
                    }
                    else if (cull.gameObject.activeInHierarchy)
                    {
                        cull.IntensityMultiplier = 0f;
                        if (cull._light)
                        {
                            cull._light.enabled = false;
                        }

                        _asleep.RemoveAt(i);
                    }
                }

                for (var i = _asleepAdvanced.Count - 1; i >= 0; i--)
                {
                    var cull = _asleepAdvanced[i];
                    if (!cull)
                    {
                        _asleepAdvanced.RemoveAt(i);
                    }
                    else if (cull.gameObject.activeInHierarchy)
                    {
                        cull.IntensityMultiplier = 0f;
                        if (cull._light)
                        {
                            cull._light.m_Intensity = 0f;
                        }

                        _asleepAdvanced.RemoveAt(i);
                    }
                }

                // a muted light that got its intensity back from some other writer
                for (var i = 0; i < _culled.Count; i++)
                {
                    var cull = _culled[i];
                    if (cull && cull._light && cull._light.enabled && cull._light.intensity > 0f)
                    {
                        cull._light.intensity = 0f;
                    }
                }

                for (var i = 0; i < _culledAdvanced.Count; i++)
                {
                    var cull = _culledAdvanced[i];
                    if (cull && cull._light && cull._light.m_Intensity != 0f)
                    {
                        cull._light.m_Intensity = 0f;
                    }
                }

                for (var i = 0; i < _stray.Count; i++)
                {
                    if (_stray[i] && _stray[i].enabled)
                    {
                        _stray[i].enabled = false;
                    }
                }

                for (var i = 0; i < _strayBase.Count; i++)
                {
                    if (_strayBase[i] && _strayBase[i].m_Intensity != 0f)
                    {
                        _strayBase[i].m_Intensity = 0f;
                    }
                }

                if (_settings)
                {
                    _settings.SkyColor = Color.black;
                    _settings.EquatorColor = Color.black;
                    _settings.GroundColor = Color.black;
                    _settings.AmbientIntensity = 0f;
                    _settings.NightVisionSkyColor = Color.black;
                    _settings.NightVisionEquatorColor = Color.black;
                    _settings.NightVisionGroundColor = Color.black;
                    _settings.NightVisionAmbientIntensity = 0f;
                }

                for (var i = 0; _ambient != null && i < _ambient.Length; i++)
                {
                    if (_ambient[i])
                    {
                        _ambient[i].SetSH(default(SphericalHarmonicsL2));
                        _ambient[i].SetReflectionIntensity(0f);
                    }
                }

                RenderSettings.ambientIntensity = 0f;
                RenderSettings.ambientLight = Color.black;
                RenderSettings.reflectionIntensity = 0f;
            }

            // materials are shared assets and outlive the raid; everything else here dies with the scene
            private void OnDestroy()
            {
                Active = false;
                foreach (var pair in _emissive)
                {
                    var material = pair.Key;
                    if (!material)
                    {
                        continue;
                    }

                    foreach (var saved in pair.Value)
                    {
                        if (saved.Value is float number)
                        {
                            material.SetFloat(saved.Key, number);
                        }
                        else if (saved.Value is Color color)
                        {
                            material.SetColor(saved.Key, color);
                        }
                    }

                    if (_emissiveKeyword.Contains(material))
                    {
                        material.EnableKeyword("_EMISSION");
                    }
                }

                foreach (var pair in _specular)
                {
                    if (pair.Key)
                    {
                        pair.Key.SetVector(SpecVals, pair.Value);
                    }
                }

                if (_settings)
                {
                    _settings.SkyColor = _sky;
                    _settings.EquatorColor = _equator;
                    _settings.GroundColor = _ground;
                    _settings.AmbientIntensity = _intensity;
                    _settings.NightVisionSkyColor = _nvSky;
                    _settings.NightVisionEquatorColor = _nvEquator;
                    _settings.NightVisionGroundColor = _nvGround;
                    _settings.NightVisionAmbientIntensity = _nvIntensity;
                }
            }
        }
    }
}
