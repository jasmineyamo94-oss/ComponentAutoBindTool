using System.IO;
using UnityEngine;

namespace PsdTools
{
    // ────────────────────────────────────────────────────────────────────────────
    // Nine-slice parameter JSON schema
    // Path: Assets/PsdToUnityUI/EditorConfig/PSD_NineSliceConfig.json
    // ────────────────────────────────────────────────────────────────────────────
    [System.Serializable]
    public class PSDNineSliceConfigData
    {
        /// <summary>Border inset in pixels to avoid slicing on color boundaries (same as testforpsd.py BORDER_INSET).</summary>
        public int borderInset      = 2;
        /// <summary>Per-channel RGBA difference threshold (0–255) for adjacent columns/rows; above this, slicing is not allowed.</summary>
        public int pixelThreshold   = 10;
        /// <summary>Max target center column count when nine-slice participates (horizontal).</summary>
        public int minCenterCols    = 10;
        /// <summary>Max target center row count when nine-slice participates (vertical).</summary>
        public int minCenterRows    = 10;
        /// <summary>Minimum length (px) of longest continuous sliceable band for a direction to count as “strong”.</summary>
        public int minSameZone      = 15;
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Dedup parameter JSON schema
    // Path: Assets/PsdToUnityUI/EditorConfig/PSD_DedupConfig.json
    // ────────────────────────────────────────────────────────────────────────────
    [System.Serializable]
    public class PSDDedupConfigData
    {
        /// <summary>
        /// MAE dedup threshold (premultiplied RGBA, 0–1): images with mean absolute error per channel at or below this after scaling fingerprints are treated as identical.
        /// Smaller = stricter, larger = looser. Default 0.06.
        /// </summary>
        public float maeThreshold        = 0.04f;
        /// <summary>
        /// Fingerprint resize target N (RGBA fingerprint taken from N×N downscale).
        /// Smaller = faster but weaker discrimination; larger = more accurate. Default 8.
        /// </summary>
        public int   fingerprintSize     = 8;
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Nine-slice config — Load / Save
    // ────────────────────────────────────────────────────────────────────────────
    public static class PSDNineSliceConfig
    {
        public static string ConfigPath =>
            Path.Combine(Application.dataPath, "PsdToUnityUI", "EditorConfig", "PSD_NineSliceConfig.json");

        private static PSDNineSliceConfigData _cache;

        public static PSDNineSliceConfigData Load(bool forceReload = false)
        {
            if (_cache != null && !forceReload) return _cache;

            if (!File.Exists(ConfigPath))
            {
                _cache = new PSDNineSliceConfigData();
                return _cache;
            }
            try
            {
                var data = JsonUtility.FromJson<PSDNineSliceConfigData>(File.ReadAllText(ConfigPath));
                _cache = data ?? new PSDNineSliceConfigData();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[PSDNineSliceConfig] Load failed: {ex.Message}. Using defaults.");
                _cache = new PSDNineSliceConfigData();
            }
            return _cache;
        }

        public static void Save(PSDNineSliceConfigData data)
        {
            if (data == null) data = new PSDNineSliceConfigData();
            _cache = data;
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(ConfigPath, JsonUtility.ToJson(data, true));
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[PSDNineSliceConfig] Save failed: {ex.Message}");
            }
        }

        /// <summary>Next Load() will read from disk again (call before export to pick up latest values).</summary>
        public static void Invalidate() => _cache = null;
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Dedup config — Load / Save
    // ────────────────────────────────────────────────────────────────────────────
    public static class PSDDedupConfig
    {
        public static string ConfigPath =>
            Path.Combine(Application.dataPath, "PsdToUnityUI", "EditorConfig", "PSD_DedupConfig.json");

        private static PSDDedupConfigData _cache;

        public static PSDDedupConfigData Load(bool forceReload = false)
        {
            if (_cache != null && !forceReload) return _cache;

            if (!File.Exists(ConfigPath))
            {
                _cache = new PSDDedupConfigData();
                return _cache;
            }
            try
            {
                var data = JsonUtility.FromJson<PSDDedupConfigData>(File.ReadAllText(ConfigPath));
                _cache = data ?? new PSDDedupConfigData();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[PSDDedupConfig] Load failed: {ex.Message}. Using defaults.");
                _cache = new PSDDedupConfigData();
            }
            return _cache;
        }

        public static void Save(PSDDedupConfigData data)
        {
            if (data == null) data = new PSDDedupConfigData();
            _cache = data;
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(ConfigPath, JsonUtility.ToJson(data, true));
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[PSDDedupConfig] Save failed: {ex.Message}");
            }
        }

        public static void Invalidate() => _cache = null;
    }
} // namespace PsdTools
