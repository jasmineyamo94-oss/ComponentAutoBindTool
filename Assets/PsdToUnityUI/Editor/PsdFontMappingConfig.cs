using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PsdTools
{
    /// <summary>Single PSD font name → Unity Text / TextMeshPro font asset mapping entry.</summary>
    [System.Serializable]
    public class PsdFontMappingEntry
    {
        /// <summary>Font name as recorded in the PSD (matches Photoshop layer font name).</summary>
        public string psdFontName = "";
        /// <summary>Font asset path for legacy <see cref="UnityEngine.UI.Text"/> (Assets/...).</summary>
        public string legacyFontAssetPath = "";
        /// <summary>TMP_FONT asset path for TextMeshPro (Assets/...).</summary>
        public string tmpFontAssetPath = "";
    }

    /// <summary>Root structure of the font mapping JSON at Assets/PsdToUnityUI/Editor/EditorConfig/PSD_FontMapping.json.</summary>
    [System.Serializable]
    public class PsdFontMappingData
    {
        public PsdFontMappingEntry[] entries;
    }

    /// <summary>Read/write PSD font mapping config (for the editor window and export logic).</summary>
    public static class PsdFontMappingConfig
    {
        public static string ConfigPath => Path.Combine(Application.dataPath, "PsdToUnityUI", "EditorConfig", "PSD_FontMapping.json");

        public static PsdFontMappingData Load()
        {
            if (!File.Exists(ConfigPath))
                return new PsdFontMappingData { entries = new PsdFontMappingEntry[0] };

            try
            {
                string json = File.ReadAllText(ConfigPath);
                var data = JsonUtility.FromJson<PsdFontMappingData>(json);
                if (data == null)
                    return new PsdFontMappingData { entries = new PsdFontMappingEntry[0] };
                if (data.entries == null)
                    data.entries = new PsdFontMappingEntry[0];
                return data;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Failed to load font mapping config: {ex.Message}");
                return new PsdFontMappingData { entries = new PsdFontMappingEntry[0] };
            }
        }

        public static void Save(PsdFontMappingData data)
        {
            if (data == null)
                data = new PsdFontMappingData { entries = new PsdFontMappingEntry[0] };
            if (data.entries == null)
                data.entries = new PsdFontMappingEntry[0];

            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonUtility.ToJson(data, true);
                File.WriteAllText(ConfigPath, json);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Failed to save font mapping config: {ex.Message}");
            }
        }

        /// <summary>Merges new entries into the existing config (dedupe by psdFontName, case-insensitive) and saves.</summary>
        public static void MergeAndSaveNewEntries(IEnumerable<PsdFontMappingEntry> newEntries)
        {
            if (newEntries == null) return;
            var data = Load();
            var list = new List<PsdFontMappingEntry>();
            if (data.entries != null)
                list.AddRange(data.entries);
            var existing = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var e in list)
            {
                if (!string.IsNullOrEmpty(e.psdFontName))
                    existing.Add(e.psdFontName.Trim());
            }
            foreach (var n in newEntries)
            {
                if (string.IsNullOrEmpty(n.psdFontName)) continue;
                string key = n.psdFontName.Trim();
                if (existing.Contains(key)) continue;
                existing.Add(key);
                list.Add(new PsdFontMappingEntry
                {
                    psdFontName = key,
                    legacyFontAssetPath = n.legacyFontAssetPath ?? "",
                    tmpFontAssetPath = n.tmpFontAssetPath ?? ""
                });
            }
            data.entries = list.ToArray();
            Save(data);
        }
    }
} // namespace PsdTools
