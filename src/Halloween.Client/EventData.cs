using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Manimal.Halloween2026.Client
{
    // plugin-data/zones.json, written by tools/Extract-QuestZones.py from the retail scenes
    internal static class EventData
    {
#pragma warning disable 0649 // filled by json
        internal sealed class Zone
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("kind")] public string Kind;
            [JsonProperty("raid")] public string Raid;
            [JsonProperty("position")] public float[] Position;
            [JsonProperty("rotation")] public float[] Rotation;
            [JsonProperty("scale")] public float[] Scale;
            [JsonProperty("center")] public float[] Center;
            [JsonProperty("size")] public float[] Size;
        }

        internal sealed class Body
        {
            [JsonProperty("raid")] public string Raid;
            [JsonProperty("container")] public string Container;
            [JsonProperty("position")] public float[] Position;
            [JsonProperty("rotation")] public float[] Rotation;
            // (count, weight) pairs
            [JsonProperty("loot")] public int[][] Loot;
        }

        private sealed class DataFile
        {
            [JsonProperty("zones")] public List<Zone> Zones;
            [JsonProperty("bodies")] public List<Body> Bodies;
        }
#pragma warning restore 0649

        private static DataFile _file;

        internal static List<Zone> Zones => Load().Zones;
        internal static List<Body> Bodies => Load().Bodies;

        private static DataFile Load()
        {
            if (_file != null)
            {
                return _file;
            }

            var path = Path.Combine(Plugin.PluginDir, "plugin-data", "zones.json");
            if (File.Exists(path))
            {
                _file = JsonConvert.DeserializeObject<DataFile>(File.ReadAllText(path));
            }
            else
            {
                Plugin.Log.LogError($"[Halloween2026] {path} is missing: no quest zones and no scientist bodies this session");
            }

            _file = _file ?? new DataFile();
            _file.Zones = _file.Zones ?? new List<Zone>();
            _file.Bodies = _file.Bodies ?? new List<Body>();
            return _file;
        }
    }
}
