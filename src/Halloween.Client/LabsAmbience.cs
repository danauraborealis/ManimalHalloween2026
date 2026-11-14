using System;
using System.Collections.Generic;
using System.IO;
using Comfort.Common;
using EFT;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;

namespace Manimal.Halloween2026.Client
{
    // the event's own ambience: retail's event sound scene, player by player, from
    // plugin-data/labs/ambience.json (tools/labs/Extract-EventAmbience.py). the 4.1 client has most of
    // these player classes but not retail's layout of them, so they are replayed by one component here,
    // on the game's rules: a loop, or a random clip after a random gap; some jump between fixed points,
    // some are only heard from certain rooms, some ride along a pipe.
    internal static class LabsAmbience
    {
#pragma warning disable 0649 // filled by json
        internal sealed class Box
        {
            [JsonProperty("center")] public float[] Center;
            [JsonProperty("rotation")] public float[] Rotation;
            [JsonProperty("half")] public float[] Half;
        }

        internal sealed class Route
        {
            [JsonProperty("points")] public float[][] Points;
            [JsonProperty("action")] public int Action;
            [JsonProperty("wait")] public float[] Wait;
            [JsonProperty("speed")] public float Speed;
            [JsonProperty("fade")] public bool Fade;
        }

        internal sealed class Row
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("kind")] public string Kind;
            [JsonProperty("group")] public string Group;
            [JsonProperty("volume")] public float Volume;
            [JsonProperty("deltaVolume")] public float DeltaVolume;
            [JsonProperty("pitch")] public float[] Pitch;
            [JsonProperty("fadeIn")] public float FadeIn;
            [JsonProperty("fadeOut")] public float FadeOut;
            [JsonProperty("spread")] public float Spread;
            [JsonProperty("spatialBlend")] public float SpatialBlend;
            [JsonProperty("minDistance")] public float MinDistance;
            [JsonProperty("maxDistance")] public float MaxDistance;
            [JsonProperty("rolloff")] public float[][] Rolloff;
            [JsonProperty("startDelay")] public float[] StartDelay;
            [JsonProperty("gap")] public float[] Gap;
            [JsonProperty("position")] public float[] Position;
            [JsonProperty("clips")] public string[] Clips;
            [JsonProperty("points")] public float[][] Points;
            [JsonProperty("pan")] public float[] Pan;
            [JsonProperty("rooms")] public Box[] Rooms;
            [JsonProperty("path")] public Route Path;
        }

        private sealed class Table
        {
            [JsonProperty("players")] public List<Row> Players;
        }
#pragma warning restore 0649

        private const string FallbackGroup = "CommonAmbInEffects";

        // how many players run; 0 means the map keeps its own sounds
        internal static int Start(Scene scene, string folder)
        {
            try
            {
                var path = Path.Combine(Plugin.PluginDir, "plugin-data", folder, "ambience.json");
                if (!File.Exists(path) || !scene.IsValid() || !scene.isLoaded)
                {
                    return 0;
                }

                var table = JsonConvert.DeserializeObject<Table>(File.ReadAllText(path));
                if (table == null || table.Players == null)
                {
                    return 0;
                }

                var host = new GameObject("Halloween2026_Ambience");
                SceneManager.MoveGameObjectToScene(host, scene);
                var runner = host.AddComponent<LabsAmbiencePlayers>();
                var groups = new Dictionary<string, AudioMixerGroup>();
                var missing = 0;
                for (var i = 0; i < table.Players.Count; i++)
                {
                    var voice = Build(table.Players[i], host.transform, groups);
                    if (voice == null)
                    {
                        missing++;
                        continue;
                    }

                    runner.Voices.Add(voice);
                }

                Plugin.Log.LogInfo($"[Halloween2026] event ambience ({folder}): {runner.Voices.Count} players"
                    + (missing > 0 ? $", {missing} left out for want of their clips" : ""));
                return runner.Voices.Count;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] the event ambience failed to start: {e}");
                return 0;
            }
        }

        private static LabsAmbiencePlayers.Voice Build(Row row, Transform parent, Dictionary<string, AudioMixerGroup> groups)
        {
            var clips = new List<AudioClip>();
            for (var i = 0; row.Clips != null && i < row.Clips.Length; i++)
            {
                var clip = LayerSounds.Clip(row.Clips[i]);
                if (clip)
                {
                    clips.Add(clip);
                }
            }

            if (clips.Count == 0)
            {
                return null;
            }

            var holder = new GameObject(row.Name ?? "player");
            holder.transform.SetParent(parent, false);
            holder.transform.position = Vec(row.Position);

            var source = holder.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.dopplerLevel = 0f;
            source.volume = 0f;
            source.spatialBlend = Mathf.Clamp01(row.SpatialBlend);
            source.minDistance = row.MinDistance;
            source.maxDistance = Mathf.Max(row.MaxDistance, row.MinDistance + 0.1f);
            source.spread = Mathf.Lerp(180f, 0f, Mathf.Clamp01(row.Spread));
            if (row.Rolloff != null && row.Rolloff.Length >= 2)
            {
                var keys = new Keyframe[row.Rolloff.Length];
                for (var k = 0; k < keys.Length; k++)
                {
                    var key = row.Rolloff[k];
                    keys[k] = new Keyframe(Mathf.Clamp01(key[0]), Mathf.Clamp01(key[1]), key[2], key[3]);
                }

                source.rolloffMode = AudioRolloffMode.Custom;
                source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, new AnimationCurve(keys));
            }
            else
            {
                source.rolloffMode = AudioRolloffMode.Linear;
            }

            source.outputAudioMixerGroup = Group(row.Group, groups);

            var voice = new LabsAmbiencePlayers.Voice
            {
                Source = source,
                Loop = row.Kind == "loop",
                Clips = clips,
                Volume = row.Volume,
                DeltaVolume = row.DeltaVolume,
                Pitch = Pair(row.Pitch, 1f, 1f),
                FadeIn = row.FadeIn,
                FadeOut = row.FadeOut,
                Gap = Pair(row.Gap, 15f, 20f),
                Pan = row.Pan != null && row.Pan.Length == 4 ? row.Pan : null,
            };
            source.loop = voice.Loop;
            var delay = Pair(row.StartDelay, 0f, 0f);
            voice.Next = Time.time + UnityEngine.Random.Range(delay.x, delay.y)
                + (voice.Loop ? 0f : UnityEngine.Random.Range(voice.Gap.x, voice.Gap.y));

            if (row.Points != null && row.Points.Length > 0)
            {
                voice.Points = new Vector3[row.Points.Length];
                for (var i = 0; i < row.Points.Length; i++)
                {
                    voice.Points[i] = Vec(row.Points[i]);
                }
            }

            // an observer with no rooms never gates anything in the game either
            if (row.Rooms != null && row.Rooms.Length > 0)
            {
                voice.Rooms = new LabsAmbiencePlayers.Room[row.Rooms.Length];
                for (var i = 0; i < row.Rooms.Length; i++)
                {
                    var box = row.Rooms[i];
                    voice.Rooms[i] = new LabsAmbiencePlayers.Room
                    {
                        Center = Vec(box.Center),
                        Inverse = Quaternion.Inverse(new Quaternion(box.Rotation[0], box.Rotation[1], box.Rotation[2], box.Rotation[3])),
                        Half = Vec(box.Half),
                    };
                }
            }

            if (row.Path != null && row.Path.Points != null && row.Path.Points.Length >= 2 && row.Path.Speed > 0f)
            {
                var points = new Vector3[row.Path.Points.Length];
                var lengths = new float[points.Length];
                for (var i = 0; i < points.Length; i++)
                {
                    points[i] = Vec(row.Path.Points[i]);
                    lengths[i] = i == 0 ? 0f : lengths[i - 1] + Vector3.Distance(points[i - 1], points[i]);
                }

                voice.Path = points;
                voice.PathLengths = lengths;
                voice.PathSpeed = row.Path.Speed;
                voice.PathAction = row.Path.Action;
                voice.PathFade = row.Path.Fade;
                voice.PathWait = Pair(row.Path.Wait, 0f, 0f);
                voice.PathUntil = Time.time + UnityEngine.Random.Range(voice.PathWait.x, voice.PathWait.y);
                holder.transform.position = points[0];
            }

            return voice;
        }

        private static AudioMixerGroup Group(string name, Dictionary<string, AudioMixerGroup> groups)
        {
            name = string.IsNullOrEmpty(name) ? FallbackGroup : name;
            if (!groups.TryGetValue(name, out var group))
            {
                group = LayerSounds.FindGroup(name);
                if (!group && name != FallbackGroup)
                {
                    group = Group(FallbackGroup, groups);
                }

                groups[name] = group;
            }

            return group;
        }

        private static Vector3 Vec(float[] v) => v != null && v.Length >= 3 ? new Vector3(v[0], v[1], v[2]) : Vector3.zero;

        private static Vector2 Pair(float[] v, float a, float b) => v != null && v.Length >= 2 ? new Vector2(v[0], v[1]) : new Vector2(a, b);
    }

    internal sealed class LabsAmbiencePlayers : MonoBehaviour
    {
        internal struct Room
        {
            public Vector3 Center;
            public Quaternion Inverse;
            public Vector3 Half;
        }

        internal sealed class Voice
        {
            public AudioSource Source;
            public bool Loop;
            public List<AudioClip> Clips;
            public float Volume, DeltaVolume, FadeIn, FadeOut;
            public Vector2 Pitch, Gap;
            public float[] Pan;
            public Vector3[] Points;
            public Room[] Rooms;

            public Vector3[] Path;
            public float[] PathLengths;
            public float PathSpeed;
            public int PathAction;
            public bool PathFade;
            public Vector2 PathWait;
            public float PathUntil, PathProgress, PathLevel = 1f;
            public bool PathMoving, PathBackward, PathDone;

            public bool Heard = true;
            public float Level, Current, Next;
            public int Picked = -1;
        }

        internal readonly List<Voice> Voices = new List<Voice>();

        private const float RoomCheckEvery = 0.25f;
        private float _nextRoomCheck;

        private void Update()
        {
            var now = Time.time;
            var dt = Time.deltaTime;
            if (now >= _nextRoomCheck)
            {
                _nextRoomCheck = now + RoomCheckEvery;
                CheckRooms(now);
            }

            for (var i = 0; i < Voices.Count; i++)
            {
                var voice = Voices[i];
                if (!voice.Source)
                {
                    continue;
                }

                if (voice.Path != null)
                {
                    Ride(voice, now, dt);
                }

                var fade = voice.Heard ? voice.FadeIn : voice.FadeOut;
                voice.Level = fade <= 0f ? (voice.Heard ? 1f : 0f) : Mathf.MoveTowards(voice.Level, voice.Heard ? 1f : 0f, dt / fade);
                voice.Source.volume = voice.Current * voice.Level * voice.PathLevel;

                if (voice.Loop)
                {
                    if (voice.Heard && !voice.Source.isPlaying)
                    {
                        voice.Current = Loudness(voice);
                        voice.Source.pitch = UnityEngine.Random.Range(voice.Pitch.x, voice.Pitch.y);
                        voice.Source.clip = voice.Clips[0];
                        voice.Source.Play();
                    }
                    else if (!voice.Heard && voice.Level <= 0f && voice.Source.isPlaying)
                    {
                        voice.Source.Stop();
                    }
                }
                else if (voice.Heard && now >= voice.Next && !voice.Source.isPlaying)
                {
                    Shot(voice, now);
                }
            }
        }

        private void CheckRooms(float now)
        {
            var world = Singleton<GameWorld>.Instance;
            var player = world ? world.MainPlayer : null;
            if (!player)
            {
                return;
            }

            var at = player.Position;
            for (var i = 0; i < Voices.Count; i++)
            {
                var voice = Voices[i];
                if (voice.Rooms == null)
                {
                    continue;
                }

                var inside = false;
                for (var r = 0; r < voice.Rooms.Length && !inside; r++)
                {
                    var local = voice.Rooms[r].Inverse * (at - voice.Rooms[r].Center);
                    var half = voice.Rooms[r].Half;
                    inside = Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z;
                }

                // the game restarts a random player on entry, so its first clip waits out a gap
                if (inside && !voice.Heard && !voice.Loop && voice.Next <= now)
                {
                    voice.Next = now + UnityEngine.Random.Range(voice.Gap.x, voice.Gap.y);
                }

                voice.Heard = inside;
            }
        }

        private static float Loudness(Voice voice)
        {
            return voice.DeltaVolume <= 0f ? voice.Volume : voice.Volume + UnityEngine.Random.Range(-voice.DeltaVolume, 0f);
        }

        // the game's cycle: wait out a random gap, play a clip to its end, start over
        private static void Shot(Voice voice, float now)
        {
            var pick = UnityEngine.Random.Range(0, voice.Clips.Count);
            if (voice.Clips.Count > 1 && pick == voice.Picked)
            {
                pick = (pick + 1) % voice.Clips.Count;
            }

            voice.Picked = pick;
            var clip = voice.Clips[pick];
            var pitch = UnityEngine.Random.Range(voice.Pitch.x, voice.Pitch.y);
            if (voice.Points != null)
            {
                voice.Source.transform.position = voice.Points[UnityEngine.Random.Range(0, voice.Points.Length)];
            }

            if (voice.Pan != null)
            {
                voice.Source.panStereo = UnityEngine.Random.value < 0.5f
                    ? UnityEngine.Random.Range(voice.Pan[0], voice.Pan[1])
                    : UnityEngine.Random.Range(voice.Pan[2], voice.Pan[3]);
            }

            voice.Current = Loudness(voice);
            voice.Source.volume = voice.Current * voice.Level * voice.PathLevel;
            voice.Source.pitch = pitch;
            voice.Source.clip = clip;
            voice.Source.Play();
            voice.Next = now + clip.length / Mathf.Max(0.1f, pitch) + UnityEngine.Random.Range(voice.Gap.x, voice.Gap.y);
        }

        // a pipe emitter: fades out and waits, then runs the length of its path, and again.
        // speed is taken as path lengths per second, the reading the game's 0..1 progress suggests.
        private static void Ride(Voice voice, float now, float dt)
        {
            if (voice.PathDone)
            {
                return;
            }

            if (!voice.PathMoving)
            {
                if (voice.PathFade)
                {
                    voice.PathLevel = Mathf.MoveTowards(voice.PathLevel, 0f, dt / Mathf.Max(0.3f, voice.PathWait.x));
                }

                if (now < voice.PathUntil)
                {
                    return;
                }

                voice.PathMoving = true;
                voice.PathProgress = 0f;
            }

            if (voice.PathFade)
            {
                voice.PathLevel = Mathf.MoveTowards(voice.PathLevel, 1f, dt / Mathf.Max(0.3f, voice.FadeIn));
            }

            voice.PathProgress += voice.PathSpeed * dt;
            var along = Mathf.Clamp01(voice.PathBackward ? 1f - voice.PathProgress : voice.PathProgress);
            voice.Source.transform.position = PointAt(voice, along);
            if (voice.PathProgress < 1f)
            {
                return;
            }

            // 0 stop, 1 continue, 2 repeat, 3 reverse
            voice.PathMoving = false;
            if (voice.PathAction == 0)
            {
                voice.PathDone = true;
                return;
            }

            if (voice.PathAction == 3)
            {
                voice.PathBackward = !voice.PathBackward;
            }

            voice.PathUntil = now + UnityEngine.Random.Range(voice.PathWait.x, voice.PathWait.y);
        }

        private static Vector3 PointAt(Voice voice, float along)
        {
            var lengths = voice.PathLengths;
            var target = along * lengths[lengths.Length - 1];
            for (var i = 1; i < lengths.Length; i++)
            {
                if (target <= lengths[i])
                {
                    var span = lengths[i] - lengths[i - 1];
                    return Vector3.Lerp(voice.Path[i - 1], voice.Path[i], span <= 0f ? 0f : (target - lengths[i - 1]) / span);
                }
            }

            return voice.Path[voice.Path.Length - 1];
        }
    }
}
