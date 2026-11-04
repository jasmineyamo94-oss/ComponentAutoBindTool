using UnityEditor;
using UnityEngine;

namespace PsdTools
{
    /// <summary>
    /// Editor window: select two PNG files and compare them with PSDAutoPrefab's dedup algorithm
    /// (same fingerprint + MAE logic used during real exports).
    /// Open via Tools/PSD/Test: Dedup two images...
    /// </summary>
    public class DedupTestWindow : EditorWindow
    {
        private string _pathA = "";
        private string _pathB = "";
        private Texture2D _texA;
        private Texture2D _texB;

        private enum State { Idle, HasResult, Error }

        private State _state = State.Idle;
        private string _errorMsg = "";

        private float[] _fpA;
        private float[] _fpB;
        private float _mae;
        private bool _wouldDedup;

        private float _maeThreshold;
        private int _fingerprintSize;

        private Vector2 _scrollPos;

        [MenuItem("Tools/PSD/Test: Dedup two images...")]
        public static void ShowWindow()
        {
            var window = GetWindow<DedupTestWindow>("Dedup test");
            window.minSize = new Vector2(480, 460);
        }

        private void OnGUI()
        {
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Image Dedup Test", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Uses PSDAutoPrefab's ComputeFingerprint + MAE check (same parameters as a real export).",
                MessageType.None);
            EditorGUILayout.Space(4);

            DrawImagePicker("Image A", ref _pathA, ref _texA);
            DrawImagePicker("Image B", ref _pathB, ref _texB);

            EditorGUILayout.Space(8);

            bool canCompare = !string.IsNullOrEmpty(_pathA) && !string.IsNullOrEmpty(_pathB)
                              && _texA != null && _texB != null;

            using (new EditorGUI.DisabledScope(!canCompare))
            {
                if (GUILayout.Button("Compare", GUILayout.Height(28)))
                    RunCompare();
            }

            EditorGUILayout.Space(8);

            if (_state == State.Error)
            {
                EditorGUILayout.HelpBox(_errorMsg, MessageType.Error);
            }
            else if (_state == State.HasResult)
            {
                DrawResult();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawImagePicker(string label, ref string path, ref Texture2D tex)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(label);
                string display = string.IsNullOrEmpty(path) ? "(none)" : System.IO.Path.GetFileName(path);
                if (GUILayout.Button(display, EditorStyles.textField, GUILayout.ExpandWidth(true)))
                {
                    string chosen = EditorUtility.OpenFilePanel("Select image", Application.dataPath, "png");
                    if (!string.IsNullOrEmpty(chosen))
                    {
                        path = chosen;
                        tex = LoadPngAsTexture(chosen);
                        _state = State.Idle;
                    }
                }

                if (GUILayout.Button("...", GUILayout.Width(32)))
                {
                    string chosen = EditorUtility.OpenFilePanel("Select image", Application.dataPath, "png");
                    if (!string.IsNullOrEmpty(chosen))
                    {
                        path = chosen;
                        tex = LoadPngAsTexture(chosen);
                        _state = State.Idle;
                    }
                }
            }

            if (tex != null)
            {
                const float previewSize = 64f;
                Rect rect = GUILayoutUtility.GetRect(previewSize, previewSize,
                    GUILayout.Width(previewSize), GUILayout.Height(previewSize));
                rect.x += EditorGUIUtility.labelWidth + 4f;
                GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit, true);
                GUILayout.Space(previewSize + 4f - 20f);
            }
        }

        private void RunCompare()
        {
            try
            {
                _maeThreshold = PSDAutoPrefab.DedupMaeThresholdForTest;
                _fingerprintSize = PSDAutoPrefab.DedupFingerprintSizeForTest;

                _fpA = PSDAutoPrefab.ComputeFingerprintForTest(_texA);
                _fpB = PSDAutoPrefab.ComputeFingerprintForTest(_texB);

                float sumAbs = 0f;
                for (int index = 0; index < _fpA.Length; index++)
                    sumAbs += Mathf.Abs(_fpA[index] - _fpB[index]);
                _mae = sumAbs / _fpA.Length;

                _wouldDedup = PSDAutoPrefab.FingerprintsMatchForTest(_fpA, _fpB);
                _state = State.HasResult;
            }
            catch (System.Exception exception)
            {
                _errorMsg = exception.Message;
                _state = State.Error;
            }
        }

        private void DrawResult()
        {
            Color previousBackground = GUI.backgroundColor;
            GUI.backgroundColor = _wouldDedup
                ? new Color(0.55f, 0.9f, 0.55f)
                : new Color(0.95f, 0.55f, 0.45f);

            EditorGUILayout.HelpBox(
                _wouldDedup ? "WOULD DEDUP  (images treated as identical)" : "WOULD NOT DEDUP  (images are distinct)",
                _wouldDedup ? MessageType.None : MessageType.Warning);

            GUI.backgroundColor = previousBackground;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Details", EditorStyles.boldLabel);

            EditorGUILayout.LabelField(
                "Config snapshot",
                $"MAE threshold={_maeThreshold:F4}   fingerprint size={_fingerprintSize}x{_fingerprintSize}");
            EditorGUILayout.LabelField("Image A", $"size={_texA.width}x{_texA.height}");
            EditorGUILayout.LabelField("Image B", $"size={_texB.width}x{_texB.height}");
            EditorGUILayout.LabelField(
                "MAE",
                $"{_mae:F6}  (threshold {_maeThreshold:F4})  ->  {(_mae <= _maeThreshold ? "pass" : "fail")}");

            EditorGUILayout.Space(4);

            if (_fpA == null || _fpB == null)
                return;

            var diffs = new float[_fpA.Length];
            for (int index = 0; index < _fpA.Length; index++)
                diffs[index] = Mathf.Abs(_fpA[index] - _fpB[index]);

            var order = new int[diffs.Length];
            for (int index = 0; index < order.Length; index++)
                order[index] = index;
            System.Array.Sort(order, (left, right) => diffs[right].CompareTo(diffs[left]));

            EditorGUILayout.LabelField(
                "Top-8 fingerprint channel differences (index  A  B  |diff|):",
                EditorStyles.miniLabel);

            for (int rank = 0; rank < Mathf.Min(8, order.Length); rank++)
            {
                int index = order[rank];
                string channel = new[] { "R", "G", "B", "A" }[index % 4];
                int pixel = index / 4;
                int py = pixel / 8;
                int px = pixel % 8;
                EditorGUILayout.LabelField(
                    "  " +
                    $"[{index:D3}] pixel({px},{py}) {channel}  " +
                    $"A={_fpA[index]:F4}  B={_fpB[index]:F4}  |d|={diffs[index]:F4}",
                    EditorStyles.miniLabel);
            }
        }

        private static Texture2D LoadPngAsTexture(string absolutePath)
        {
            byte[] bytes = System.IO.File.ReadAllBytes(absolutePath);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes))
            {
                DestroyImmediate(texture);
                throw new System.Exception($"Failed to load image: {absolutePath}");
            }

            return texture;
        }
    }

    /// <summary>
    /// Utility to clear all EditorPrefs keys used by the PSD tools.
    /// Menu: Tools/PSD/Clear All PSD EditorPrefs
    /// </summary>
    public static class PsdEditorPrefsCleanup
    {
        // ── PSDEditorWindow prefs ──
        private static readonly string[] s_allKeys =
        {
            // PSDEditorWindow
            "PSDEditor_ExportAssetsFolder",          // legacy key
            "PSDEditor_ExportImageAssetsFolder",
            "PSDEditor_ExportPrefabAssetsFolder",
            "PSDEditor_ExportAutoImageNaming",
            "PSDEditor_ExportCompareNameDiff",
            "PSDEditor_AutoNavigateAfterExport",
            "PSDEditor_LiveComposite",
            "PSDEditor_DefaultUseTMP",
            "PSDEditor_DefaultSliceImage",
            "PSDEditor_DetectCommonDirLargerImage",
            "PSDEditor_UsePsdNodeOrder",
            "PSDEditor_ClearExportFolderBeforeExport",
            "PSDEditor_RecentFiles",

            // PSDProjectContextMenus
            "PSDEditor_SuppressAddCommonDirDialog",

            // PSDAutoPrefab — nine-slice
            PSDNineSlicePrefs.KeyBorderInset,
            PSDNineSlicePrefs.KeyPixelThreshold,
            PSDNineSlicePrefs.KeyMinCenterCols,
            PSDNineSlicePrefs.KeyMinCenterRows,
            PSDNineSlicePrefs.KeyMinSameZone,

            // PSDAutoPrefab — dedup
            PSDDedupPrefs.KeyMaeThreshold,
            PSDDedupPrefs.KeyFingerprintSize,
        };

        // [MenuItem("Tools/PSD/Clear All PSD EditorPrefs")]
        // public static void ClearAllPsdEditorPrefs()
        // {
        //     int cleared = 0;
        //     var deleted = new System.Text.StringBuilder();
        //     foreach (string key in s_allKeys)
        //     {
        //         if (EditorPrefs.HasKey(key))
        //         {
        //             EditorPrefs.DeleteKey(key);
        //             deleted.AppendLine($"  Deleted: {key}");
        //             cleared++;
        //         }
        //     }

        //     if (cleared == 0)
        //         Debug.Log("[PSD] No PSD EditorPrefs keys found to clear.");
        //     else
        //         Debug.Log($"[PSD] Cleared {cleared} EditorPrefs key(s):\n{deleted}");
        // }
    }
}