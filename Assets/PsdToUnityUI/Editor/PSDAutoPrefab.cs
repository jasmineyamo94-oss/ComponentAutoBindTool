using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.UI;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Security.Cryptography;
using PsdTools;
using PsdTools.Layers;
using PsdTools.Constants;
using System.Text.RegularExpressions;

#if USE_TMP
using TMPro;
#endif

namespace PsdTools
{
    /// <summary>EditorPrefs keys and defaults for nine-slice detection and export (menu Tools/PSD/Nine-slice settings).</summary>
    internal static class PSDNineSlicePrefs
    {
        public const string KeyBorderInset = "PSDAutoPrefab_BorderInset";
        public const int DefaultBorderInset = 2;

        public const string KeyPixelThreshold = "PSDAutoPrefab_NineSlice_PixelThreshold";
        public const int DefaultPixelThreshold = 10;

        public const string KeyMinCenterCols = "PSDAutoPrefab_NineSlice_MinCenterCols";
        public const int DefaultMinCenterCols = 10;

        public const string KeyMinCenterRows = "PSDAutoPrefab_NineSlice_MinCenterRows";
        public const int DefaultMinCenterRows = 10;

        public const string KeyMinSameZone = "PSDAutoPrefab_NineSlice_MinSameZone";
        public const int DefaultMinSameZone = 15;
    }

    /// <summary>EditorPrefs keys and defaults for image dedup settings (menu Tools/PSD/Dedup settings).</summary>
    internal static class PSDDedupPrefs
    {
        /// <summary>
        /// MAE dedup threshold (0~1, premultiplied RGBA): mean abs error per channel after scaled fingerprint ≤ this ⇒ same image.
        /// Lower = stricter (harder to merge), higher = looser (easier to merge). Default 0.06.
        /// </summary>
        public const string KeyMaeThreshold = "PSDAutoPrefab_Dedup_MaeThreshold";
        public const float DefaultMaeThreshold = 0.04f;
        public const float MinMaeThreshold = 0.001f;
        public const float MaxMaeThreshold = 0.5f;

        /// <summary>
        /// Target size for dedup fingerprint (each image scaled to N×N then RGBA fingerprint).
        /// Smaller = faster but weaker discrimination; larger = more accurate but slower. Default 8 (8×8 = 64 samples).
        /// </summary>
        public const string KeyFingerprintSize = "PSDAutoPrefab_Dedup_FingerprintSize";
        public const int DefaultFingerprintSize = 8;
        public const int MinFingerprintSize = 4;
        public const int MaxFingerprintSize = 32;
    }

    public class PSDAutoPrefab : EditorWindow 
    {
        // Output folder absolute path (sliced PNGs, etc.)
        private static string _outputFolder;
        /// <summary>Subfolder name under PSDCache for this export (from _outputFolder), avoids filename clashes across batches.</summary>
        private static string _psdCacheBatchKey;
        /// <summary>true: filename nodeName_LayerId.png; false: nodeName only (duplicate names risk PSDCache clash; export aborts after slicing).</summary>
        private static bool _useAutoImageNaming = true;
        /// <summary>Assets root for Prefab output (editor or menu single-path mode), e.g. "Assets" or "Assets/UI/Prefabs".</summary>
        private static string _prefabExportAssetsRootRelative;

        /// <summary>Assets path of main Prefab from last "export slices + build Prefab" (e.g. "Assets/UI/Prefabs/xxx.prefab"); kept on failure.</summary>
        public static string LastGeneratedPrefabAssetPath { get; private set; }

        /// <summary>Last image Assets folder written (slice or full export, e.g. "Assets/UI/Images/xxx"); kept on failure.</summary>
        public static string LastExportedImageAssetsFolder { get; private set; }

        /// <summary>Whether to compare node names during difference comparison (passed from PSD editor).</summary>
        public static bool CompareNameDiff { get; set; } = true;


        private static Dictionary<Layer, string> _layerImagePaths;
        
        // PSD canvas size
        private static int _canvasWidth;
        private static int _canvasHeight;

        /// <summary>PsdImage reference for the current export session; used by merge-export to composite groups with clipping support.</summary>
        private static PsdImage _psdImage;

        // Nine-slice detection: Layer -> Vector4(left, bottom, right, top)
        private static Dictionary<Layer, Vector4> _layerSliceBorders;

        // Layers merged by clipping mask (not exported / no separate nodes)
        private static HashSet<Layer> _clippedLayers;

        // Merge-export config: LayerId -> merge export (PSD editor "Merge display" → _export_config.json)
        private static Dictionary<int, bool> _mergeExportConfig;
        // Export Prefab: LayerId set; when exporting main Prefab, also export a standalone Prefab per layer
        private static HashSet<int> _exportPrefabLayerIds;
        /// <summary>Final asset paths for per-node Prefabs this export (after user rename); referenced from main Prefab.</summary>
        private static Dictionary<int, string> _exportPrefabAssetPathByLayerId;
        // External Prefab: LayerId -> asset path; no slice; instantiate Prefab and keep placement
        private static Dictionary<int, string> _externalPrefabByLayerId;
        private static Dictionary<int, bool> _externalPrefabReusePosition;
        private static Dictionary<int, bool> _externalPrefabReuseSize;
        /// <summary>Participate in local dedup: LayerId -> compare with other images in this export; default true.</summary>
        private static Dictionary<int, bool> _participateLocalDedupByLayerId;
        /// <summary>Participate in common-dir dedup: config "compare with images already in common dirs"; see <see cref="GetParticipatesCommonDirectoryCacheDedup"/>.</summary>
        private static Dictionary<int, bool> _participateCommonDedupByLayerId;
        /// <summary>Slice (nine-slice): LayerId -> true = nine-slice path; false = full image; missing config falls back to the editor default.</summary>
        private static Dictionary<int, bool> _sliceImageByLayerId;
        /// <summary>Local dedup primary node: if any checked in group, virtual rep uses primary (largest if several).</summary>
        private static Dictionary<int, bool> _primaryDedupNodeByLayerId;
        /// <summary>Use custom image: LayerId -> user Sprite; skip slice/nine-slice/dedup/Prefab swap.</summary>
        private static Dictionary<int, bool> _useCustomImageByLayerId;
        /// <summary>Custom image Assets path (e.g. Assets/xxx.png); only when useCustomImage is true.</summary>
        private static Dictionary<int, string> _customImagePathByLayerId;
        /// <summary>Export this node: if false, no slice, no Prefab node (default true).</summary>
        private static Dictionary<int, bool> _exportedByLayerId;
        /// <summary>Full layer export config (uiComponentType, etc.) for UI component handlers.</summary>
        private static Dictionary<int, LayerConfigEntry> _layerConfigByLayerId;
        /// <summary>Type layer uses TextMeshPro (LayerId → config; default false if missing).</summary>
        private static Dictionary<int, bool> _useTextMeshProByLayerId;
        /// <summary>Same as <see cref="_useTextMeshProByLayerId"/> but keyed by Layer reference so it survives <see cref="PsdImage.ReleaseAllData"/> (which clears TaggedBlock data and makes <see cref="Layer.LayerId"/> return null).</summary>
        private static Dictionary<Layer, bool> _useTextMeshProByLayerRef;
        /// <summary>PSD file path for the current export session; stored so that the "Fresh Overwrite" delayCall callback can re-invoke <see cref="RunGenerateFromPsdCore"/> after PSD data has been released.</summary>
        private static string _sessionPsdPath;
        /// <summary>autoImageNaming value for the current export session; mirrors the parameter passed to <see cref="RunGenerateFromPsdCore"/>.</summary>
        private static bool _sessionAutoImageNaming = true;
        /// <summary>When true, the diff-window is waiting for user decision; <see cref="RunGenerateFromPsdCore"/>'s finally block stores psd here instead of releasing it, and <see cref="PsdCacheExportCleanup"/> skips <see cref="ReleaseExportPinnedStateCore"/>.</summary>
        private static bool _deferPsdRelease = false;
        /// <summary>PsdImage held alive until the diff-window callback fires (see <see cref="_deferPsdRelease"/>).</summary>
        private static PsdImage _deferredPsd = null;
        private const string PrefDefaultSliceImage = "PSDEditor_DefaultSliceImage";

        private sealed class TmpImportRequiredException : System.Exception
        {
            public TmpImportRequiredException(string message) : base(message) { }
        }

        private static bool GetDefaultSliceImagePreference()
        {
            return EditorPrefs.GetBool(PrefDefaultSliceImage, true);
        }
    #if USE_TMP
        /// <summary>TMP face material variant key → Material (avoid duplicate loads per export).</summary>
        private static Dictionary<string, Material> _tmpFaceMaterialVariantCache;
    #endif
        /// <summary>LayerId → Layer map; sync layer names from _export_config.json.</summary>
        private static Dictionary<int, Layer> _layersById;

        /// <summary>During Prefab build: maps scene-side GameObject instanceID to PSD layer ID; used to resolve fileIDs after save.</summary>
        private static Dictionary<int, int> _goInstanceIdToLayerId;

        /// <summary>During incremental patch: layer IDs that already exist in the Prefab and must not be recreated when new parent groups are built via CreateLayerGameObject.</summary>
        private static HashSet<int> _existingLayerIdsSkipForAdd;

        /// <summary>Current export: PSD font name → mapping entry (case-insensitive).</summary>
        private static Dictionary<string, PsdFontMappingEntry> _psdFontMappingLookup;
        /// <summary>PSD font names missing from mapping this export (unique set).</summary>
        private static HashSet<string> _unrecognizedPsdFontNamesThisExport;
        /// <summary>New entries to merge into PSD_FontMapping.json.</summary>
        private static List<PsdFontMappingEntry> _pendingFontMappingEntries;

        /// <summary>Dedup entry: fingerprint and metadata for exported image; MAE comparison.</summary>
        private struct DedupEntry
        {
            public float[] fingerprint;
            public string fullPath;
            public Vector4? sliceBorder;
            /// <summary>Nine-slice signature (slice flag, params, detected border); avoids wrong dedup same fingerprint.</summary>
            public string nineSliceDedupKey;
        }
        private static List<DedupEntry> _dedupEntries;

        /// <summary>Common-dir dedup: fingerprint and size of an existing image for comparison.</summary>
        private struct CommonDirImageEntry
        {
            public string fullPath;
            public float[] fingerprint;
            public int width;
            public int height;
        }
        private static List<CommonDirImageEntry> _commonDirImageCache;

        /// <summary>Slicing defers disk write; duplicate basenames are auto-renamed when auto-naming is off; write to PSDCache via <see cref="TryFinishPngExportAfterSlicePass"/>.
        /// Each entry carries the full list of affected layers (<see cref="SaveLayerTextureGrouped"/>'s groupMembers) so that <see cref="ResolveDuplicateBasenamesInPendingPsdWrites"/> can patch <see cref="_layerImagePaths"/> precisely without relying on path-string equality (which breaks when two distinct entries share the same original path).</summary>
        private static List<(string fullPath, byte[] pngBytes, List<Layer> layers)> _pendingPsdCacheWrites;

        /// <summary>After full tree walk: local dedup groups on source, then slice, common dedup, write.</summary>
        private static Dictionary<Layer, Texture2D> _rasterPending;

        /// <summary>Common directories config JSON path; same as PSDEditorWindow.</summary>
        private static string CommonDirectoriesConfigPath => Path.Combine(Application.dataPath, "PsdToUnityUI", "EditorConfig", "PSD_CommonDirectories.json");

        /// <summary>Fingerprint scale (image scaled to this size). Adjust via Tools/PSD/Dedup settings; see <see cref="DedupFingerprintSize"/>.</summary>
        private const int DEDUP_FINGERPRINT_SIZE = 8;
        /// <summary>Mean abs error threshold in premultiplied RGBA (0-1); below = same image. Tools/PSD/Dedup settings; <see cref="DedupMaeThreshold"/>.</summary>
        private const float DEDUP_MAX_DIFF = 0.06f;

        // ===== Nine-slice params (PSDNineSlicePrefs; edit Tools/PSD/Nine-slice settings) =====

        /// <summary>Per-channel RGBA delta threshold for adjacent columns/rows; exceeding it blocks a slice. Reads PSD_NineSliceConfig.json.</summary>
        private static int NineSlicePixelThreshold => PSDNineSliceConfig.Load().pixelThreshold;

        /// <summary>When nine-slice runs: horizontal center compression target column cap (min with croppable region).</summary>
        private static int NineSliceMinCenterCols => PSDNineSliceConfig.Load().minCenterCols;

        /// <summary>When nine-slice runs: vertical center compression target row cap.</summary>
        private static int NineSliceMinCenterRows => PSDNineSliceConfig.Load().minCenterRows;

        /// <summary>Minimum length (px) of longest continuous sliceable band for the "strong" axis.</summary>
        private static int NineSliceMinSameZone => PSDNineSliceConfig.Load().minSameZone;

        /// <summary>Nine-slice border inset in pixels; avoids cutting on color boundaries (same as testforpsd.py BORDER_INSET).</summary>
        private static int BorderInset => PSDNineSliceConfig.Load().borderInset;

        /// <summary>
        /// MAE dedup threshold (premultiplied RGBA, 0~1): mean abs error after scaled fingerprint ≤ this ⇒ same image.
        /// PSD_DedupConfig.json maeThreshold, default 0.04. Reads PSD_DedupConfig.json.
        /// </summary>
        private static float DedupMaeThreshold =>
            Mathf.Clamp(PSDDedupConfig.Load().maeThreshold,
                PSDDedupPrefs.MinMaeThreshold, PSDDedupPrefs.MaxMaeThreshold);

        /// <summary>
        /// Dedup fingerprint size N (image scaled to N×N for RGBA fingerprint).
        /// PSD_DedupConfig.json fingerprintSize, default 8. Reads PSD_DedupConfig.json.
        /// </summary>
        private static int DedupFingerprintSize =>
            Mathf.Clamp(PSDDedupConfig.Load().fingerprintSize,
                PSDDedupPrefs.MinFingerprintSize, PSDDedupPrefs.MaxFingerprintSize);

        [MenuItem("Tools/PSD/Dedup settings...")]
        public static void ShowDedupSettings()
        {
            var w = GetWindow<PSDDedupSettingsWindow>("Image deduplication settings");
            w.minSize = new Vector2(420, 360);
        }

        [MenuItem("Tools/PSD/Nine-slice settings...")]
        public static void ShowNineSliceSettings()
        {
            var w = GetWindow<PSDNineSliceSettingsWindow>("Nine-slice settings");
            w.minSize = new Vector2(420, 340);
        }

        // [MenuItem("Tools/PSD/Generate UI Prefab from PSD (C# library)")]
        // public static void GenerateFromPSD() 
        // {
        //     string psdPath = EditorUtility.OpenFilePanel("Choose PSD file", "", "psd");
        //     if (string.IsNullOrEmpty(psdPath)) return;
        //     GenerateFromPSD(psdPath);
        // }

        /// <summary>Export from PSD (menu path): slices next to PSD as {psdName}; Prefab at Assets/{psdName}.prefab.</summary>
        public static void GenerateFromPSD(string psdPath)
        {
            string psdDir = Path.GetDirectoryName(psdPath);
            string psdName = Path.GetFileNameWithoutExtension(psdPath);
            _prefabExportAssetsRootRelative = "Assets";
            _outputFolder = Path.Combine(psdDir, psdName);
            if (!Directory.Exists(_outputFolder))
                Directory.CreateDirectory(_outputFolder);
            RunGenerateFromPsdCore(psdPath, psdName);
            TrimExportManagedHeapAfterSession();
        }

        /// <summary>Export from PSD (editor): images under image root as {psdName}; Prefab under prefab root.</summary>
        public static void GenerateFromPSD(string psdPath, string imageAssetsExportRoot, string prefabAssetsExportRoot, bool autoImageNaming = true)
        {
            string psdName = Path.GetFileNameWithoutExtension(psdPath);
            string imageRoot = NormalizeAssetsExportRoot(imageAssetsExportRoot);
            _prefabExportAssetsRootRelative = NormalizeAssetsExportRoot(prefabAssetsExportRoot);
            string diskRoot = GetDiskPathUnderAssetsRoot(imageRoot);
            _outputFolder = Path.Combine(diskRoot, psdName);
            if (!Directory.Exists(_outputFolder))
                Directory.CreateDirectory(_outputFolder);
            LastExportedImageAssetsFolder = imageRoot + "/" + psdName;
            LastGeneratedPrefabAssetPath = null;
            RunGenerateFromPsdCore(psdPath, psdName, autoImageNaming);
            TrimExportManagedHeapAfterSession();
        }

        /// <summary>Export textures only from PSD (slice/nine-slice/dedup, etc.); no Prefabs.</summary>
        public static void ExportTexturesFromPSD(string psdPath, string imageAssetsExportRoot, bool autoImageNaming = true)
        {
            string psdName = Path.GetFileNameWithoutExtension(psdPath);
            string imageRoot = NormalizeAssetsExportRoot(imageAssetsExportRoot);
            string diskRoot = GetDiskPathUnderAssetsRoot(imageRoot);
            _outputFolder = Path.Combine(diskRoot, psdName);
            if (!Directory.Exists(_outputFolder))
                Directory.CreateDirectory(_outputFolder);
            LastExportedImageAssetsFolder = imageRoot + "/" + psdName;
            RunExportTexturesCore(psdPath, psdName, autoImageNaming);
            TrimExportManagedHeapAfterSession();
        }

        /// <summary>
        /// PSD editor "Export now": slice only the chosen layer subtree (nine-slice/dedup, etc.); standalone Prefabs for nodes with "Export Prefab" in that subtree; no full-document main Prefab.
        /// </summary>
        public static void GenerateSubtreeExportFromPSD(string psdPath, string imageAssetsExportRoot, string prefabAssetsExportRoot, int subtreeRootLayerId, bool autoImageNaming = true)
        {
            string psdName = Path.GetFileNameWithoutExtension(psdPath);
            string imageRoot = NormalizeAssetsExportRoot(imageAssetsExportRoot);
            _prefabExportAssetsRootRelative = NormalizeAssetsExportRoot(prefabAssetsExportRoot);
            string diskRoot = GetDiskPathUnderAssetsRoot(imageRoot);
            _outputFolder = Path.Combine(diskRoot, psdName);
            if (!Directory.Exists(_outputFolder))
                Directory.CreateDirectory(_outputFolder);
            RunGenerateSubtreeCore(psdPath, psdName, subtreeRootLayerId, autoImageNaming);
            TrimExportManagedHeapAfterSession();
        }

        /// <summary>
        /// Single-layer export with no dedup: rasterize just this one layer (or merged group) and write PNG directly.
        /// Nine-slice and save-to-common-directory follow the layer's config; dedup (local and common-dir) is entirely skipped.
        /// The output folder is never deleted regardless of the "clear before export" setting.
        /// </summary>
        public static void ExportSingleLayerNoDedupFromPSD(string psdPath, string imageAssetsExportRoot, int targetLayerId, bool autoImageNaming = true)
        {
            string psdName = Path.GetFileNameWithoutExtension(psdPath);
            string imageRoot = NormalizeAssetsExportRoot(imageAssetsExportRoot);
            string diskRoot = GetDiskPathUnderAssetsRoot(imageRoot);
            _outputFolder = Path.Combine(diskRoot, psdName);
            // Never delete the folder on single-layer export; only create if missing
            if (!Directory.Exists(_outputFolder))
                Directory.CreateDirectory(_outputFolder);
            LastExportedImageAssetsFolder = imageRoot + "/" + psdName;
            RunSingleLayerNoDedupCore(psdPath, psdName, targetLayerId, autoImageNaming);
            TrimExportManagedHeapAfterSession();
        }

        private static void RunSingleLayerNoDedupCore(string psdPath, string psdName, int targetLayerId, bool autoImageNaming = true)
        {
            _useAutoImageNaming = autoImageNaming;
            InitPsdCacheBatchKeyForCurrentExport();
            PsdImage psd = null;
            using (new PsdCacheExportCleanup())
            {
            try
            {
            _layerImagePaths = new Dictionary<Layer, string>();
            _layerSliceBorders = new Dictionary<Layer, Vector4>();
            _dedupEntries = new List<DedupEntry>();
            _clippedLayers = new HashSet<Layer>();
            _pendingPsdCacheWrites = new List<(string fullPath, byte[] pngBytes, List<Layer> layers)>();
            _rasterPending = new Dictionary<Layer, Texture2D>();
#if USE_TMP
            _tmpFaceMaterialVariantCache = new Dictionary<string, Material>();
#endif

            Debug.Log($"[Single export] Parsing PSD: {psdPath}");
            try
            {
                psd = PsdImage.Open(psdPath);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Failed to parse PSD: {ex.Message}");
                EditorUtility.DisplayDialog("Error", $"Failed to parse PSD: {ex.Message}", "OK");
                return;
            }

            _canvasWidth = psd.Width;
            _canvasHeight = psd.Height;
            _psdImage = psd;

            _layersById = new Dictionary<int, Layer>();
            BuildLayersById(psd.Root);

            LoadMergeExportConfig(psdPath);

            Layer targetLayer = FindLayerById(psd.Root, targetLayerId);
            if (targetLayer == null)
            {
                EditorUtility.DisplayDialog("Error", $"Layer id={targetLayerId} not found.", "OK");
                return;
            }
            if (!targetLayer.Visible)
            {
                EditorUtility.DisplayDialog("Error", "This layer is hidden; cannot export.", "OK");
                return;
            }

            // Rasterize the single target layer (or merge group composite),
            // then composite any clipping layers that are attached to it.
            Texture2D raster = null;
            int rasterLeft, rasterTop;
            if (targetLayer.IsGroup && GetMergeExport(targetLayer))
            {
                raster = _psdImage != null
                    ? _psdImage.CompositeGroupWithClipping((PsdTools.Layers.Group)targetLayer)
                    : ((PsdTools.Layers.Group)targetLayer).Composite();
                if (raster == null)
                {
                    EditorUtility.DisplayDialog("Error", "Failed to composite group layer.", "OK");
                    return;
                }
                var bbox = ((PsdTools.Layers.Group)targetLayer).BBox;
                rasterLeft = bbox.Left;
                rasterTop  = bbox.Top;
            }
            else if (!targetLayer.IsGroup)
            {
                raster = CreateLayerTexture(targetLayer);
                if (raster == null)
                {
                    EditorUtility.DisplayDialog("Error", "Failed to rasterize layer.", "OK");
                    return;
                }
                rasterLeft = targetLayer.Left;
                rasterTop  = targetLayer.Top;
            }
            else
            {
                EditorUtility.DisplayDialog("Error", "Single export is only available for image layers or merged groups.", "OK");
                return;
            }

            // Find and composite any clipping layers sitting on top of targetLayer in its parent group
            var clippingLayers = CollectClippingLayersForBase(targetLayer);
            if (clippingLayers.Count > 0 && targetLayer.Kind != LayerKind.Type)
            {
                // Store base original alpha as mask (same logic as ProcessClippingGroup)
                Color32[] basePixels = raster.GetPixels32();
                byte[] originalAlpha = new byte[basePixels.Length];
                for (int i = 0; i < basePixels.Length; i++)
                    originalAlpha[i] = basePixels[i].a;

                foreach (var clipLayer in clippingLayers)
                {
                    if (!clipLayer.Visible) continue;
                    Texture2D clipTex = null;
                    int clipLeft, clipTop;
                    if (clipLayer.IsGroup)
                    {
                        var cbbox = ((PsdTools.Layers.Group)clipLayer).BBox;
                        clipLeft = cbbox.Left;
                        clipTop  = cbbox.Top;
                        int cw = cbbox.Right - cbbox.Left;
                        int ch = cbbox.Bottom - cbbox.Top;
                        if (cw <= 0 || ch <= 0) continue;
                        clipTex = ((PsdTools.Layers.Group)clipLayer).Composite();
                        if (clipTex != null)
                            ApplyLayerMask(clipTex, clipLayer);
                    }
                    else
                    {
                        if (clipLayer.Width <= 0 || clipLayer.Height <= 0) continue;
                        clipLeft = clipLayer.Left;
                        clipTop  = clipLayer.Top;
                        clipTex = CreateLayerTexture(clipLayer);
                    }
                    if (clipTex == null) continue;
                    CompositeClippedOntoBase(raster, rasterLeft, rasterTop, clipTex, clipLeft, clipTop, originalAlpha);
                    Object.DestroyImmediate(clipTex);
                    Debug.Log($"[Single export] Clipping merge: {clipLayer.Name} -> {targetLayer.Name}");
                }
            }

            string targetFolder = _outputFolder;
            if (!string.IsNullOrEmpty(targetFolder) && !Directory.Exists(targetFolder))
                Directory.CreateDirectory(targetFolder);

            string fileName = BuildExportImageFileName(targetLayer);
            string fullPath = Path.Combine(targetFolder, fileName);

            // Nine-slice
            bool sliceEnabled = GetSliceImage(targetLayer);
            Vector4? sliceBorder = null;
            Texture2D imageToSave = raster;
            Texture2D slicedTex = null;
            GetNineSliceParamsForLayer(targetLayer, out int nsBi, out int nsPt, out int nsMcc, out int nsMcr, out int nsMsz);

            if (sliceEnabled)
            {
                sliceBorder = DetectNineSlice(raster, nsBi, nsPt, nsMsz, nsMcc, nsMcr);
                if (sliceBorder.HasValue)
                {
                    int l = (int)sliceBorder.Value.x;
                    int b = (int)sliceBorder.Value.y;
                    int r = (int)sliceBorder.Value.z;
                    int t = (int)sliceBorder.Value.w;
                    slicedTex = BuildNineSliceImage(raster, l, b, r, t, nsMcc, nsMcr);
                    imageToSave = slicedTex;
                }
            }


            // Write PNG (direct, no dedup check)
            byte[] pngBytes = imageToSave.EncodeToPNG();
            WritePngBytesToDestinationViaPsdCacheImmediate(fullPath, pngBytes);

            _layerImagePaths[targetLayer] = fullPath;
            if (sliceBorder.HasValue)
                _layerSliceBorders[targetLayer] = sliceBorder.Value;

            if (sliceBorder.HasValue && slicedTex != null)
            {
                int l = (int)sliceBorder.Value.x;
                int b = (int)sliceBorder.Value.y;
                int r = (int)sliceBorder.Value.z;
                int t = (int)sliceBorder.Value.w;
                ComputeNineSliceCenterCrop(raster.width, raster.height, l, r, b, t, nsMcc, nsMcr, out int logCC, out int logCR);
                Debug.Log($"[Single export] Nine-slice: {targetLayer.Name} -> {fileName} " +
                          $"(source {raster.width}x{raster.height} -> {l + logCC + r}x{b + logCR + t}), " +
                          $"inset: L={l} B={b} R={r} T={t}, center shrink: {logCC}x{logCR}");
                Object.DestroyImmediate(slicedTex);
            }
            else
            {
                Debug.Log($"[Single export] {targetLayer.Name} -> {fileName}");
            }
            Object.DestroyImmediate(raster);

            AssetDatabase.Refresh();
            SetupAllSprites();
            AssetDatabase.Refresh();

            Debug.Log("[Single export] Done.");
            }
            finally
            {
                psd?.ReleaseAllData();
            }
            }
        }

        private static void RunGenerateFromPsdCore(string psdPath, string psdName, bool autoImageNaming = true)
        {
            _sessionPsdPath = psdPath;
            _sessionAutoImageNaming = autoImageNaming;
            _useAutoImageNaming = autoImageNaming;
            InitPsdCacheBatchKeyForCurrentExport();
            PsdImage psd = null;
            using (new PsdCacheExportCleanup())
            {
            try
            {
            _layerImagePaths = new Dictionary<Layer, string>();
            _layerSliceBorders = new Dictionary<Layer, Vector4>();
            _dedupEntries = new List<DedupEntry>();
            _clippedLayers = new HashSet<Layer>();
            _pendingPsdCacheWrites = new List<(string fullPath, byte[] pngBytes, List<Layer> layers)>();
            _rasterPending = new Dictionary<Layer, Texture2D>();
    #if USE_TMP
            _tmpFaceMaterialVariantCache = new Dictionary<string, Material>();
    #endif

            Debug.Log($"Parsing PSD: {psdPath}");
            try
            {
                psd = PsdImage.Open(psdPath);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Failed to parse PSD: {ex.Message}");
                EditorUtility.DisplayDialog("Error", $"Failed to parse PSD: {ex.Message}", "OK");
                return;
            }

            _canvasWidth = psd.Width;
            _canvasHeight = psd.Height;
            _psdImage = psd;

            Debug.Log($"PSD size: {psd.Width}x{psd.Height}, layers: {psd.LayerCount}");

            _layersById = new Dictionary<int, Layer>();
            BuildLayersById(psd.Root);

            LoadMergeExportConfig(psdPath);
            InitFontMappingForExport();
            BuildCommonDirImageCache();

            Debug.Log("=== PSD layer tree ===");
            PrintLayerTree(psd.Root, 0);
            Debug.Log("===================");

            ExportAllLayerImages(psd.Root);

            if (!ProcessPendingRasterExports(out string rasterAbort))
            {
                EditorUtility.DisplayDialog("Export aborted", rasterAbort, "OK");
                return;
            }

            if (!TryFinishPngExportAfterSlicePass(out string psdCacheDupDetail))
            {
                PsdCacheDuplicateNameDialogWindow.ShowWindow(PsdCacheDuplicateNameDialogWindow.ExportAbortedIntro, psdCacheDupDetail);
                return;
            }

            AssetDatabase.Refresh();
            SetupAllSprites();
            AssetDatabase.Refresh();

            CreateHierarchicalUIPrefab(psd, psdName);

            FinalizeFontMappingAfterExport();
            }
            catch (TmpImportRequiredException)
            {
                return;
            }
            finally
            {
                // Eagerly release large PSD byte[] buffers; Boehm conservative GC may pin psd via stale stack refs
                // In finally so early returns / exceptions still release.
                // Exception: when the diff window is waiting for user input, defer release until the callback fires.
                if (_deferPsdRelease)
                    _deferredPsd = psd;
                else
                    psd?.ReleaseAllData();
            }
            }
        }

        /// <summary>Textures only (slice/nine-slice/dedup/Sprite import); no Prefabs.</summary>
        private static void RunExportTexturesCore(string psdPath, string psdName, bool autoImageNaming = true)
        {
            _useAutoImageNaming = autoImageNaming;
            InitPsdCacheBatchKeyForCurrentExport();
            PsdImage psd = null;
            using (new PsdCacheExportCleanup())
            {
            try
            {
            _layerImagePaths = new Dictionary<Layer, string>();
            _layerSliceBorders = new Dictionary<Layer, Vector4>();
            _dedupEntries = new List<DedupEntry>();
            _clippedLayers = new HashSet<Layer>();
            _pendingPsdCacheWrites = new List<(string fullPath, byte[] pngBytes, List<Layer> layers)>();
            _rasterPending = new Dictionary<Layer, Texture2D>();
    #if USE_TMP
            _tmpFaceMaterialVariantCache = new Dictionary<string, Material>();
    #endif

            Debug.Log($"[Export textures] Parsing PSD: {psdPath}");
            try
            {
                psd = PsdImage.Open(psdPath);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Failed to parse PSD: {ex.Message}");
                EditorUtility.DisplayDialog("Error", $"Failed to parse PSD: {ex.Message}", "OK");
                return;
            }

            _canvasWidth = psd.Width;
            _canvasHeight = psd.Height;
            _psdImage = psd;

            Debug.Log($"[Export textures] PSD size: {psd.Width}x{psd.Height}, layers: {psd.LayerCount}");

            _layersById = new Dictionary<int, Layer>();
            BuildLayersById(psd.Root);

            LoadMergeExportConfig(psdPath);
            InitFontMappingForExport();
            BuildCommonDirImageCache();

            ExportAllLayerImages(psd.Root);

            if (!ProcessPendingRasterExports(out string rasterAbort))
            {
                EditorUtility.DisplayDialog("Export aborted", rasterAbort, "OK");
                return;
            }

            if (!TryFinishPngExportAfterSlicePass(out string psdCacheDupDetail))
            {
                PsdCacheDuplicateNameDialogWindow.ShowWindow(PsdCacheDuplicateNameDialogWindow.ExportAbortedIntro, psdCacheDupDetail);
                return;
            }

            AssetDatabase.Refresh();
            SetupAllSprites();
            AssetDatabase.Refresh();

            FinalizeFontMappingAfterExport();
            Debug.Log("[Export textures] Done.");
            }
            catch (TmpImportRequiredException)
            {
                return;
            }
            finally
            {
                // Eagerly release large PSD byte[] buffers; Boehm conservative GC may pin psd via stale stack refs
                psd?.ReleaseAllData();
            }
            }
        }

        private static void RunGenerateSubtreeCore(string psdPath, string psdName, int subtreeRootLayerId, bool autoImageNaming = true)
        {
            _useAutoImageNaming = autoImageNaming;
            InitPsdCacheBatchKeyForCurrentExport();
            PsdImage psd = null;
            using (new PsdCacheExportCleanup())
            {
            try
            {
            _layerImagePaths = new Dictionary<Layer, string>();
            _layerSliceBorders = new Dictionary<Layer, Vector4>();
            _dedupEntries = new List<DedupEntry>();
            _clippedLayers = new HashSet<Layer>();
            _pendingPsdCacheWrites = new List<(string fullPath, byte[] pngBytes, List<Layer> layers)>();
            _rasterPending = new Dictionary<Layer, Texture2D>();
    #if USE_TMP
            _tmpFaceMaterialVariantCache = new Dictionary<string, Material>();
    #endif

            Debug.Log($"[Export now] Parsing PSD: {psdPath}");
            try
            {
                psd = PsdImage.Open(psdPath);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Failed to parse PSD: {ex.Message}");
                EditorUtility.DisplayDialog("Error", $"Failed to parse PSD: {ex.Message}", "OK");
                return;
            }

            _canvasWidth = psd.Width;
            _canvasHeight = psd.Height;
            _psdImage = psd;

            _layersById = new Dictionary<int, Layer>();
            BuildLayersById(psd.Root);

            LoadMergeExportConfig(psdPath);
            InitFontMappingForExport();
            BuildCommonDirImageCache();

            Layer subtreeRoot = FindLayerById(psd.Root, subtreeRootLayerId);
            if (subtreeRoot == null)
            {
                EditorUtility.DisplayDialog("Error", $"Layer id={subtreeRootLayerId} not found.", "OK");
                return;
            }
            if (!subtreeRoot.Visible)
            {
                EditorUtility.DisplayDialog("Error", "This layer is hidden and cannot be exported.", "OK");
                return;
            }
            if (!GetExported(subtreeRoot))
            {
                EditorUtility.DisplayDialog("Error", "This layer is set to not export; \"Export now\" cannot run.", "OK");
                return;
            }

            // Only nodes in this subtree with "Export Prefab" get standalone Prefabs; subtree root is always included.
            var filteredPrefabIds = new HashSet<int>();
            if (_exportPrefabLayerIds != null)
            {
                foreach (int id in _exportPrefabLayerIds)
                {
                    var L = FindLayerById(psd.Root, id);
                    if (L != null && IsLayerUnderSubtree(subtreeRoot, L))
                        filteredPrefabIds.Add(id);
                }
            }
            filteredPrefabIds.Add(subtreeRootLayerId);
            _exportPrefabLayerIds = filteredPrefabIds;

            Debug.Log($"[Export now] Subtree root: {subtreeRoot.Name} (id={subtreeRootLayerId}); slicing this subtree only; standalone Prefab layer ids: {filteredPrefabIds.Count}");

            ExportAllLayerImages(subtreeRoot);

            if (!ProcessPendingRasterExports(out string rasterAbortSubtree))
            {
                EditorUtility.DisplayDialog("Export aborted", rasterAbortSubtree, "OK");
                return;
            }

            if (!TryFinishPngExportAfterSlicePass(out string psdCacheDupDetailSubtree))
            {
                PsdCacheDuplicateNameDialogWindow.ShowWindow(PsdCacheDuplicateNameDialogWindow.ExportAbortedIntro, psdCacheDupDetailSubtree);
                return;
            }

            AssetDatabase.Refresh();
            SetupAllSprites();
            AssetDatabase.Refresh();

            _exportPrefabAssetPathByLayerId = new Dictionary<int, string>();
            if (_exportPrefabLayerIds != null && _exportPrefabLayerIds.Count > 0)
            {
                string mainPrefabPath = CombineWithPrefabExportRoot($"{psdName}.prefab");
                ExportSingleNodePrefabs(psd, mainPrefabPath);
                AssetDatabase.Refresh();
            }

            FinalizeFontMappingAfterExport();

            if (_exportPrefabAssetPathByLayerId != null
                && _exportPrefabAssetPathByLayerId.TryGetValue(subtreeRootLayerId, out string rootPrefabPath))
            {
                GameObject go = AssetDatabase.LoadAssetAtPath<GameObject>(rootPrefabPath);
                if (go != null)
                    Selection.activeObject = go;
            }

            Debug.Log("[Export now] Done.");
            }
            catch (TmpImportRequiredException)
            {
                return;
            }
            finally
            {
                // Eagerly release large PSD byte[] buffers; Boehm conservative GC may pin psd via stale stack refs
                psd?.ReleaseAllData();
            }
            }
        }

        /// <summary>True if <paramref name="node"/> equals <paramref name="ancestor"/> or is a descendant in the PSD tree.</summary>
        private static bool IsLayerUnderSubtree(Layer ancestor, Layer node)
        {
            if (ancestor == null || node == null) return false;
            for (Layer p = node; p != null; p = p.Parent)
            {
                if (p == ancestor) return true;
            }
            return false;
        }

        private static string NormalizeAssetsExportRoot(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                folder = "Assets";
            folder = folder.Replace('\\', '/').Trim().TrimEnd('/');
            if (!folder.StartsWith("Assets", System.StringComparison.OrdinalIgnoreCase))
                folder = "Assets/" + folder.TrimStart('/');
            return folder;
        }

        private static string GetDiskPathUnderAssetsRoot(string assetsFolderNormalized)
        {
            if (assetsFolderNormalized.Equals("Assets", System.StringComparison.OrdinalIgnoreCase))
                return Application.dataPath;
            if (!assetsFolderNormalized.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
                return Application.dataPath;
            string suffix = assetsFolderNormalized.Substring("Assets/".Length);
            return Path.Combine(Application.dataPath, suffix.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>PSDCache root next to the Assets folder (absolute disk path).</summary>
        private static string GetPsdCacheDirectoryRoot()
        {
            string projectDir = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrEmpty(projectDir))
                projectDir = Application.dataPath;
            return Path.Combine(projectDir, "PSDCache");
        }

        private static void InitPsdCacheBatchKeyForCurrentExport()
        {
            if (string.IsNullOrEmpty(_outputFolder))
            {
                _psdCacheBatchKey = "default";
                return;
            }
            string full = Path.GetFullPath(_outputFolder);
            string leaf = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(leaf))
                leaf = "export";
            unchecked
            {
                _psdCacheBatchKey = $"{CleanFileName(leaf)}_{full.GetHashCode():X8}";
            }
        }

        /// <summary>
        /// Queue PNG export: <see cref="TryFinishPngExportAfterSlicePass"/> resolves any duplicate basenames (auto-rename) then writes to PSDCache and copies to final path.
        /// <paramref name="affectedLayers"/> must be the full groupMembers list so that <see cref="ResolveDuplicateBasenamesInPendingPsdWrites"/> can patch <see cref="_layerImagePaths"/> for every member precisely.
        /// </summary>
        private static void WritePngBytesToDestinationViaPsdCache(string finalFullPath, byte[] pngBytes, List<Layer> affectedLayers)
        {
            if (pngBytes == null || pngBytes.Length == 0 || string.IsNullOrEmpty(finalFullPath))
                return;
            if (_pendingPsdCacheWrites == null)
                _pendingPsdCacheWrites = new List<(string fullPath, byte[] pngBytes, List<Layer> layers)>();
            _pendingPsdCacheWrites.Add((finalFullPath, pngBytes, affectedLayers));
        }

        private static void WritePngBytesToDestinationViaPsdCacheImmediate(string finalFullPath, byte[] pngBytes)
        {
            if (pngBytes == null || pngBytes.Length == 0 || string.IsNullOrEmpty(finalFullPath))
                return;

            if (string.IsNullOrEmpty(_psdCacheBatchKey))
                InitPsdCacheBatchKeyForCurrentExport();

            string cacheRoot = GetPsdCacheDirectoryRoot();
            string cacheBatchDir = Path.Combine(cacheRoot, _psdCacheBatchKey);
            try
            {
                Directory.CreateDirectory(cacheBatchDir);
                string cacheFilePath = Path.Combine(cacheBatchDir, Path.GetFileName(finalFullPath));
                File.WriteAllBytes(cacheFilePath, pngBytes);

                string destDir = Path.GetDirectoryName(finalFullPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);
                File.Copy(cacheFilePath, finalFullPath, true);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"PSDCache staging or copy failed; writing directly to destination: {ex.Message}");
                string destDir = Path.GetDirectoryName(finalFullPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);
                File.WriteAllBytes(finalFullPath, pngBytes);
            }
        }

        private static string NormalizeExportFullPathForCompare(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            try { return Path.GetFullPath(p); }
            catch { return p; }
        }

        private static string FormatExportNodeLine(Layer layer)
        {
            if (layer == null) return "(unknown node)";
            return $"{layer.Name}\n    path: {GetLayerHierarchyPathForExport(layer)}";
        }

        /// <summary>
        /// PSDCache staging keys are filenames only. Duplicate basenames in the pending queue are unsafe; abort export.
        /// </summary>
        private static bool TryValidateNoDuplicateBasenamesInPendingPsdWrites(out string fullDetailMessage)
        {
            fullDetailMessage = null;
            if (_pendingPsdCacheWrites == null || _pendingPsdCacheWrites.Count <= 1)
                return true;

            var byBase = new Dictionary<string, List<(string path, Layer repLayer)>>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var item in _pendingPsdCacheWrites)
            {
                string bn = Path.GetFileName(item.fullPath);
                if (!byBase.TryGetValue(bn, out var list))
                {
                    list = new List<(string path, Layer repLayer)>();
                    byBase[bn] = list;
                }
                // Use first non-null layer as the representative for error reporting.
                Layer repLayer = item.layers?.FirstOrDefault(l => l != null);
                list.Add((item.fullPath, repLayer));
            }

            var dupNames = new List<string>();
            foreach (var kvp in byBase)
            {
                if (kvp.Value.Count > 1)
                    dupNames.Add(kvp.Key);
            }
            if (dupNames.Count == 0)
                return true;

            dupNames.Sort(System.StringComparer.OrdinalIgnoreCase);
            var fullSb = new StringBuilder(1024);
            fullSb.AppendLine("These filenames map to the same PSDCache staging key (would overwrite). Rename layers or enable auto-naming, then export again:");
            fullSb.AppendLine();
            foreach (string bn in dupNames)
            {
                fullSb.Append('「').Append(bn).AppendLine("」");
                int n = 1;
                foreach (var entry in byBase[bn])
                {
                    fullSb.Append("  ").Append(n++).Append(". node: ").AppendLine(FormatExportNodeLine(entry.repLayer));
                }
                fullSb.AppendLine();
            }
            fullDetailMessage = fullSb.ToString().TrimEnd();
            Debug.LogError("PSD export aborted (duplicate PSDCache filenames). Full list:\n" + fullDetailMessage);
            return false;
        }

        private static void FlushPendingPsdCacheWrites()
        {
            if (_pendingPsdCacheWrites == null || _pendingPsdCacheWrites.Count == 0)
                return;
            foreach (var item in _pendingPsdCacheWrites)
                WritePngBytesToDestinationViaPsdCacheImmediate(item.fullPath, item.pngBytes);
            _pendingPsdCacheWrites.Clear();
        }


        /// <summary>
        /// When <see cref="_useAutoImageNaming"/> is false, same-named layers produce identical basenames in the PSDCache queue.
        /// Instead of aborting, generate non-colliding names (_1, _2, …) for every extra entry and patch
        /// <see cref="_pendingPsdCacheWrites"/>, <see cref="_layerImagePaths"/> and <see cref="_dedupEntries"/> in place.
        /// No-op when auto-naming is enabled (LayerId suffix already guarantees uniqueness).
        /// Returns a human-readable rename report when any renames occurred; null otherwise.
        /// </summary>
        private static string ResolveDuplicateBasenamesInPendingPsdWrites()
        {
            if (_useAutoImageNaming || _pendingPsdCacheWrites == null || _pendingPsdCacheWrites.Count <= 1)
                return null;

            // Seed the collision set with every basename currently queued.
            var takenBasenames = new HashSet<string>(
                _pendingPsdCacheWrites.Select(e => Path.GetFileName(e.fullPath).ToLowerInvariant()));

            // Group queue indices by their current basename.
            var byBase = new Dictionary<string, List<int>>(System.StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _pendingPsdCacheWrites.Count; i++)
            {
                string bn = Path.GetFileName(_pendingPsdCacheWrites[i].fullPath);
                if (!byBase.TryGetValue(bn, out var list)) { list = new List<int>(); byBase[bn] = list; }
                list.Add(i);
            }

            var reportSb = new StringBuilder();

            foreach (var kvp in byBase)
            {
                var indices = kvp.Value;
                if (indices.Count <= 1) continue;   // no collision for this name

                reportSb.Append('「').Append(kvp.Key).AppendLine("」");

                // Output indices[0] (the one that keeps the original name)
                {
                    var firstEntry = _pendingPsdCacheWrites[indices[0]];
                    Layer repLayer = firstEntry.layers?.FirstOrDefault(l => l != null);
                    string layerPath = repLayer != null ? GetLayerHierarchyPathForExport(repLayer) : "?";
                    reportSb.Append("  ").Append(layerPath).Append("  →  \"").Append(kvp.Key).AppendLine("\"");
                }

                // Rename indices[1 .. n-1] with _1, _2, …
                int counter = 1;
                for (int i = 1; i < indices.Count; i++)
                {
                    int idx = indices[i];
                    var entry = _pendingPsdCacheWrites[idx];
                    string oldPath = entry.fullPath;
                    string dir  = Path.GetDirectoryName(oldPath);
                    string stem = Path.GetFileNameWithoutExtension(oldPath);
                    string ext  = Path.GetExtension(oldPath);   // ".png"

                    // Find a basename not yet taken by any other pending write.
                    string newBasename;
                    do
                    {
                        newBasename = $"{stem}_{counter}{ext}";
                        counter++;
                    }
                    while (takenBasenames.Contains(newBasename.ToLowerInvariant()));
                    takenBasenames.Add(newBasename.ToLowerInvariant());

                    string newPath = string.IsNullOrEmpty(dir)
                        ? newBasename
                        : Path.Combine(dir, newBasename);

                    // Patch pending write entry (tuple is a value type – must re-assign).
                    _pendingPsdCacheWrites[idx] = (newPath, entry.pngBytes, entry.layers);

                    // Patch _layerImagePaths precisely using the entry's own layer list.
                    // NOTE: Do NOT use path-string equality here — two distinct entries may share
                    // the same oldPath string (same-named layers), which would wrongly overwrite
                    // the index-0 entry's layers with the renamed path.
                    if (_layerImagePaths != null && entry.layers != null)
                    {
                        foreach (var affectedLayer in entry.layers)
                        {
                            if (affectedLayer != null && _layerImagePaths.ContainsKey(affectedLayer))
                                _layerImagePaths[affectedLayer] = newPath;
                        }
                    }

                    // Patch _dedupEntries (struct – copy, modify, re-assign).
                    // Use path-string equality here: dedup entries are registered once per unique
                    // image write, not per layer, so there is no ambiguity.
                    if (_dedupEntries != null)
                    {
                        for (int d = 0; d < _dedupEntries.Count; d++)
                        {
                            if (_dedupEntries[d].fullPath == oldPath)
                            {
                                var de = _dedupEntries[d];
                                de.fullPath = newPath;
                                _dedupEntries[d] = de;
                            }
                        }
                    }

                    // Build a representative layer name for the log (use first non-null layer).
                    Layer repLayer = entry.layers?.FirstOrDefault(l => l != null);
                    string layerPath = repLayer != null ? GetLayerHierarchyPathForExport(repLayer) : "?";
                    string renameMsg = $"  {layerPath}  →  \"{newBasename}\"";
                    reportSb.AppendLine(renameMsg);
                    int memberCount = entry.layers?.Count ?? 0;
                    string memberNote = memberCount > 1 ? $" [{memberCount} dedup members]" : "";
                    Debug.Log($"[PSD export] Duplicate basename resolved: \"{Path.GetFileName(oldPath)}\" → \"{newBasename}\" (layer: {repLayer?.Name ?? "?"}{memberNote})");
                }
                reportSb.AppendLine();
            }

            return reportSb.Length > 0 ? reportSb.ToString().TrimEnd() : null;
        }

        /// <summary>
        /// After slicing: resolve any duplicate PSDCache basenames (auto-rename when auto-naming is off),
        /// then write all pending PSDCache files and copy to targets. Always returns true.
        /// When renames occurred, opens <see cref="PsdCacheDuplicateNameDialogWindow"/> to list them.
        /// </summary>
        private static bool TryFinishPngExportAfterSlicePass(out string abortDetailMessage)
        {
            abortDetailMessage = null;
            string renameReport = ResolveDuplicateBasenamesInPendingPsdWrites();
            FlushPendingPsdCacheWrites();
            if (!string.IsNullOrEmpty(renameReport))
                PsdCacheDuplicateNameDialogWindow.ShowWindow(
                    PsdCacheDuplicateNameDialogWindow.DuplicatesRenamedIntro,
                    renameReport,
                    "Duplicate layer names auto-renamed");
            return true;
        }

        /// <summary>Recursively delete entire PSDCache folder next to Assets (all contents).</summary>
        private static void TryDeletePsdCacheStagingForCurrentBatch()
        {
            string root = GetPsdCacheDirectoryRoot();
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Failed to delete PSDCache directory: {root} — {ex.Message}");
            }
        }

        /// <summary>
        /// Release static references from export: <see cref="Layer"/> / <see cref="Texture2D"/> / dedup caches.
        /// Otherwise dict keys and queued Layers pin the whole <see cref="PsdImage"/> on the heap; closing the PSD window + GC may not free it.
        /// </summary>
        public static void ReleaseExportSessionStaticPins()
        {
            ReleaseExportPinnedStateCore();
        }

        private static void ReleaseExportPinnedStateCore()
        {
            if (_rasterPending != null)
            {
                foreach (var kv in _rasterPending)
                {
                    if (kv.Value != null)
                        Object.DestroyImmediate(kv.Value);
                }

                _rasterPending = null;
            }

            _layerImagePaths = null;
            _layerSliceBorders = null;
            _clippedLayers = null;

            if (_pendingPsdCacheWrites != null)
            {
                _pendingPsdCacheWrites.Clear();
                _pendingPsdCacheWrites = null;
            }

            _layersById = null;

            if (_dedupEntries != null)
            {
                _dedupEntries.Clear();
                _dedupEntries = null;
            }

            if (_commonDirImageCache != null)
            {
                _commonDirImageCache.Clear();
                _commonDirImageCache = null;
            }

            // Session state from LoadMergeExportConfig / InitFontMapping; does not pin Layers but holds managed memory
            _psdImage = null;
            _mergeExportConfig = null;
            _exportPrefabLayerIds = null;
            _exportPrefabAssetPathByLayerId = null;
            _externalPrefabByLayerId = null;
            _externalPrefabReusePosition = null;
            _externalPrefabReuseSize = null;
            _participateLocalDedupByLayerId = null;
            _participateCommonDedupByLayerId = null;
            _sliceImageByLayerId = null;
            _primaryDedupNodeByLayerId = null;
            _useCustomImageByLayerId = null;
            _customImagePathByLayerId = null;
            _exportedByLayerId = null;
            _layerConfigByLayerId = null;
            _useTextMeshProByLayerId = null;
            _useTextMeshProByLayerRef = null;
            _sessionPsdPath = null;

            _psdFontMappingLookup = null;
            _unrecognizedPsdFontNamesThisExport = null;
            _pendingFontMappingEntries = null;

#if USE_TMP
            _tmpFaceMaterialVariantCache = null;
#endif
        }

        /// <summary>After export session (using Dispose): nudge GC to free large managed allocations from PSD parse.</summary>
        private static void TrimExportManagedHeapAfterSession()
        {
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();
        }

        /// <summary>
        /// Called by each diff-window callback to release the deferred PSD and export state once the user's choice has been applied.
        /// Resets <see cref="_deferPsdRelease"/> and <see cref="_deferredPsd"/> then performs the normal cleanup + heap trim.
        /// </summary>
        private static void ReleaseDeferredPsdAndState()
        {
            _deferPsdRelease = false;
            _deferredPsd?.ReleaseAllData();
            _deferredPsd = null;
            ReleaseExportPinnedStateCore();
            TrimExportManagedHeapAfterSession();
        }

        /// <summary>On using end: delete PSDCache (after copy to targets) and release static Layer pins on the PSD document.</summary>
        private sealed class PsdCacheExportCleanup : System.IDisposable
        {
            public void Dispose()
            {
                TryDeletePsdCacheStagingForCurrentBatch();
                // Skip state release when the diff window is waiting; the callback will call ReleaseDeferredPsdAndState().
                if (!_deferPsdRelease)
                    ReleaseExportPinnedStateCore();
            }
        }

        private static string CombineWithPrefabExportRoot(string fileName)
        {
            string root = string.IsNullOrEmpty(_prefabExportAssetsRootRelative) ? "Assets" : _prefabExportAssetsRootRelative;
            root = root.Replace('\\', '/').TrimEnd('/');
            if (root.Equals("Assets", System.StringComparison.OrdinalIgnoreCase))
                return "Assets/" + fileName;
            return root + "/" + fileName;
        }

        private static void EnsureAssetFolderExistsForPath(string assetsPath)
        {
            if (string.IsNullOrEmpty(assetsPath) || !assetsPath.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
                return;
            string relative = assetsPath.Substring("Assets/".Length);
            string dirPart = Path.GetDirectoryName(relative);
            if (string.IsNullOrEmpty(dirPart))
                return;
            string fullDir = Path.Combine(Application.dataPath, dirPart.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(fullDir))
                Directory.CreateDirectory(fullDir);
        }

        /// <summary>
        /// Recursively build LayerId → Layer map.
        /// </summary>
        private static void BuildLayersById(Layer layer)
        {
            if (layer.LayerId.HasValue)
                _layersById[layer.LayerId.Value] = layer;
            foreach (var child in layer.Children)
                BuildLayersById(child);
        }

        /// <summary>
        /// Print layer tree (debug)
        /// </summary>
        private static void PrintLayerTree(Layer layer, int depth)
        {
            string indent = new string(' ', depth * 2);
            string info = layer.IsGroup ? "[Group]" : $"[{layer.Kind}]";
            string clipTag = layer.IsClipped ? " [Clipped]" : "";
            string maskTag = layer.HasMask ? " [HasMask]" : "";
            Debug.Log($"{indent}{info} {layer.Name} ({layer.Width}x{layer.Height}) Visible={layer.Visible}{clipTag}{maskTag}");
            
            foreach (var child in layer.Children)
            {
                PrintLayerTree(child, depth + 1);
            }
        }

        /// <summary>Load export config from _export_config.json beside the PSD (under PSDConfig).</summary>
        private static void LoadMergeExportConfig(string psdPath)
        {
            _mergeExportConfig       = new Dictionary<int, bool>();
            _exportPrefabLayerIds    = new HashSet<int>();
            _externalPrefabByLayerId = new Dictionary<int, string>();
            _externalPrefabReusePosition = new Dictionary<int, bool>();
            _externalPrefabReuseSize     = new Dictionary<int, bool>();
            _participateLocalDedupByLayerId  = new Dictionary<int, bool>();
            _participateCommonDedupByLayerId = new Dictionary<int, bool>();
            _sliceImageByLayerId = new Dictionary<int, bool>();
            _primaryDedupNodeByLayerId = new Dictionary<int, bool>();
            _useCustomImageByLayerId = new Dictionary<int, bool>();
            _customImagePathByLayerId = new Dictionary<int, string>();
            _layerConfigByLayerId = new Dictionary<int, LayerConfigEntry>();
            _useTextMeshProByLayerId = new Dictionary<int, bool>();
            _exportedByLayerId = new Dictionary<int, bool>();
            if (string.IsNullOrEmpty(psdPath)) return;
            string path = Path.Combine(
                Application.dataPath, "PsdToUnityUI", "PSDConfig",
                Path.GetFileNameWithoutExtension(psdPath) + "_export_config.json");
            if (!File.Exists(path)) return;
            try
            {
                string json = File.ReadAllText(path);
                string normalizedJson = json.Replace("\"scrollRectScrollBgChildName\"", "\"scrollRectViewportChildName\"");
                var data = JsonUtility.FromJson<MergeExportConfigData>(normalizedJson);
                if (data?.layers == null) return;

                bool hasDedupOptions = json.IndexOf("participateLocalDedup", System.StringComparison.Ordinal) >= 0;
                bool hasSliceImage = json.IndexOf("sliceImage", System.StringComparison.Ordinal) >= 0;
                bool hasUseCustomImage = json.IndexOf("useCustomImage", System.StringComparison.Ordinal) >= 0;
                bool hasUseTextMeshPro = json.IndexOf("useTextMeshPro", System.StringComparison.Ordinal) >= 0;
                bool hasPrimaryDedupNode = json.IndexOf("primaryDedupNode", System.StringComparison.Ordinal) >= 0;
                bool hasExportedField = json.IndexOf("\"exported\"", System.StringComparison.Ordinal) >= 0;
                bool defaultSliceImage = GetDefaultSliceImagePreference();

                foreach (var entry in data.layers)
                {
                    if (!string.IsNullOrEmpty(entry.name) && _layersById != null && _layersById.TryGetValue(entry.id, out var layer))
                    {
                        layer.Name = entry.name;
                        layer.Visible = entry.visible;
                    }

                    // exported: default true when missing in legacy configs
                    _exportedByLayerId[entry.id] = hasExportedField ? entry.exported : true;

                    bool useExtLoaded = entry.useExternalPrefab && !string.IsNullOrEmpty(entry.externalPrefabPath);
                    if (useExtLoaded)
                    {
                        _mergeExportConfig[entry.id] = false;
                        _externalPrefabByLayerId[entry.id]    = entry.externalPrefabPath;
                        _externalPrefabReusePosition[entry.id] = entry.reusePosition;
                        _externalPrefabReuseSize[entry.id]     = entry.reuseSize;
                        _participateLocalDedupByLayerId[entry.id]  = true;
                        _participateCommonDedupByLayerId[entry.id] = true;
                        _sliceImageByLayerId[entry.id] = true;
                        _useCustomImageByLayerId[entry.id] = false;
                        _useTextMeshProByLayerId[entry.id] = hasUseTextMeshPro ? entry.useTextMeshPro : false;
                        _layerConfigByLayerId[entry.id] = entry;
                        continue;
                    }

                    _mergeExportConfig[entry.id] = entry.merge;
                    if (entry.exportPrefab) _exportPrefabLayerIds.Add(entry.id);
                    _participateLocalDedupByLayerId[entry.id]  = hasDedupOptions ? entry.participateLocalDedup : true;
                    _participateCommonDedupByLayerId[entry.id] = hasDedupOptions ? entry.participateCommonDedup : true;
                    _sliceImageByLayerId[entry.id] = hasSliceImage ? entry.sliceImage : defaultSliceImage;
                    if (hasPrimaryDedupNode && entry.primaryDedupNode)
                        _primaryDedupNodeByLayerId[entry.id] = true;
                    _useCustomImageByLayerId[entry.id] = hasUseCustomImage ? entry.useCustomImage : false;
                    if (hasUseCustomImage && entry.useCustomImage && !string.IsNullOrEmpty(entry.customImagePath))
                        _customImagePathByLayerId[entry.id] = entry.customImagePath;

                    _useTextMeshProByLayerId[entry.id] = hasUseTextMeshPro ? entry.useTextMeshPro : false;

                    _layerConfigByLayerId[entry.id] = entry;
                }

                // Build Layer-reference lookup so GetUseTextMeshProForLayer works even after
                // PsdImage.ReleaseAllData() clears TaggedBlock data (making layer.LayerId return null).
                _useTextMeshProByLayerRef = new Dictionary<Layer, bool>();
                if (_layersById != null)
                {
                    foreach (var kv in _layersById)
                    {
                        if (_useTextMeshProByLayerId.TryGetValue(kv.Key, out bool tmpVal))
                            _useTextMeshProByLayerRef[kv.Value] = tmpVal;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Failed to load export config: {ex.Message}");
            }
        }

        /// <summary>Attach UI components from export config via <see cref="UiComponentHandlerRegistry"/>.</summary>
        private static void ApplyConfiguredUiComponents(Layer layer, GameObject go)
        {
            if (go == null || layer == null || !layer.LayerId.HasValue) return;
            if (GetUseExternalPrefab(layer)) return;
            if (_layerConfigByLayerId == null) return;
            if (!_layerConfigByLayerId.TryGetValue(layer.LayerId.Value, out var entry)) return;
            string type = string.IsNullOrEmpty(entry.uiComponentType) ? "None" : entry.uiComponentType;
            var handler = UiComponentHandlerRegistry.Get(type);
            handler?.Apply(layer, go, entry);
        }



        /// <summary>Pick non-colliding filename in folder: stem_1, stem_2, …</summary>
        private static string FindNextAvailablePngFileNameInFolder(string targetFolder, string originalFileName)
        {
            string ext = Path.GetExtension(originalFileName);
            if (string.IsNullOrEmpty(ext))
                ext = ".png";
            string stem = Path.GetFileNameWithoutExtension(originalFileName);
            for (int i = 1; i < 10000; i++)
            {
                string candidate = $"{stem}_{i}{ext}";
                if (!File.Exists(Path.Combine(targetFolder, candidate)))
                    return candidate;
            }

            return $"{stem}_{System.DateTime.UtcNow.Ticks}{ext}";
        }


        private static bool GetParticipateLocalDedup(Layer layer)
        {
            if (!layer.LayerId.HasValue || _participateLocalDedupByLayerId == null) return true;
            return !_participateLocalDedupByLayerId.TryGetValue(layer.LayerId.Value, out bool v) || v;
        }

        private static bool GetParticipateCommonDedup(Layer layer)
        {
            if (!layer.LayerId.HasValue || _participateCommonDedupByLayerId == null) return true;
            return !_participateCommonDedupByLayerId.TryGetValue(layer.LayerId.Value, out bool v) || v;
        }


        /// <summary>Use common-dir fingerprint dedup: requires "participate in common-dir dedup".</summary>
        private static bool GetParticipatesCommonDirectoryCacheDedup(Layer layer)
        {
            return GetParticipateCommonDedup(layer);
        }

        /// <summary>Run nine-slice for this node: true = current pipeline; false = export full image, no nine-slice.</summary>
        private static bool GetSliceImage(Layer layer)
        {
            bool defaultSliceImage = GetDefaultSliceImagePreference();
            if (!layer.LayerId.HasValue || _sliceImageByLayerId == null) return defaultSliceImage;
            return !_sliceImageByLayerId.TryGetValue(layer.LayerId.Value, out bool v) ? defaultSliceImage : v;
        }

        /// <summary>Marked as local-dedup "primary" (virtual rep may fix on this layer).</summary>
        private static bool GetPrimaryDedupNodeForLocalDedup(Layer layer)
        {
            if (!layer.LayerId.HasValue || _primaryDedupNodeByLayerId == null) return false;
            return _primaryDedupNodeByLayerId.TryGetValue(layer.LayerId.Value, out bool v) && v;
        }

        /// <summary>Use custom image: user Sprite; skip slice/nine-slice/dedup/Prefab swap.</summary>
        private static bool GetUseCustomImage(Layer layer)
        {
            if (!layer.LayerId.HasValue || _useCustomImageByLayerId == null) return false;
            return _useCustomImageByLayerId.TryGetValue(layer.LayerId.Value, out bool v) && v;
        }

        /// <summary>Custom image path (Assets/xxx.png); valid only when GetUseCustomImage is true.</summary>
        private static string GetCustomImagePath(Layer layer)
        {
            if (!layer.LayerId.HasValue || _customImagePathByLayerId == null) return null;
            return _customImagePathByLayerId.TryGetValue(layer.LayerId.Value, out string p) ? p : null;
        }

        /// <summary>When slicing is on, use per-layer nine-slice params from export config (_export_config.json).</summary>
        private static bool LayerUsesCustomNineSliceParams(Layer layer)
        {
            if (!GetSliceImage(layer)) return false;
            if (!layer.LayerId.HasValue || _layerConfigByLayerId == null) return false;
            return _layerConfigByLayerId.TryGetValue(layer.LayerId.Value, out var e) && e.useCustomNineSliceParams;
        }

        /// <summary>Nine-slice detection/compression params for layer: custom config or global EditorPrefs.</summary>
        private static void GetNineSliceParamsForLayer(Layer layer,
            out int borderInset, out int pixelThreshold, out int minCenterCols, out int minCenterRows, out int minSameZone)
        {
            if (LayerUsesCustomNineSliceParams(layer) &&
                _layerConfigByLayerId.TryGetValue(layer.LayerId.Value, out var e))
            {
                borderInset = Mathf.Max(0, e.nineSliceBorderInset);
                pixelThreshold = Mathf.Clamp(e.nineSlicePixelThreshold, 0, 255);
                minCenterCols = Mathf.Clamp(e.nineSliceMinCenterCols, 1, 4096);
                minCenterRows = Mathf.Clamp(e.nineSliceMinCenterRows, 1, 4096);
                minSameZone = Mathf.Max(1, e.nineSliceMinSameZone);
                return;
            }

            borderInset = Mathf.Max(0, BorderInset);
            pixelThreshold = Mathf.Clamp(NineSlicePixelThreshold, 0, 255);
            minCenterCols = Mathf.Max(1, NineSliceMinCenterCols);
            minCenterRows = Mathf.Max(1, NineSliceMinCenterRows);
            minSameZone = Mathf.Max(1, NineSliceMinSameZone);
        }

        private static string BuildNineSliceDedupKey(Layer layer, Vector4? sliceBorder)
        {
            if (!GetSliceImage(layer))
                return "ns:off";
            GetNineSliceParamsForLayer(layer, out int bi, out int pt, out int mcc, out int mcr, out int msz);
            string mode = LayerUsesCustomNineSliceParams(layer) ? "c" : "g";
            string p = $"{mode}|{bi}|{pt}|{mcc}|{mcr}|{msz}";
            if (!sliceBorder.HasValue)
                return $"ns:{p}|nb";
            Vector4 v = sliceBorder.Value;
            return $"ns:{p}|sb:{v.x},{v.y},{v.z},{v.w}";
        }

        private static string BuildNineSliceDedupKeyFromSpec(in LocalDedupExportSpec spec, Vector4? sliceBorder)
        {
            if (!spec.SliceEnabled)
                return "ns:off";
            string mode = spec.UseCustomNineSlice ? "c" : "g";
            string p = $"{mode}|{spec.Bi}|{spec.Pt}|{spec.Mcc}|{spec.Mcr}|{spec.Msz}";
            if (!sliceBorder.HasValue)
                return $"ns:{p}|nb";
            Vector4 v = sliceBorder.Value;
            return $"ns:{p}|sb:{v.x},{v.y},{v.z},{v.w}";
        }

        /// <summary>Resolve config directory (Assets/... or absolute) to full disk path for File.Copy.</summary>
        private static string ResolveCommonDirFullPath(string commonDirPath)
        {
            if (string.IsNullOrWhiteSpace(commonDirPath)) return null;
            string p = commonDirPath.Trim();
            if (Path.IsPathRooted(p))
                return p;
            if (p.StartsWith("Assets/") || p.StartsWith("Assets\\"))
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", p.Replace('/', Path.DirectorySeparatorChar)));
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", p.Replace('/', Path.DirectorySeparatorChar)));
        }

        /// <summary>Load common dirs from PSD_CommonDirectories.json; scan PNGs and build fingerprint cache for common-dir dedup.</summary>
        private static void BuildCommonDirImageCache()
        {
            _commonDirImageCache = new List<CommonDirImageEntry>();
            string configPath = CommonDirectoriesConfigPath;
            if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath)) return;
            try
            {
                string json = File.ReadAllText(configPath);
                var data = JsonUtility.FromJson<CommonDirectoriesData>(json);
                if (data?.paths == null || data.paths.Length == 0) return;
                foreach (string dirPath in data.paths)
                {
                    if (string.IsNullOrWhiteSpace(dirPath)) continue;
                    string fullDir = ResolveCommonDirFullPath(dirPath.Trim());
                    if (string.IsNullOrEmpty(fullDir) || !Directory.Exists(fullDir)) continue;
                    string[] pngFiles = Directory.GetFiles(fullDir, "*.png", SearchOption.AllDirectories);
                    foreach (string pngPath in pngFiles)
                    {
                        try
                        {
                            byte[] bytes = File.ReadAllBytes(pngPath);
                            Texture2D tex = new Texture2D(2, 2);
                            if (!tex.LoadImage(bytes)) { Object.DestroyImmediate(tex); continue; }
                            float[] fp = ComputeFingerprint(tex);
                            _commonDirImageCache.Add(new CommonDirImageEntry
                            {
                                fullPath = pngPath,
                                fingerprint = fp,
                                width = tex.width,
                                height = tex.height
                            });
                            Object.DestroyImmediate(tex);
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogWarning($"Common-dir fingerprint load failed {pngPath}: {ex.Message}");
                        }
                    }
                }
                if (_commonDirImageCache.Count > 0)
                    Debug.Log($"[Common dedup] Loaded {_commonDirImageCache.Count} common-directory image fingerprints");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Failed to load common directory config: {ex.Message}");
            }
        }

        /// <summary>Find matching image in common-dir cache by MAE (≤ <see cref="DedupMaeThreshold"/>). Index or -1.</summary>
        private static int FindDuplicateInCommonDirs(float[] fingerprint)
        {
            if (_commonDirImageCache == null) return -1;
            for (int i = 0; i < _commonDirImageCache.Count; i++)
            {
                var entry = _commonDirImageCache[i];
                float sumAbsDiff = 0f;
                for (int j = 0; j < fingerprint.Length; j++)
                    sumAbsDiff += Mathf.Abs(fingerprint[j] - entry.fingerprint[j]);
                if (sumAbsDiff / fingerprint.Length <= DedupMaeThreshold)
                    return i;
            }
            return -1;
        }

        private static bool GetMergeExport(Layer layer)
        {
            return _mergeExportConfig != null && layer.LayerId.HasValue &&
                   _mergeExportConfig.TryGetValue(layer.LayerId.Value, out bool v) && v;
        }

        /// <summary>Export this node: if false, no slice, no Prefab node; legacy configs default true when field missing.</summary>
        private static bool GetExported(Layer layer)
        {
            if (!layer.LayerId.HasValue || _exportedByLayerId == null) return true;
            return !_exportedByLayerId.TryGetValue(layer.LayerId.Value, out bool v) || v;
        }

        private static bool GetUseExternalPrefab(Layer layer)
        {
            return _externalPrefabByLayerId != null && layer.LayerId.HasValue &&
                   _externalPrefabByLayerId.ContainsKey(layer.LayerId.Value);
        }

        private static string GetExternalPrefabPath(Layer layer)
        {
            if (layer.LayerId.HasValue && _externalPrefabByLayerId != null &&
                _externalPrefabByLayerId.TryGetValue(layer.LayerId.Value, out string path))
                return path;
            return null;
        }

        private static string BuildExportImageFileName(Layer layer)
        {
            string safeName = CleanFileName(layer.Name);
            if (_useAutoImageNaming)
                return $"{safeName}_{layer.LayerId ?? layer.GetHashCode()}.png";
            return $"{safeName}.png";
        }

        private static string GetLayerHierarchyPathForExport(Layer layer)
        {
            var parts = new List<string>();
            for (Layer p = layer; p != null; p = p.Parent)
                parts.Add(string.IsNullOrEmpty(p.Name) ? "Root" : p.Name);
            parts.Reverse();
            return string.Join(" / ", parts);
        }

        /// <summary>
        /// Recursively export layer images; supports layer masks and clipping masks.
        /// </summary>
        private static void ExportAllLayerImages(Layer layer)
        {
            if (!GetExported(layer))
                return;

            if (layer.IsGroup)
            {
                ExportGroupChildren(layer);
                return;
            }

            // Single non-group layer (root level)
            ExportSingleLayer(layer);
        }

        /// <summary>
        /// Process group children: detect clipping groups and merge-export.
        /// Child order is PSD order: children[0]=bottom, children[count-1]=top.
        /// PSD clipping: NonBase clips to nearest Base below (lower index).
        /// </summary>
        private static void ExportGroupChildren(Layer group)
        {
            var children = group.Children;

            // Bottom-up scan: Base becomes currentBase; following NonBases clip to it.
            // Next Base finishes the previous clip group.

            Layer currentBase = null;
            var pendingClipped = new List<Layer>();

            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];

                if (!GetExported(child))
                {
                    // A skipped Base must still flush the previous Base+clipped group;
                    // otherwise pendingClipped from an earlier Base leaks into the next real Base,
                    // causing it to be processed as a clipping group (and merged into one image).
                    if (!child.IsClipped)
                    {
                        FinalizeCurrentBase(currentBase, pendingClipped);
                        pendingClipped.Clear();
                        currentBase = null;
                    }
                    continue;
                }

                if (child.IsClipped)
                {
                    // NonBase clips to currentBase
                    pendingClipped.Add(child);
                }
                else
                {
                    // New Base — finalize previous Base clip group
                    FinalizeCurrentBase(currentBase, pendingClipped);
                    pendingClipped.Clear();
                    currentBase = child;
                }
            }

            // Final Base
            FinalizeCurrentBase(currentBase, pendingClipped);
        }

        /// <summary>
        /// Finish one Base: merge if clipped children, else export normally.
        /// </summary>
        private static void FinalizeCurrentBase(Layer baseLayer, List<Layer> clippedLayers)
        {
            if (clippedLayers.Count > 0)
            {
                Debug.Log($"[Clipping mask] BaseLayer: {baseLayer?.Name ?? "(null)"}");
                for (int i = 0; i < clippedLayers.Count; i++)
                {
                    Debug.Log($"[Clipping mask] ClippedLayer {i}: {clippedLayers[i].Name}");
                }
            }
            if (baseLayer == null)
            {
                // No Base (orphan NonBases); export each normally
                foreach (var orphan in clippedLayers)
                {
                    if (GetUseExternalPrefab(orphan))
                        continue;
                    if (GetUseCustomImage(orphan))
                    {
                        if (orphan.IsGroup && !GetMergeExport(orphan))
                            ExportGroupChildren(orphan);
                        continue;
                    }
                    if (orphan.IsGroup && GetMergeExport(orphan))
                    {
                        Texture2D tex = _psdImage != null
                            ? _psdImage.CompositeGroupWithClipping((PsdTools.Layers.Group)orphan)
                            : ((PsdTools.Layers.Group)orphan).Composite();
                        if (tex != null)
                        {
                            QueueRasterForExport(tex, orphan);
                            Debug.Log($"[Merge export] Group merged: {orphan.Name}");
                        }
                    }
                    else if (orphan.IsGroup)
                        ExportGroupChildren(orphan);
                    else
                        ExportSingleLayer(orphan);
                }
                return;
            }

            // External Prefab: no raster export
            if (GetUseExternalPrefab(baseLayer))
                return;

            // Custom image: no raster here; if group and merge-export off, recurse children
            if (GetUseCustomImage(baseLayer))
            {
                if (baseLayer.IsGroup && !GetMergeExport(baseLayer))
                    ExportGroupChildren(baseLayer);
                return;
            }

            // Merge export: one combined image; children not exported separately
            if (baseLayer.IsGroup && GetMergeExport(baseLayer))
            {
                Texture2D tex = _psdImage != null
                    ? _psdImage.CompositeGroupWithClipping((PsdTools.Layers.Group)baseLayer)
                    : ((PsdTools.Layers.Group)baseLayer).Composite();
                if (tex != null)
                {
                    QueueRasterForExport(tex, baseLayer);
                    Debug.Log($"[Merge export] Group merged: {baseLayer.Name}");
                }
                return;
            }

            if (clippedLayers.Count > 0)
            {
                ProcessClippingGroup(baseLayer, clippedLayers);
            }
            else
            {
                if (baseLayer.IsGroup)
                    ExportGroupChildren(baseLayer);
                else
                    ExportSingleLayer(baseLayer);
            }
        }

        /// <summary>
        /// Collect the clipping (non-base) layers that are attached to <paramref name="baseLayer"/> in its parent group.
        /// Returns layers in bottom-to-top PSD order (same order as <see cref="ProcessClippingGroup"/>).
        /// Returns an empty list when the layer has no parent or no clipping siblings.
        /// </summary>
        private static List<Layer> CollectClippingLayersForBase(Layer baseLayer)
        {
            var result = new List<Layer>();
            if (baseLayer?.Parent == null) return result;
            var siblings = baseLayer.Parent.Children;
            // Find baseLayer's index; it must not itself be clipped
            int baseIdx = -1;
            for (int i = 0; i < siblings.Count; i++)
            {
                if (siblings[i] == baseLayer) { baseIdx = i; break; }
            }
            if (baseIdx < 0) return result;
            // Collect consecutive clipped layers immediately above (higher index = drawn on top in PSD)
            for (int i = baseIdx + 1; i < siblings.Count; i++)
            {
                if (siblings[i].IsClipped)
                    result.Add(siblings[i]);
                else
                    break;
            }
            return result;
        }

        private struct ClipRasterInfo
        {
            public Layer layer;
            public Texture2D texture;
            public int left;
            public int top;
        }

        private static bool TryGetLayerRasterOrigin(Layer layer, out int left, out int top)
        {
            left = 0;
            top = 0;
            if (layer == null)
                return false;

            if (layer.IsGroup)
            {
                var bbox = ((PsdTools.Layers.Group)layer).BBox;
                int width = bbox.Right - bbox.Left;
                int height = bbox.Bottom - bbox.Top;
                if (width <= 0 || height <= 0)
                    return false;

                left = bbox.Left;
                top = bbox.Top;
                return true;
            }

            if (layer.Width <= 0 || layer.Height <= 0)
                return false;

            left = layer.Left;
            top = layer.Top;
            return true;
        }

        private static bool TryBuildLayerTextureForClipping(Layer layer, out Texture2D texture, out int left, out int top)
        {
            texture = null;
            if (!TryGetLayerRasterOrigin(layer, out left, out top))
                return false;

            if (layer.IsGroup)
            {
                texture = ((PsdTools.Layers.Group)layer).Composite();
                if (texture == null)
                    return false;

                ApplyLayerMask(texture, layer);
                return true;
            }

            texture = CreateLayerTexture(layer);
            return texture != null;
        }

        private static List<ClipRasterInfo> BuildClipRasterInfos(List<Layer> clippedLayers)
        {
            var clipInfos = new List<ClipRasterInfo>();
            if (clippedLayers == null)
                return clipInfos;

            for (int i = 0; i < clippedLayers.Count; i++)
            {
                Layer clipLayer = clippedLayers[i];
                if (clipLayer == null || !clipLayer.Visible)
                    continue;

                if (!TryBuildLayerTextureForClipping(clipLayer, out Texture2D clipTex, out int clipLeft, out int clipTop))
                    continue;

                clipInfos.Add(new ClipRasterInfo
                {
                    layer = clipLayer,
                    texture = clipTex,
                    left = clipLeft,
                    top = clipTop
                });
            }

            return clipInfos;
        }

        private static void DestroyClipRasterInfos(List<ClipRasterInfo> clipInfos)
        {
            if (clipInfos == null)
                return;

            for (int i = 0; i < clipInfos.Count; i++)
            {
                if (clipInfos[i].texture != null)
                    Object.DestroyImmediate(clipInfos[i].texture);
            }
        }

        private static void MarkMergedClippingLayers(List<Layer> clippedLayers)
        {
            if (clippedLayers == null)
                return;

            for (int i = 0; i < clippedLayers.Count; i++)
            {
                Layer clipLayer = clippedLayers[i];
                if (clipLayer == null)
                    continue;

                _clippedLayers.Add(clipLayer);
                if (clipLayer.IsGroup)
                    MarkDescendantsClipped(clipLayer);
            }
        }

        private static void CollectGroupClipTargetsRecursive(Layer groupLayer, List<Layer> targets, HashSet<Layer> seen)
        {
            if (groupLayer == null || groupLayer.Children == null)
                return;

            foreach (var child in groupLayer.Children)
            {
                if (child == null)
                    continue;
                if (_clippedLayers != null && _clippedLayers.Contains(child))
                    continue;

                if (_rasterPending != null && _rasterPending.ContainsKey(child) && child.Kind != LayerKind.Type && seen.Add(child))
                    targets.Add(child);

                if (child.IsGroup)
                    CollectGroupClipTargetsRecursive(child, targets, seen);
            }
        }

        private static void ProcessClippingGroupWithGroupBase(Layer baseLayer, List<Layer> clippedLayers)
        {
            ExportGroupChildren(baseLayer);

            List<ClipRasterInfo> clipInfos = BuildClipRasterInfos(clippedLayers);
            try
            {
                if (_rasterPending == null || _rasterPending.Count == 0 || clipInfos.Count == 0)
                    return;

                var targets = new List<Layer>();
                var seen = new HashSet<Layer>();
                CollectGroupClipTargetsRecursive(baseLayer, targets, seen);
                if (targets.Count == 0)
                    return;

                Debug.Log($"[Clipping mask] Group base {baseLayer.Name}: apply {clipInfos.Count} clipped layers to {targets.Count} raster descendants");

                foreach (var targetLayer in targets)
                {
                    if (!_rasterPending.TryGetValue(targetLayer, out Texture2D targetTex) || targetTex == null)
                        continue;
                    if (!TryGetLayerRasterOrigin(targetLayer, out int targetLeft, out int targetTop))
                        continue;

                    Color32[] targetPixels = targetTex.GetPixels32();
                    byte[] originalAlpha = new byte[targetPixels.Length];
                    for (int i = 0; i < targetPixels.Length; i++)
                        originalAlpha[i] = targetPixels[i].a;

                    foreach (var clipInfo in clipInfos)
                    {
                        CompositeClippedOntoBase(targetTex, targetLeft, targetTop,
                            clipInfo.texture, clipInfo.left, clipInfo.top, originalAlpha);
                    }
                }
            }
            finally
            {
                DestroyClipRasterInfos(clipInfos);
                MarkMergedClippingLayers(clippedLayers);
            }
        }

        /// <summary>
        /// One clipping group: base layer (or group) + clipped layers.
        /// Group bases keep their hierarchy and receive the clipped composite on each raster descendant;
        /// non-group bases still merge into one image.
        /// </summary>
        private static void ProcessClippingGroup(Layer baseLayer, List<Layer> clippedLayers)
        {
            // Type base cannot raster-merge with clips; keep type export semantics; do not export clips alone
            if (baseLayer.Kind == LayerKind.Type)
            {
                foreach (var cl in clippedLayers)
                {
                    _clippedLayers.Add(cl);
                    if (cl.IsGroup)
                        MarkDescendantsClipped(cl);
                }
                return;
            }

            if (baseLayer.IsGroup)
            {
                ProcessClippingGroupWithGroupBase(baseLayer, clippedLayers);
                return;
            }

            if (!TryBuildLayerTextureForClipping(baseLayer, out Texture2D baseTex, out int baseLeft, out int baseTop))
            {
                foreach (var cl in clippedLayers)
                    ExportSingleLayer(cl);
                return;
            }

            // Store base original alpha as mask
            Color32[] basePixels = baseTex.GetPixels32();
            byte[] originalAlpha = new byte[basePixels.Length];
            for (int i = 0; i < basePixels.Length; i++)
                originalAlpha[i] = basePixels[i].a;

            List<ClipRasterInfo> clipInfos = BuildClipRasterInfos(clippedLayers);
            foreach (var clipInfo in clipInfos)
            {
                CompositeClippedOntoBase(baseTex, baseLeft, baseTop,
                    clipInfo.texture, clipInfo.left, clipInfo.top, originalAlpha);

                Debug.Log($"  Clipping merge: {clipInfo.layer.Name} -> {baseLayer.Name}");
            }
            DestroyClipRasterInfos(clipInfos);

            MarkMergedClippingLayers(clippedLayers);

            // Queue merged texture (written with dedup batch)
            QueueRasterForExport(baseTex, baseLayer);
        }

        /// <summary>
        /// Recursively mark descendants as merged (not exported separately)
        /// </summary>
        private static void MarkDescendantsClipped(Layer layer)
        {
            foreach (var child in layer.Children)
            {
                _clippedLayers.Add(child);
                if (child.IsGroup)
                    MarkDescendantsClipped(child);
            }
        }

        /// <summary>
        /// Composite clip onto base; visible only where base original alpha &gt; 0
        /// </summary>
        private static void CompositeClippedOntoBase(Texture2D baseTex, int baseLeft, int baseTop,
            Texture2D clipTex, int clipLeft, int clipTop, byte[] originalBaseAlpha)
        {
            int baseW = baseTex.width;
            int baseH = baseTex.height;
            int clipW = clipTex.width;
            int clipH = clipTex.height;

            Color32[] basePixels = baseTex.GetPixels32();
            Color32[] clipPixels = clipTex.GetPixels32();

            for (int psdY = 0; psdY < clipH; psdY++)
            {
                for (int psdX = 0; psdX < clipW; psdX++)
                {
                    // PSD world space
                    int worldX = clipLeft + psdX;
                    int worldY = clipTop + psdY;

                    // Base texture local PSD coords
                    int bx = worldX - baseLeft;
                    int by = worldY - baseTop;
                    if (bx < 0 || bx >= baseW || by < 0 || by >= baseH)
                        continue;

                    // Unity texture coords (Y flip)
                    int baseIdx = (baseH - 1 - by) * baseW + bx;
                    int clipIdx = (clipH - 1 - psdY) * clipW + psdX;

                    if (baseIdx < 0 || baseIdx >= basePixels.Length ||
                        clipIdx < 0 || clipIdx >= clipPixels.Length)
                        continue;

                    byte maskAlpha = originalBaseAlpha[baseIdx];
                    if (maskAlpha == 0)
                        continue;

                    Color32 src = clipPixels[clipIdx];
                    float srcA = (src.a / 255f) * (maskAlpha / 255f);
                    if (srcA < 0.001f)
                        continue;

                    Color32 dst = basePixels[baseIdx];
                    float dstA = dst.a / 255f;
                    float outA = srcA + dstA * (1f - srcA);

                    if (outA > 0f)
                    {
                        basePixels[baseIdx] = new Color32(
                            (byte)((src.r * srcA + dst.r * dstA * (1f - srcA)) / outA),
                            (byte)((src.g * srcA + dst.g * dstA * (1f - srcA)) / outA),
                            (byte)((src.b * srcA + dst.b * dstA * (1f - srcA)) / outA),
                            (byte)(outA * 255f)
                        );
                    }
                }
            }

            baseTex.SetPixels32(basePixels);
            baseTex.Apply();
        }

        /// <summary>
        /// Build processed layer texture (effects + layer mask); not saved to disk
        /// </summary>
        private static Texture2D CreateLayerTexture(Layer layer)
        {
                Texture2D texture = layer.Composite();
            bool hasGradient = TryGetGradientOverlay(layer, out var gradStops, out float gradAngle, out float gradOpacity);

                if (texture == null)
                {
                if (hasGradient)
                {
                    texture = CreateGradientTexture(layer.Width, layer.Height, gradStops, gradAngle, gradOpacity * layer.OpacityFloat);
                }
                else if (TryGetLayerColor(layer, out Color32 fallbackColor, out float colorOpacity))
                {
                    texture = CreateSolidColorTexture(layer.Width, layer.Height, fallbackColor, layer.OpacityFloat * colorOpacity);
                }
                else
                {
                    return null;
                }
            }
            else
            {
                if (hasGradient)
                    ApplyGradientOverlay(texture, gradStops, gradAngle, gradOpacity, layer.OpacityFloat);
                else
                    ApplyFillColorAndOpacity(texture, layer);
            }

            // Layer mask
            ApplyLayerMask(texture, layer);

            return texture;
        }

        /// <summary>
        /// Apply layer mask (multiply alpha by mask gray)
        /// </summary>
        private static void ApplyLayerMask(Texture2D texture, Layer layer)
        {
            if (!layer.HasMask || layer.Mask.IsDisabled)
                return;

            byte[] maskData = layer.GetMaskChannelData();
            if (maskData == null || maskData.Length == 0)
                return;

            var mask = layer.Mask;
            int maskW = mask.Width;
            int maskH = mask.Height;
            byte defaultColor = mask.DefaultColor;

            int texW = texture.width;
            int texH = texture.height;

            Color32[] pixels = texture.GetPixels32();

            for (int psdY = 0; psdY < layer.Height; psdY++)
            {
                for (int psdX = 0; psdX < layer.Width; psdX++)
                {
                    // PSD world space
                    int worldX = layer.Left + psdX;
                    int worldY = layer.Top + psdY;

                    // Mask local coords
                    int mx = worldX - mask.Left;
                    int my = worldY - mask.Top;

                    byte maskValue;
                    if (mx >= 0 && mx < maskW && my >= 0 && my < maskH)
                    {
                        int maskIdx = my * maskW + mx;
                        maskValue = (maskIdx < maskData.Length) ? maskData[maskIdx] : defaultColor;
                    }
                    else
                    {
                        maskValue = defaultColor;
                    }

                    if (maskValue == 255)
                        continue;

                    // Unity texture coords (Y flip)
                    int texIdx = (texH - 1 - psdY) * texW + psdX;
                    if (texIdx < 0 || texIdx >= pixels.Length)
                        continue;

                    pixels[texIdx].a = (byte)(pixels[texIdx].a * maskValue / 255);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
        }

        /// <summary>
        /// Export one layer (not a clipping-group path)
        /// </summary>
        private static void ExportSingleLayer(Layer layer)
        {
            if (GetUseExternalPrefab(layer) || GetUseCustomImage(layer))
                return;
            if (layer.Kind == LayerKind.Type)
                return;
            if (layer.Width <= 0 || layer.Height <= 0)
                return;
            if (!layer.HasPixels() && !HasExtractableColor(layer))
                return;

            Texture2D texture = CreateLayerTexture(layer);
            if (texture == null)
            {
                Debug.LogWarning($"Cannot composite layer: {layer.Name}");
                return;
            }

            QueueRasterForExport(texture, layer);
        }

        /// <summary>
        /// Bilinear sample in premultiplied alpha space to avoid dark fringes.
        /// </summary>
        private static Color BilinearSamplePremultiplied(Color32[] pixels, int w, int h, float x, float y)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, w - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, h - 1);
            int x1 = Mathf.Min(x0 + 1, w - 1);
            int y1 = Mathf.Min(y0 + 1, h - 1);

            float fx = Mathf.Max(0f, x - x0);
            float fy = Mathf.Max(0f, y - y0);
            float ifx = 1f - fx;
            float ify = 1f - fy;

            Color32 c00 = pixels[y0 * w + x0];
            Color32 c10 = pixels[y0 * w + x1];
            Color32 c01 = pixels[y1 * w + x0];
            Color32 c11 = pixels[y1 * w + x1];

            float a00 = c00.a / 255f, a10 = c10.a / 255f;
            float a01 = c01.a / 255f, a11 = c11.a / 255f;

            float pr = (c00.r / 255f * a00 * ifx + c10.r / 255f * a10 * fx) * ify +
                       (c01.r / 255f * a01 * ifx + c11.r / 255f * a11 * fx) * fy;
            float pg = (c00.g / 255f * a00 * ifx + c10.g / 255f * a10 * fx) * ify +
                       (c01.g / 255f * a01 * ifx + c11.g / 255f * a11 * fx) * fy;
            float pb = (c00.b / 255f * a00 * ifx + c10.b / 255f * a10 * fx) * ify +
                       (c01.b / 255f * a01 * ifx + c11.b / 255f * a11 * fx) * fy;
            float pa = (a00 * ifx + a10 * fx) * ify + (a01 * ifx + a11 * fx) * fy;

            if (pa > 0.001f)
                return new Color(Mathf.Clamp01(pr / pa), Mathf.Clamp01(pg / pa), Mathf.Clamp01(pb / pa), pa);
            return new Color(0, 0, 0, 0);
        }

        /// <summary>
        /// Trim fully transparent rows/columns; returns content pixels and size.
        /// If fully transparent, returns input unchanged.
        /// </summary>
        private static (Color32[] pixels, int width, int height) TrimTransparentBorders(
            Color32[] srcPixels, int srcW, int srcH)
        {
            int minX = srcW, maxX = -1, minY = srcH, maxY = -1;
            for (int y = 0; y < srcH; y++)
            {
                for (int x = 0; x < srcW; x++)
                {
                    if (srcPixels[y * srcW + x].a > 0)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            if (maxX < 0)
                return (srcPixels, srcW, srcH);

            int trimW = maxX - minX + 1;
            int trimH = maxY - minY + 1;
            if (trimW == srcW && trimH == srcH)
                return (srcPixels, srcW, srcH);

            Color32[] trimmed = new Color32[trimW * trimH];
            for (int y = 0; y < trimH; y++)
            {
                for (int x = 0; x < trimW; x++)
                {
                    trimmed[y * trimW + x] = srcPixels[(minY + y) * srcW + (minX + x)];
                }
            }
            return (trimmed, trimW, trimH);
        }

        /// <summary>
        /// Dedup fingerprint: trim transparent border → scale to <see cref="DedupFingerprintSize"/>² → 
        /// premultiplied RGBA floats for MAE (threshold <see cref="DedupMaeThreshold"/>).
        /// </summary>
        private static float[] ComputeFingerprint(Texture2D texture)
        {
            Color32[] srcPixels = texture.GetPixels32();
            var (trimPixels, trimW, trimH) = TrimTransparentBorders(srcPixels, texture.width, texture.height);

            int sz = DedupFingerprintSize;
            float[] fp = new float[sz * sz * 4];

            for (int y = 0; y < sz; y++)
            {
                for (int x = 0; x < sz; x++)
                {
                    float sx = (x + 0.5f) * trimW / sz - 0.5f;
                    float sy = (y + 0.5f) * trimH / sz - 0.5f;
                    Color c = BilinearSamplePremultiplied(trimPixels, trimW, trimH, sx, sy);

                    int idx = (y * sz + x) * 4;
                    fp[idx + 0] = c.r * c.a;
                    fp[idx + 1] = c.g * c.a;
                    fp[idx + 2] = c.b * c.a;
                    fp[idx + 3] = c.a;
                }
            }
            return fp;
        }

        private static void QueueRasterForExport(Texture2D texture, Layer layer)
        {
            if (texture == null || layer == null) return;
            if (_rasterPending == null)
                _rasterPending = new Dictionary<Layer, Texture2D>();
            if (_rasterPending.ContainsKey(layer))
            {
                Debug.LogError($"[PSD export] Duplicate raster enqueue: {layer.Name}");
                Object.DestroyImmediate(texture);
                return;
            }
            _rasterPending[layer] = texture;
        }

        /// <summary>Local dedup on source by MAE only (nine-slice key not compared).</summary>
        private static bool FingerprintsMatchLocal(float[] a, float[] b)
        {
            float sumAbsDiff = 0f;
            for (int j = 0; j < a.Length; j++)
                sumAbsDiff += Mathf.Abs(a[j] - b[j]);
            return sumAbsDiff / a.Length <= DedupMaeThreshold;
        }

        // ── Internal test helpers (used by DedupTestWindow) ──

        /// <summary>Exposed for dedup test tool only; calls the same <see cref="ComputeFingerprint"/> path as a real export.</summary>
        internal static float[] ComputeFingerprintForTest(Texture2D tex)
            => ComputeFingerprint(tex);

        /// <summary>Exposed for dedup test tool only; calls the same <see cref="FingerprintsMatchLocal"/> path as a real export.</summary>
        internal static bool FingerprintsMatchForTest(float[] a, float[] b)
            => FingerprintsMatchLocal(a, b);

        /// <summary>Current effective MAE threshold (from config); exposed for dedup test tool display.</summary>
        internal static float DedupMaeThresholdForTest => DedupMaeThreshold;

        /// <summary>Current effective fingerprint size (from config); exposed for dedup test tool display.</summary>
        internal static int DedupFingerprintSizeForTest => DedupFingerprintSize;

        /// <summary>Export params for "covers" comparison inside a local dedup group (slice, nine-slice, common dir, common-cache dedup); see <see cref="LocalDedupExportSpecCovers"/>.</summary>
        private readonly struct LocalDedupExportSpec
        {
            public readonly bool SliceEnabled;
            public readonly bool UseCustomNineSlice;
            public readonly int Bi, Pt, Mcc, Mcr, Msz;
            /// <summary>Compare against common-dir scanned images (same as <see cref="GetParticipatesCommonDirectoryCacheDedup"/>).</summary>
            public readonly bool ParticipateCommonDedup;

            public static LocalDedupExportSpec FromLayer(Layer layer)
            {
                bool pcd = GetParticipatesCommonDirectoryCacheDedup(layer);
                bool slice = GetSliceImage(layer);
                if (!slice)
                    return new LocalDedupExportSpec(false, false, 0, 0, 0, 0, 0, pcd);
                GetNineSliceParamsForLayer(layer, out int bi, out int pt, out int mcc, out int mcr, out int msz);
                bool useC = LayerUsesCustomNineSliceParams(layer);
                return new LocalDedupExportSpec(true, useC, bi, pt, mcc, mcr, msz, pcd);
            }

            private LocalDedupExportSpec(bool slice, bool useC, int bi, int pt, int mcc, int mcr, int msz,
                bool participateCommonDedup)
            {
                SliceEnabled = slice;
                UseCustomNineSlice = useC;
                Bi = bi; Pt = pt; Mcc = mcc; Mcr = mcr; Msz = msz;
                ParticipateCommonDedup = participateCommonDedup;
            }

            /// <summary>Synthetic export spec (virtual rep): same fields as <see cref="FromLayer"/>.</summary>
            public static LocalDedupExportSpec FromSynthetic(bool sliceEnabled, bool useCustomNineSlice, int bi, int pt,
                int mcc, int mcr, int msz, bool participateCommonCacheDedup)
            {
                return new LocalDedupExportSpec(sliceEnabled, useCustomNineSlice, bi, pt, mcc, mcr, msz,
                    participateCommonCacheDedup);
            }
        }

        /// <summary>
        /// Whether <paramref name="broader"/> fully covers <paramref name="narrower"/> (pick rep in same fingerprint group):
        /// Slice: "no slice" covers "slice"; if both slice, nine-slice params must match; "slice" cannot cover "no slice".
        /// Common-dir cache dedup: "off" covers "on"; "on" cannot cover "off".
        /// Common-dir path: only empty→non-empty widening or identical paths.
        /// </summary>
        private static bool LocalDedupExportSpecCovers(in LocalDedupExportSpec broader, in LocalDedupExportSpec narrower)
        {
            if (broader.SliceEnabled && !narrower.SliceEnabled)
                return false;
            if (broader.SliceEnabled && narrower.SliceEnabled)
            {
                if (broader.UseCustomNineSlice != narrower.UseCustomNineSlice) return false;
                if (broader.Bi != narrower.Bi || broader.Pt != narrower.Pt || broader.Mcc != narrower.Mcc ||
                    broader.Mcr != narrower.Mcr || broader.Msz != narrower.Msz)
                    return false;
            }
            if (broader.ParticipateCommonDedup && !narrower.ParticipateCommonDedup)
                return false;
            return true;
        }

        private static string FormatLocalDedupExportSpec(in LocalDedupExportSpec s)
        {
            string pcd = s.ParticipateCommonDedup ? "yes" : "no";
            if (!s.SliceEnabled)
                return $"no slice | common dedup: {pcd}";
            string mode = s.UseCustomNineSlice ? "custom nine-slice" : "global nine-slice";
            return $"{mode} bi={s.Bi} pt={s.Pt} mcc={s.Mcc} mcr={s.Mcr} msz={s.Msz} | common dedup: {pcd}";
        }

        /// <summary>Debug signature: common dir + slice flag + nine-slice params (global or custom values).</summary>
        private static string BuildExportParamSignature(Layer layer)
        {
            var s = LocalDedupExportSpec.FromLayer(layer);
            return FormatLocalDedupExportSpec(s);
        }

        private sealed class LocalDedupUnionFind
        {
            private readonly int[] _parent;
            public LocalDedupUnionFind(int n)
            {
                _parent = new int[n];
                for (int i = 0; i < n; i++) _parent[i] = i;
            }
            public int Find(int i)
            {
                if (_parent[i] != i) _parent[i] = Find(_parent[i]);
                return _parent[i];
            }
            public void Union(int a, int b)
            {
                a = Find(a); b = Find(b);
                if (a != b) _parent[b] = a;
            }
            public List<List<int>> BuildGroups(int n)
            {
                var buckets = new Dictionary<int, List<int>>();
                for (int i = 0; i < n; i++)
                {
                    int r = Find(i);
                    if (!buckets.TryGetValue(r, out var list))
                    {
                        list = new List<int>();
                        buckets[r] = list;
                    }
                    list.Add(i);
                }
                return buckets.Values.ToList();
            }
        }

        /// <summary>Largest raster by pixel count in local dedup group (tie-break: lower LayerId, then name).</summary>
        private static Layer PickLargestRasterSourceLayer(List<Layer> cluster)
        {
            Layer best = cluster[0];
            int bestPx = _rasterPending[best].width * _rasterPending[best].height;
            foreach (var l in cluster)
            {
                var tex = _rasterPending[l];
                int px = tex.width * tex.height;
                if (px > bestPx)
                {
                    bestPx = px;
                    best = l;
                }
                else if (px == bestPx)
                {
                    int ida = l.LayerId ?? int.MaxValue;
                    int idb = best.LayerId ?? int.MaxValue;
                    if (ida < idb || (ida == idb && string.Compare(l.Name, best.Name, System.StringComparison.Ordinal) < 0))
                        best = l;
                }
            }
            return best;
        }

        /// <summary>
        /// Build widest synthetic export spec over cluster per <see cref="LocalDedupExportSpecCovers"/>: common-dir paths must match or be partially empty;
        /// no common-cache dedup in synthetic rule; if any member disables slice, synthetic disables slice; if all slice but nine-slice numbers differ, error (non-custom nodes use global numbers); if all slice and match, shared nine-slice params.
        /// </summary>
        private static bool TryBuildSyntheticLocalDedupExportSpec(List<Layer> cluster, out LocalDedupExportSpec spec,
            out string errorMessage)
        {
            errorMessage = null;
            spec = default;
            if (cluster == null || cluster.Count == 0)
            {
                errorMessage = "Local dedup group is empty.";
                return false;
            }

            var specs = new List<LocalDedupExportSpec>(cluster.Count);
            foreach (var l in cluster)
                specs.Add(LocalDedupExportSpec.FromLayer(l));

            bool syntheticParticipateCommonCacheDedup = specs.TrueForAll(s => s.ParticipateCommonDedup);

            bool anyNoSlice = specs.Exists(s => !s.SliceEnabled);
            bool sliceEnabled;
            bool useC = false;
            int bi = 0, pt = 0, mcc = 0, mcr = 0, msz = 0;
            if (anyNoSlice)
            {
                sliceEnabled = false;
            }
            else
            {
                // All slice on: compare five numeric fields (non-custom nodes already resolved via GetNineSliceParamsForLayer).
                // Mismatch = config conflict; error and abort rather than silently disabling slice.
                LocalDedupExportSpec f = specs[0];
                bool allSameNine = specs.TrueForAll(s =>
                    s.Bi == f.Bi && s.Pt == f.Pt && s.Mcc == f.Mcc && s.Mcr == f.Mcr && s.Msz == f.Msz);
                if (allSameNine)
                {
                    sliceEnabled = true;
                    useC = f.UseCustomNineSlice;
                    bi = f.Bi;
                    pt = f.Pt;
                    mcc = f.Mcc;
                    mcr = f.Mcr;
                    msz = f.Msz;
                }
                else
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("These layers share a fingerprint group and all have slice enabled, but nine-slice effective values differ:");
                    sb.AppendLine();
                    foreach (var l in cluster)
                    {
                        var ns = LocalDedupExportSpec.FromLayer(l);
                        string nsMode = ns.UseCustomNineSlice ? "custom" : "global";
                        sb.Append("• ").AppendLine(FormatExportNodeLine(l));
                        sb.Append("    slice params: ").AppendLine($"{nsMode} bi={ns.Bi} pt={ns.Pt} mcc={ns.Mcc} mcr={ns.Mcr} msz={ns.Msz}");
                    }
                    errorMessage = sb.ToString().TrimEnd();
                    Debug.LogError(errorMessage);
                    return false;
                }
            }

            spec = LocalDedupExportSpec.FromSynthetic(sliceEnabled, useC, bi, pt, mcc, mcr, msz,
                syntheticParticipateCommonCacheDedup);

            for (int i = 0; i < specs.Count; i++)
            {
                if (!LocalDedupExportSpecCovers(spec, specs[i]))
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("Local dedup group cannot synthesize virtual export params satisfying cover rules (internal check failed).");
                    sb.AppendLine();
                    foreach (var l in cluster)
                    {
                        sb.Append("• ").AppendLine(FormatExportNodeLine(l));
                        sb.Append("  ").AppendLine(FormatLocalDedupExportSpec(LocalDedupExportSpec.FromLayer(l)));
                    }
                    errorMessage = sb.ToString().TrimEnd();
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Virtual rep: if any "primary" marked, use largest resolution among primaries (pixels + params from that layer); else largest raster + synthetic spec from full group.
        /// </summary>
        private static bool TryResolveVirtualLocalDedupGroup(List<Layer> cluster, out Layer imageSourceLayer,
            out LocalDedupExportSpec exportSpec, out string errorMessage)
        {
            errorMessage = null;
            imageSourceLayer = null;
            exportSpec = default;
            if (cluster == null || cluster.Count == 0)
                return false;
            if (cluster.Count == 1)
            {
                imageSourceLayer = cluster[0];
                exportSpec = LocalDedupExportSpec.FromLayer(cluster[0]);
                return true;
            }

            var primaryMarked = new List<Layer>();
            foreach (var l in cluster)
            {
                if (GetPrimaryDedupNodeForLocalDedup(l))
                    primaryMarked.Add(l);
            }

            if (primaryMarked.Count > 0)
            {
                imageSourceLayer = PickLargestRasterSourceLayer(primaryMarked);
                exportSpec = LocalDedupExportSpec.FromLayer(imageSourceLayer);
                return true;
            }

            imageSourceLayer = PickLargestRasterSourceLayer(cluster);
            return TryBuildSyntheticLocalDedupExportSpec(cluster, out exportSpec, out errorMessage);
        }

        private static void DisposeAllRasterPending()
        {
            if (_rasterPending == null) return;
            foreach (var kvp in _rasterPending)
            {
                if (kvp.Value != null)
                    Object.DestroyImmediate(kvp.Value);
            }
            _rasterPending.Clear();
        }

        private static bool ProcessPendingRasterExports(out string abortMessage)
        {
            abortMessage = null;
            if (_rasterPending == null || _rasterPending.Count == 0)
                return true;

            var allLayers = _rasterPending.Keys.ToList();
            var participators = new List<Layer>();
            var nonParticipators = new List<Layer>();
            foreach (var l in allLayers)
            {
                if (GetParticipateLocalDedup(l))
                    participators.Add(l);
                else
                    nonParticipators.Add(l);
            }

            var clusters = new List<List<Layer>>();
            int n = participators.Count;
            if (n > 0)
            {
                var fpCache = new float[n][];
                for (int i = 0; i < n; i++)
                {
                    fpCache[i] = ComputeFingerprint(_rasterPending[participators[i]]);
                }

                var uf = new LocalDedupUnionFind(n);
                for (int i = 0; i < n; i++)
                {
                    for (int j = i + 1; j < n; j++)
                    {
                        if (FingerprintsMatchLocal(fpCache[i], fpCache[j]))
                            uf.Union(i, j);
                    }
                }

                foreach (var idxGroup in uf.BuildGroups(n))
                {
                    var cluster = new List<Layer>();
                    foreach (int idx in idxGroup)
                        cluster.Add(participators[idx]);
                    clusters.Add(cluster);
                }
            }

            var exportJobs = new List<(List<Layer> cluster, Layer imageSource, LocalDedupExportSpec exportSpec)>();

            foreach (var c in clusters)
            {
                if (!TryResolveVirtualLocalDedupGroup(c, out Layer imgSrc, out LocalDedupExportSpec spec, out string err))
                {
                    abortMessage = err;
                    DisposeAllRasterPending();
                    return false;
                }
                exportJobs.Add((c, imgSrc, spec));
            }

            foreach (var solo in nonParticipators)
            {
                var one = new List<Layer> { solo };
                if (!TryResolveVirtualLocalDedupGroup(one, out Layer imgSolo, out LocalDedupExportSpec specSolo, out string err2))
                {
                    abortMessage = err2;
                    DisposeAllRasterPending();
                    return false;
                }
                exportJobs.Add((one, imgSolo, specSolo));
            }

            foreach (var job in exportJobs)
            {
                if (!SaveLayerTextureGrouped(_rasterPending[job.imageSource], job.imageSource, job.cluster, job.exportSpec,
                        out string jobAbort))
                {
                    abortMessage = string.IsNullOrEmpty(jobAbort) ? "Export canceled." : jobAbort;
                    DisposeAllRasterPending();
                    return false;
                }
            }

            DisposeAllRasterPending();
            return true;
        }

        /// <summary>
        /// Use <paramref name="namingLayer"/> raster and filename with synthetic <paramref name="exportSpec"/> for nine-slice, common-dir dedup, PNG write;
        /// same path mapping for <paramref name="groupMembers"/> (virtual rep: pixels vs params may differ).
        /// </summary>
        private static bool SaveLayerTextureGrouped(Texture2D texture, Layer namingLayer, List<Layer> groupMembers,
            LocalDedupExportSpec exportSpec, out string abortMessage)
        {
            abortMessage = null;
            if (texture == null || namingLayer == null || groupMembers == null || groupMembers.Count == 0)
                return true;

            string fileName = BuildExportImageFileName(namingLayer);
            string targetFolder = _outputFolder;
            if (!string.IsNullOrEmpty(targetFolder))
            {
                if (!Directory.Exists(targetFolder))
                    Directory.CreateDirectory(targetFolder);
            }
            else
                targetFolder = _outputFolder;
            string fullPath = Path.Combine(targetFolder, fileName);

            Vector4? sliceBorder = null;
            Texture2D imageToSave = texture;
            Texture2D slicedTex = null;
            int nsBorderInset = exportSpec.Bi;
            int nsPixelThresh = exportSpec.Pt;
            int nsMinCC = exportSpec.Mcc;
            int nsMinCR = exportSpec.Mcr;
            int nsMinSame = exportSpec.Msz;
            if (exportSpec.SliceEnabled)
            {
                sliceBorder = DetectNineSlice(texture, nsBorderInset, nsPixelThresh, nsMinSame, nsMinCC, nsMinCR);
                if (sliceBorder.HasValue)
                {
                    int l = (int)sliceBorder.Value.x;
                    int b = (int)sliceBorder.Value.y;
                    int r = (int)sliceBorder.Value.z;
                    int t = (int)sliceBorder.Value.w;
                    slicedTex = BuildNineSliceImage(texture, l, b, r, t, nsMinCC, nsMinCR);
                    imageToSave = slicedTex;
                }
            }


            string nineSliceDedupKey = BuildNineSliceDedupKeyFromSpec(exportSpec, sliceBorder);
            float[] fingerprintCommon = ComputeFingerprint(imageToSave);

            void AssignPathsAfterWrite(string primaryWrittenPath)
            {
                foreach (var m in groupMembers)
                {
                    _layerImagePaths[m] = primaryWrittenPath;
                    if (sliceBorder.HasValue)
                        _layerSliceBorders[m] = sliceBorder.Value;
                }
            }

            void RegisterDedupEntry(string pathForEntry)
            {
                _dedupEntries.Add(new DedupEntry
                {
                    fingerprint = fingerprintCommon,
                    fullPath = pathForEntry,
                    sliceBorder = sliceBorder,
                    nineSliceDedupKey = nineSliceDedupKey
                });
            }

            // Common-dir dedup: synthetic spec "participate common cache dedup" (true only if all in group participate)
            if (exportSpec.ParticipateCommonDedup && _commonDirImageCache != null && _commonDirImageCache.Count > 0)
            {
                int commonIdx = FindDuplicateInCommonDirs(fingerprintCommon);
                if (commonIdx >= 0)
                {
                    var entry = _commonDirImageCache[commonIdx];
                    int currentPixels = imageToSave.width * imageToSave.height;
                    int commonPixels = entry.width * entry.height;
                    bool detectCommonDirLargerImage = EditorPrefs.GetBool("PSDEditor_DetectCommonDirLargerImage", false);
                    if (detectCommonDirLargerImage && currentPixels > commonPixels)
                    {
                        var dedupPick = CommonDirectoryDuplicatePreviewWindow.ShowModalCommonDedup(entry.fullPath, imageToSave,
                            namingLayer.Name, groupMembers.Count);
                        if (dedupPick == CommonDirectoryDuplicatePreviewWindow.CommonDedupUserChoice.ReplaceWithCurrentImage)
                        {
                            try
                            {
                                string dir = Path.GetDirectoryName(entry.fullPath);
                                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                    Directory.CreateDirectory(dir);
                                byte[] bytes = imageToSave.EncodeToPNG();
                                WritePngBytesToDestinationViaPsdCache(entry.fullPath, bytes, groupMembers);
                                _commonDirImageCache[commonIdx] = new CommonDirImageEntry
                                {
                                    fullPath = entry.fullPath,
                                    fingerprint = fingerprintCommon,
                                    width = imageToSave.width,
                                    height = imageToSave.height
                                };
                                RegisterDedupEntry(entry.fullPath);
                                AssignPathsAfterWrite(entry.fullPath);
                                string groupNote = groupMembers.Count > 1 ? $" [{groupMembers.Count} nodes in group]" : "";
                                Debug.Log($"[Common dedup-replace] {namingLayer.Name}{groupNote} -> replaced common-dir image: {Path.GetFileName(entry.fullPath)}");
                                if (slicedTex != null)
                                {
                                    Object.DestroyImmediate(slicedTex);
                                    slicedTex = null;
                                }
                                return true;
                            }
                            catch (System.Exception ex)
                            {
                                Debug.LogWarning($"Replace common-dir image failed: {ex.Message}");
                            }
                        }
                    }

                    AssignPathsAfterWrite(entry.fullPath);
                    string noteReuse = groupMembers.Count > 1 ? $" [{groupMembers.Count} nodes in group]" : "";
                    Debug.Log($"[Common dedup] {namingLayer.Name}{noteReuse} reusing common-dir image: {Path.GetFileName(entry.fullPath)}");
                    if (slicedTex != null)
                        Object.DestroyImmediate(slicedTex);
                    return true;
                }
            }

            if (exportSpec.SliceEnabled && sliceBorder.HasValue)
            {
                int l = (int)sliceBorder.Value.x;
                int b = (int)sliceBorder.Value.y;
                int r = (int)sliceBorder.Value.z;
                int t = (int)sliceBorder.Value.w;
                WritePngBytesToDestinationViaPsdCache(fullPath, slicedTex.EncodeToPNG(), groupMembers);
                Object.DestroyImmediate(slicedTex);

                ComputeNineSliceCenterCrop(texture.width, texture.height, l, r, b, t, nsMinCC, nsMinCR, out int logCC, out int logCR);
                Debug.Log($"Nine-slice export: {namingLayer.Name} -> {fileName} (source {texture.width}x{texture.height} -> " +
                          $"{l + logCC + r}x{b + logCR + t}), inset: L={l} B={b} R={r} T={t}, center shrink: {logCC}x{logCR}" +
                          (exportSpec.UseCustomNineSlice ? " [custom nine-slice params]" : "") +
                          (groupMembers.Count > 1 ? $" [local dedup {groupMembers.Count} nodes · virtual rep]" : ""));
            }
            else
            {
                WritePngBytesToDestinationViaPsdCache(fullPath, texture.EncodeToPNG(), groupMembers);
                Debug.Log($"Export layer: {namingLayer.Name} -> {fileName}" +
                          (groupMembers.Count > 1 ? $" [local dedup {groupMembers.Count} nodes · virtual rep]" : ""));
            }

            RegisterDedupEntry(fullPath);
            AssignPathsAfterWrite(fullPath);
            return true;
        }

        /// <summary>
        /// Set all exported images to Sprite and apply spriteBorder for nine-slice.
        /// </summary>
        private static void SetupAllSprites()
        {
            if (_dedupEntries != null && _dedupEntries.Count > 0)
                Debug.Log($"Image dedup: {_dedupEntries.Count} distinct image(s)");

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var kvp in _layerImagePaths)
                {
                    Layer layer = kvp.Key;
                    string imagePath = kvp.Value;
                    if (string.IsNullOrEmpty(imagePath))
                        continue;

                    string relativePath = GetRelativeAssetsPath(imagePath);
                    AssetImporter importer = AssetImporter.GetAtPath(relativePath);
                    if (importer is TextureImporter texImporter)
                    {
                        bool needReimport = false;

                        if (texImporter.textureType != TextureImporterType.Sprite)
                        {
                            texImporter.textureType = TextureImporterType.Sprite;
                            needReimport = true;
                        }

                        if (texImporter.spriteImportMode != SpriteImportMode.Single)
                        {
                            texImporter.spriteImportMode = SpriteImportMode.Single;
                            needReimport = true;
                        }

                        if (_layerSliceBorders.TryGetValue(layer, out Vector4 border))
                        {
                            texImporter.spriteBorder = border;
                            needReimport = true;
                            Debug.Log($"Nine-slice border: {layer.Name} -> L={border.x} B={border.y} R={border.z} T={border.w}");
                        }

                        if (needReimport)
                        {
                            texImporter.SaveAndReimport();
                        }
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
        }

        /// <summary>
        /// Create hierarchical UI Prefab
        /// </summary>
        private static void CreateHierarchicalUIPrefab(PsdImage psd, string psdName)
        {
            string savePath = CombineWithPrefabExportRoot($"{psdName}.prefab");
            EnsureAssetFolderExistsForPath(savePath);

            // ── Difference comparison mode: Existing Prefab + PrefabMap → Generate temporary Prefab B → Compare → Popup for selection ──
            string mapFilePath = Path.Combine(Application.dataPath, "PsdToUnityUI", "PrefabMap",
                psdName + "_PrefabMap.json");
            bool prefabExists = AssetDatabase.LoadAssetAtPath<GameObject>(savePath) != null;
            bool mapExists    = File.Exists(mapFilePath);
            bool existingPrefabValid = prefabExists && mapExists;
            Debug.Log($"[PSD DiffCheck] savePath={savePath} | prefabExists={prefabExists} | mapFilePath={mapFilePath} | mapExists={mapExists} | existingPrefabValid={existingPrefabValid}");

            if (existingPrefabValid)
            {
                // Generate temporary Prefab B (current PSD state)
                // dataB is read from the saved TempB Prefab asset, ensuring driven RectTransform properties
                // (for example Slider/Scrollbar-driven handles) are in the same serialization state as dataA
                // to avoid false differences.
                string tempPrefabPath = GenerateTempPrefabB(psd, psdName, savePath, out var dataB);
                string tempMapPath = Path.Combine(Application.dataPath, "PsdToUnityUI", "PrefabMap",
                    psdName + "_TempB_PrefabMap.json");
                Debug.Log($"[PSD DiffCheck] GenerateTempPrefabB returned: '{tempPrefabPath}'");
                Debug.Log($"[PSD DiffCheck] dataB={( dataB == null ? "null" : dataB.Count.ToString() + " nodes")}");
                if (!string.IsNullOrEmpty(tempPrefabPath))
                {
                    // Build Dataset A (existing Prefab, still following SavedPrefab+PrefabMap path)
                    var dataA = BuildDatasetFromPrefab(savePath, mapFilePath);
                    Debug.Log($"[PSD DiffCheck] dataA={( dataA == null ? "null" : dataA.Count.ToString() + " nodes")}");

                    if (dataA != null && dataB != null)
                    {
                        // Check for differences
                        bool hasDiff = CheckHasDifferences(dataA, dataB, CompareNameDiff);
                        Debug.Log($"[PSD DiffCheck] CheckHasDifferences={hasDiff}");
                        if (hasDiff)
                        {
                            // Capture current export static state for callback use
                            var capturedState = CaptureExportState();

                            // Inform finally block and PsdCacheExportCleanup to retain PSD data, waiting for user decision
                            _deferPsdRelease = true;

                            // Open comparison window, three callbacks
                            PsdExportDiffWindow.Show(dataA, dataB, psdName, CompareNameDiff,
                                // Incremental apply
                                (ExportDiffDecisions diffDecisions) =>
                                {
                                    EditorApplication.delayCall += () =>
                                    {
                                        RestoreExportState(capturedState);
                                        ApplyExportDiffPatch(capturedState.psdImage, psdName, savePath, diffDecisions);
                                        CleanupTempPrefab(tempPrefabPath, tempMapPath);
                                        LastGeneratedPrefabAssetPath = savePath;
                                        ReleaseDeferredPsdAndState();
                                    };
                                },
                                // Fresh overwrite export
                                () =>
                                {
                                    EditorApplication.delayCall += () =>
                                    {
                                        RestoreExportState(capturedState);
                                        CleanupTempPrefab(tempPrefabPath, tempMapPath);
                                        // If the Prefab is currently open in Prefab Stage, close it first,
                                        // to prevent stale data in the Stage from overwriting the newly generated Prefab during user save, causing fileID association breakage.
                                        bool wasInStage = ClosePrefabStageIfOpenForAsset(savePath);
                                        try
                                        {
                                            // PSD data is still valid (ReleaseAllData was deferred), reuse directly.
                                            // Save over the existing asset so its .meta GUID remains stable.
                                            DoFreshPrefabExport(capturedState.psdImage, psdName, savePath);
                                        }
                                        finally
                                        {
                                            // If the Prefab was previously open in Stage, reopen it to show latest content.
                                            if (wasInStage) ReopenPrefabInStage(savePath);
                                            ReleaseDeferredPsdAndState();
                                        }
                                    };
                                },
                                // Cancel
                                () =>
                                {
                                    EditorApplication.delayCall += () =>
                                    {
                                        CleanupTempPrefab(tempPrefabPath, tempMapPath);
                                        LastGeneratedPrefabAssetPath = savePath;
                                        Debug.Log("[PSD Export] User canceled difference comparison, keeping existing Prefab unchanged.");
                                        ReleaseDeferredPsdAndState();
                                    };
                                });

                            // Return immediately after opening comparison window; execution continues in callback
                            LastGeneratedPrefabAssetPath = savePath;
                            return;
                        }
                        else
                        {
                            // No differences: overwrite directly (silent)
                            CleanupTempPrefab(tempPrefabPath, tempMapPath);
                            Debug.Log($"[PSD Export] Existing Prefab has no differences with current PSD, skipping overwrite. {savePath}");
                            LastGeneratedPrefabAssetPath = savePath;
                            return;
                        }
                    }
                    else
                    {
                        // Dataset build failed, cleaning up temp files then normal export
                        Debug.LogWarning($"[PSD DiffCheck] Dataset build failed (dataA={( dataA == null ? "null" : "ok")}, dataB={( dataB == null ? "null" : "ok")}), falling back to fresh export.");
                        CleanupTempPrefab(tempPrefabPath, tempMapPath);
                    }
                }
                else
                {
                    Debug.LogWarning("[PSD DiffCheck] GenerateTempPrefabB returned empty path, falling back to fresh export.");
                }
            }

            // ── Normal fresh export flow (direct replacement, diff comparison path already handles prompt scenarios) ──
            // If the Prefab is currently open in Prefab Stage, close it first,
            // to prevent stale data in the Stage from overwriting the newly generated Prefab during user save, causing fileID association breakage.
            bool wasInStage = ClosePrefabStageIfOpenForAsset(savePath);
            try
            {
                DoFreshPrefabExport(psd, psdName, savePath);
            }
            finally
            {
                // If previously open in Stage, reopen it to show latest content.
                if (wasInStage) ReopenPrefabInStage(savePath);
            }
        }

        /// <summary>Execute fresh Prefab export (excluding difference comparison).</summary>
        private static void DoFreshPrefabExport(PsdImage psd, string psdName, string savePath)
        {
            EnsureAssetFolderExistsForPath(savePath);

            // Ensure font mapping state is initialized (may have been released when triggered via delayCall callback)
            if (_unrecognizedPsdFontNamesThisExport == null || _pendingFontMappingEntries == null)
                InitFontMappingForExport();
#if USE_TMP
            if (_tmpFaceMaterialVariantCache == null)
                _tmpFaceMaterialVariantCache = new Dictionary<string, Material>();
#endif

            // Export per-node Prefabs (deepest first), record paths; main Prefab references them
            _exportPrefabAssetPathByLayerId = new Dictionary<int, string>();
            if (_exportPrefabLayerIds != null && _exportPrefabLayerIds.Count > 0)
            {
                ExportSingleNodePrefabs(psd, savePath);
                AssetDatabase.Refresh();
            }

            // Initialize instanceID → layerID tracking for PrefabMap generation
            _goInstanceIdToLayerId = new Dictionary<int, int>();

            // Root GameObject
            GameObject rootGo = new GameObject(psdName, typeof(RectTransform));
            try
            {
                RectTransform rootRT = rootGo.GetComponent<RectTransform>();
                rootRT.sizeDelta = new Vector2(psd.Width, psd.Height);

                // Children: parent center in PSD space
                float rootCenterX = psd.Width / 2f;
                float rootCenterY = psd.Height / 2f;
                foreach (var child in psd.Children)
                {
                    CreateLayerGameObject(child, rootGo.transform, rootCenterX, rootCenterY);
                }

                // Save over the existing Prefab without deleting its .meta, preserving its GUID.
                SaveMainPrefabPreservingGuid(rootGo, savePath);
                Debug.Log($"Prefab saved: {savePath}");

                // Generate fileID → layerID mapping JSON.
                // SaveMainPrefabPreservingGuid refreshes the AssetDatabase first so fileIDs are available.
                SavePrefabMap(savePath, psdName);

                // Last generated Prefab path (external navigation)
                LastGeneratedPrefabAssetPath = savePath;
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
            }
        }

        /// <summary>Save the main Prefab in place and verify that an existing asset keeps its GUID.</summary>
        internal static void SaveMainPrefabPreservingGuid(GameObject rootGo, string savePath)
        {
            if (rootGo == null)
                throw new System.ArgumentNullException(nameof(rootGo));
            if (string.IsNullOrEmpty(savePath))
                throw new System.ArgumentException("Prefab save path cannot be empty.", nameof(savePath));

            string guidBefore = AssetDatabase.AssetPathToGUID(savePath);
            PrefabUtility.SaveAsPrefabAsset(rootGo, savePath, out bool success);
            if (!success)
                throw new System.InvalidOperationException($"Failed to save Prefab: {savePath}");

            // Refresh so the saved asset, its GUID, and its local fileIDs are immediately queryable.
            AssetDatabase.Refresh();

            string guidAfter = AssetDatabase.AssetPathToGUID(savePath);
            if (string.IsNullOrEmpty(guidAfter))
                throw new System.InvalidOperationException($"Saved Prefab has no GUID: {savePath}");
            if (!string.IsNullOrEmpty(guidBefore) && guidBefore != guidAfter)
            {
                throw new System.InvalidOperationException(
                    $"Prefab GUID changed during in-place overwrite: {savePath} ({guidBefore} -> {guidAfter})");
            }
        }

        /// <summary>
        /// If specified Prefab is open in Prefab Stage, close it first (return to main scene),
        /// to prevent stale Stage content from overwriting the fresh Prefab during save, causing fileID association breakage.
        /// Returns true if a Stage was closed.
        /// </summary>
        private static bool ClosePrefabStageIfOpenForAsset(string prefabAssetPath)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null) return false;

            string stageFull = Path.GetFullPath(stage.assetPath);
            string targetFull = Path.GetFullPath(prefabAssetPath);
            if (!string.Equals(stageFull, targetFull, System.StringComparison.OrdinalIgnoreCase))
                return false;

            Debug.Log($"[PSD Export] Closing Prefab Stage: '{prefabAssetPath}', preventing stale Stage data from overwriting new Prefab.");
            StageUtility.GoToMainStage();
            return true;
        }

        /// <summary>
        /// After fresh export, reopen the Prefab Stage (equivalent to double-click) to let user see latest content.
        /// </summary>
        private static void ReopenPrefabInStage(string prefabAssetPath)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath);
            if (prefab != null)
            {
                AssetDatabase.OpenAsset(prefab);
                Debug.Log($"[PSD Export] Fresh export complete, reopened Prefab Stage: '{prefabAssetPath}'.");
            }
        }

        // ───────────────────────── Export Diff Helpers ─────────────────────────

        /// <summary>Generates temporary Prefab B (current PSD state), saves to TempPrefab folder, and generates PrefabMap.
        /// dataBInMemory is prioritized from the saved TempB Prefab asset, ensuring driven RectTransform properties
        /// (for example Slider/Scrollbar-driven handles) are in the same serialization state as dataA to avoid false differences.
        /// Only falls back to direct scene GO tree build if the Prefab asset fails to load.</summary>
        private static string GenerateTempPrefabB(PsdImage psd, string psdName, string existingPrefabPath,
            out List<ExportDiffNode> dataBInMemory)
        {
            dataBInMemory = null;
            string tempFolder = "Assets/PsdToUnityUI/TempPrefab";
            EnsureAssetFolderExistsForPath(tempFolder + "/dummy.prefab");
            string tempSavePath = tempFolder + "/" + psdName + "_TempB.prefab";
            Debug.Log($"[PSD TempPrefabB] Starting generation of temporary Prefab B: {tempSavePath}");

            // Export per-node Prefabs: export directly to target folder, ensuring reference paths remain valid after incremental update
            _exportPrefabAssetPathByLayerId = new Dictionary<int, string>();
            if (_exportPrefabLayerIds != null && _exportPrefabLayerIds.Count > 0)
            {
                // Note: must pass existingPrefabPath (official path) here, not tempSavePath (temp path)
                // This ensures sub-prefabs are generated in their official location, and _exportPrefabAssetPathByLayerId records long-term valid paths
                ExportSingleNodePrefabs(psd, existingPrefabPath);
                AssetDatabase.Refresh();
            }

            // Initialize instanceID → layerID tracking
            _goInstanceIdToLayerId = new Dictionary<int, int>();

            // Root GameObject
            GameObject rootGo = new GameObject(psdName, typeof(RectTransform));
            RectTransform rootRT = rootGo.GetComponent<RectTransform>();
            rootRT.sizeDelta = new Vector2(psd.Width, psd.Height);

            float rootCenterX = psd.Width / 2f;
            float rootCenterY = psd.Height / 2f;
            foreach (var child in psd.Children)
            {
                CreateLayerGameObject(child, rootGo.transform, rootCenterX, rootCenterY);
            }

            Debug.Log($"[PSD TempPrefabB] Scene GO build complete, _goInstanceIdToLayerId count={_goInstanceIdToLayerId?.Count}");

            // Save temp Prefab first — serialization strips driven RectTransform properties
            // (e.g. Slider/Scrollbar driving handle anchors) back to their stored
            // defaults, matching the state that dataA reads from the existing Prefab asset.
            PrefabUtility.SaveAsPrefabAsset(rootGo, tempSavePath);
            AssetDatabase.Refresh();
            SavePrefabMap(tempSavePath, psdName + "_TempB");

            // Primary path: build dataB from the serialized TempB Prefab asset.
            // This ensures driven properties (Slider handle RectTransform, etc.) are in the
            // same serialized state as dataA, avoiding false diffs.
            string tempMapPath = Path.Combine(Application.dataPath, "PsdToUnityUI", "PrefabMap",
                psdName + "_TempB_PrefabMap.json");
            dataBInMemory = BuildDatasetFromPrefab(tempSavePath, tempMapPath);
            Debug.Log($"[PSD TempPrefabB] Built dataBInMemory from saved Prefab asset: {( dataBInMemory == null ? "null" : dataBInMemory.Count.ToString() + " nodes")}");

            // Fallback: if LoadAllAssetsAtPath returned empty (AssetDatabase timing issue),
            // build from the still-alive scene GO tree. Driven properties may cause false
            // diffs, but this is better than returning null.
            if (dataBInMemory == null)
            {
                Debug.LogWarning("[PSD TempPrefabB] Prefab asset load failed, falling back to scene GO tree for dataB (driven properties may cause false differences)");
                dataBInMemory = BuildDatasetFromSceneGoTree(rootGo, _goInstanceIdToLayerId);
                Debug.Log($"[PSD TempPrefabB] Fallback scene GO tree built dataBInMemory: {( dataBInMemory == null ? "null" : dataBInMemory.Count.ToString() + " nodes")}");
            }

            Object.DestroyImmediate(rootGo);

            Debug.Log($"[PSD TempPrefabB] Temporary Prefab B generated: {tempSavePath}");
            return tempSavePath;
        }

        /// <summary>
        /// Build ExportDiffNode list directly from the scene GO tree (with <paramref name="root"/> as root).
        /// <paramref name="instanceIdToLayerId"/> must be called after <see cref="CreateLayerGameObject"/>
        /// and before DestroyImmediate, otherwise instanceIDs will be invalid.
        /// Independent of JSON files or AssetDatabase, avoiding Refresh timing issues.
        /// </summary>
        private static List<ExportDiffNode> BuildDatasetFromSceneGoTree(
            GameObject root,
            Dictionary<int, int> instanceIdToLayerId)
        {
            if (root == null || instanceIdToLayerId == null || instanceIdToLayerId.Count == 0)
            {
                Debug.LogWarning("[PSD BuildDataset SceneGO] root or instanceIdToLayerId is null/empty, returning null");
                return null;
            }

            // Establish fast lookup for instanceID -> layerId (provided by parameter),
            // then map GO instanceID -> parentLayerId (inferred from GO hierarchy)
            var result = new List<ExportDiffNode>(instanceIdToLayerId.Count);

            // Collect all mapped GOs (DFS)
            void Traverse(Transform t)
            {
                int iid = t.gameObject.GetInstanceID();
                int treeNodeId;
                int psdLayerId = -1;
                bool isPsdNode = false;

                if (instanceIdToLayerId.TryGetValue(iid, out int layerId))
                {
                    treeNodeId = layerId;
                    psdLayerId = layerId;
                    isPsdNode = true;
                }
                else
                {
                    treeNodeId = -Mathf.Abs(iid);
                }

                int parentTreeNodeId = -1;
                int parentPsdLayerId = -1;
                string parentName = "";

                if (t.parent != null)
                {
                    parentName = t.parent.name;
                    int parentIid = t.parent.gameObject.GetInstanceID();
                    if (instanceIdToLayerId.TryGetValue(parentIid, out int pLayerId))
                        parentTreeNodeId = pLayerId;
                    else
                        parentTreeNodeId = -Mathf.Abs(parentIid);

                    // Traverse ancestors to find the nearest ancestor with a layerId as the logical parent for diff evaluation
                    Transform ancestor = t.parent;
                    while (ancestor != null)
                    {
                        if (instanceIdToLayerId.TryGetValue(ancestor.gameObject.GetInstanceID(), out parentPsdLayerId))
                            break;
                        parentPsdLayerId = -1;
                        ancestor = ancestor.parent;
                    }
                }

                result.Add(new ExportDiffNode
                {
                    treeNodeId = treeNodeId,
                    psdLayerId = psdLayerId,
                    isPsdNode = isPsdNode,
                    name = t.gameObject.name,
                    parentTreeNodeId = parentTreeNodeId,
                    parentPsdLayerId = parentPsdLayerId,
                    parentName = parentName,
                    componentsJson = BuildGameObjectComponentsJson(t.gameObject),
                    siblingIndex = t.GetSiblingIndex(),
                    isPrefab = PrefabUtility.IsPartOfPrefabInstance(t.gameObject),
                });

                foreach (Transform child in t)
                    Traverse(child);
            }

            Traverse(root.transform);

            Debug.Log($"[PSD BuildDataset SceneGO] nodes built from scene GO tree={result.Count}");
            return result.Count > 0 ? result : null;
        }

        /// <summary>Build ExportDiffNode list from existing Prefab + PrefabMap.</summary>
        private static List<ExportDiffNode> BuildDatasetFromPrefab(string prefabPath, string mapFilePath)
        {
            Debug.Log($"[PSD BuildDataset] prefabPath={prefabPath} | mapFilePath={mapFilePath} | mapExists={File.Exists(mapFilePath)}");
            if (!File.Exists(mapFilePath))
            {
                Debug.LogWarning($"[PSD BuildDataset] PrefabMap file missing, returning null: {mapFilePath}");
                return null;
            }
            PrefabMapData mapData;
            try
            {
                mapData = JsonUtility.FromJson<PrefabMapData>(
                    File.ReadAllText(mapFilePath, System.Text.Encoding.UTF8));
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[PSD BuildDataset] PrefabMap load failed: {ex.Message}");
                return null;
            }
            if (mapData?.entries == null || mapData.entries.Length == 0)
            {
                Debug.LogWarning($"[PSD BuildDataset] PrefabMap entries empty, returning null: {mapFilePath}");
                return null;
            }
            Debug.Log($"[PSD BuildDataset] PrefabMap loaded, entries={mapData.entries.Length}");

            // ── Matching by path (fileId changes after Prefab deletion/reconstruction, path is independent of fileId) ──
            var pathToEntry = new Dictionary<string, PrefabMapEntry>(mapData.entries.Length, System.StringComparer.Ordinal);
            var pathToLayerId = new Dictionary<string, int>(mapData.entries.Length, System.StringComparer.Ordinal);
            // fileId -> entry: Fallback matching for path changes (manual node movement)
            var fileIdToEntry = new Dictionary<long, PrefabMapEntry>(mapData.entries.Length);
            foreach (var e in mapData.entries)
            {
                if (string.IsNullOrEmpty(e.path)) continue;
                pathToEntry[e.path] = e;
                pathToLayerId[e.path] = e.layerId;
                if (e.fileId != 0) fileIdToEntry[e.fileId] = e;
            }

            var prefabAssets = AssetDatabase.LoadAllAssetsAtPath(prefabPath);
            int goCountInPrefab = prefabAssets == null ? 0 : prefabAssets.Count(a => a is GameObject);
            Debug.Log($"[PSD BuildDataset] LoadAllAssetsAtPath({prefabPath}) -> Total assets={prefabAssets?.Length ?? 0}, GO count={goCountInPrefab}");

            if (prefabAssets == null || goCountInPrefab == 0)
            {
                Debug.LogWarning($"[PSD BuildDataset] No GO found in Prefab, returning null");
                return null;
            }

            // Build GO instanceID -> true psd layerId mapping
            var goPsdLayerId = new Dictionary<int, int>();
            foreach (var asset in prefabAssets)
            {
                if (!(asset is GameObject go)) continue;
                string goPath = GetTransformPathWithSiblingIndices(go.transform);
                if (pathToLayerId.TryGetValue(goPath, out int lid))
                {
                    goPsdLayerId[go.GetInstanceID()] = lid;
                }
                else
                {
                    // Path matching failed: trying fileId fallback
                    long fid = (long)GlobalObjectId.GetGlobalObjectIdSlow(go).targetObjectId;
                    if (fid != 0 && fileIdToEntry.TryGetValue(fid, out var fe))
                        goPsdLayerId[go.GetInstanceID()] = fe.layerId;
                }
            }

            int GetTreeNodeId(int iid)
            {
                return goPsdLayerId.TryGetValue(iid, out int pid) ? pid : -Mathf.Abs(iid);
            }

            // Build node list
            var result = new List<ExportDiffNode>();
            int skippedNoEntry = 0; // skippedNoEntry no longer used
            foreach (var asset in prefabAssets)
            {
                if (!(asset is GameObject go)) continue;
                
                int iid = go.GetInstanceID();
                int treeNodeId = GetTreeNodeId(iid);
                bool isPsdNode = goPsdLayerId.TryGetValue(iid, out int psdLayerId);
                if (!isPsdNode) psdLayerId = -1;

                string parentName = go.transform.parent != null ? go.transform.parent.name : "";
                int parentTreeNodeId = -1;
                int parentPsdLayerId = -1;

                if (go.transform.parent != null)
                {
                    parentTreeNodeId = GetTreeNodeId(go.transform.parent.gameObject.GetInstanceID());

                    // Traverse ancestors to find the nearest PSD layer ancestor as logical parent
                    Transform ancestor = go.transform.parent;
                    while (ancestor != null)
                    {
                        if (goPsdLayerId.TryGetValue(ancestor.gameObject.GetInstanceID(), out parentPsdLayerId))
                            break;
                        parentPsdLayerId = -1;
                        ancestor = ancestor.parent;
                    }
                }

                string componentsJson = BuildGameObjectComponentsJson(go);

                result.Add(new ExportDiffNode
                {
                    treeNodeId = treeNodeId,
                    psdLayerId = psdLayerId,
                    isPsdNode = isPsdNode,
                    name = go.name,
                    parentTreeNodeId = parentTreeNodeId,
                    parentPsdLayerId = parentPsdLayerId,
                    parentName = parentName,
                    componentsJson = componentsJson,
                    siblingIndex = go.transform.GetSiblingIndex(),
                    isPrefab = PrefabUtility.IsPartOfPrefabInstance(go),
                });
            }
            Debug.Log($"[PSD BuildDataset] Result nodes={result.Count}, skippedNoEntry={skippedNoEntry}");
            return result;
        }

        /// <summary>Checks if there are any differences between Dataset A and B.</summary>
        private static bool CheckHasDifferences(List<ExportDiffNode> dataA, List<ExportDiffNode> dataB, bool compareNameDiff)
        {
            var aByLayerId = new Dictionary<int, ExportDiffNode>();
            foreach (var a in dataA)
                if (a.isPsdNode) aByLayerId[a.psdLayerId] = a;

            var bByLayerId = new Dictionary<int, ExportDiffNode>();
            foreach (var b in dataB)
                if (b.isPsdNode) bByLayerId[b.psdLayerId] = b;

            // Check nodes only in A or only in B
            foreach (var kv in aByLayerId)
                if (!bByLayerId.ContainsKey(kv.Key)) return true;
            foreach (var kv in bByLayerId)
                if (!aByLayerId.ContainsKey(kv.Key)) return true;

            // Check nodes present in both: parent and component differences
            foreach (var kv in aByLayerId)
            {
                if (!bByLayerId.TryGetValue(kv.Key, out var b)) continue;
                var a = kv.Value;
                if (a.parentPsdLayerId != b.parentPsdLayerId) return true;
                if (compareNameDiff && a.name != b.name) return true;
                if (a.isPrefab != b.isPrefab) return true;
                if (ComponentsJsonChanged(a.componentsJson, b.componentsJson)) return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true if two componentsJson strings represent any difference in component properties.
        /// New format: {"CompName":{...},...}. Falls back to string equality for unknown formats.
        /// </summary>
        private static bool ComponentsJsonChanged(string jsonA, string jsonB)
        {
            if (jsonA == jsonB) return false;
            if (string.IsNullOrEmpty(jsonA) || string.IsNullOrEmpty(jsonB)) return true;

            Dictionary<string, string> mapA = ParseFlatComponentsJson(jsonA);
            Dictionary<string, string> mapB = ParseFlatComponentsJson(jsonB);

            if (mapA == null && mapB == null) return jsonA != jsonB;
            if (mapA == null || mapB == null) return true;

            if (mapA.Count != mapB.Count) return true;
            foreach (var kv in mapA)
            {
                if (!mapB.TryGetValue(kv.Key, out string bVal)) return true;
                if (kv.Value != bVal) return true;
            }
            return false;
        }

        /// <summary>Parse a flat componentsJson {"CompName":{...},...} into a key→valueJson dictionary.</summary>
        private static Dictionary<string, string> ParseFlatComponentsJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            json = json.Trim();
            if (!json.StartsWith("{") || !json.EndsWith("}")) return null;

            var result = new Dictionary<string, string>(System.StringComparer.Ordinal);
            int i = 1, len = json.Length;
            while (i < len - 1)
            {
                while (i < len && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r' || json[i] == ',')) i++;
                if (i >= len - 1) break;
                if (json[i] != '"') return null;
                int keyStart = i + 1;
                i++;
                while (i < len && (json[i] != '"' || json[i - 1] == '\\')) i++;
                if (i >= len) return null;
                string key = json.Substring(keyStart, i - keyStart);
                i++;
                while (i < len && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r')) i++;
                if (i >= len || json[i] != ':') return null;
                i++;
                while (i < len && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r')) i++;
                if (i >= len || json[i] != '{') return null;
                int valueStart = i;
                int depth = 0;
                bool inStr = false;
                while (i < len)
                {
                    char c = json[i];
                    if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; }
                    else { if (c == '"') inStr = true; else if (c == '{') depth++; else if (c == '}') { if (--depth == 0) { i++; break; } } }
                    i++;
                }
                result[key] = json.Substring(valueStart, i - valueStart);
            }
            return result.Count > 0 ? result : null;
        }

        /// <summary>Captures necessary static state snapshot for export (static variables may be cleared when callback executes in delayCall).</summary>
        private sealed class ExportStateSnapshot
        {
            public Dictionary<Layer, string> layerImagePaths;
            public Dictionary<Layer, Vector4> layerSliceBorders;
            public HashSet<Layer> clippedLayers;
            public Dictionary<int, Layer> layersById;
            public Dictionary<int, bool> mergeExportConfig;
            public HashSet<int> exportPrefabLayerIds;
            public Dictionary<int, string> exportPrefabAssetPathByLayerId;
            public Dictionary<int, string> externalPrefabByLayerId;
            public Dictionary<int, bool> externalPrefabReusePosition;
            public Dictionary<int, bool> externalPrefabReuseSize;
            public Dictionary<int, bool> useCustomImageByLayerId;
            public Dictionary<int, string> customImagePathByLayerId;
            public Dictionary<int, bool> exportedByLayerId;
            public Dictionary<int, LayerConfigEntry> layerConfigByLayerId;
            public Dictionary<int, bool> useTextMeshProByLayerId;
            public Dictionary<Layer, bool> useTextMeshProByLayerRef;
            public string prefabExportAssetsRootRelative;
            public string outputFolder;
            public string sessionPsdPath;
            public bool sessionAutoImageNaming;
            public int canvasWidth;
            public int canvasHeight;
            public PsdImage psdImage;
        }

        private static ExportStateSnapshot CaptureExportState()
        {
            return new ExportStateSnapshot
            {
                layerImagePaths = _layerImagePaths != null ? new Dictionary<Layer, string>(_layerImagePaths) : null,
                layerSliceBorders = _layerSliceBorders != null ? new Dictionary<Layer, Vector4>(_layerSliceBorders) : null,
                clippedLayers = _clippedLayers != null ? new HashSet<Layer>(_clippedLayers) : null,
                layersById = _layersById != null ? new Dictionary<int, Layer>(_layersById) : null,
                mergeExportConfig = _mergeExportConfig != null ? new Dictionary<int, bool>(_mergeExportConfig) : null,
                exportPrefabLayerIds = _exportPrefabLayerIds != null ? new HashSet<int>(_exportPrefabLayerIds) : null,
                exportPrefabAssetPathByLayerId = _exportPrefabAssetPathByLayerId != null ? new Dictionary<int, string>(_exportPrefabAssetPathByLayerId) : null,
                externalPrefabByLayerId = _externalPrefabByLayerId != null ? new Dictionary<int, string>(_externalPrefabByLayerId) : null,
                externalPrefabReusePosition = _externalPrefabReusePosition != null ? new Dictionary<int, bool>(_externalPrefabReusePosition) : null,
                externalPrefabReuseSize = _externalPrefabReuseSize != null ? new Dictionary<int, bool>(_externalPrefabReuseSize) : null,
                useCustomImageByLayerId = _useCustomImageByLayerId != null ? new Dictionary<int, bool>(_useCustomImageByLayerId) : null,
                customImagePathByLayerId = _customImagePathByLayerId != null ? new Dictionary<int, string>(_customImagePathByLayerId) : null,
                exportedByLayerId = _exportedByLayerId != null ? new Dictionary<int, bool>(_exportedByLayerId) : null,
                layerConfigByLayerId = _layerConfigByLayerId != null ? new Dictionary<int, LayerConfigEntry>(_layerConfigByLayerId) : null,
                useTextMeshProByLayerId = _useTextMeshProByLayerId != null ? new Dictionary<int, bool>(_useTextMeshProByLayerId) : null,
                useTextMeshProByLayerRef = _useTextMeshProByLayerRef != null ? new Dictionary<Layer, bool>(_useTextMeshProByLayerRef) : null,
                prefabExportAssetsRootRelative = _prefabExportAssetsRootRelative,
                outputFolder = _outputFolder,
                sessionPsdPath = _sessionPsdPath,
                sessionAutoImageNaming = _sessionAutoImageNaming,
                canvasWidth = _canvasWidth,
                canvasHeight = _canvasHeight,
                psdImage = _psdImage,
            };
        }

        private static void RestoreExportState(ExportStateSnapshot snapshot)
        {
            _layerImagePaths = snapshot.layerImagePaths;
            _layerSliceBorders = snapshot.layerSliceBorders;
            _clippedLayers = snapshot.clippedLayers;
            _layersById = snapshot.layersById;
            _mergeExportConfig = snapshot.mergeExportConfig;
            _exportPrefabLayerIds = snapshot.exportPrefabLayerIds;
            _exportPrefabAssetPathByLayerId = snapshot.exportPrefabAssetPathByLayerId;
            _externalPrefabByLayerId = snapshot.externalPrefabByLayerId;
            _externalPrefabReusePosition = snapshot.externalPrefabReusePosition;
            _externalPrefabReuseSize = snapshot.externalPrefabReuseSize;
            _useCustomImageByLayerId = snapshot.useCustomImageByLayerId;
            _customImagePathByLayerId = snapshot.customImagePathByLayerId;
            _exportedByLayerId = snapshot.exportedByLayerId;
            _layerConfigByLayerId = snapshot.layerConfigByLayerId;
            _useTextMeshProByLayerId = snapshot.useTextMeshProByLayerId;
            _useTextMeshProByLayerRef = snapshot.useTextMeshProByLayerRef;
            _prefabExportAssetsRootRelative = snapshot.prefabExportAssetsRootRelative;
            _outputFolder = snapshot.outputFolder;
            _sessionPsdPath = snapshot.sessionPsdPath;
            _sessionAutoImageNaming = snapshot.sessionAutoImageNaming;
            _canvasWidth = snapshot.canvasWidth;
            _canvasHeight = snapshot.canvasHeight;
            _psdImage = snapshot.psdImage;
        }

        /// <summary>Cleanup temporary Prefab and its PrefabMap files.</summary>
        private static void CleanupTempPrefab(string tempPrefabPath, string tempMapPath)
        {
            if (!string.IsNullOrEmpty(tempPrefabPath) && AssetDatabase.LoadAssetAtPath<Object>(tempPrefabPath) != null)
            {
                AssetDatabase.DeleteAsset(tempPrefabPath);
            }
            if (!string.IsNullOrEmpty(tempMapPath) && File.Exists(tempMapPath))
            {
                File.Delete(tempMapPath);
            }
            // Cleanup TempPrefab folder (if empty)
            string tempFolder = Path.Combine(Application.dataPath, "PsdToUnityUI", "TempPrefab");
            if (Directory.Exists(tempFolder) && Directory.GetFiles(tempFolder).Length == 0
                && Directory.GetDirectories(tempFolder).Length == 0)
            {
                AssetDatabase.DeleteAsset("Assets/PsdToUnityUI/TempPrefab");
            }
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// Performs incremental updates to existing Prefab based on ExportDiffDecisions.
        /// </summary>
        private static void ApplyExportDiffPatch(PsdImage psd, string psdName,
            string prefabPath, ExportDiffDecisions decisions)
        {
            if (!decisions.HasAnyAction)
            {
                Debug.Log("[PSD Export] No changes to apply, existing Prefab remains unchanged.");
                return;
            }

            // Ensure font mapping & TMP material cache are initialized (may have been released when callback triggers)
            if (_unrecognizedPsdFontNamesThisExport == null || _pendingFontMappingEntries == null)
                InitFontMappingForExport();
#if USE_TMP
            if (_tmpFaceMaterialVariantCache == null)
                _tmpFaceMaterialVariantCache = new Dictionary<string, Material>();
#endif

            string mapFilePath = Path.Combine(Application.dataPath, "PsdToUnityUI", "PrefabMap",
                psdName + "_PrefabMap.json");
            if (!File.Exists(mapFilePath))
            {
                Debug.LogWarning("[PSD Export] PrefabMap missing, skipping incremental update.");
                return;
            }

            PrefabMapData mapData;
            try
            {
                mapData = JsonUtility.FromJson<PrefabMapData>(
                    File.ReadAllText(mapFilePath, System.Text.Encoding.UTF8));
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[PSD Export] PrefabMap load failed: {ex.Message}");
                return;
            }
            if (mapData?.entries == null)
            {
                Debug.LogWarning("[PSD Export] PrefabMap empty, skipping incremental update.");
                return;
            }

            var layerIdToPath = new Dictionary<int, string>(mapData.entries.Length);
            var layerIdToFileId = new Dictionary<int, long>(mapData.entries.Length);
            foreach (var e in mapData.entries)
            {
                layerIdToPath[e.layerId] = e.path;
                layerIdToFileId[e.layerId] = e.fileId;
            }

            // User may have modified the Prefab hierarchy, paths in PrefabMap may be outdated.
            // Before entering staging area, use AssetDatabase (persisted Prefab) + GlobalObjectId
            // to build fileId -> current path mapping for fallback when nodes are moved.
            var fileIdToCurrentPath = new Dictionary<long, string>();
            var persistedAssets = AssetDatabase.LoadAllAssetsAtPath(prefabPath);
            if (persistedAssets != null)
            {
                foreach (var asset in persistedAssets)
                {
                    if (!(asset is GameObject pgo)) continue;
                    long fid = (long)GlobalObjectId.GetGlobalObjectIdSlow(pgo).targetObjectId;
                    if (fid != 0)
                        fileIdToCurrentPath[fid] = GetTransformPathWithSiblingIndices(pgo.transform);
                }
            }

            var toDeleteSet = new HashSet<int>(decisions.nodesToDelete);
            var toReparentSet = new HashSet<int>(decisions.nodesToApplyStructure);
            var toApplyCompSet = new HashSet<int>(decisions.nodesToApplyBComponents);
            var toAddSet = new HashSet<int>(decisions.nodesToAdd);

            Debug.Log($"[PSD Patch] decisions: toDelete=[{string.Join(",", toDeleteSet)}] toAdd=[{string.Join(",", toAddSet)}] toReparent=[{string.Join(",", toReparentSet)}] toApplyComp=[{string.Join(",", toApplyCompSet)}]");

            _goInstanceIdToLayerId = new Dictionary<int, int>();

            // If the Prefab is currently open in Prefab Stage, operating on Stage prefabContentsRoot
            // often leads to various ghost bugs (especially missing fileIDs for new nodes) due to
            // Unity's save and refresh mechanisms.
            // Solution: use LoadPrefabContents (offline mode), close Stage before operation and reopen after.
            bool wasInStage = ClosePrefabStageIfOpenForAsset(prefabPath);

            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var layerIdToGO = BuildLayerIdToGOFromPrefabWithFallback(
                    prefabRoot, layerIdToPath, layerIdToFileId, fileIdToCurrentPath);

                foreach (var kv in layerIdToGO)
                    _goInstanceIdToLayerId[kv.Value.GetInstanceID()] = kv.Key;

                // Step 1: Reparent
                // Nodes whose new parent is not yet in layerIdToGO (will be added in Step 4) are collected here and applied after Step 4.
                var deferredReparentSet = new HashSet<int>();
                foreach (int lid in toReparentSet)
                {
                    if (!layerIdToGO.TryGetValue(lid, out var go)) continue;
                    if (_layersById == null || !_layersById.TryGetValue(lid, out var layer)) continue;

                    int newParentLayerId = (layer.Parent != null && layer.Parent.LayerId.HasValue)
                        ? layer.Parent.LayerId.Value : -1;
                    GameObject newParentGO = null;
                    if (newParentLayerId != -1)
                        layerIdToGO.TryGetValue(newParentLayerId, out newParentGO);

                    // New parent not yet created (will be added in Step 4 as a new group): defer reparent
                    if (newParentGO == null && newParentLayerId != -1)
                    {
                        deferredReparentSet.Add(lid);
                        Debug.Log($"[PSD Export Patch] Reparent deferred for [{lid}] -> Parent [{newParentLayerId}] (parent not yet in prefab, will retry after Step 4)");
                        continue;
                    }

                    if (newParentGO == null)
                        newParentGO = prefabRoot;

                    // worldPositionStays=true: maintain world coordinates when reparenting
                    go.transform.SetParent(newParentGO.transform, true);
                    if (decisions.nodesToApplyStructureSiblingIndex != null
                        && decisions.nodesToApplyStructureSiblingIndex.TryGetValue(lid, out int reparentSibIdx)
                        && reparentSibIdx >= 0)
                    {
                        go.transform.SetSiblingIndex(Mathf.Clamp(reparentSibIdx, 0, newParentGO.transform.childCount - 1));
                    }
                    // Only adjust parent relationship, do not recalculate RectTransform position, preserving user modifications
                    Debug.Log($"[PSD Export Patch] Reparented: [{lid}] -> Parent [{newParentLayerId}]");
                }

                // Step 2: Apply B Components
                foreach (int lid in toApplyCompSet)
                {
                    if (!layerIdToGO.TryGetValue(lid, out var go)) continue;
                    if (_layersById == null || !_layersById.TryGetValue(lid, out var layer)) continue;

                    // --- [Deep Patch] Handle Prefab type conversion (Regular GO <-> Prefab Instance) ---
                    bool targetIsPrefab = (_exportPrefabLayerIds != null && layer.LayerId.HasValue && _exportPrefabLayerIds.Contains(layer.LayerId.Value)) 
                                         || !string.IsNullOrEmpty(GetExternalPrefabPath(layer));
                    bool currentIsInstance = PrefabUtility.IsPartOfPrefabInstance(go);

                    // Check if "species conversion" is needed
                    if (targetIsPrefab != currentIsInstance)
                    {
                        Transform parent = go.transform.parent;
                        int siblingIndex = go.transform.GetSiblingIndex();
                        
                        // Prepare outer coordinate context (used for position reconstruction)
                        float pCX = 0, pCY = 0;
                        if (layer.Parent != null && layer.Parent.LayerId.HasValue)
                        {
                            var pb = CalculateLayerBounds(layer.Parent);
                            if (pb.HasValue) { pCX = pb.Value.left + pb.Value.width / 2f; pCY = pb.Value.top + pb.Value.height / 2f; }
                        }

                        // Destroy old node (must destroy immediately to release name placeholder)
                        GameObject.DestroyImmediate(go);
                        
                        // Recreate this node (with new identity: Prefab instance or regular object)
                        GameObject newGo;
                        if (targetIsPrefab)
                        {
                            // Re-instantiate Prefab (CreateLayerGameObject handles instantiation of Export Prefab and External Prefab internaly)
                            CreateLayerGameObject(layer, parent, pCX, pCY);
                            // Find the object just created (CreateLayerGameObject attaches it to parent after instantiation)
                            newGo = parent.GetChild(parent.childCount - 1).gameObject;
                        }
                        else
                        {
                            // Downgrade to regular GameObject
                            newGo = new GameObject(GetSafeHierarchyName(layer.Name), typeof(RectTransform));
                            newGo.transform.SetParent(parent, false);
                            if (layer.LayerId.HasValue && _goInstanceIdToLayerId != null)
                                _goInstanceIdToLayerId[newGo.GetInstanceID()] = layer.LayerId.Value;
                        }

                        // Restore hierarchy position
                        newGo.transform.SetSiblingIndex(siblingIndex);
                        
                        // Update mapping info to ensure subsequent steps use the new object
                        go = newGo;
                        layerIdToGO[lid] = newGo;
                        Debug.Log($"[PSD Patch] Node [{lid}] {layer.Name} type conversion executed (IsPrefab: {targetIsPrefab})");
                    }

                    ClearUiComponentsForPatch(go);
                    SetupLayerComponents(layer, go);
                    ApplyConfiguredUiComponents(layer, go);

                    string targetName = GetSafeHierarchyName(layer.Name);
                    string extPath = GetExternalPrefabPath(layer);
                    if (!string.IsNullOrEmpty(extPath))
                    {
                        GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(extPath);
                        if (prefabAsset != null) targetName = prefabAsset.name;
                    }

                    if (decisions.compareNameDiff && !string.Equals(go.name, targetName))
                    {
                        go.name = string.IsNullOrEmpty(targetName) ? go.name : targetName;
                    }

                    bool activeRestoredFromBJson = false;
                    if (decisions.bComponentsJsonByLayerId != null
                        && decisions.bComponentsJsonByLayerId.TryGetValue(lid, out string bGoStateJson)
                        && !string.IsNullOrEmpty(bGoStateJson))
                    {
                        activeRestoredFromBJson = TryRestoreGameObjectStateFromComponentsJson(go, bGoStateJson);
                    }
                    if (!activeRestoredFromBJson)
                        go.SetActive(layer.Visible);

                    // Prioritize restoring RectTransform directly from B-side componentsJson (reliable, avoids PSD layer data recalculation)
                    bool rtRestoredFromBJson = false;
                    if (decisions.bComponentsJsonByLayerId != null
                        && decisions.bComponentsJsonByLayerId.TryGetValue(lid, out string bCompJson)
                        && !string.IsNullOrEmpty(bCompJson))
                    {
                        rtRestoredFromBJson = TryRestoreRectTransformFromComponentsJson(go, bCompJson);
                    }

                    // Fallback: Recalculate position from PSD layer data (when B JSON is unavailable or parsing fails)
                    if (!rtRestoredFromBJson)
                    {
                        int parentLayerId = (layer.Parent != null && layer.Parent.LayerId.HasValue)
                            ? layer.Parent.LayerId.Value : -1;
                        UpdateRectTransformFromPsdLayer(go, layer, parentLayerId);
                    }

                    ReapplyAncestorUiHandlersThatTouchDescendants(go);
                    Debug.Log($"[PSD Export Patch] Updated components: [{lid}] rtFromBJson={rtRestoredFromBJson}");
                }

                // Step 3: Add new nodes
                if (toAddSet.Count > 0)
                {
                    var existingLayerIds = new HashSet<int>(layerIdToGO.Keys);
                    Debug.Log($"[PSD Patch Step4] existingLayerIds count={existingLayerIds.Count} | toAddSet=[{string.Join(",", toAddSet)}]");
                    AddNewPsdNodesToPrefabFiltered(psd, prefabRoot, layerIdToGO, existingLayerIds, toAddSet, decisions.nodesToAddSiblingIndex);

                    // Validation: Count prefabRoot direct child names
                    var directChildNames = new System.Collections.Generic.List<string>();
                    for (int _i = 0; _i < prefabRoot.transform.childCount; _i++)
                        directChildNames.Add(prefabRoot.transform.GetChild(_i).name);
                    Debug.Log($"[PSD Patch Step4] prefabRoot direct children after add=[{string.Join(",", directChildNames)}]");
                }

                // Step 4: Deferred reparents — nodes whose new parent was not yet in layerIdToGO during Step 1 (e.g. parent is a newly added group)
                foreach (int lid in deferredReparentSet)
                {
                    if (!layerIdToGO.TryGetValue(lid, out var go)) continue;
                    if (_layersById == null || !_layersById.TryGetValue(lid, out var layer)) continue;

                    int newParentLayerId = (layer.Parent != null && layer.Parent.LayerId.HasValue)
                        ? layer.Parent.LayerId.Value : -1;
                    GameObject newParentGO = null;
                    if (newParentLayerId != -1)
                        layerIdToGO.TryGetValue(newParentLayerId, out newParentGO);
                    if (newParentGO == null)
                    {
                        Debug.LogWarning($"[PSD Export Patch] Deferred reparent skipped: parent [{newParentLayerId}] still not found after Step 4 for [{lid}]");
                        continue;
                    }
                    go.transform.SetParent(newParentGO.transform, true);
                    if (decisions.nodesToApplyStructureSiblingIndex != null
                        && decisions.nodesToApplyStructureSiblingIndex.TryGetValue(lid, out int reparentSibIdx)
                        && reparentSibIdx >= 0)
                    {
                        go.transform.SetSiblingIndex(Mathf.Clamp(reparentSibIdx, 0, newParentGO.transform.childCount - 1));
                    }
                    Debug.Log($"[PSD Export Patch] Deferred reparent applied: [{lid}] -> Parent [{newParentLayerId}]");
                }

                // Step 5: Delete roots after surviving descendants have been reparanted or recreated elsewhere.
                foreach (int lid in toDeleteSet)
                {
                    if (!layerIdToGO.TryGetValue(lid, out var go))
                    {
                        string path = layerIdToPath.ContainsKey(lid) ? layerIdToPath[lid] : "unknown";
                        Debug.LogError($"[PSD Export Patch] ‼️ [CRITICAL] Cannot delete node [{lid}] because it was not found in the Prefab (recorded path='{path}'). Incremental sync failed, object will remain in Unity. Check if the path contains illegal characters or was manually renamed.");
                        continue;
                    }
                    _goInstanceIdToLayerId.Remove(go.GetInstanceID());
                    layerIdToGO.Remove(lid);
                    Object.DestroyImmediate(go);
                    Debug.Log($"[PSD Export Patch] Deleted: [{lid}]");
                }

                PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
                Debug.Log($"[PSD Export Patch] Prefab incremental update complete: {prefabPath}");

                // Extract mapping before Refresh, because Refresh() might reload Prefab Stage and invalidate existing InstanceIDs (turning them to null)
                var pathToLayerIdCache = BuildPathToLayerIdMap();

                // Refresh ensures newly added GameObjects are assigned fileIds so SavePrefabMap can read them
                AssetDatabase.Refresh();

                SavePrefabMap(prefabPath, psdName, pathToLayerIdCache);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }

            AssetDatabase.Refresh();

            if (wasInStage)
                ReopenPrefabInStage(prefabPath);
        }

        /// <summary>Only add PSD nodes specified in toAddSet to the Prefab.</summary>
        private static void AddNewPsdNodesToPrefabFiltered(PsdImage psd, GameObject prefabRoot,
            Dictionary<int, GameObject> layerIdToGO, HashSet<int> existingLayerIds, HashSet<int> toAddSet,
            Dictionary<int, int> siblingIndexByLayerId = null)
        {
            Debug.Log($"[PSD AddFiltered] toAddSet={string.Join(",", toAddSet)} | existingLayerIds count={existingLayerIds.Count} | psd.Children count={psd.Children.Count()}");
            var toAdd = new List<(Layer layer, GameObject parent, float parentCX, float parentCY, int siblingIndex)>();
            foreach (var child in psd.Children)
                CollectNodesToAddFiltered(child, prefabRoot, layerIdToGO, existingLayerIds, toAddSet,
                    psd.Width / 2f, psd.Height / 2f, toAdd, siblingIndexByLayerId);

            Debug.Log($"[PSD AddFiltered] toAdd.Count={toAdd.Count}");
            // During creation: prevent duplicating nodes that already exist in the Prefab when recursively building new group subtrees
            _existingLayerIdsSkipForAdd = existingLayerIds;
            try
            {
                // Create one by one and insert according to siblingIndex in B side
                foreach (var (layer, parentGO, pcx, pcy, sibIdx) in toAdd)
                {
                    Debug.Log($"[PSD AddFiltered] Creating node: [{layer.LayerId}] {layer.Name} parent={parentGO?.name} siblingIndex={sibIdx}");
                    int countBefore = parentGO.transform.childCount;
                    CreateLayerGameObject(layer, parentGO.transform, pcx, pcy);
                    if (parentGO.transform.childCount > countBefore)
                    {
                        var newChild = parentGO.transform.GetChild(parentGO.transform.childCount - 1);
                        if (sibIdx >= 0)
                            newChild.SetSiblingIndex(Mathf.Clamp(sibIdx, 0, parentGO.transform.childCount - 1));
                        // Register all newly created nodes (including subtree) so deferred reparents can resolve their new parents
                        RegisterNewSubtreeInLayerIdToGO(newChild, layerIdToGO);
                    }
                }
            }
            finally
            {
                _existingLayerIdsSkipForAdd = null;
            }
        }

        /// <summary>
        /// Recursively walk a newly created subtree and register each node in layerIdToGO (keyed by PSD layer ID).
        /// Only registers nodes not already present, so existing original nodes are never overwritten.
        /// Used after AddNewPsdNodesToPrefabFiltered to allow deferred reparents to resolve their new parent GOs.
        /// </summary>
        private static void RegisterNewSubtreeInLayerIdToGO(Transform root, Dictionary<int, GameObject> layerIdToGO)
        {
            if (_goInstanceIdToLayerId != null
                && _goInstanceIdToLayerId.TryGetValue(root.gameObject.GetInstanceID(), out int lid)
                && !layerIdToGO.ContainsKey(lid))
                layerIdToGO[lid] = root.gameObject;
            for (int i = 0; i < root.childCount; i++)
                RegisterNewSubtreeInLayerIdToGO(root.GetChild(i), layerIdToGO);
        }

        /// <summary>Recursively collect nodes to be added (only those in toAddSet).</summary>
        private static void CollectNodesToAddFiltered(Layer layer, GameObject prefabRoot,
            Dictionary<int, GameObject> layerIdToGO, HashSet<int> existingLayerIds,
            HashSet<int> toAddSet, float parentCX, float parentCY,
            List<(Layer, GameObject, float, float, int)> toAdd,
            Dictionary<int, int> siblingIndexByLayerId = null)
        {
            if (!layer.LayerId.HasValue) return;
            int layerId = layer.LayerId.Value;

            float cx, cy;
            if (layer.IsGroup)
            {
                var b = CalculateLayerBounds(layer);
                cx = b.HasValue ? b.Value.left + b.Value.width / 2f : parentCX;
                cy = b.HasValue ? b.Value.top + b.Value.height / 2f : parentCY;
            }
            else
            {
                cx = layer.Left + layer.Width / 2f;
                cy = layer.Top + layer.Height / 2f;
            }

            if (!existingLayerIds.Contains(layerId) && toAddSet.Contains(layerId))
            {
                // Find parent GO
                int parentLayerId = (layer.Parent != null && layer.Parent.LayerId.HasValue)
                    ? layer.Parent.LayerId.Value : -1;
                GameObject parentGO = null;
                if (parentLayerId != -1)
                    layerIdToGO.TryGetValue(parentLayerId, out parentGO);
                if (parentLayerId != -1 && parentGO == null)
                {
                    Debug.LogWarning($"[PSD CollectFiltered] Skip toAdd node: [{layerId}] {layer.Name} because parent [{parentLayerId}] is not present in the selected add set.");
                    return;
                }
                if (parentGO == null)
                    parentGO = prefabRoot;

                int sibIdx = -1;
                siblingIndexByLayerId?.TryGetValue(layerId, out sibIdx);
                Debug.Log($"[PSD CollectFiltered] Hit toAddSet: [{layerId}] {layer.Name} parentLayerId={parentLayerId} parentGO={parentGO?.name} sibIdx={sibIdx}");
                toAdd.Add((layer, parentGO, parentCX, parentCY, sibIdx));
            }
            else if (layer.IsGroup)
            {
                // Existing group node (or group not in toAddSet): recursively check children
                Debug.Log($"[PSD CollectFiltered] Recursion Group: [{layerId}] {layer.Name} inExisting={existingLayerIds.Contains(layerId)}");
                foreach (var child in layer.Children)
                    CollectNodesToAddFiltered(child, prefabRoot, layerIdToGO, existingLayerIds,
                        toAddSet, cx, cy, toAdd, siblingIndexByLayerId);
            }
            else
            {
                Debug.Log($"[PSD CollectFiltered] Skip leaf node: [{layerId}] {layer.Name} inExisting={existingLayerIds.Contains(layerId)} inToAdd={toAddSet.Contains(layerId)}");
            }
        }

        /// <summary>Locate each GO inside Prefab using the layerId -> path table from PrefabMap.</summary>
        private static Dictionary<int, GameObject> BuildLayerIdToGOFromPrefab(
            GameObject prefabRoot, Dictionary<int, string> layerIdToPath)
        {
            var result = new Dictionary<int, GameObject>(layerIdToPath.Count);
            foreach (var kv in layerIdToPath)
            {
                var go = FindGOByPrefabPath(prefabRoot, kv.Value);
                if (go != null)
                    result[kv.Key] = go;
            }
            return result;
        }

        /// Same as <see cref="BuildLayerIdToGOFromPrefab"/>, but when GO is not found by PrefabMap path (user manually moved direct child),
        /// use fileId -> currentPath mapping (pre-built from AssetDatabase side) to fallback search in staging area.
        /// </summary>
        private static Dictionary<int, GameObject> BuildLayerIdToGOFromPrefabWithFallback(
            GameObject prefabRoot,
            Dictionary<int, string> layerIdToPath,
            Dictionary<int, long> layerIdToFileId,
            Dictionary<long, string> fileIdToCurrentPath)
        {
            var result = new Dictionary<int, GameObject>(layerIdToPath.Count);
            foreach (var kv in layerIdToPath)
            {
                // Prioritize path recorded in PrefabMap (valid if user didn't move it)
                var go = FindGOByPrefabPath(prefabRoot, kv.Value);

                // Path invalid (user modified hierarchy) -> Fallback search using current path corresponding to fileId
                if (go == null
                    && layerIdToFileId.TryGetValue(kv.Key, out long fileId)
                    && fileIdToCurrentPath.TryGetValue(fileId, out string currentPath)
                    && currentPath != kv.Value)
                {
                    go = FindGOByPrefabPath(prefabRoot, currentPath);
                    if (go != null)
                        Debug.Log($"[PSD Patch] layerId={kv.Key} Path fallback successful: '{kv.Value}' -> '{currentPath}'");
                }

                if (go != null)
                    result[kv.Key] = go;
                else
                    Debug.LogWarning($"[PSD Patch] layerId={kv.Key} cannot find corresponding GO in Prefab (Path='{kv.Value}')");
            }
            return result;
        }

        /// <summary>Find GO by full path (including root name) in prefabRoot hierarchy.
        /// Supports "::N" sibling-index disambiguation suffix appended by <see cref="BuildPathToLayerIdMap"/>
        /// for same-named siblings (e.g. "Root/icon::1"). The suffix is stripped before navigation,
        /// and the sibling index is used to pick the correct child at the leaf level.</summary>
        private static GameObject FindGOByPrefabPath(GameObject root, string fullPath)
        {
            if (root == null || string.IsNullOrEmpty(fullPath)) return null;

            // Path format: "RootName/Seg::N/Seg::N/..." where every non-root segment
            // carries a "::siblingIndex" suffix for disambiguation.
            // Backward-compatible: segments without "::N" match by name only.
            string[] segments = fullPath.Split('/');

            // First segment is the root (no ::N suffix).
            if (segments[0] != root.name) return null;

            Transform current = root.transform;
            for (int s = 1; s < segments.Length; s++)
            {
                string seg = segments[s];
                string childName;
                int childSibIdx = -1;
                int sep = seg.LastIndexOf("::");
                if (sep >= 0 && int.TryParse(seg.Substring(sep + 2), out int parsedSib))
                {
                    childName = seg.Substring(0, sep);
                    childSibIdx = parsedSib;
                }
                else
                {
                    childName = seg;
                }

                Transform next = null;
                for (int i = 0; i < current.childCount; i++)
                {
                    var child = current.GetChild(i);
                    if (child.name == childName &&
                        (childSibIdx < 0 || child.GetSiblingIndex() == childSibIdx))
                    {
                        next = child;
                        break;
                    }
                }
                if (next == null) return null;
                current = next;
            }
            return current.gameObject;
        }

        /// <summary>Remove UI components from GO (keep RectTransform / Canvas / CanvasScaler / GraphicRaycaster).</summary>
        private static void ClearUiComponentsForPatch(GameObject go)
        {
            var keep = new HashSet<System.Type>
            {
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(UnityEngine.UI.GraphicRaycaster),
            };
            var comps = go.GetComponents<Component>();
            var toRemove = new List<Component>(comps.Length);
            foreach (var c in comps)
                if (c != null && !keep.Contains(c.GetType()))
                    toRemove.Add(c);

            // Reverse removal: components added last are removed first, reducing dependency conflict probability
            for (int i = toRemove.Count - 1; i >= 0; i--)
            {
                if (toRemove[i] != null)
                    try { Object.DestroyImmediate(toRemove[i]); }
                    catch (System.Exception ex)
                    { Debug.LogWarning($"[PSD Patch] Failed to remove component {toRemove[i]?.GetType().Name}: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Some UI handlers add components to descendant nodes instead of the selected PSD node itself,
        /// for example ScrollRect adds Image + Mask to its viewport and Slider/ScrollBar/Toggle/
        /// Dropdown/InputField rebind child references from the parent component. Reapply those
        /// ancestor handlers so
        /// "Apply Right Components" persists the same descendant-side effects and child references
        /// as a fresh export.
        /// </summary>
        private static void ReapplyAncestorUiHandlersThatTouchDescendants(GameObject node)
        {
            if (node == null || _goInstanceIdToLayerId == null || _layerConfigByLayerId == null || _layersById == null)
                return;

            RectTransform nodeRt = node.transform as RectTransform;
            if (nodeRt == null)
                return;

            Transform current = node.transform.parent;
            while (current != null)
            {
                if (_goInstanceIdToLayerId.TryGetValue(current.gameObject.GetInstanceID(), out int layerId)
                    && _layerConfigByLayerId.TryGetValue(layerId, out LayerConfigEntry entry)
                    && _layersById.TryGetValue(layerId, out Layer layer))
                {
                    bool shouldReapply = false;
                    if (entry.uiComponentType == "ScrollRect")
                    {
                        shouldReapply = IsMatchingNamedRectTransform(current, entry.scrollRectViewportChildName, nodeRt)
                            || IsMatchingNamedRectTransform(current, entry.scrollRectContentChildName, nodeRt)
                            || IsMatchingNamedRectTransform(current, entry.scrollRectHorizontalScrollbarChildName, nodeRt)
                            || IsMatchingNamedRectTransform(current, entry.scrollRectVerticalScrollbarChildName, nodeRt);
                    }
                    else if (entry.uiComponentType == "ScrollBar")
                    {
                        shouldReapply = IsMatchingNamedRectTransform(current, entry.scrollBarHandleChildName, nodeRt);
                    }
                    else if (entry.uiComponentType == "Slider")
                    {
                        shouldReapply = IsMatchingNamedRectTransform(current, entry.sliderFillRectChildName, nodeRt)
                            || IsMatchingNamedRectTransform(current, entry.sliderHandleRectChildName, nodeRt);
                    }
                    else if (entry.uiComponentType == "Toggle")
                    {
                        shouldReapply = IsMatchingNamedRectTransform(current, entry.toggleGraphicChildName, nodeRt);
                    }
                    else if (entry.uiComponentType == "Dropdown(Legacy)"
                        || entry.uiComponentType == "Dropdown(TMP)")
                    {
                        shouldReapply = IsMatchingNamedRectTransform(current, entry.dropdownTemplateChildName, nodeRt)
                            || IsMatchingNamedRectTransform(current, entry.dropdownCaptionTextChildName, nodeRt)
                            || IsMatchingNamedRectTransform(current, entry.dropdownItemTextChildName, nodeRt);
                    }
                    else if (entry.uiComponentType == "InputField(Legacy)")
                    {
                        shouldReapply = IsMatchingNamedRectTransform(current, entry.inputFieldTextChildName, nodeRt)
                            || IsMatchingNamedRectTransform(current, entry.inputFieldPlaceholderChildName, nodeRt);
                    }
                    else if (entry.uiComponentType == "InputField(TMP)")
                    {
                        shouldReapply = IsMatchingNamedRectTransform(current, entry.inputFieldTextChildName, nodeRt)
                            || IsMatchingNamedRectTransform(current, entry.inputFieldPlaceholderChildName, nodeRt)
                            || IsMatchingNamedRectTransform(current, entry.inputFieldTextViewportChildName, nodeRt);
                    }

                    if (shouldReapply)
                    {
                        var handler = UiComponentHandlerRegistry.Get(entry.uiComponentType);
                        handler?.Apply(layer, current.gameObject, entry);
                    }
                }

                current = current.parent;
            }
        }

        private static bool IsMatchingNamedRectTransform(Transform root, string childName, RectTransform expected)
        {
            if (root == null || expected == null || string.IsNullOrEmpty(childName))
                return false;

            RectTransform found = PsdUiRectTransformUtil.FindDescendantRectTransformByName(root, childName);
            return found == expected;
        }

        /// <summary>
        /// Restore anchoredPosition and sizeDelta of RectTransform directly from componentsJson (B-side snapshot).
        /// More reliable than recalculating from PSD layer, does not depend on whether static cache is fully restored.
        /// Returns true for success; false for parsing failure or RectTransform data not found.
        /// </summary>
        private static bool TryRestoreRectTransformFromComponentsJson(GameObject go, string componentsJson)
        {
            if (go == null || string.IsNullOrEmpty(componentsJson))
                return false;
            var rt = go.GetComponent<RectTransform>();
            if (rt == null)
                return false;

            try
            {
                // New format: {"RectTransform":{...}, ...}
                string rtJson = ExtractJsonValueForKey(componentsJson, "RectTransform");
                if (!string.IsNullOrEmpty(rtJson))
                {
                    var snap = JsonUtility.FromJson<RectTransformSnapshot>(rtJson);
                    if (snap != null)
                    {
                        snap.ApplyTo(rt);
                        return true;
                    }
                }

                // Legacy fallback: {"components":[{"type":"UnityEngine.RectTransform","json":"..."},...]}
                // Try to extract via the old bundle format
                PrefabNodeComponentsJsonBundle bundle;
                try { bundle = JsonUtility.FromJson<PrefabNodeComponentsJsonBundle>(componentsJson); }
                catch { return false; }
                if (bundle?.components == null) return false;

                foreach (var entry in bundle.components)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.type) || string.IsNullOrEmpty(entry.json)) continue;
                    if (entry.type != typeof(RectTransform).FullName) continue;
                    try
                    {
                        EditorJsonUtility.FromJsonOverwrite(entry.json, rt);
                        return true;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[PSD Patch] TryRestoreRectTransformFromComponentsJson legacy fallback failed: {ex.Message}");
                        return false;
                    }
                }
                return false;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[PSD Patch] TryRestoreRectTransformFromComponentsJson failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Restore GameObject semantic state directly from componentsJson (currently activeSelf only).
        /// Returns true when a GameObject snapshot was found and applied.
        /// </summary>
        private static bool TryRestoreGameObjectStateFromComponentsJson(GameObject go, string componentsJson)
        {
            if (go == null || string.IsNullOrEmpty(componentsJson))
                return false;

            try
            {
                string goJson = ExtractJsonValueForKey(componentsJson, "GameObject");
                if (string.IsNullOrEmpty(goJson))
                    return false;

                var snap = JsonUtility.FromJson<GameObjectSnapshot>(goJson);
                if (snap == null)
                    return false;

                snap.ApplyTo(go);
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[PSD Patch] TryRestoreGameObjectStateFromComponentsJson failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Extracts the JSON object value for a given top-level key from a JSON object string.
        /// Returns null if the key is not found or the value is not a JSON object.
        /// </summary>
        private static string ExtractJsonValueForKey(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string searchFor = "\"" + key + "\":";
            int idx = json.IndexOf(searchFor, System.StringComparison.Ordinal);
            if (idx < 0) return null;
            int valueStart = idx + searchFor.Length;
            // Skip whitespace
            while (valueStart < json.Length &&
                   (json[valueStart] == ' ' || json[valueStart] == '\t' ||
                    json[valueStart] == '\n' || json[valueStart] == '\r'))
                valueStart++;
            if (valueStart >= json.Length || json[valueStart] != '{') return null;

            int depth = 0, end = valueStart;
            bool inString = false;
            while (end < json.Length)
            {
                char c = json[end];
                if (inString)
                {
                    if (c == '\\') end++; // skip escaped character
                    else if (c == '"') inString = false;
                }
                else
                {
                    if (c == '"') inString = true;
                    else if (c == '{') depth++;
                    else if (c == '}') { if (--depth == 0) { end++; break; } }
                }
                end++;
            }
            return json.Substring(valueStart, end - valueStart);
        }

        /// <summary>Update RectTransform (size + anchor position) of GO according to PSD layer data.</summary>
        private static void UpdateRectTransformFromPsdLayer(GameObject go, Layer layer, int parentLayerId)
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return;

            // Calculate PSD space center and size of current layer
            float w, h, layerCX, layerCY;
            if (layer.IsGroup)
            {
                var b = CalculateLayerBounds(layer);
                if (!b.HasValue) return;
                w = b.Value.width; h = b.Value.height;
                layerCX = b.Value.left + w / 2f;
                layerCY = b.Value.top + h / 2f;
            }
            else
            {
                w = layer.Width; h = layer.Height;
                layerCX = layer.Left + w / 2f;
                layerCY = layer.Top + h / 2f;
            }

            rt.sizeDelta = new Vector2(w, h);

            // Calculate PSD space center of parent layer
            float parentCX, parentCY;
            if (parentLayerId == -1)
            {
                parentCX = _canvasWidth / 2f;
                parentCY = _canvasHeight / 2f;
            }
            else if (_layersById.TryGetValue(parentLayerId, out var parentLayer))
            {
                if (parentLayer.IsGroup)
                {
                    var pb = CalculateLayerBounds(parentLayer);
                    if (!pb.HasValue) return;
                    parentCX = pb.Value.left + pb.Value.width / 2f;
                    parentCY = pb.Value.top + pb.Value.height / 2f;
                }
                else
                {
                    parentCX = parentLayer.Left + parentLayer.Width / 2f;
                    parentCY = parentLayer.Top + parentLayer.Height / 2f;
                }
            }
            else return;

            rt.anchoredPosition = new Vector2(layerCX - parentCX, parentCY - layerCY);
        }

        /// <summary>Add nodes present in PSD but missing in Prefab to the Prefab.</summary>
        private static void AddNewPsdNodesToPrefab(PsdImage psd, GameObject prefabRoot,
            Dictionary<int, GameObject> layerIdToGO, HashSet<int> existingLayerIds)
        {
            var toAdd = new List<(Layer layer, GameObject parentGO, float parentCX, float parentCY)>();
            float rootCX = psd.Width / 2f;
            float rootCY = psd.Height / 2f;
            foreach (var topLayer in psd.Children)
                CollectNodesToAdd(topLayer, prefabRoot, layerIdToGO, existingLayerIds, rootCX, rootCY, toAdd);

            foreach (var (layer, parentGO, pCX, pCY) in toAdd)
            {
                CreateLayerGameObject(layer, parentGO.transform, pCX, pCY);
                Debug.Log($"[PSD Patch] Added node: [{layer.LayerId}] {layer.Name}");
            }
        }

        /// <summary>Recursively collect PSD nodes to be added (existing nodes recursively check their children).</summary>
        private static void CollectNodesToAdd(Layer layer, GameObject prefabRoot,
            Dictionary<int, GameObject> layerIdToGO, HashSet<int> existingLayerIds,
            float parentCX, float parentCY,
            List<(Layer, GameObject, float, float)> toAdd)
        {
            if (!layer.LayerId.HasValue) return;
            int layerId = layer.LayerId.Value;

            // Calculate current layer PSD space center (used as parentCX/CY for children recursion)
            float layerCX, layerCY;
            if (layer.IsGroup)
            {
                var b = CalculateLayerBounds(layer);
                layerCX = b.HasValue ? b.Value.left + b.Value.width / 2f : parentCX;
                layerCY = b.HasValue ? b.Value.top + b.Value.height / 2f : parentCY;
            }
            else
            {
                layerCX = layer.Left + layer.Width / 2f;
                layerCY = layer.Top + layer.Height / 2f;
            }

            if (!existingLayerIds.Contains(layerId))
            {
                // Find parent GO
                int parentLayerId = (layer.Parent != null && layer.Parent.LayerId.HasValue)
                    ? layer.Parent.LayerId.Value : -1;
                layerIdToGO.TryGetValue(parentLayerId, out var parentGO);
                if (parentGO == null) parentGO = prefabRoot;

                // If group contains existing child nodes, skip group itself (avoid duplicates), only recursively check children
                if (layer.IsGroup && LayerSubtreeHasExistingNode(layer, existingLayerIds))
                {
                    Debug.LogWarning($"[PSD Patch] Group [{layerId}] {layer.Name} contains existing child nodes, skipping overall creation; please perform full re-export to handle this group.");
                    foreach (var child in layer.Children)
                        CollectNodesToAdd(child, prefabRoot, layerIdToGO, existingLayerIds,
                            layerCX, layerCY, toAdd);
                }
                else
                {
                    // Leaf node or brand new group: Safe creation (CreateLayerGameObject handles children recursively)
                    toAdd.Add((layer, parentGO, parentCX, parentCY));
                }
            }
            else
            {
                // Already exists: recursively check sub-nodes
                foreach (var child in layer.Children)
                    CollectNodesToAdd(child, prefabRoot, layerIdToGO, existingLayerIds,
                        layerCX, layerCY, toAdd);
            }
        }

        /// <summary>Check if any node in the layer subtree already exists in the Prefab.</summary>
        private static bool LayerSubtreeHasExistingNode(Layer layer, HashSet<int> existingLayerIds)
        {
            foreach (var child in layer.Children)
            {
                if (child.LayerId.HasValue && existingLayerIds.Contains(child.LayerId.Value))
                    return true;
                if (child.IsGroup && LayerSubtreeHasExistingNode(child, existingLayerIds))
                    return true;
            }
            return false;
        }

        /// <summary>Pre-collect path-to-LayerID mapping from current Scene / Stage objects.</summary>
        private static Dictionary<string, int> BuildPathToLayerIdMap()
        {
            var pathToLayerId = new Dictionary<string, int>();
            if (_goInstanceIdToLayerId == null || _goInstanceIdToLayerId.Count == 0)
                return pathToLayerId;

            int nullSceneObj = 0;
            foreach (var kv in _goInstanceIdToLayerId)
            {
                var sceneObj = EditorUtility.InstanceIDToObject(kv.Key) as GameObject;
                if (sceneObj == null) { nullSceneObj++; continue; }
                // Use per-segment sibling indices to disambiguate same-named siblings at every level.
                string path = GetTransformPathWithSiblingIndices(sceneObj.transform);
                pathToLayerId[path] = kv.Value;
            }
            Debug.Log($"[PSD SavePrefabMap] BuildPathToLayerIdMap: entries={pathToLayerId.Count}, nullSceneObj={nullSceneObj}");
            return pathToLayerId;
        }

        /// <summary>Build and save a JSON file mapping Prefab fileIDs to PSD layer IDs.</summary>
        private static void SavePrefabMap(string prefabAssetPath, string psdName, Dictionary<string, int> prebuiltPathToLayerId = null)
        {
            Debug.Log($"[PSD SavePrefabMap] Start prefabAssetPath={prefabAssetPath} psdName={psdName} | _goInstanceIdToLayerId count={_goInstanceIdToLayerId?.Count}");
            
            var pathToLayerId = prebuiltPathToLayerId ?? BuildPathToLayerIdMap();
            if (pathToLayerId.Count == 0)
            {
                Debug.LogWarning("[PSD SavePrefabMap] Phase1 pathToLayerId is empty, returning directly (no file written)");
                return;
            }

            // Phase 2: iterate saved Prefab assets, resolve fileIDs, match path → layerId
            var allAssets = AssetDatabase.LoadAllAssetsAtPath(prefabAssetPath);
            int prefabGoCount = allAssets == null ? 0 : allAssets.Count(a => a is GameObject);
            Debug.Log($"[PSD SavePrefabMap] Phase2 LoadAllAssetsAtPath({prefabAssetPath}): total assets={allAssets?.Length ?? 0}, GO count={prefabGoCount}");
            if (allAssets == null || allAssets.Length == 0)
            {
                Debug.LogWarning("[PSD SavePrefabMap] allAssets is empty, returning directly (no file written)");
                return;
            }

            var entries = new List<PrefabMapEntry>();
            int skippedPathMiss = 0, skippedFileId0 = 0;
            foreach (var asset in allAssets)
            {
                if (!(asset is GameObject go)) continue;

                // Must match the key format used in BuildPathToLayerIdMap (per-segment sibling indices).
                string goPath = GetTransformPathWithSiblingIndices(go.transform);
                if (!pathToLayerId.TryGetValue(goPath, out int layerId)) { skippedPathMiss++; continue; }

                var gid = GlobalObjectId.GetGlobalObjectIdSlow(go);
                long fileId = (long)gid.targetObjectId;
                if (fileId == 0) { skippedFileId0++; Debug.LogWarning($"[PSD SavePrefabMap] GO '{go.name}' fileId=0, skipping"); continue; }

                string layerName = "";
                if (_layersById != null && _layersById.TryGetValue(layerId, out Layer layer))
                    layerName = layer.Name ?? "";

                entries.Add(new PrefabMapEntry
                {
                    fileId = fileId,
                    layerId = layerId,
                    layerName = layerName,
                    path = goPath,
                    componentsJson = BuildGameObjectComponentsJson(go)
                });
            }
            Debug.Log($"[PSD SavePrefabMap] Phase2 entries={entries.Count}, skippedPathMiss={skippedPathMiss}, skippedFileId0={skippedFileId0}");

            if (entries.Count == 0)
            {
                Debug.LogWarning("[PSD SavePrefabMap] entries is empty, returning directly (no file written)");
                return;
            }

            var mapData = new PrefabMapData
            {
                prefabPath = prefabAssetPath,
                entries = entries.OrderBy(e => e.path).ToArray()
            };

            // Save to Assets/PsdToUnityUI/PrefabMap/{psdName}_PrefabMap.json
            string mapFolder = Path.Combine(Application.dataPath, "PsdToUnityUI", "PrefabMap");
            if (!Directory.Exists(mapFolder))
                Directory.CreateDirectory(mapFolder);
            string mapFilePath = Path.Combine(mapFolder, $"{psdName}_PrefabMap.json");
            File.WriteAllText(mapFilePath, JsonUtility.ToJson(mapData, true), System.Text.Encoding.UTF8);
            AssetDatabase.Refresh();
            Debug.Log($"PrefabMap saved: Assets/PsdToUnityUI/PrefabMap/{psdName}_PrefabMap.json  ({entries.Count} entries)");
        }

        /// <summary>
        /// Serialize this GameObject's own components into one JSON string payload.
        /// Format: {"ComponentShortName":{visibleProps}, ...}
        /// RectTransform / Image / Text / TextMeshProUGUI use hand-crafted snapshots
        /// (only editor-visible, stable properties — no internal hashcodes or instanceIDs).
        /// All other components fall back to EditorJsonUtility.
        /// </summary>
        internal static string BuildGameObjectComponentsJson(GameObject go)
        {
            if (go == null)
                return "{}";

            var sb = new System.Text.StringBuilder("{");
            bool first = true;

            void Append(string key, string valueJson)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(key).Append("\":").Append(valueJson);
            }

            // ── RectTransform (always present on UI nodes) ──
            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
                Append("RectTransform", JsonUtility.ToJson(RectTransformSnapshot.From(rt), false));

            // ── Image ──
            var img = go.GetComponent<Image>();
            if (img != null)
                Append("Image", JsonUtility.ToJson(ImageSnapshot.From(img), false));

            // ── Text (legacy UnityEngine.UI.Text) ──
            // InputField drives its textComponent.text to "" at runtime;
            // normalize to match serialized Prefab state.
            var txt = go.GetComponent<Text>();
            if (txt != null)
            {
                var textSnap = TextSnapshot.From(txt);
                if (IsTextDrivenByInputField(go, txt))
                    textSnap.text = "";
                if (IsPlaceholderDrivenByInputField(go, txt))
                    textSnap.enabled = true;
                Append("Text", JsonUtility.ToJson(textSnap, false));
            }

#if USE_TMP
            // ── TextMeshProUGUI ──
            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
            {
                var tmpSnap = TmpTextSnapshot.From(tmp);
                if (IsTextDrivenByTmpInputField(go, tmp))
                    tmpSnap.text = "";
                if (IsPlaceholderDrivenByTmpInputField(go, tmp))
                    tmpSnap.enabled = true;
                Append("TextMeshProUGUI", JsonUtility.ToJson(tmpSnap, false));
            }
#endif

            // ── GameObject state (not represented by a Component, but part of exported node semantics) ──
            Append("GameObject", JsonUtility.ToJson(GameObjectSnapshot.From(go), false));

            // ── All other components: only capture enabled state ──
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                if (comp is RectTransform) continue;
                if (comp is Image) continue;
                if (comp is Text) continue;
#if USE_TMP
                if (comp is TextMeshProUGUI) continue;
#endif
                bool enabled = comp is Behaviour beh ? beh.enabled : true;
                Append(comp.GetType().Name, JsonUtility.ToJson(new ComponentEnabledSnapshot { enabled = enabled }, false));
            }

            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Returns true if <paramref name="txt"/> is the textComponent of an InputField
        /// on this GO or a parent GO. InputField drives textComponent.text to "" at runtime.
        /// </summary>
        private static bool IsTextDrivenByInputField(GameObject go, Text txt)
        {
            // Check this GO and ancestors for an InputField whose textComponent is this Text
            Transform current = go.transform;
            while (current != null)
            {
                var inp = current.GetComponent<InputField>();
                if (inp != null && inp.textComponent == txt)
                    return true;
                current = current.parent;
            }
            return false;
        }

        /// <summary>
        /// Returns true if <paramref name="txt"/> is the placeholder of an InputField
        /// on this GO or a parent GO. InputField toggles placeholder.enabled at runtime,
        /// so the diff should ignore that driven state.
        /// </summary>
        private static bool IsPlaceholderDrivenByInputField(GameObject go, Text txt)
        {
            Transform current = go.transform;
            while (current != null)
            {
                var inp = current.GetComponent<InputField>();
                if (inp != null && inp.placeholder == txt)
                    return true;
                current = current.parent;
            }
            return false;
        }

#if USE_TMP
        /// <summary>
        /// Returns true if <paramref name="txt"/> is the textComponent of a TMP_InputField
        /// on this GO or a parent GO. TMP_InputField drives textComponent.text to "" at runtime.
        /// </summary>
        private static bool IsTextDrivenByTmpInputField(GameObject go, TMP_Text txt)
        {
            Transform current = go.transform;
            while (current != null)
            {
                var inp = current.GetComponent<TMP_InputField>();
                if (inp != null && inp.textComponent == txt)
                    return true;
                current = current.parent;
            }
            return false;
        }

        /// <summary>
        /// Returns true if <paramref name="txt"/> is the placeholder of a TMP_InputField
        /// on this GO or a parent GO. TMP_InputField toggles placeholder.enabled at runtime,
        /// so the diff should ignore that driven state.
        /// </summary>
        private static bool IsPlaceholderDrivenByTmpInputField(GameObject go, TMP_Text txt)
        {
            Transform current = go.transform;
            while (current != null)
            {
                var inp = current.GetComponent<TMP_InputField>();
                if (inp != null && inp.placeholder == txt)
                    return true;
                current = current.parent;
            }
            return false;
        }
#endif

        /// <summary>Returns the "/" separated path of a Transform relative to the root (root itself returns its name).</summary>
        private static string GetTransformPath(Transform t)
        {
            if (t == null) return "";
            var parts = new List<string>();
            while (t != null)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", parts);
        }

        /// <summary>Returns the "/" separated path where every non-root segment is disambiguated
        /// with "::" + siblingIndex (e.g. "Root/Parent::1/Leaf::0").
        /// This ensures same-named siblings at any level produce unique keys.</summary>
        private static string GetTransformPathWithSiblingIndices(Transform t)
        {
            if (t == null) return "";
            var parts = new List<string>();
            while (t != null)
            {
                // Root has no parent, so no sibling index needed.
                string segment = t.parent != null
                    ? t.name + "::" + t.GetSiblingIndex()
                    : t.name;
                parts.Insert(0, segment);
                t = t.parent;
            }
            return string.Join("/", parts);
        }

        /// <summary>Depth in PSD tree (first level under root = 1); deeper nodes export before parents.</summary>
        private static int GetLayerDepth(Layer layer)
        {
            int d = 0;
            for (Layer p = layer?.Parent; p != null; p = p.Parent)
                d++;
            return d;
        }

        /// <summary>Find layer by LayerId in tree.</summary>
        private static Layer FindLayerById(Layer root, int layerId)
        {
            if (root.LayerId.HasValue && root.LayerId.Value == layerId)
                return root;
            foreach (var child in root.Children)
            {
                var found = FindLayerById(child, layerId);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Export standalone Prefab per node with "export Prefab" (root is that node, no extra parent).</summary>
        private static void ExportSingleNodePrefabs(PsdImage psd, string mainPrefabAssetPath)
        {
            string dir = Path.GetDirectoryName(mainPrefabAssetPath)?.Replace('\\', '/') ?? "Assets";
            if (string.IsNullOrEmpty(dir))
                dir = "Assets";

            var orderedIds = _exportPrefabLayerIds
                .OrderByDescending(id =>
                {
                    var L = FindLayerById(psd.Root, id);
                    return L != null ? GetLayerDepth(L) : -1;
                })
                .ToList();

            foreach (int layerId in orderedIds)
            {
                var layer = FindLayerById(psd.Root, layerId);
                if (layer == null || !GetExported(layer)) continue;

                float w, h, left, top;
                if (layer.IsGroup)
                {
                    var b = CalculateLayerBounds(layer);
                    if (!b.HasValue) continue;
                    left = b.Value.left;
                    top = b.Value.top;
                    w = b.Value.width;
                    h = b.Value.height;
                }
                else
                {
                    left = layer.Left;
                    top = layer.Top;
                    w = layer.Width;
                    h = layer.Height;
                }
                if (w <= 0 || h <= 0) continue;

                float centerX = left + w / 2f;
                float centerY = top + h / 2f;

                string safeName = CleanFileName(layer.Name);
                if (string.IsNullOrEmpty(safeName))
                    safeName = "Node_" + layerId;

                string prefabPath = $"{dir}/{safeName}.prefab";
                // Always replace existing prefab in-place (preserves GUID so existing references remain valid).
                EnsureAssetFolderExistsForPath(prefabPath);

                GameObject tempParent = new GameObject("_PsdExportTempRoot", typeof(RectTransform));
                GameObject rootGo = null;
                try
                {
                    CreateLayerGameObject(layer, tempParent.transform, centerX, centerY, standaloneExportPrefabRoot: true);
                    if (tempParent.transform.childCount == 0)
                    {
                        Debug.LogWarning($"[Node Prefab] No object generated (may be skipped): {layer.Name} (id={layerId})");
                        continue;
                    }

                    rootGo = tempParent.transform.GetChild(0).gameObject;
                    string targetRootName = string.IsNullOrEmpty(layer.Name) ? safeName : GetSafeHierarchyName(layer.Name);
                    string extPath = GetExternalPrefabPath(layer);
                    if (!string.IsNullOrEmpty(extPath))
                    {
                        GameObject externalAsset = AssetDatabase.LoadAssetAtPath<GameObject>(extPath);
                        if (externalAsset != null) targetRootName = externalAsset.name;
                    }
                    rootGo.name = targetRootName;
                    rootGo.transform.SetParent(null, false);

                    PrefabUtility.SaveAsPrefabAsset(rootGo, prefabPath);
                    _exportPrefabAssetPathByLayerId[layerId] = prefabPath;
                    Debug.Log($"[Node Prefab] Saved: {prefabPath}");
                }
                finally
                {
                    if (rootGo != null)
                        Object.DestroyImmediate(rootGo);
                    if (tempParent != null)
                        Object.DestroyImmediate(tempParent);
                }
            }
        }

        /// <summary>
        /// Recursively create GameObject for layer.
        /// parentCenterX/Y is parent center in PSD space;
        /// child anchoredPosition is relative to that center.
        /// </summary>
        /// <param name="standaloneExportPrefabRoot">If true, layer root uses anchoredPosition (0,0) for single-node Prefab export without an extra wrapper.</param>
        private static void CreateLayerGameObject(Layer layer, Transform parent, float parentCenterX, float parentCenterY, bool standaloneExportPrefabRoot = false)
        {
            if (!GetExported(layer))
                return;

            // Merged by clipping mask: no separate node
            if (_clippedLayers.Contains(layer))
                return;

            // During incremental patch: skip recreating nodes that already exist in the Prefab
            if (_existingLayerIdsSkipForAdd != null && layer.LayerId.HasValue
                && _existingLayerIdsSkipForAdd.Contains(layer.LayerId.Value))
                return;

            // Already exported as standalone Prefab: main (or parent) references asset, subtree not expanded; building that node Prefab alone still uses full build
            if (!standaloneExportPrefabRoot
                && layer.LayerId.HasValue
                && _exportPrefabAssetPathByLayerId != null
                && _exportPrefabAssetPathByLayerId.TryGetValue(layer.LayerId.Value, out string nodePrefabPath))
            {
                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(nodePrefabPath);
                if (prefabAsset != null)
                {
                    GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
                    string targetName = layer.Name;
                    string extPath = GetExternalPrefabPath(layer);
                    if (!string.IsNullOrEmpty(extPath))
                    {
                        GameObject externalAsset = AssetDatabase.LoadAssetAtPath<GameObject>(extPath);
                        if (externalAsset != null) targetName = externalAsset.name;
                    }
                    else
                    {
                        // Sub-prefab asset name usually matches PSD layer name, but if layer name is empty, default to asset name
                        if (string.IsNullOrEmpty(targetName)) targetName = prefabAsset.name;
                    }
                    instance.name = targetName;
                    instance.transform.SetParent(parent, false);
                    if (!layer.Visible)
                        instance.SetActive(false);
                    RectTransform rt = instance.GetComponent<RectTransform>();
                    if (rt == null)
                        rt = instance.AddComponent<RectTransform>();

                    float w = 0, h = 0, centerX = 0, centerY = 0;
                    bool hasBounds = false;
                    if (layer.IsGroup)
                    {
                        var b = CalculateLayerBounds(layer);
                        if (b.HasValue)
                        {
                            w = b.Value.width;
                            h = b.Value.height;
                            centerX = b.Value.left + w / 2f;
                            centerY = b.Value.top + h / 2f;
                            hasBounds = true;
                        }
                    }
                    else
                    {
                        w = layer.Width;
                        h = layer.Height;
                        centerX = layer.Left + w / 2f;
                        centerY = layer.Top + h / 2f;
                        hasBounds = true;
                    }

                    if (hasBounds && w > 0 && h > 0)
                        rt.sizeDelta = new Vector2(w, h);
                    if (hasBounds)
                        rt.anchoredPosition = new Vector2(
                            centerX - parentCenterX,
                            parentCenterY - centerY);

                    if (layer.LayerId.HasValue && _goInstanceIdToLayerId != null)
                        _goInstanceIdToLayerId[instance.GetInstanceID()] = layer.LayerId.Value;

                    Debug.Log($"[Ref node Prefab] {layer.Name} -> {nodePrefabPath}");
                    return;
                }
                Debug.LogWarning($"[Ref node Prefab] Cannot load: {nodePrefabPath}; falling back to inline build");
            }

            // External Prefab: replace node with chosen Prefab; optionally reuse PSD position/size from config
            string externalPath = GetExternalPrefabPath(layer);
            if (!string.IsNullOrEmpty(externalPath))
            {
                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(externalPath);
                if (prefabAsset != null)
                {
                    GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
                    instance.name = prefabAsset.name;
                    instance.transform.SetParent(parent, false);
                    if (!layer.Visible)
                        instance.SetActive(false);
                    RectTransform prefabRt = instance.GetComponent<RectTransform>();
                    if (prefabRt == null)
                        prefabRt = instance.AddComponent<RectTransform>();

                    bool reusePos = false;
                    bool reuseSz = false;
                    if (layer.LayerId.HasValue)
                    {
                        if (_externalPrefabReusePosition != null && _externalPrefabReusePosition.TryGetValue(layer.LayerId.Value, out bool rp))
                            reusePos = rp;
                        if (_externalPrefabReuseSize != null && _externalPrefabReuseSize.TryGetValue(layer.LayerId.Value, out bool rs))
                            reuseSz = rs;
                    }

                    float w = 0, h = 0, centerX = 0, centerY = 0;
                    bool hasBounds = false;
                    if (layer.IsGroup)
                    {
                        var b = CalculateLayerBounds(layer);
                        if (b.HasValue)
                        {
                            w = b.Value.width;
                            h = b.Value.height;
                            centerX = b.Value.left + w / 2f;
                            centerY = b.Value.top + h / 2f;
                            hasBounds = true;
                        }
                    }
                    else
                    {
                        w = layer.Width;
                        h = layer.Height;
                        centerX = layer.Left + w / 2f;
                        centerY = layer.Top + h / 2f;
                        hasBounds = true;
                    }

                    if (reuseSz && w > 0 && h > 0)
                        prefabRt.sizeDelta = new Vector2(w, h);
                    if (reusePos && hasBounds && !standaloneExportPrefabRoot)
                        prefabRt.anchoredPosition = new Vector2(centerX - parentCenterX, parentCenterY - centerY);

                    if (layer.LayerId.HasValue && _goInstanceIdToLayerId != null)
                        _goInstanceIdToLayerId[instance.GetInstanceID()] = layer.LayerId.Value;

                    Debug.Log($"[Ref Prefab] {layer.Name} -> {externalPath}");
                }
                else
                    Debug.LogWarning($"[Ref Prefab] Cannot load: {externalPath}");
                return;
            }

            // Custom image: use custom Sprite; recurse children if merge-export is off
            if (GetUseCustomImage(layer))
            {
                float w = 0, h = 0, centerX = 0, centerY = 0;
                bool hasBounds = false;
                if (layer.IsGroup)
                {
                    var b = CalculateLayerBounds(layer);
                    if (b.HasValue)
                    {
                        w = b.Value.width;
                        h = b.Value.height;
                        centerX = b.Value.left + w / 2f;
                        centerY = b.Value.top + h / 2f;
                        hasBounds = true;
                    }
                }
                else
                {
                    w = layer.Width;
                    h = layer.Height;
                    centerX = layer.Left + w / 2f;
                    centerY = layer.Top + h / 2f;
                    hasBounds = true;
                }

                GameObject go = new GameObject(GetSafeHierarchyName(layer.Name), typeof(RectTransform));
                go.transform.SetParent(parent, false);
                if (layer.LayerId.HasValue && _goInstanceIdToLayerId != null)
                    _goInstanceIdToLayerId[go.GetInstanceID()] = layer.LayerId.Value;
                if (!layer.Visible)
                    go.SetActive(false);
                RectTransform rtCustom = go.GetComponent<RectTransform>();
                if (w > 0 && h > 0)
                    rtCustom.sizeDelta = new Vector2(w, h);
                if (hasBounds && !standaloneExportPrefabRoot)
                    rtCustom.anchoredPosition = new Vector2(centerX - parentCenterX, parentCenterY - centerY);
                SetupLayerComponents(layer, go); // uses custom sprite
                ApplyConfiguredUiComponents(layer, go);
                Debug.Log($"[Custom image] {layer.Name} -> {GetCustomImagePath(layer)}");
                // Merge-export off: recurse children
                if (layer.IsGroup && !GetMergeExport(layer))
                {
                    foreach (var child in layer.Children)
                    {
                        CreateLayerGameObject(child, go.transform, centerX, centerY);
                    }
                }
                return;
            }

            GameObject goChild = new GameObject(GetSafeHierarchyName(layer.Name), typeof(RectTransform));
            goChild.transform.SetParent(parent, false);
            if (layer.LayerId.HasValue && _goInstanceIdToLayerId != null)
                _goInstanceIdToLayerId[goChild.GetInstanceID()] = layer.LayerId.Value;
            RectTransform rtChild = goChild.GetComponent<RectTransform>();

            // Prefab visibility: hide when layer not visible
            if (!layer.Visible)
                goChild.SetActive(false);

            if (layer.IsGroup && _layerImagePaths.ContainsKey(layer))
            {
                // Group exports as a single image — treat as leaf
                var bbox = ((PsdTools.Layers.Group)layer).BBox;
                int bw = bbox.Right - bbox.Left;
                int bh = bbox.Bottom - bbox.Top;
                float centerX = bbox.Left + bw / 2f;
                float centerY = bbox.Top + bh / 2f;

                rtChild.sizeDelta = new Vector2(bw, bh);
                rtChild.anchoredPosition = standaloneExportPrefabRoot
                    ? Vector2.zero
                    : new Vector2(centerX - parentCenterX, parentCenterY - centerY);
                SetupLayerComponents(layer, goChild);
                ApplyConfiguredUiComponents(layer, goChild);
            }
            else if (layer.IsGroup)
            {
                var bounds = CalculateLayerBounds(layer);
            if (bounds.HasValue)
            {
                    float groupCenterX = bounds.Value.left + bounds.Value.width / 2f;
                    float groupCenterY = bounds.Value.top + bounds.Value.height / 2f;

                rtChild.sizeDelta = new Vector2(bounds.Value.width, bounds.Value.height);
                    rtChild.anchoredPosition = standaloneExportPrefabRoot
                        ? Vector2.zero
                        : new Vector2(
                            groupCenterX - parentCenterX,
                            parentCenterY - groupCenterY
                        );

                    foreach (var child in layer.Children)
                    {
                        CreateLayerGameObject(child, goChild.transform, groupCenterX, groupCenterY);
                    }
                    ApplyConfiguredUiComponents(layer, goChild);
            }
            else
            {
                rtChild.sizeDelta = Vector2.zero;
                rtChild.anchoredPosition = Vector2.zero;
                    foreach (var child in layer.Children)
                    {
                        CreateLayerGameObject(child, goChild.transform, parentCenterX, parentCenterY);
                    }
                    ApplyConfiguredUiComponents(layer, goChild);
                }
            }
            else
            {
                float layerCenterX = layer.Left + layer.Width / 2f;
                float layerCenterY = layer.Top + layer.Height / 2f;

            rtChild.sizeDelta = new Vector2(layer.Width, layer.Height);
                rtChild.anchoredPosition = standaloneExportPrefabRoot
                    ? Vector2.zero
                    : new Vector2(
                        layerCenterX - parentCenterX,
                        parentCenterY - layerCenterY
                    );

                SetupLayerComponents(layer, goChild);
                ApplyConfiguredUiComponents(layer, goChild);
            }
        }

        private static void InitFontMappingForExport()
        {
            _unrecognizedPsdFontNamesThisExport = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            _pendingFontMappingEntries = new List<PsdFontMappingEntry>();
            _psdFontMappingLookup = new Dictionary<string, PsdFontMappingEntry>(System.StringComparer.OrdinalIgnoreCase);
            var data = PsdFontMappingConfig.Load();
            if (data.entries == null) return;
            foreach (var e in data.entries)
            {
                if (string.IsNullOrEmpty(e.psdFontName)) continue;
                string k = e.psdFontName.Trim();
                if (!_psdFontMappingLookup.ContainsKey(k))
                    _psdFontMappingLookup[k] = e;
            }
        }

        private static void FinalizeFontMappingAfterExport()
        {
            if (_pendingFontMappingEntries == null || _pendingFontMappingEntries.Count == 0)
                return;
            PsdFontMappingConfig.MergeAndSaveNewEntries(_pendingFontMappingEntries);
            AssetDatabase.Refresh();
            var sorted = _unrecognizedPsdFontNamesThisExport.OrderBy(s => s).ToArray();
            string body =
                "These PSD fonts are not in the mapping file; Unity default fonts were used and entries were merged into Assets/Editor/PSD_FontMapping.json.\n\n" +
                "You can assign project font assets under Tools > PSD > Font mapping config.\n\n" +
                string.Join("\n", sorted);
            EditorUtility.DisplayDialog("Unrecognized PSD fonts", body, "OK");
        }

        private static string GetDefaultLegacyFontAssetPathForConfig()
        {
            Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) return "";
            string p = AssetDatabase.GetAssetPath(f);
            return p ?? "";
        }

    #if USE_TMP
        private static void AbortExportForMissingTmp(System.Exception ex)
        {
            const string title = "TextMeshPro Required";
            string body =
                "TextMeshPro Essentials have not been imported, so a TMP font asset could not be resolved.\n\n" +
                "Please import TMP Essentials, then run the export again.";
            Debug.LogWarning($"[TMP] {body} Exception: {ex.Message}");
            EditorUtility.DisplayDialog(title, body, "OK");
            throw new TmpImportRequiredException(body);
        }

        private static string GetDefaultTmpFontAssetPathForConfig()
        {
            try
            {
                if (TMP_Settings.defaultFontAsset != null)
                    return AssetDatabase.GetAssetPath(TMP_Settings.defaultFontAsset) ?? "";
            }
            catch (System.Exception ex)
            {
                AbortExportForMissingTmp(ex);
            }
            var r = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            if (r != null)
                return AssetDatabase.GetAssetPath(r) ?? "";
            return "";
        }

        private static TMP_FontAsset GetDefaultTmpFontAsset()
        {
            try
            {
                if (TMP_Settings.defaultFontAsset != null)
                    return TMP_Settings.defaultFontAsset;
            }
            catch (System.Exception ex)
            {
                AbortExportForMissingTmp(ex);
            }
            return Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        }
    #else
        private static string GetDefaultTmpFontAssetPathForConfig() => "";
    #endif

        private static Font GetBuiltinLegacyFont() => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        private static void RegisterUnknownPsdFont(string psdFontName, bool needTmpDefault)
        {
            if (string.IsNullOrEmpty(psdFontName)) return;
            string k = psdFontName.Trim();
            if (string.IsNullOrEmpty(k)) return;
            if (_unrecognizedPsdFontNamesThisExport.Contains(k)) return;
            _unrecognizedPsdFontNamesThisExport.Add(k);
            _pendingFontMappingEntries.Add(new PsdFontMappingEntry
            {
                psdFontName = k,
                legacyFontAssetPath = GetDefaultLegacyFontAssetPathForConfig(),
                tmpFontAssetPath = needTmpDefault ? GetDefaultTmpFontAssetPathForConfig() : ""
            });
        }

        private static Font ResolveLegacyFontForLayer(string psdFontName)
        {
            Font builtin = GetBuiltinLegacyFont();
            if (string.IsNullOrEmpty(psdFontName?.Trim()))
                return builtin;
            string k = psdFontName.Trim();
            if (_psdFontMappingLookup == null || !_psdFontMappingLookup.TryGetValue(k, out var entry))
            {
                RegisterUnknownPsdFont(k, false);
                return builtin;
            }
            if (!string.IsNullOrEmpty(entry.legacyFontAssetPath))
            {
                var f = AssetDatabase.LoadAssetAtPath<Font>(entry.legacyFontAssetPath);
                if (f != null) return f;
                Debug.LogWarning($"[Font mapping] Cannot load Text font: {entry.legacyFontAssetPath}; using built-in font.");
            }
            return builtin;
        }

    #if USE_TMP
        private static TMP_FontAsset ResolveTmpFontForLayer(string psdFontName)
        {
            TMP_FontAsset def = GetDefaultTmpFontAsset();
            if (string.IsNullOrEmpty(psdFontName?.Trim()))
                return def;
            string k = psdFontName.Trim();
            if (_psdFontMappingLookup == null || !_psdFontMappingLookup.TryGetValue(k, out var entry))
            {
                RegisterUnknownPsdFont(k, true);
                return def;
            }
            if (!string.IsNullOrEmpty(entry.tmpFontAssetPath))
            {
                var f = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(entry.tmpFontAssetPath);
                if (f != null) return f;
                Debug.LogWarning($"[Font mapping] Cannot load TextMeshPro font: {entry.tmpFontAssetPath}; using default TMP font.");
            }
            return def;
        }
    #endif

        /// <summary>Whether type layer uses TextMeshPro from export config (default false if unset).</summary>
        private static bool GetUseTextMeshProForLayer(Layer layer)
        {
            // Check by Layer object reference first — survives PsdImage.ReleaseAllData() which clears
            // TaggedBlock data and makes layer.LayerId return null.
            if (_useTextMeshProByLayerRef != null && _useTextMeshProByLayerRef.TryGetValue(layer, out bool vRef))
                return vRef;
            if (!layer.LayerId.HasValue) return false;
            if (_useTextMeshProByLayerId != null &&
                _useTextMeshProByLayerId.TryGetValue(layer.LayerId.Value, out bool v))
                return v;
            return false;
        }

        /// <summary>
        /// Attach UI components for layer
        /// </summary>
        private static void SetupLayerComponents(Layer layer, GameObject go)
        {
            if (layer is TypeLayer textLayer)
            {
                float[] fc = textLayer.FillColor;
                Color textColor = Color.white;
                if (fc != null && fc.Length >= 4)
                {
                    textColor = new Color(
                        Mathf.Clamp01(fc[1]),
                        Mathf.Clamp01(fc[2]),
                        Mathf.Clamp01(fc[3]),
                        Mathf.Clamp01(fc[0])
                    );
                }

                bool hasStroke = TryGetStrokeEffect(layer, out Color strokeColor, out float strokeSize);
                bool hasShadow = TryGetDropShadowEffect(layer, out Color shadowColor, out Vector2 shadowOffset);
                string psdFontName = textLayer.PsdFontName;

    #if USE_TMP
                if (GetUseTextMeshProForLayer(layer))
                {
                    TMP_FontAsset tmpFont = ResolveTmpFontForLayer(psdFontName);
                    SetupTextMeshPro(go, layer, textLayer, textColor, hasStroke, strokeColor, strokeSize, hasShadow, shadowColor, shadowOffset, tmpFont);
                    Debug.Log($"Type layer: {layer.Name}, text: '{textLayer.Text}', size: {textLayer.EffectiveFontSize}, TMP=true, PSD font: '{psdFontName}'");
                }
                else
    #endif
                {
                    Font legacyFont = ResolveLegacyFontForLayer(psdFontName);
                    SetupLegacyText(go, layer, textLayer, textColor, hasStroke, strokeColor, strokeSize, hasShadow, shadowColor, shadowOffset, legacyFont);
                    Debug.Log($"Type layer: {layer.Name}, text: '{textLayer.Text}', size: {textLayer.EffectiveFontSize}, TMP=false, PSD font: '{psdFontName}'");
                }
            }
            else if (GetUseCustomImage(layer))
            {
                string customPath = GetCustomImagePath(layer);
                if (!string.IsNullOrEmpty(customPath))
                {
                    Image img = go.AddComponent<Image>();
                    Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(customPath);
                    if (sprite != null)
                    {
                        img.sprite = sprite;
                    }
                    else
                    {
                        Debug.LogWarning($"[Custom image] Cannot load Sprite: {customPath}");
                    }
                }
            }
            else if (_layerImagePaths.TryGetValue(layer, out string imagePath))
            {
                Image img = go.AddComponent<Image>();
                string relativePath = GetRelativeAssetsPath(imagePath);
                
                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(relativePath);
                if (sprite != null)
                {
                    img.sprite = sprite;
                    if (_layerSliceBorders.ContainsKey(layer))
                    {
                        img.type = Image.Type.Sliced;
                    }
                }
                else
                {
                    Debug.LogWarning($"Cannot load Sprite: {relativePath}");
                }
            }
        }

        /// <summary>Unity legacy Text + Outline/Shadow; PSD gradient overlay approximated via <see cref="TextGradient"/> corners (same sampling idea as TMP).</summary>
        private static void SetupLegacyText(GameObject go, Layer layer, TypeLayer textLayer, Color textColor,
            bool hasStroke, Color strokeColor, float strokeSize,
            bool hasShadow, Color shadowColor, Vector2 shadowOffset, Font font)
        {
            Text txt = go.AddComponent<Text>();
            string cleanText = Regex.Replace(textLayer.Text, @"[\r\n\x03\x0B\x0C\x85\u2028\u2029]+", "\n");
            txt.text = cleanText;
            txt.fontSize = Mathf.Max(1, (int)textLayer.EffectiveFontSize);
            txt.alignment = TextAnchor.MiddleCenter;
            txt.font = font != null ? font : GetBuiltinLegacyFont();
            txt.raycastTarget = false;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;

            if (TryGetTextGradientCornersFromLayer(layer, textColor, out Color32 cTL, out Color32 cTR, out Color32 cBL, out Color32 cBR))
            {
                txt.color = Color.white;
                TextGradient tg = go.AddComponent<TextGradient>();
                tg.topLeftColor = cTL;
                tg.topRightColor = cTR;
                tg.bottomLeftColor = cBL;
                tg.bottomRightColor = cBR;
            }
            else
            {
                txt.color = textColor;
            }

            if (hasStroke)
            {
                Outline outline = go.AddComponent<Outline>();
                outline.effectColor = strokeColor;
                float half = Mathf.Clamp(strokeSize * 0.5f, 0.5f, 5f);
                outline.effectDistance = new Vector2(half, -half);
            }

            if (hasShadow)
            {
                Shadow shadow = go.AddComponent<Shadow>();
                shadow.effectColor = shadowColor;
                shadow.effectDistance = shadowOffset;
            }
        }

        /// <summary>
        /// Same as raster/TMP path: gradient texture then corner colors (layer top = Top). Shared by legacy <see cref="TextGradient"/> and TMP <see cref="VertexGradient"/>.
        /// </summary>
        private static bool TryGetTextGradientCornersFromLayer(Layer layer, Color textColor,
            out Color32 topLeft, out Color32 topRight, out Color32 bottomLeft, out Color32 bottomRight)
        {
            topLeft = topRight = bottomLeft = bottomRight = default;
            if (layer.Width <= 0 || layer.Height <= 0)
                return false;
            if (!TryGetGradientOverlay(layer, out List<GradientColorStop> stops, out float angle, out float gradOpacity))
                return false;

            float combinedOpacity = Mathf.Clamp01(gradOpacity * layer.OpacityFloat);
            Texture2D tex = CreateGradientTexture(layer.Width, layer.Height, stops, angle, combinedOpacity);
            if (tex == null)
                return false;

            try
            {
                Color32[] pixels = tex.GetPixels32();
                int w = tex.width;
                int h = tex.height;
                if (pixels == null || pixels.Length != w * h)
                    return false;

                Color32 cTL = pixels[0];
                Color32 cTR = pixels[w - 1];
                Color32 cBL = pixels[(h - 1) * w];
                Color32 cBR = pixels[(h - 1) * w + w - 1];

                byte CombineAlpha(byte gradA)
                {
                    float a01 = textColor.a * (gradA / 255f);
                    return (byte)Mathf.Clamp(Mathf.RoundToInt(a01 * 255f), 0, 255);
                }

                topLeft = new Color32(cTL.r, cTL.g, cTL.b, CombineAlpha(cTL.a));
                topRight = new Color32(cTR.r, cTR.g, cTR.b, CombineAlpha(cTR.a));
                bottomLeft = new Color32(cBL.r, cBL.g, cBL.b, CombineAlpha(cBL.a));
                bottomRight = new Color32(cBR.r, cBR.g, cBR.b, CombineAlpha(cBR.a));
                return true;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

    #if USE_TMP
        /// <summary>
        /// PSD gradient corners for TMP <see cref="VertexGradient"/> (see <see cref="TryGetTextGradientCornersFromLayer"/>).
        /// </summary>
        private static bool TryGetTextMeshProGradientFromLayer(Layer layer, Color textColor, out VertexGradient vertexGradient)
        {
            vertexGradient = default;
            if (!TryGetTextGradientCornersFromLayer(layer, textColor, out Color32 cTL, out Color32 cTR, out Color32 cBL, out Color32 cBR))
                return false;
            vertexGradient = new VertexGradient(cTL, cTR, cBL, cBR);
            return true;
        }

        private const string TmpMaterialVariantFolder = "Assets/PsdToUnityUI/TMP_PsdExport_Materials";

        private static string BuildTmpMaterialVariantKey(string fontAssetPath,
            bool hasStroke, float outlineWidth01, Color strokeColor,
            bool hasShadow, float underlayOffsetX, float underlayOffsetY, Color shadowColor)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string strokePart = hasStroke
                ? string.Format(inv, "1,{0:R},{1:F6},{2:F6},{3:F6},{4:F6}", outlineWidth01, strokeColor.r, strokeColor.g, strokeColor.b, strokeColor.a)
                : "0";
            string shadowPart = hasShadow
                ? string.Format(inv, "1,{0:R},{1:R},{2:F6},{3:F6},{4:F6},{5:F6}", underlayOffsetX, underlayOffsetY, shadowColor.r, shadowColor.g, shadowColor.b, shadowColor.a)
                : "0";
            return (string.IsNullOrEmpty(fontAssetPath) ? "?" : fontAssetPath) + "|" + strokePart + "|" + shadowPart;
        }

        private static string HashMaterialKey(string key)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
                var sb = new StringBuilder(32);
                for (int i = 0; i < h.Length; i++)
                    sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Human-readable material suffix: stroke width + RGBA255, or NoStroke; underlay offset em + RGBA255, or NoShadow.
        /// For .mat filenames and Material Preset display; pass through <see cref="SanitizeForTmpMaterialFileName"/>.
        /// </summary>
        private static string BuildTmpMaterialLabelSuffix(bool hasStroke, float outlineWidth01, Color strokeColor,
            bool hasShadow, float underlayX, float underlayY, Color shadowColor)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            int Sr = Mathf.RoundToInt(Mathf.Clamp01(strokeColor.r) * 255f);
            int Sg = Mathf.RoundToInt(Mathf.Clamp01(strokeColor.g) * 255f);
            int Sb = Mathf.RoundToInt(Mathf.Clamp01(strokeColor.b) * 255f);
            int Sa = Mathf.RoundToInt(Mathf.Clamp01(strokeColor.a) * 255f);
            string strokePart = hasStroke
                ? string.Format(inv, "Stroke_w{0:F6}_{1}_{2}_{3}_{4}", outlineWidth01, Sr, Sg, Sb, Sa)
                : "NoStroke";

            int Dr = Mathf.RoundToInt(Mathf.Clamp01(shadowColor.r) * 255f);
            int Dg = Mathf.RoundToInt(Mathf.Clamp01(shadowColor.g) * 255f);
            int Db = Mathf.RoundToInt(Mathf.Clamp01(shadowColor.b) * 255f);
            int Da = Mathf.RoundToInt(Mathf.Clamp01(shadowColor.a) * 255f);
            string shadowPart = hasShadow
                ? string.Format(inv, "Shadow_x{0:F6}_y{1:F6}_{2}_{3}_{4}_{5}", underlayX, underlayY, Dr, Dg, Db, Da)
                : "NoShadow";

            return strokePart + "_" + shadowPart;
        }

        /// <summary>TMP Material Preset lists materials whose name starts with FontAsset.name and atlas matches; filename must be valid.</summary>
        private static string SanitizeForTmpMaterialFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Material";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                bool bad = false;
                foreach (char x in invalid)
                {
                    if (x == c) { bad = true; break; }
                }
                sb.Append(bad ? '_' : c);
            }
            return sb.ToString();
        }

        private static void ApplyTmpMaterialFaceSettings(Material mat,
            bool hasStroke, Color strokeColor, float outlineWidth01,
            bool hasShadow, Color shadowColor, Vector2 shadowOffsetEm)
        {
            if (hasStroke)
            {
                mat.EnableKeyword(ShaderUtilities.Keyword_Outline);
                mat.SetColor(ShaderUtilities.ID_OutlineColor, strokeColor);
                mat.SetFloat(ShaderUtilities.ID_OutlineWidth, outlineWidth01);
            }
            else
            {
                mat.DisableKeyword(ShaderUtilities.Keyword_Outline);
                mat.SetFloat(ShaderUtilities.ID_OutlineWidth, 0f);
            }

            if (hasShadow)
            {
                mat.EnableKeyword(ShaderUtilities.Keyword_Underlay);
                mat.SetColor(ShaderUtilities.ID_UnderlayColor, shadowColor);
                mat.SetFloat(ShaderUtilities.ID_UnderlayOffsetX, shadowOffsetEm.x);
                mat.SetFloat(ShaderUtilities.ID_UnderlayOffsetY, shadowOffsetEm.y);
                mat.SetFloat(ShaderUtilities.ID_UnderlaySoftness, 0f);
            }
            else
            {
                mat.DisableKeyword(ShaderUtilities.Keyword_Underlay);
            }
        }

        /// <summary>
        /// Clone FontAsset base material: reuse matching .mat under <c>Assets/PsdToUnityUI/TMP_PsdExport_Materials</c>, else create.
        /// Includes no-stroke/no-shadow key to turn off effects enabled on default font material.
        /// </summary>
        private static Material GetOrCreateTmpFontSharedMaterial(TMP_FontAsset fontAsset,
            bool hasStroke, Color strokeColor, float outlineWidth01,
            bool hasShadow, Color shadowColor, Vector2 shadowOffsetEm)
        {
            if (fontAsset == null)
                return null;

            Material baseMat = fontAsset.material;
            if (baseMat == null)
            {
                Debug.LogWarning("[TMP] FontAsset has no base material; cannot create stroke/shadow variant.");
                return null;
            }

            string fontPath = AssetDatabase.GetAssetPath(fontAsset);
            if (string.IsNullOrEmpty(fontPath))
                fontPath = fontAsset.name;

            string cacheKey = BuildTmpMaterialVariantKey(fontPath, hasStroke, outlineWidth01, strokeColor,
                hasShadow, shadowOffsetEm.x, shadowOffsetEm.y, shadowColor);

            if (_tmpFaceMaterialVariantCache != null && _tmpFaceMaterialVariantCache.TryGetValue(cacheKey, out Material mem) && mem != null)
                return mem;

            string hash = HashMaterialKey(cacheKey);
            string labelSuffix = BuildTmpMaterialLabelSuffix(hasStroke, outlineWidth01, strokeColor,
                hasShadow, shadowOffsetEm.x, shadowOffsetEm.y, shadowColor);
            string rawBaseName = $"{fontAsset.name}_PsdExport_{labelSuffix}";
            string newFileBase = SanitizeForTmpMaterialFileName(rawBaseName);
            if (newFileBase.Length > 180)
            {
                int keep = Mathf.Max(80, 180 - 9);
                newFileBase = SanitizeForTmpMaterialFileName(
                    rawBaseName.Substring(0, Mathf.Min(keep, rawBaseName.Length)) + "_" + hash.Substring(0, 8));
            }

            string presetDisplayName = newFileBase;
            string assetPath = $"{TmpMaterialVariantFolder}/{newFileBase}.mat";

        string physicalDir = Path.Combine(Application.dataPath, "PsdToUnityUI", "TMP_PsdExport_Materials");
            if (!Directory.Exists(physicalDir))
                Directory.CreateDirectory(physicalDir);

            Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (existing == null)
                existing = AssetDatabase.LoadAssetAtPath<Material>($"{TmpMaterialVariantFolder}/TMP_{hash}.mat");
            if (existing != null && existing.shader == baseMat.shader)
            {
                if (existing.name != presetDisplayName)
                {
                    existing.name = presetDisplayName;
                    EditorUtility.SetDirty(existing);
                }
                _tmpFaceMaterialVariantCache[cacheKey] = existing;
                return existing;
            }

            Material variant = new Material(baseMat);
            ApplyTmpMaterialFaceSettings(variant, hasStroke, strokeColor, outlineWidth01, hasShadow, shadowColor, shadowOffsetEm);
            variant.name = presetDisplayName;

            if (existing != null && existing.shader != baseMat.shader)
                assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);

            AssetDatabase.CreateAsset(variant, assetPath);
            AssetDatabase.SaveAssets();

            _tmpFaceMaterialVariantCache[cacheKey] = variant;
            return variant;
        }

        /// <summary>Type layer via TextMeshProUGUI; face material always a variant (even with no PSD stroke/shadow, to disable defaults on font asset). Gradient via VertexGradient corners.</summary>
        private static void SetupTextMeshPro(GameObject go, Layer layer, TypeLayer textLayer, Color textColor,
            bool hasStroke, Color strokeColor, float strokeSize,
            bool hasShadow, Color shadowColor, Vector2 shadowOffset, TMP_FontAsset fontAsset)
        {
            TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
            string cleanText = Regex.Replace(textLayer.Text, @"[\r\n\x03\x0B\x0C\x85\u2028\u2029]+", "\n");
            tmp.text = cleanText;
            //tmp.text = textLayer.Text;
            tmp.fontSize = Mathf.Max(1f, textLayer.EffectiveFontSize);
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.ForceMeshUpdate();
            if (TryGetTextMeshProGradientFromLayer(layer, textColor, out VertexGradient grad))
            {
                tmp.enableVertexGradient = true;
                tmp.colorGradient = grad;
                tmp.color = Color.white;
            }
            else
            {
                tmp.color = textColor;
            }
            if (fontAsset != null)
                tmp.font = fontAsset;

            float fontSizePx = Mathf.Max(1f, textLayer.EffectiveFontSize);
            float normWidth = 0f;
            if (hasStroke)
            {
                normWidth = Mathf.Clamp01(strokeSize / fontSizePx);
                normWidth = Mathf.Clamp01(normWidth * 2f);
            }
            Vector2 shadowEm = hasShadow
                ? new Vector2(shadowOffset.x / fontSizePx, shadowOffset.y / fontSizePx)
                : Vector2.zero;

            // No stroke/shadow still needs a clean variant or font default may keep outline/underlay on.
            if (fontAsset != null)
            {
                Material faceMat = GetOrCreateTmpFontSharedMaterial(fontAsset, hasStroke, strokeColor, normWidth,
                    hasShadow, shadowColor, shadowEm);
                if (faceMat != null)
                    tmp.fontSharedMaterial = faceMat;
            }
        }
    #endif

        /// <summary>
        /// Bounds for layer or group
        /// </summary>
        private static (float left, float top, float width, float height)? CalculateLayerBounds(Layer layer)
        {
            if (!layer.IsGroup)
            {
                if (layer.Width <= 0 || layer.Height <= 0)
                    return null;
                return (layer.Left, layer.Top, layer.Width, layer.Height);
            }

            // Group: union of visible children
            float minLeft = float.MaxValue;
            float minTop = float.MaxValue;
            float maxRight = float.MinValue;
            float maxBottom = float.MinValue;
            bool hasVisibleChild = false;

            CalculateBoundsRecursive(layer, ref minLeft, ref minTop, ref maxRight, ref maxBottom, ref hasVisibleChild);

            if (!hasVisibleChild)
                return null;

            return (minLeft, minTop, maxRight - minLeft, maxBottom - minTop);
        }

        private static void CalculateBoundsRecursive(Layer layer, ref float minLeft, ref float minTop, 
            ref float maxRight, ref float maxBottom, ref bool hasVisibleChild)
        {
            foreach (var child in layer.Children)
            {
                if (_clippedLayers.Contains(child))
                    continue;

                if (child.IsGroup)
                {
                    CalculateBoundsRecursive(child, ref minLeft, ref minTop, ref maxRight, ref maxBottom, ref hasVisibleChild);
                }
                else
                {
                    if (child.Width <= 0 || child.Height <= 0)
                        continue;

                    hasVisibleChild = true;
                    if (child.Left < minLeft) minLeft = child.Left;
                    if (child.Top < minTop) minTop = child.Top;
                    if (child.Right > maxRight) maxRight = child.Right;
                    if (child.Bottom > maxBottom) maxBottom = child.Bottom;
                }
            }
        }

        #region Layer color extraction & opacity

        /// <summary>
        /// Whether layer has solid fill, shape fill, or color overlay to read
        /// </summary>
        private static bool HasExtractableColor(Layer layer)
        {
            var blocks = layer.TaggedBlocks;
            if (blocks == null) return false;
            return blocks.Contains(Tag.SOLID_COLOR) ||
                   blocks.Contains(Tag.VECTOR_STROKE_CONTENT_DATA) ||
                   blocks.Contains(Tag.OBJECT_BASED_EFFECTS_LAYER1) ||
                   blocks.Contains(Tag.OBJECT_BASED_EFFECTS_LAYER2);
        }

        /// <summary>
        /// Solid fill from SoCo / vscg via raw "Clr " → RGB scan.
        /// </summary>
        private static bool TryGetSolidColor(Layer layer, out Color32 color)
        {
            color = default;
            var blocks = layer.TaggedBlocks;
            if (blocks == null) return false;

            // 1) SoCo: solid color fill layer
            var socoData = blocks.GetData(Tag.SOLID_COLOR);
            if (socoData != null)
            {
                int clrPos = FindPattern(socoData, "Clr ");
                if (clrPos >= 0 && TryReadRGBFromRawBytes(socoData, clrPos, out byte r, out byte g, out byte b))
                {
                    color = new Color32(r, g, b, 255);
                    return true;
                }
            }

            // 2) vscg: vector shape fill
            var vscgData = blocks.GetData(Tag.VECTOR_STROKE_CONTENT_DATA);
            if (vscgData != null)
            {
                int clrPos = FindPattern(vscgData, "Clr ");
                if (clrPos >= 0 && TryReadRGBFromRawBytes(vscgData, clrPos, out byte r, out byte g, out byte b))
                {
                    color = new Color32(r, g, b, 255);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Find ASCII pattern in byte array from startIndex.
        /// </summary>
        private static int FindPattern(byte[] data, string pattern, int startIndex = 0)
        {
            byte[] patternBytes = System.Text.Encoding.ASCII.GetBytes(pattern);
            for (int i = startIndex; i <= data.Length - patternBytes.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < patternBytes.Length; j++)
                {
                    if (data[i + j] != patternBytes[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        /// <summary>
        /// Read big-endian double (8 bytes) from byte array
        /// </summary>
        private static double ReadBigEndianDouble(byte[] data, int offset)
        {
            if (offset + 8 > data.Length) return 0;
            byte[] temp = new byte[8];
            System.Array.Copy(data, offset, temp, 0, 8);
            if (System.BitConverter.IsLittleEndian)
                System.Array.Reverse(temp);
            return System.BitConverter.ToDouble(temp, 0);
        }

        /// <summary>
        /// Read big-endian int32 (4 bytes) from byte array
        /// </summary>
        private static int ReadBigEndianInt32(byte[] data, int offset)
        {
            if (offset + 4 > data.Length) return 0;
            return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        }

        /// <summary>
        /// Parse RGB from lfx2 raw bytes via "Rd  " / "doub" + double pattern.
        /// searchStart: range start (e.g. after "Clr ").
        /// </summary>
        private static bool TryReadRGBFromRawBytes(byte[] data, int searchStart, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            // Find "Rd  " then "doub" + 8-byte double
            int rdPos = FindPattern(data, "Rd  ", searchStart);
            if (rdPos < 0) return false;
            int rdDoub = FindPattern(data, "doub", rdPos + 4);
            if (rdDoub < 0 || rdDoub > rdPos + 20) return false;
            double rdVal = ReadBigEndianDouble(data, rdDoub + 4);

            int grnPos = FindPattern(data, "Grn ", rdDoub);
            if (grnPos < 0) return false;
            int grnDoub = FindPattern(data, "doub", grnPos + 4);
            if (grnDoub < 0 || grnDoub > grnPos + 20) return false;
            double grnVal = ReadBigEndianDouble(data, grnDoub + 4);

            int blPos = FindPattern(data, "Bl  ", grnDoub);
            if (blPos < 0) return false;
            int blDoub = FindPattern(data, "doub", blPos + 4);
            if (blDoub < 0 || blDoub > blPos + 20) return false;
            double blVal = ReadBigEndianDouble(data, blDoub + 4);

            r = (byte)Mathf.Clamp(Mathf.RoundToInt((float)rdVal), 0, 255);
            g = (byte)Mathf.Clamp(Mathf.RoundToInt((float)grnVal), 0, 255);
            b = (byte)Mathf.Clamp(Mathf.RoundToInt((float)blVal), 0, 255);
            return true;
        }

        /// <summary>
        /// Color overlay (SoFi) from lfx2 raw bytes: "SoFi" → "Clr " → RGB, bypassing descriptor offset drift.
        /// </summary>
        private static bool TryGetColorOverlay(Layer layer, out Color32 color, out float overlayOpacity)
        {
            color = default;
            overlayOpacity = 1f;
            var blocks = layer.TaggedBlocks;
            if (blocks == null) return false;

            byte[] data = blocks.GetData(Tag.OBJECT_BASED_EFFECTS_LAYER1);
            if (data == null)
                data = blocks.GetData(Tag.OBJECT_BASED_EFFECTS_LAYER2);
            if (data == null) return false;

            // Find "SoFi" (color overlay) in raw bytes
            int sofiPos = FindPattern(data, "SoFi");
            if (sofiPos < 0) return false;

            // Check "enab" after SoFi (bounded search)
            int enabPos = FindPattern(data, "enab", sofiPos);
            if (enabPos >= 0 && enabPos < sofiPos + 200)
            {
                // "enab" then "bool" (4 bytes) + 1-byte flag
                int boolPos = FindPattern(data, "bool", enabPos + 4);
                if (boolPos >= 0 && boolPos < enabPos + 20 && boolPos + 5 <= data.Length)
                {
                    if (data[boolPos + 4] == 0)
                        return false; // effect disabled
                }
            }

            // "Clr " after SoFi
            int clrPos = FindPattern(data, "Clr ", sofiPos);
            if (clrPos < 0 || clrPos > sofiPos + 500) return false;

            // RGB
            if (!TryReadRGBFromRawBytes(data, clrPos, out byte r, out byte g, out byte b))
                return false;

            // Opacity "Opct" → "#Prc" + double (percent)
            int opctPos = FindPattern(data, "Opct", sofiPos);
            if (opctPos >= 0 && opctPos < clrPos)
            {
                // "Opct" then "UntF" + "#Prc" + 8-byte double
                int untfPos = FindPattern(data, "UntF", opctPos + 4);
                if (untfPos >= 0 && untfPos < opctPos + 20)
                {
                    // "#Prc" + 8-byte double
                    int prcPos = FindPattern(data, "#Prc", untfPos + 4);
                    if (prcPos >= 0 && prcPos < untfPos + 12 && prcPos + 12 <= data.Length)
                    {
                        double pct = ReadBigEndianDouble(data, prcPos + 4);
                        overlayOpacity = Mathf.Clamp01((float)pct / 100f);
                    }
                }
            }

            color = new Color32(r, g, b, 255);
            Debug.Log($"Color overlay: R={r} G={g} B={b}, opacity={overlayOpacity:F2}");
            return true;
        }

        /// <summary>
        /// Layer color: color overlay &gt; solid fill &gt; none
        /// </summary>
        private static bool TryGetLayerColor(Layer layer, out Color32 color, out float colorOpacity)
        {
            // 1) Color overlay first
            if (TryGetColorOverlay(layer, out color, out colorOpacity))
                return true;

            // 2) Solid fill (SoCo / vscg)
            if (TryGetSolidColor(layer, out color))
            {
                colorOpacity = 1f;
                return true;
            }

            colorOpacity = 1f;
            return false;
        }

        /// <summary>
        /// Apply fill RGB (keep A) and layer opacity to composite texture
        /// </summary>
        private static void ApplyFillColorAndOpacity(Texture2D texture, Layer layer)
        {
            bool hasColor = TryGetLayerColor(layer, out Color32 fillColor, out float colorOpacity);
            float layerOpacity = layer.OpacityFloat;
            float totalAlphaScale = layerOpacity * colorOpacity;
            bool needsAlphaScale = totalAlphaScale < 0.999f;

            if (!hasColor && !needsAlphaScale) return;

            Color32[] pixels = texture.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                if (hasColor)
                {
                    pixels[i].r = fillColor.r;
                    pixels[i].g = fillColor.g;
                    pixels[i].b = fillColor.b;
                }
                if (needsAlphaScale)
                {
                    pixels[i].a = (byte)Mathf.RoundToInt(pixels[i].a * totalAlphaScale);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
        }

        /// <summary>
        /// Solid-color texture for empty shape/fill layers
        /// </summary>
        private static Texture2D CreateSolidColorTexture(int width, int height, Color32 color, float opacity)
        {
            byte a = (byte)Mathf.RoundToInt(color.a * opacity);
            Color32 finalColor = new Color32(color.r, color.g, color.b, a);

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = finalColor;
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        /// <summary>
        /// Gradient color stop
        /// </summary>
        private struct GradientColorStop
        {
            public float position; // 0-1
            public byte r, g, b;
        }

        /// <summary>
        /// Parse gradient overlay (GrFl) from lfx2 effect bytes
        /// </summary>
        private static bool TryGetGradientOverlay(Layer layer, out List<GradientColorStop> stops,
            out float angle, out float opacity)
        {
            stops = null;
            angle = 90f;
            opacity = 1f;
            var blocks = layer.TaggedBlocks;
            if (blocks == null) return false;

            byte[] data = blocks.GetData(Tag.OBJECT_BASED_EFFECTS_LAYER1);
            if (data == null)
                data = blocks.GetData(Tag.OBJECT_BASED_EFFECTS_LAYER2);
            if (data == null) return false;

            int grflPos = FindPattern(data, "GrFl");
            if (grflPos < 0) return false;

            // enabled flag
            int enabPos = FindPattern(data, "enab", grflPos);
            if (enabPos >= 0 && enabPos < grflPos + 200)
            {
                int boolPos = FindPattern(data, "bool", enabPos + 4);
                if (boolPos >= 0 && boolPos < enabPos + 20 && boolPos + 5 <= data.Length)
                {
                    if (data[boolPos + 4] == 0)
                        return false;
                }
            }

            // Opacity
            int opctPos = FindPattern(data, "Opct", grflPos);
            if (opctPos >= 0 && opctPos < grflPos + 300)
            {
                int untfPos = FindPattern(data, "UntF", opctPos + 4);
                if (untfPos >= 0 && untfPos < opctPos + 20)
                {
                    int prcPos = FindPattern(data, "#Prc", untfPos + 4);
                    if (prcPos >= 0 && prcPos < untfPos + 12 && prcPos + 12 <= data.Length)
                    {
                        double pct = ReadBigEndianDouble(data, prcPos + 4);
                        opacity = Mathf.Clamp01((float)pct / 100f);
                    }
                }
            }

            // Angle "Angl"
            int anglPos = FindPattern(data, "Angl", grflPos);
            if (anglPos >= 0 && anglPos < grflPos + 500)
            {
                int untfPos = FindPattern(data, "UntF", anglPos + 4);
                if (untfPos >= 0 && untfPos < anglPos + 20 && untfPos + 16 <= data.Length)
                {
                    double a = ReadBigEndianDouble(data, untfPos + 8);
                    angle = (float)a;
                }
            }

            // Color stops: "Clrs" → "VlLs" → count → Objc blocks
            int clrsPos = FindPattern(data, "Clrs", grflPos);
            if (clrsPos < 0) return false;

            int vlLsPos = FindPattern(data, "VlLs", clrsPos);
            if (vlLsPos < 0 || vlLsPos > clrsPos + 20) return false;

            int stopCount = ReadBigEndianInt32(data, vlLsPos + 4);
            if (stopCount < 2 || stopCount > 100) return false;

            // "Trns" starts alpha stops — limit color stop search
            int trnsPos = FindPattern(data, "Trns", vlLsPos);
            int colorRegionEnd = trnsPos > 0 ? trnsPos : data.Length;

            stops = new List<GradientColorStop>();
            int searchFrom = vlLsPos + 8;

            for (int i = 0; i < stopCount; i++)
            {
                // Each stop: Objc with "Clr " RGB and "Lctn" position
                int clrPos = FindPattern(data, "Clr ", searchFrom);
                if (clrPos < 0 || clrPos >= colorRegionEnd) break;

                // Lctn position 0-4096
                int lctnPos = FindPattern(data, "Lctn", clrPos);
                if (lctnPos < 0 || lctnPos >= colorRegionEnd) break;

                if (!TryReadRGBFromRawBytes(data, clrPos, out byte r, out byte g, out byte b))
                    break;

                // "Lctn" → "long" → int32 value
                int longPos = FindPattern(data, "long", lctnPos + 4);
                if (longPos < 0 || longPos > lctnPos + 20 || longPos + 8 > data.Length)
                    break;

                int lctnValue = ReadBigEndianInt32(data, longPos + 4);
                float pos = Mathf.Clamp01(lctnValue / 4096f);

                stops.Add(new GradientColorStop { position = pos, r = r, g = g, b = b });
                searchFrom = longPos + 8;
            }

            if (stops.Count < 2) { stops = null; return false; }

            stops.Sort((a, b) => a.position.CompareTo(b.position));

            Debug.Log($"Gradient overlay: {stops.Count} stops, angle={angle}, opacity={opacity:F2}");
            return true;
        }

        /// <summary>
        /// Interpolate gradient at t
        /// </summary>
        private static Color32 SampleGradient(List<GradientColorStop> stops, float t)
        {
            t = Mathf.Clamp01(t);

            if (t <= stops[0].position)
                return new Color32(stops[0].r, stops[0].g, stops[0].b, 255);
            if (t >= stops[stops.Count - 1].position)
            {
                var last = stops[stops.Count - 1];
                return new Color32(last.r, last.g, last.b, 255);
            }

            for (int i = 0; i < stops.Count - 1; i++)
            {
                if (t >= stops[i].position && t <= stops[i + 1].position)
                {
                    float range = stops[i + 1].position - stops[i].position;
                    float lerp = range > 0.0001f ? (t - stops[i].position) / range : 0f;
                    byte r = (byte)Mathf.RoundToInt(Mathf.Lerp(stops[i].r, stops[i + 1].r, lerp));
                    byte g = (byte)Mathf.RoundToInt(Mathf.Lerp(stops[i].g, stops[i + 1].g, lerp));
                    byte b = (byte)Mathf.RoundToInt(Mathf.Lerp(stops[i].b, stops[i + 1].b, lerp));
                    return new Color32(r, g, b, 255);
                }
            }

            var fallback = stops[stops.Count - 1];
            return new Color32(fallback.r, fallback.g, fallback.b, 255);
        }

        /// <summary>
        /// Apply gradient overlay (replace RGB; scale A by opacities)
        /// </summary>
        private static void ApplyGradientOverlay(Texture2D texture, List<GradientColorStop> stops,
            float angleDeg, float opacity, float layerOpacity)
        {
            int w = texture.width;
            int h = texture.height;
            float totalAlpha = opacity * layerOpacity;

            float rad = angleDeg * Mathf.Deg2Rad;
            float dx = Mathf.Cos(rad);
            float dy = Mathf.Sin(rad);

            Color32[] pixels = texture.GetPixels32();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // Normalized coords (0-1), Y top-down
                    float nx = (float)x / Mathf.Max(1, w - 1);
                    float ny = 1f - (float)y / Mathf.Max(1, h - 1);

                    // Project on gradient axis from center
                    float cx = nx - 0.5f;
                    float cy = ny - 0.5f;
                    float t = cx * dx + cy * dy + 0.5f;

                    Color32 gc = SampleGradient(stops, t);
                    int idx = y * w + x;
                    pixels[idx].r = gc.r;
                    pixels[idx].g = gc.g;
                    pixels[idx].b = gc.b;
                    if (totalAlpha < 0.999f)
                        pixels[idx].a = (byte)Mathf.RoundToInt(pixels[idx].a * totalAlpha);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
        }

        /// <summary>
        /// Gradient-only texture when layer has no pixels but has gradient overlay
        /// </summary>
        private static Texture2D CreateGradientTexture(int width, int height,
            List<GradientColorStop> stops, float angleDeg, float opacity)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];

            float rad = angleDeg * Mathf.Deg2Rad;
            float dx = Mathf.Cos(rad);
            float dy = Mathf.Sin(rad);
            byte alphaByte = (byte)Mathf.RoundToInt(Mathf.Clamp01(opacity) * 255f);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float nx = (float)x / Mathf.Max(1, width - 1);
                    float ny = 1f - (float)y / Mathf.Max(1, height - 1);
                    float cx = nx - 0.5f;
                    float cy = ny - 0.5f;
                    float t = cx * dx + cy * dy + 0.5f;

                    Color32 gc = SampleGradient(stops, t);
                    gc.a = alphaByte;
                    pixels[y * width + x] = gc;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        /// <summary>
        /// Stroke (FrFX) from lfx2 bytes
        /// </summary>
        private static bool TryGetStrokeEffect(Layer layer, out Color strokeColor, out float strokeSize)
        {
            strokeColor = Color.black;
            strokeSize = 1f;
            var blocks = layer.TaggedBlocks;
            if (blocks == null) return false;

            byte[] data = blocks.GetData(Tag.OBJECT_BASED_EFFECTS_LAYER1);
            if (data == null)
                data = blocks.GetData(Tag.OBJECT_BASED_EFFECTS_LAYER2);
            if (data == null) return false;

            int frfxPos = FindPattern(data, "FrFX");
            if (frfxPos < 0) return false;

            // Approximate end of this effect block
            int nextEffect = data.Length;
            string[] effectKeys = { "DrSh", "IrSh", "OrGl", "IrGl", "ebbl", "SoFi", "patternFill", "GrFl" };
            foreach (var key in effectKeys)
            {
                int pos = FindPattern(data, key, frfxPos + 4);
                if (pos > frfxPos && pos < nextEffect)
                    nextEffect = pos;
            }

            int enabPos = FindPattern(data, "enab", frfxPos);
            if (enabPos >= 0 && enabPos < frfxPos + 200 && enabPos < nextEffect)
            {
                int boolPos = FindPattern(data, "bool", enabPos + 4);
                if (boolPos >= 0 && boolPos < enabPos + 20 && boolPos + 5 <= data.Length)
                {
                    if (data[boolPos + 4] == 0)
                        return false;
                }
            }

            // Stroke size "Sz  " → "UntF" → double
            int szPos = FindPattern(data, "Sz  ", frfxPos);
            if (szPos >= 0 && szPos < nextEffect)
            {
                int untfPos = FindPattern(data, "UntF", szPos + 4);
                if (untfPos >= 0 && untfPos < szPos + 20)
                {
                    // unit id 4 bytes + 8-byte double
                    if (untfPos + 16 <= data.Length)
                    {
                        double sz = ReadBigEndianDouble(data, untfPos + 8);
                        strokeSize = Mathf.Max(1f, (float)sz);
                    }
                }
            }

            // Stroke color "Clr " → RGB
            int clrPos = FindPattern(data, "Clr ", frfxPos);
            if (clrPos >= 0 && clrPos < nextEffect)
            {
                if (TryReadRGBFromRawBytes(data, clrPos, out byte r, out byte g, out byte b))
                {
                    float opacity = 1f;
                    int opctPos = FindPattern(data, "Opct", frfxPos);
                    if (opctPos >= 0 && opctPos < clrPos && opctPos < nextEffect)
                    {
                        int untfPos = FindPattern(data, "UntF", opctPos + 4);
                        if (untfPos >= 0 && untfPos < opctPos + 20)
                        {
                            int prcPos = FindPattern(data, "#Prc", untfPos + 4);
                            if (prcPos >= 0 && prcPos < untfPos + 12 && prcPos + 12 <= data.Length)
                            {
                                double pct = ReadBigEndianDouble(data, prcPos + 4);
                                opacity = Mathf.Clamp01((float)pct / 100f);
                            }
                        }
                    }
                    strokeColor = new Color(r / 255f, g / 255f, b / 255f, opacity);
                    Debug.Log($"Stroke: R={r} G={g} B={b}, size={strokeSize}, opacity={opacity:F2}");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Drop shadow (DrSh) from lfx2 bytes
        /// </summary>
        private static bool TryGetDropShadowEffect(Layer layer, out Color shadowColor, out Vector2 shadowOffset)
        {
            shadowColor = new Color(0, 0, 0, 0.75f);
            shadowOffset = new Vector2(1, -1);
            var blocks = layer.TaggedBlocks;
            if (blocks == null) return false;

            byte[] data = blocks.GetData(Tag.OBJECT_BASED_EFFECTS_LAYER1);
            if (data == null)
                data = blocks.GetData(Tag.OBJECT_BASED_EFFECTS_LAYER2);
            if (data == null) return false;

            int drshPos = FindPattern(data, "DrSh");
            if (drshPos < 0) return false;

            int nextEffect = data.Length;
            string[] effectKeys = { "IrSh", "OrGl", "IrGl", "ebbl", "SoFi", "FrFX", "patternFill", "GrFl" };
            foreach (var key in effectKeys)
            {
                int pos = FindPattern(data, key, drshPos + 4);
                if (pos > drshPos && pos < nextEffect)
                    nextEffect = pos;
            }

            int enabPos = FindPattern(data, "enab", drshPos);
            if (enabPos >= 0 && enabPos < drshPos + 200 && enabPos < nextEffect)
            {
                int boolPos = FindPattern(data, "bool", enabPos + 4);
                if (boolPos >= 0 && boolPos < enabPos + 20 && boolPos + 5 <= data.Length)
                {
                    if (data[boolPos + 4] == 0)
                        return false;
                }
            }

            // Distance "Dstn" → double
            float distance = 5f;
            int dstnPos = FindPattern(data, "Dstn", drshPos);
            if (dstnPos >= 0 && dstnPos < nextEffect)
            {
                int untfPos = FindPattern(data, "UntF", dstnPos + 4);
                if (untfPos >= 0 && untfPos < dstnPos + 20 && untfPos + 16 <= data.Length)
                {
                    double d = ReadBigEndianDouble(data, untfPos + 8);
                    distance = (float)d;
                }
            }

            // Angle "lagl"
            float angle = 120f;
            int laglPos = FindPattern(data, "lagl", drshPos);
            if (laglPos >= 0 && laglPos < nextEffect)
            {
                int untfPos = FindPattern(data, "UntF", laglPos + 4);
                if (untfPos >= 0 && untfPos < laglPos + 20 && untfPos + 16 <= data.Length)
                {
                    double a = ReadBigEndianDouble(data, untfPos + 8);
                    angle = (float)a;
                }
            }

            // Angle + distance → offset
            float rad = angle * Mathf.Deg2Rad;
            float offsetX = distance * Mathf.Cos(rad);
            float offsetY = distance * Mathf.Sin(rad);
            shadowOffset = new Vector2(offsetX, -offsetY);

            float opacity = 0.75f;
            int opctPos = FindPattern(data, "Opct", drshPos);
            if (opctPos >= 0 && opctPos < nextEffect)
            {
                int untfPos = FindPattern(data, "UntF", opctPos + 4);
                if (untfPos >= 0 && untfPos < opctPos + 20)
                {
                    int prcPos = FindPattern(data, "#Prc", untfPos + 4);
                    if (prcPos >= 0 && prcPos < untfPos + 12 && prcPos + 12 <= data.Length)
                    {
                        double pct = ReadBigEndianDouble(data, prcPos + 4);
                        opacity = Mathf.Clamp01((float)pct / 100f);
                    }
                }
            }

            int clrPos = FindPattern(data, "Clr ", drshPos);
            if (clrPos >= 0 && clrPos < nextEffect)
            {
                if (TryReadRGBFromRawBytes(data, clrPos, out byte r, out byte g, out byte b))
                {
                    shadowColor = new Color(r / 255f, g / 255f, b / 255f, opacity);
                }
                else
                {
                    shadowColor = new Color(0, 0, 0, opacity);
                }
            }
            else
            {
                shadowColor = new Color(0, 0, 0, opacity);
            }

            Debug.Log($"Drop shadow: color=({shadowColor.r:F2},{shadowColor.g:F2},{shadowColor.b:F2}), " +
                      $"opacity={opacity:F2}, distance={distance}, angle={angle}, offset=({shadowOffset.x:F1},{shadowOffset.y:F1})");
            return true;
        }

        #endregion

        #region Nine-slice auto detection

        /// <summary>
        /// Compare column x vs x+1; false if any channel delta &gt; threshold
        /// </summary>
        private static bool ColsAreSame(Color32[] pixels, int width, int height, int x, int threshold)
        {
            if (x + 1 >= width) return false;
            for (int y = 0; y < height; y++)
            {
                Color32 p0 = pixels[y * width + x];
                Color32 p1 = pixels[y * width + x + 1];
                if (Mathf.Abs(p0.r - p1.r) > threshold ||
                    Mathf.Abs(p0.g - p1.g) > threshold ||
                    Mathf.Abs(p0.b - p1.b) > threshold ||
                    Mathf.Abs(p0.a - p1.a) > threshold)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Compare row y vs y+1; false if any channel delta &gt; threshold
        /// </summary>
        private static bool RowsAreSame(Color32[] pixels, int width, int height, int y, int threshold)
        {
            if (y + 1 >= height) return false;
            for (int x = 0; x < width; x++)
            {
                Color32 p0 = pixels[y * width + x];
                Color32 p1 = pixels[(y + 1) * width + x];
                if (Mathf.Abs(p0.r - p1.r) > threshold ||
                    Mathf.Abs(p0.g - p1.g) > threshold ||
                    Mathf.Abs(p0.b - p1.b) > threshold ||
                    Mathf.Abs(p0.a - p1.a) > threshold)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Longest run of true in flags; must be ≥ minLength. Returns (start, length) or (-1, 0).
        /// </summary>
        private static (int start, int length) FindLongestSameZone(bool[] sameFlags, int minLength)
        {
            int bestStart = -1, bestLen = 0;
            int curStart = 0, curLen = 0;
            for (int i = 0; i < sameFlags.Length; i++)
            {
                if (sameFlags[i])
                {
                    if (curLen == 0) curStart = i;
                    curLen++;
                    if (curLen > bestLen)
                    {
                        bestStart = curStart;
                        bestLen = curLen;
                    }
                }
                else
                {
                    curLen = 0;
                }
            }
            return bestLen >= minLength ? (bestStart, bestLen) : (-1, 0);
        }

        /// <summary>
        /// From fixed borders, computes the croppable center pixel size in the source (between the two fixed strips after the longest cut zone and borderInset),
        /// then takes the smaller of that and the nine-slice max center column/row caps as the actual compressed center width/height.
        /// When left=right=0 (or bottom=top=0) on an axis, that axis does not participate in nine-slice;
        /// centerCols (or centerRows) then uses the full croppable width (or height), not the compression cap.
        /// </summary>
        private static void ComputeNineSliceCenterCrop(int srcW, int srcH, int left, int right, int bottom, int top,
            int maxCenterCols, int maxCenterRows,
            out int centerCols, out int centerRows)
        {
            bool horizontalSlice = (left > 0 || right > 0);
            bool verticalSlice = (bottom > 0 || top > 0);
            maxCenterCols = Mathf.Max(1, maxCenterCols);
            maxCenterRows = Mathf.Max(1, maxCenterRows);

            if (horizontalSlice)
            {
                int croppableW = Mathf.Max(1, srcW - left - right);
                centerCols = Mathf.Min(maxCenterCols, croppableW);
            }
            else
            {
                centerCols = srcW;
            }

            if (verticalSlice)
            {
                int croppableH = Mathf.Max(1, srcH - bottom - top);
                centerRows = Mathf.Min(maxCenterRows, croppableH);
            }
            else
            {
                centerRows = srcH;
            }
        }

        /// <summary>
        /// Detects whether the image is suitable for nine-slice slicing (parameters from caller: global EditorPrefs or per-layer overrides).
        /// Either horizontal or vertical longest contiguous cuttable zone &gt;= minSameZone suffices; if the other axis does not qualify,
        /// its border is computed only when that axis' longest zone length &gt; borderInset, otherwise that axis is 0.
        /// Returns Vector4(left, bottom, right, top); null if unsuitable.
        /// </summary>
        private static Vector4? DetectNineSlice(Texture2D texture,
            int borderInset, int pixelThresh, int minSameZone, int minCenterCols, int minCenterRows)
        {
            int w = texture.width;
            int h = texture.height;
            if (w < 4 || h < 4) return null;

            Color32[] pixels = texture.GetPixels32();
            borderInset = Mathf.Max(0, borderInset);
            pixelThresh = Mathf.Clamp(pixelThresh, 0, 255);
            minSameZone = Mathf.Max(1, minSameZone);
            minCenterCols = Mathf.Max(1, minCenterCols);
            minCenterRows = Mathf.Max(1, minCenterRows);

            // Horizontal: compare column by column
            bool[] colFlags = new bool[w - 1];
            for (int x = 0; x < w - 1; x++)
                colFlags[x] = ColsAreSame(pixels, w, h, x, pixelThresh);
            var (hZoneStart, hZoneLen) = FindLongestSameZone(colFlags, 1);

            // Vertical: compare row by row (Unity: y=0 at bottom)
            bool[] rowFlags = new bool[h - 1];
            for (int y = 0; y < h - 1; y++)
                rowFlags[y] = RowsAreSame(pixels, w, h, y, pixelThresh);
            var (vZoneStart, vZoneLen) = FindLongestSameZone(rowFlags, 1);

            bool horizontalStrong = hZoneLen >= minSameZone;
            bool verticalStrong = vZoneLen >= minSameZone;
            if (!horizontalStrong && !verticalStrong)
                return null;

            int left, right;
            if (horizontalStrong || (hZoneStart >= 0 && hZoneLen > borderInset))
            {
                int hInset = Mathf.Min(borderInset, hZoneLen / 2);
                left = hZoneStart + hInset;
                right = Mathf.Max(0, w - 1 - (hZoneStart + hZoneLen) + hInset);
            }
            else
            {
                left = 0;
                right = 0;
            }

            int bottom, top;
            if (verticalStrong || (vZoneStart >= 0 && vZoneLen > borderInset))
            {
                int vInset = Mathf.Min(borderInset, vZoneLen / 2);
                bottom = vZoneStart + vInset;
                top = Mathf.Max(0, h - 1 - (vZoneStart + vZoneLen) + vInset);
            }
            else
            {
                bottom = 0;
                top = 0;
            }

            ComputeNineSliceCenterCrop(w, h, left, right, bottom, top, minCenterCols, minCenterRows, out int effCC, out int effCR);
            int outW = left + effCC + right;
            int outH = bottom + effCR + top;

            if (outW >= w && outH >= h) return null;
            return new Vector4(left, bottom, right, top);
        }

        /// <summary>
        /// Copies a rectangular region between two Color32 arrays.
        /// </summary>
        private static void CopyRegion(Color32[] src, int srcW, int srcX, int srcY,
                                        int regionW, int regionH,
                                        Color32[] dst, int dstW, int dstX, int dstY)
        {
            for (int y = 0; y < regionH; y++)
            {
                for (int x = 0; x < regionW; x++)
                {
                    dst[(dstY + y) * dstW + (dstX + x)] = src[(srcY + y) * srcW + (srcX + x)];
                }
            }
        }

        /// <summary>
        /// Builds a compressed nine-slice image: keeps four corners and four edges; downsamples the center.
        /// left/bottom/right/top are fixed pixel counts per edge (Unity coordinates).
        /// Center block size is min(nine-slice max center cols/rows, croppable region); croppable region is the source pixel range between borders.
        /// </summary>
        private static Texture2D BuildNineSliceImage(Texture2D src, int left, int bottom, int right, int top,
            int maxCenterCols, int maxCenterRows)
        {
            int w = src.width;
            int h = src.height;
            ComputeNineSliceCenterCrop(w, h, left, right, bottom, top, maxCenterCols, maxCenterRows, out int centerCols, out int centerRows);
            int outW = left + centerCols + right;
            int outH = bottom + centerRows + top;
            Color32[] srcPixels = src.GetPixels32();
            Color32[] outPixels = new Color32[outW * outH];

            int srcCW = (w - right) - left;
            int srcCH = (h - top) - bottom;
            int sampleX = left + Mathf.Max(0, (srcCW - centerCols) / 2);
            int sampleY = bottom + Mathf.Max(0, (srcCH - centerRows) / 2);

            // Bottom-left
            if (left > 0 && bottom > 0)
                CopyRegion(srcPixels, w, 0, 0, left, bottom, outPixels, outW, 0, 0);
            // Bottom edge
            if (centerCols > 0 && bottom > 0)
                CopyRegion(srcPixels, w, sampleX, 0, centerCols, bottom, outPixels, outW, left, 0);
            // Bottom-right
            if (right > 0 && bottom > 0)
                CopyRegion(srcPixels, w, w - right, 0, right, bottom, outPixels, outW, left + centerCols, 0);
            // Left edge
            if (left > 0 && centerRows > 0)
                CopyRegion(srcPixels, w, 0, sampleY, left, centerRows, outPixels, outW, 0, bottom);
            // Center
            if (centerCols > 0 && centerRows > 0)
                CopyRegion(srcPixels, w, sampleX, sampleY, centerCols, centerRows, outPixels, outW, left, bottom);
            // Right edge
            if (right > 0 && centerRows > 0)
                CopyRegion(srcPixels, w, w - right, sampleY, right, centerRows, outPixels, outW, left + centerCols, bottom);
            // Top-left
            if (left > 0 && top > 0)
                CopyRegion(srcPixels, w, 0, h - top, left, top, outPixels, outW, 0, bottom + centerRows);
            // Top edge
            if (centerCols > 0 && top > 0)
                CopyRegion(srcPixels, w, sampleX, h - top, centerCols, top, outPixels, outW, left, bottom + centerRows);
            // Top-right
            if (right > 0 && top > 0)
                CopyRegion(srcPixels, w, w - right, h - top, right, top, outPixels, outW, left + centerCols, bottom + centerRows);
            Texture2D outTex = new Texture2D(outW, outH, TextureFormat.RGBA32, false);
            outTex.SetPixels32(outPixels);
            outTex.Apply();
            return outTex;
        }

        #endregion

        private static string CleanFileName(string fileName)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            foreach (char c in invalidChars)
            {
                fileName = fileName.Replace(c, '_');
            }
            fileName = fileName.Replace(' ', '_');
            return fileName;
        }

        /// <summary>Returns a string suitable for use as a node name in the Unity hierarchy (replacing path separators / and \).</summary>
        private static string GetSafeHierarchyName(string originalName)
        {
            if (string.IsNullOrEmpty(originalName)) return "Unnamed";
            return originalName.Replace('/', '_').Replace('\\', '_');
        }

        private static string GetRelativeAssetsPath(string fullPath) 
        {
            string normalizedFullPath = fullPath.Replace("\\", "/");
            string normalizedDataPath = Application.dataPath.Replace("\\", "/");
            return "Assets" + normalizedFullPath.Replace(normalizedDataPath, "");
        }
    }

    // ─────────────── Component-JSON helper types (shared by PSDAutoPrefab and PSDEditorWindow) ───────────────

    [System.Serializable]
    internal class PrefabNodeComponentJson
    {
        public string type;
        public string json;
    }

    [System.Serializable]
    internal class PrefabNodeComponentsJsonBundle
    {
        public PrefabNodeComponentJson[] components;
    }

    // ─────────────── Snapshot types for stable component-JSON serialization ───────────────
    // Only editor-visible, semantically meaningful properties are captured.
    // Internal hashcodes, instanceIDs and runtime-only fields are intentionally excluded.

    /// <summary>Minimal snapshot for non-special components: only the enabled state is tracked.</summary>
    [System.Serializable]
    internal class ComponentEnabledSnapshot
    {
        public bool enabled;
    }

    /// <summary>Stable snapshot of GameObject-level exported state that is not represented by a Component.</summary>
    [System.Serializable]
    internal class GameObjectSnapshot
    {
        public bool activeSelf;

        public static GameObjectSnapshot From(GameObject go)
        {
            return new GameObjectSnapshot
            {
                activeSelf = go != null && go.activeSelf,
            };
        }

        public void ApplyTo(GameObject go)
        {
            if (go == null)
                return;
            go.SetActive(activeSelf);
        }
    }

    /// <summary>Stable snapshot of RectTransform's editor-visible layout properties.
    /// Slider / Scrollbar driven properties are zeroed out so that snapshots from live scene GOs and
    /// from serialized Prefab assets match. ScrollRect.content keeps its layout diffable, but its
    /// anchoredPosition is normalized because Unity nudges it when viewport / scrollbar geometry changes.
    /// localPosition is intentionally excluded: in UGUI it is derived from anchoredPosition + anchor
    /// layout and carries no independent semantic meaning.</summary>
    [System.Serializable]
    internal class RectTransformSnapshot
    {
        public float anchorMinX, anchorMinY;
        public float anchorMaxX, anchorMaxY;
        public float anchoredPositionX, anchoredPositionY;
        public float sizeDeltaX, sizeDeltaY;
        public float pivotX, pivotY;
        public float localEulerX, localEulerY, localEulerZ;
        public float localScaleX, localScaleY, localScaleZ;

        public static RectTransformSnapshot From(RectTransform rt)
        {
            var snap = new RectTransformSnapshot
            {
                anchorMinX = rt.anchorMin.x, anchorMinY = rt.anchorMin.y,
                anchorMaxX = rt.anchorMax.x, anchorMaxY = rt.anchorMax.y,
                anchoredPositionX = rt.anchoredPosition.x, anchoredPositionY = rt.anchoredPosition.y,
                sizeDeltaX = rt.sizeDelta.x, sizeDeltaY = rt.sizeDelta.y,
                pivotX = rt.pivot.x, pivotY = rt.pivot.y,
                localEulerX = rt.localEulerAngles.x, localEulerY = rt.localEulerAngles.y, localEulerZ = rt.localEulerAngles.z,
                localScaleX = rt.localScale.x, localScaleY = rt.localScale.y, localScaleZ = rt.localScale.z,
            };

            // Zero out properties driven by Slider / Scrollbar so that
            // snapshots from a live scene GO tree and from a serialized Prefab asset
            // produce identical results (driven values differ at runtime vs serialization).
            if (IsDrivenByParentUIComponent(rt))
            {
                snap.anchorMinX = 0; snap.anchorMinY = 0;
                snap.anchorMaxX = 0; snap.anchorMaxY = 0;
                snap.anchoredPositionX = 0; snap.anchoredPositionY = 0;
                snap.sizeDeltaX = 0; snap.sizeDeltaY = 0;
            }

            if (IsScrollRectContent(rt))
            {
                snap.anchoredPositionX = 0;
                snap.anchoredPositionY = 0;
            }

            return snap;
        }

        /// <summary>
        /// Returns true if <paramref name="rt"/> is a RectTransform whose anchor/position/sizeDelta
        /// are driven by a parent Slider or Scrollbar component.
        /// Specifically checks: Slider.fillRect, Slider.handleRect, Scrollbar.handleRect.
        /// </summary>
        private static bool IsDrivenByParentUIComponent(RectTransform rt)
        {
            if (rt == null || rt.parent == null) return false;

            // Walk up ancestors (the driving component may be on a grandparent, e.g. Slider → HandleSlideArea → Handle)
            Transform current = rt.parent;
            while (current != null)
            {
                // Slider drives fillRect and handleRect
                var slider = current.GetComponent<Slider>();
                if (slider != null && (slider.fillRect == rt || slider.handleRect == rt))
                    return true;

                // Scrollbar drives handleRect
                var scrollbar = current.GetComponent<Scrollbar>();
                if (scrollbar != null && scrollbar.handleRect == rt)
                    return true;

                current = current.parent;
            }
            return false;
        }

        private static bool IsScrollRectContent(RectTransform rt)
        {
            if (rt == null || rt.parent == null) return false;

            Transform current = rt.parent;
            while (current != null)
            {
                var scrollRect = current.GetComponent<ScrollRect>();
                if (scrollRect != null && scrollRect.content == rt)
                    return true;

                current = current.parent;
            }
            return false;
        }

        public void ApplyTo(RectTransform rt)
        {
            rt.anchorMin = new Vector2(anchorMinX, anchorMinY);
            rt.anchorMax = new Vector2(anchorMaxX, anchorMaxY);
            rt.anchoredPosition = new Vector2(anchoredPositionX, anchoredPositionY);
            rt.sizeDelta = new Vector2(sizeDeltaX, sizeDeltaY);
            rt.pivot = new Vector2(pivotX, pivotY);
            rt.localEulerAngles = new Vector3(localEulerX, localEulerY, localEulerZ);
            rt.localScale = new Vector3(localScaleX, localScaleY, localScaleZ);
        }
    }

    /// <summary>Stable snapshot of UnityEngine.UI.Image's editor-visible properties.</summary>
    [System.Serializable]
    internal class ImageSnapshot
    {
        public bool enabled;
        public float colorR, colorG, colorB, colorA;
        public bool raycastTarget;
        public bool maskable;
        // Sprite is identified by its asset GUID to stay stable across re-imports
        public string spriteGuid;
        public int imageType;          // Image.Type enum int
        public bool preserveAspect;
        public bool fillCenter;
        public int fillMethod;         // Image.FillMethod enum int
        public float fillAmount;
        public bool fillClockwise;
        public int fillOrigin;
        public float pixelsPerUnitMultiplier;
        public bool useSpriteMesh;

        public static ImageSnapshot From(Image img)
        {
            string guid = "";
            if (img.sprite != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(img.sprite);
                if (!string.IsNullOrEmpty(assetPath))
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(img.sprite, out guid, out long _);
            }
            return new ImageSnapshot
            {
                enabled = img.enabled,
                colorR = img.color.r, colorG = img.color.g, colorB = img.color.b, colorA = img.color.a,
                raycastTarget = img.raycastTarget,
                maskable = img.maskable,
                spriteGuid = guid,
                imageType = (int)img.type,
                preserveAspect = img.preserveAspect,
                fillCenter = img.fillCenter,
                fillMethod = (int)img.fillMethod,
                fillAmount = img.fillAmount,
                fillClockwise = img.fillClockwise,
                fillOrigin = img.fillOrigin,
                pixelsPerUnitMultiplier = img.pixelsPerUnitMultiplier,
                useSpriteMesh = img.useSpriteMesh,
            };
        }
    }

    /// <summary>Stable snapshot of legacy UnityEngine.UI.Text's editor-visible properties.</summary>
    [System.Serializable]
    internal class TextSnapshot
    {
        public bool enabled;
        public string text;
        public string fontGuid;
        public int fontSize;
        public int fontStyle;           // FontStyle enum int
        public bool resizeTextForBestFit;
        public int alignment;           // TextAnchor enum int
        public bool alignByGeometry;
        public float lineSpacing;
        public bool supportRichText;
        public bool horizontalOverflow;  // HorizontalWrapMode
        public bool verticalOverflow;    // VerticalWrapMode
        public float colorR, colorG, colorB, colorA;
        public bool raycastTarget;
        public bool maskable;

        public static TextSnapshot From(Text txt)
        {
            string guid = "";
            if (txt.font != null)
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(txt.font, out guid, out long _);
            return new TextSnapshot
            {
                enabled = txt.enabled,
                text = txt.text,
                fontGuid = guid,
                fontSize = txt.fontSize,
                fontStyle = (int)txt.fontStyle,
                resizeTextForBestFit = txt.resizeTextForBestFit,
                alignment = (int)txt.alignment,
                alignByGeometry = txt.alignByGeometry,
                lineSpacing = txt.lineSpacing,
                supportRichText = txt.supportRichText,
                horizontalOverflow = txt.horizontalOverflow == HorizontalWrapMode.Wrap,
                verticalOverflow = txt.verticalOverflow == VerticalWrapMode.Truncate,
                colorR = txt.color.r, colorG = txt.color.g, colorB = txt.color.b, colorA = txt.color.a,
                raycastTarget = txt.raycastTarget,
                maskable = txt.maskable,
            };
        }
    }

#if USE_TMP
    /// <summary>Stable snapshot of TextMeshProUGUI's editor-visible properties.</summary>
    [System.Serializable]
    internal class TmpTextSnapshot
    {
        public bool enabled;
        public string text;
        public string fontAssetGuid;
        public float fontSize;
        public float fontSizeMin;
        public float fontSizeMax;
        public bool enableAutoSizing;
        public int fontStyle;           // FontStyles enum int
        public int horizontalAlignment; // HorizontalAlignmentOptions enum int
        public int verticalAlignment;   // VerticalAlignmentOptions enum int
        public bool enableWordWrapping;
        public int overflowMode;        // TextOverflowModes enum int
        public bool richText;
        public float characterSpacing;
        public float wordSpacing;
        public float lineSpacing;
        public float paragraphSpacing;
        public float colorR, colorG, colorB, colorA;
        public bool enableVertexGradient;
        public bool raycastTarget;
        public bool maskable;

        public static TmpTextSnapshot From(TextMeshProUGUI tmp)
        {
            string guid = "";
            if (tmp.font != null)
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(tmp.font, out guid, out long _);
            return new TmpTextSnapshot
            {
                enabled = tmp.enabled,
                text = tmp.text,
                fontAssetGuid = guid,
                fontSize = tmp.fontSize,
                fontSizeMin = tmp.fontSizeMin,
                fontSizeMax = tmp.fontSizeMax,
                enableAutoSizing = tmp.enableAutoSizing,
                fontStyle = (int)tmp.fontStyle,
                horizontalAlignment = (int)tmp.horizontalAlignment,
                verticalAlignment = (int)tmp.verticalAlignment,
                enableWordWrapping = tmp.enableWordWrapping,
                overflowMode = (int)tmp.overflowMode,
                richText = tmp.richText,
                characterSpacing = tmp.characterSpacing,
                wordSpacing = tmp.wordSpacing,
                lineSpacing = tmp.lineSpacing,
                paragraphSpacing = tmp.paragraphSpacing,
                colorR = tmp.color.r, colorG = tmp.color.g, colorB = tmp.color.b, colorA = tmp.color.a,
                enableVertexGradient = tmp.enableVertexGradient,
                raycastTarget = tmp.raycastTarget,
                maskable = tmp.maskable,
            };
        }
    }
#endif

    /// <summary>Dedup settings (persisted to EditorConfig/PSD_DedupConfig.json): MAE threshold and fingerprint size.</summary>
    public class PSDDedupSettingsWindow : EditorWindow
    {
        private float _maeThreshold;
        private int _fingerprintSize;

        private void OnEnable()
        {
            var data = PSDDedupConfig.Load(forceReload: true);
            _maeThreshold = data.maeThreshold;
            _fingerprintSize = data.fingerprintSize;
        }

        private void ApplyToPrefs()
        {
            _maeThreshold = Mathf.Clamp(_maeThreshold, PSDDedupPrefs.MinMaeThreshold, PSDDedupPrefs.MaxMaeThreshold);
            _fingerprintSize = Mathf.Clamp(_fingerprintSize, PSDDedupPrefs.MinFingerprintSize, PSDDedupPrefs.MaxFingerprintSize);

            // Write JSON (primary storage)
            PSDDedupConfig.Save(new PSDDedupConfigData
            {
                maeThreshold = _maeThreshold,
                fingerprintSize = _fingerprintSize
            });
            // Compatibility: also sync EditorPrefs
            EditorPrefs.SetFloat(PSDDedupPrefs.KeyMaeThreshold, _maeThreshold);
            EditorPrefs.SetInt(PSDDedupPrefs.KeyFingerprintSize, _fingerprintSize);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Image dedup (same as PSDAutoPrefab)", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.Space(8);

            // -- MAE threshold --
            _maeThreshold = EditorGUILayout.Slider(
                "MAE threshold (dedup limit, 0–1)",
                _maeThreshold,
                PSDDedupPrefs.MinMaeThreshold,
                PSDDedupPrefs.MaxMaeThreshold);
            EditorGUILayout.HelpBox(
                "MAE (mean absolute error) = mean per-channel difference on scaled premultiplied RGBA fingerprints (0–1).\n" +
                "Every image pair now goes through the MAE comparison directly; there is no aspect pre-filter.\n" +
                "MAE ≤ this value means same image (reuse); above means different. Lower (e.g. 0.02) is stricter; higher (e.g. 0.15) is looser (risk of false merges).\n" +
                "Re-export to apply.",
                MessageType.None);

            EditorGUILayout.Space(8);

            // -- Fingerprint size --
            _fingerprintSize = EditorGUILayout.IntSlider(
                "Fingerprint size N (downscale to N×N)",
                _fingerprintSize,
                PSDDedupPrefs.MinFingerprintSize,
                PSDDedupPrefs.MaxFingerprintSize);
            EditorGUILayout.HelpBox(
                "Each image is bilinearly scaled to N×N before extracting premultiplied RGBA fingerprints (N×N×4 floats).\n" +
                "Default 8 (8×8 = 64 samples) matches legacy and is fast.\n" +
                "Larger (16–32) is finer and safer but slower; smaller (4) is faster but more false positives.\n" +
                "Note: changing N invalidates lengths in shared-folder cache—clear cache or re-export.",
                MessageType.None);

            EditorGUILayout.Space(10);
            if (EditorGUI.EndChangeCheck())
                ApplyToPrefs();
            if (GUILayout.Button("Restore defaults", GUILayout.Height(28)))
            {
                _maeThreshold = PSDDedupPrefs.DefaultMaeThreshold;
                _fingerprintSize = PSDDedupPrefs.DefaultFingerprintSize;
                ApplyToPrefs();
                Debug.Log($"Dedup restored to defaults: MAE threshold={PSDDedupPrefs.DefaultMaeThreshold}, fingerprint size={PSDDedupPrefs.DefaultFingerprintSize}");
            }
        }
    }

    /// <summary>Nine-slice settings: border inset, pixel threshold, center compression caps, minimum cut zone. Persisted to EditorConfig/PSD_NineSliceConfig.json.</summary>
    public class PSDNineSliceSettingsWindow : EditorWindow
    {
        private int _borderInset;
        private int _pixelThreshold;
        private int _minCenterCols;
        private int _minCenterRows;
        private int _minSameZone;

        private void OnEnable()
        {
            LoadFromPrefs();
        }

        private void LoadFromPrefs()
        {
            var data = PSDNineSliceConfig.Load(forceReload: true);
            _borderInset    = data.borderInset;
            _pixelThreshold = data.pixelThreshold;
            _minCenterCols  = data.minCenterCols;
            _minCenterRows  = data.minCenterRows;
            _minSameZone    = data.minSameZone;
        }

        private void ApplyToPrefs()
        {
            _borderInset    = Mathf.Max(0, _borderInset);
            _pixelThreshold = Mathf.Clamp(_pixelThreshold, 0, 255);
            _minCenterCols  = Mathf.Clamp(_minCenterCols, 1, 4096);
            _minCenterRows  = Mathf.Clamp(_minCenterRows, 1, 4096);
            _minSameZone    = Mathf.Clamp(_minSameZone, 1, 4096);

            // Write JSON (primary storage)
            PSDNineSliceConfig.Save(new PSDNineSliceConfigData
            {
                borderInset    = _borderInset,
                pixelThreshold = _pixelThreshold,
                minCenterCols  = _minCenterCols,
                minCenterRows  = _minCenterRows,
                minSameZone    = _minSameZone
            });
            // Compatibility: also sync EditorPrefs
            EditorPrefs.SetInt(PSDNineSlicePrefs.KeyBorderInset,    _borderInset);
            EditorPrefs.SetInt(PSDNineSlicePrefs.KeyPixelThreshold, _pixelThreshold);
            EditorPrefs.SetInt(PSDNineSlicePrefs.KeyMinCenterCols,  _minCenterCols);
            EditorPrefs.SetInt(PSDNineSlicePrefs.KeyMinCenterRows,  _minCenterRows);
            EditorPrefs.SetInt(PSDNineSlicePrefs.KeyMinSameZone,    _minSameZone);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Nine-slice detection and export", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);
            EditorGUI.BeginChangeCheck();

            _borderInset = EditorGUILayout.IntField("Border inset (BORDER_INSET)", _borderInset);
            EditorGUILayout.HelpBox("Keeps cut lines away from color boundaries; same as testforpsd.py BORDER_INSET.", MessageType.None);

            EditorGUILayout.Space(6);
            _pixelThreshold = EditorGUILayout.IntField("Adjacent pixel difference threshold (PIXEL_THRESHOLD)", _pixelThreshold);
            EditorGUILayout.HelpBox("Max allowed per-channel RGBA difference (0–255) when deciding if adjacent columns/rows are same-color and cuttable.", MessageType.None);

            EditorGUILayout.Space(4);
            _minSameZone = EditorGUILayout.IntField("Minimum contiguous cut zone (MIN_SAME_ZONE)", _minSameZone);
            EditorGUILayout.HelpBox("At least one axis’ longest contiguous cuttable zone must be ≥ this to qualify for nine-slice (strong axis).", MessageType.None);

            EditorGUILayout.Space(4);
            _minCenterCols = EditorGUILayout.IntField("Max center columns (MIN_CENTER_COLS)", _minCenterCols);
            _minCenterRows = EditorGUILayout.IntField("Max center rows (MIN_CENTER_ROWS)", _minCenterRows);
            EditorGUILayout.HelpBox("With horizontal/vertical borders, center block uses the smaller of cap and croppable region; when an axis skips nine-slice, full width/height is kept.", MessageType.None);

            EditorGUILayout.Space(10);
            if (EditorGUI.EndChangeCheck())
                ApplyToPrefs();
            if (GUILayout.Button("Restore defaults", GUILayout.Height(28)))
            {
                _borderInset = PSDNineSlicePrefs.DefaultBorderInset;
                _pixelThreshold = PSDNineSlicePrefs.DefaultPixelThreshold;
                _minCenterCols = PSDNineSlicePrefs.DefaultMinCenterCols;
                _minCenterRows = PSDNineSlicePrefs.DefaultMinCenterRows;
                _minSameZone = PSDNineSlicePrefs.DefaultMinSameZone;
                ApplyToPrefs();
                Debug.Log("Nine-slice settings restored to defaults.");
            }
        }
    }

    /// <summary>Scrollable window for PSDCache name conflicts; <see cref="EditorUtility.DisplayDialog"/> cannot add a scroll bar.</summary>
    internal sealed class PsdCacheDuplicateNameDialogWindow : EditorWindow
    {
        public const string ExportAbortedIntro =
            "After slicing, PSDCache file name conflicts were detected; this export was aborted.\n\n" +
            "Rename layers or enable auto-naming, then export again. Details below are scrollable.";

        public const string DuplicatesRenamedIntro =
            "Auto-naming is off and multiple layers share the same name.\n\n" +
            "Duplicate filenames have been automatically renamed (\"layerName_1.png\", \"layerName_2.png\", …). " +
            "The export completed normally. The list below is scrollable.";

        private string _intro = "";
        private string _body = "";
        private Vector2 _scroll;

        public static void ShowWindow(string intro, string body)
            => ShowWindow(intro, body, "Export aborted");

        public static void ShowWindow(string intro, string body, string windowTitle)
        {
            var w = GetWindow<PsdCacheDuplicateNameDialogWindow>(true, windowTitle ?? "Export aborted", true);
            w._intro = intro ?? "";
            w._body = body ?? "";
            w._scroll = Vector2.zero;
            w.minSize = new Vector2(480, 300);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(_intro, EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Details", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            float wrapW = Mathf.Max(120f, EditorGUIUtility.currentViewWidth - 28f);
            EditorGUILayout.LabelField(
                string.IsNullOrEmpty(_body) ? "(none)" : _body,
                EditorStyles.wordWrappedLabel,
                GUILayout.MaxWidth(wrapW));
            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space(8);
            if (GUILayout.Button("OK", GUILayout.Height(28)))
                Close();
        }
    }

    // ─────────────────────── PrefabMap JSON types ───────────────────────

    /// <summary>One entry in the Prefab → PSD layer mapping table.</summary>
    [System.Serializable]
    public class PrefabMapEntry
    {
        /// <summary>Unity local file identifier (fileID) of the GameObject inside the Prefab asset.</summary>
        public long fileId;
        /// <summary>PSD layer ID.</summary>
        public int layerId;
        /// <summary>Layer name for readability.</summary>
        public string layerName;
        /// <summary>Hierarchy path inside the Prefab (e.g. "Root/Panel/Button").</summary>
        public string path;
        /// <summary>JSON string containing this node's own component snapshots ({ type, json }[]).</summary>
        public string componentsJson;
    }

    /// <summary>Root object saved to {psdName}_PrefabMap.json.</summary>
    [System.Serializable]
    public class PrefabMapData
    {
        /// <summary>Assets path of the Prefab this map belongs to.</summary>
        public string prefabPath;
        public PrefabMapEntry[] entries;
    }

} // namespace PsdTools
