using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PsdTools
{
    /// <summary>
    /// Right-click context menu items for the Unity Project window.
    /// "PsdToUnityUI/Add to Common Dir" — adds the selected folder to the common directories list
    /// used by the PSD Editor for dedup / save-to-common-dir.
    /// </summary>
    public static class PSDProjectContextMenus
    {
        private const string MenuPath = "Assets/PsdToUnityUI/Add to Common Dir";
        private const string PrefSuppressAddCommonDirDialog = "PSDEditor_SuppressAddCommonDirDialog";

        private static string CommonDirectoriesConfigPath =>
            Path.Combine(Application.dataPath, "PsdToUnityUI", "EditorConfig", "PSD_CommonDirectories.json");

        // ── Context menu entry ────────────────────────────────────────────────

        [MenuItem(MenuPath, false, 1000)]
        private static void AddToCommonDir()
        {
            string assetPath = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(assetPath)) return;

            // Normalise separators and strip trailing slash
            string normalizedPath = assetPath.Replace('\\', '/').TrimEnd('/');

            // Load existing list
            var list = LoadCommonDirList();

            // Reject duplicates (case-insensitive)
            foreach (string existing in list)
            {
                if (string.Equals(existing.Replace('\\', '/').TrimEnd('/'), normalizedPath,
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    EditorUtility.DisplayDialog(
                        "Already in Common Directories",
                        $"\"{normalizedPath}\" is already in the common directories list.",
                        "OK");
                    return;
                }
            }

            // Add, save, and notify
            list.Add(normalizedPath);
            SaveCommonDirList(list);

            bool suppressed = EditorPrefs.GetBool(PrefSuppressAddCommonDirDialog, false);
            if (!suppressed)
            {
                AddCommonDirSuccessPopup.Show(
                    $"\"{normalizedPath}\" has been added to the common directories list.\n\n" +
                    "Please reopen PSD Editor for the change to take effect.");
            }
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateAddToCommonDir()
        {
            if (Selection.activeObject == null) return false;
            string assetPath = AssetDatabase.GetAssetPath(Selection.activeObject);
            return !string.IsNullOrEmpty(assetPath) && AssetDatabase.IsValidFolder(assetPath);
        }

        // ── Config helpers ────────────────────────────────────────────────────

        private static List<string> LoadCommonDirList()
        {
            var result = new List<string>();
            string configPath = CommonDirectoriesConfigPath;
            if (!File.Exists(configPath)) return result;
            try
            {
                string json = File.ReadAllText(configPath);
                var data = JsonUtility.FromJson<CommonDirectoriesData>(json);
                if (data?.paths != null)
                    foreach (string p in data.paths)
                        if (!string.IsNullOrWhiteSpace(p))
                            result.Add(p.Trim());
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AddToCommonDir] Failed to load common directories: {ex.Message}");
            }
            return result;
        }

        private static void SaveCommonDirList(List<string> list)
        {
            string configPath = CommonDirectoriesConfigPath;
            try
            {
                string dir = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var data = new CommonDirectoriesData { paths = list.ToArray() };
                File.WriteAllText(configPath, JsonUtility.ToJson(data, true));
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[AddToCommonDir] Failed to save common directories: {ex.Message}");
            }
        }
    }

    // ── Success notification popup ────────────────────────────────────────────

    /// <summary>
    /// Small modal popup shown after a directory is successfully added to the common directories list.
    /// Contains a "Don't show again" toggle that persists via EditorPrefs.
    /// </summary>
    internal sealed class AddCommonDirSuccessPopup : EditorWindow
    {
        private const string PrefSuppressAddCommonDirDialog = "PSDEditor_SuppressAddCommonDirDialog";

        private string _message;
        private bool _dontShowAgain;

        /// <summary>Show the popup modally. Blocks until the user dismisses it.</summary>
        public static void Show(string message)
        {
            var win = CreateInstance<AddCommonDirSuccessPopup>();
            win._message = message;
            win._dontShowAgain = false;
            win.titleContent = new GUIContent("PSD Editor — Common Dir Updated");
            win.minSize = new Vector2(420, 130);
            win.maxSize = new Vector2(560, 160);
            win.ShowModalUtility();
        }

        private void OnGUI()
        {
            GUILayout.Space(14);
            EditorGUILayout.LabelField(_message, EditorStyles.wordWrappedLabel);
            GUILayout.Space(10);
            _dontShowAgain = EditorGUILayout.ToggleLeft("Don't show this again", _dontShowAgain);
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("OK", GUILayout.Width(80)))
            {
                if (_dontShowAgain)
                    EditorPrefs.SetBool(PrefSuppressAddCommonDirDialog, true);
                Close();
            }
            GUILayout.Space(10);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(10);
        }
    }
}
