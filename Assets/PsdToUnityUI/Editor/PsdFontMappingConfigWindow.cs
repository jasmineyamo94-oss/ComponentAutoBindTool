using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
#if USE_TMP
using TMPro;
#endif

namespace PsdTools
{
    /// <summary>Editor window for mapping PSD font names to Unity Text / TMP font assets.</summary>
    public class PsdFontMappingConfigWindow : EditorWindow
    {
        private List<PsdFontMappingEntry> _rows = new List<PsdFontMappingEntry>();
        private Vector2 _scroll;
        private bool _dirty;

        [MenuItem("Tools/PSD/Font Mapping...")]
        public static void Open()
        {
            var w = GetWindow<PsdFontMappingConfigWindow>("PSD Font Mapping");
            w.minSize = new Vector2(520, 280);
            w.Show();
        }

        private void OnEnable()
        {
            ReloadFromDisk();
        }

        private void ReloadFromDisk()
        {
            var data = PsdFontMappingConfig.Load();
            _rows = new List<PsdFontMappingEntry>();
            if (data.entries != null)
            {
                foreach (var e in data.entries)
                {
                    _rows.Add(new PsdFontMappingEntry
                    {
                        psdFontName = e.psdFontName ?? "",
                        legacyFontAssetPath = e.legacyFontAssetPath ?? "",
                        tmpFontAssetPath = e.tmpFontAssetPath ?? ""
                    });
                }
            }
            _dirty = false;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Map font names from the PSD to font assets in the project.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.HelpBox(
                "Configuration is saved under Assets/PsdToUnityUI/EditorConfig/PSD_FontMapping.json. Export logic uses it to assign Text / TextMeshPro fonts.",
                MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Reload from disk", GUILayout.Width(120)))
                ReloadFromDisk();
            if (GUILayout.Button("Add row", GUILayout.Width(80)))
            {
                _rows.Add(new PsdFontMappingEntry());
                _dirty = true;
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawHeaderRow();
            for (int i = 0; i < _rows.Count; i++)
                DrawRow(i);
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"{_rows.Count} entries · Config: {PsdFontMappingConfig.ConfigPath}", EditorStyles.miniLabel);

            if (_dirty)
                SaveToDisk();
        }

        private static void DrawHeaderRow()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("PSD font name", EditorStyles.miniBoldLabel, GUILayout.MinWidth(120));
            GUILayout.Label("Text font", EditorStyles.miniBoldLabel, GUILayout.MinWidth(140));
            GUILayout.Label("TextMeshPro font", EditorStyles.miniBoldLabel, GUILayout.MinWidth(140));
            GUILayout.Label("", GUILayout.Width(56));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        private void DrawRow(int index)
        {
            PsdFontMappingEntry row = _rows[index];
            EditorGUILayout.BeginHorizontal();

            string newName = EditorGUILayout.TextField(row.psdFontName, GUILayout.MinWidth(120));
            if (newName != row.psdFontName)
            {
                row.psdFontName = newName;
                _dirty = true;
            }

            Font legacy = string.IsNullOrEmpty(row.legacyFontAssetPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<Font>(row.legacyFontAssetPath);
            Font newLegacy = (Font)EditorGUILayout.ObjectField(legacy, typeof(Font), false, GUILayout.MinWidth(140));
            string legacyPath = newLegacy != null ? AssetDatabase.GetAssetPath(newLegacy) : "";
            if (legacyPath != row.legacyFontAssetPath)
            {
                row.legacyFontAssetPath = legacyPath;
                _dirty = true;
            }

    #if USE_TMP
            TMP_FontAsset tmp = string.IsNullOrEmpty(row.tmpFontAssetPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(row.tmpFontAssetPath);
            TMP_FontAsset newTmp = (TMP_FontAsset)EditorGUILayout.ObjectField(tmp, typeof(TMP_FontAsset), false, GUILayout.MinWidth(140));
            string tmpPath = newTmp != null ? AssetDatabase.GetAssetPath(newTmp) : "";
    #else
            EditorGUILayout.LabelField("(TMP not referenced)", GUILayout.MinWidth(140));
            string tmpPath = row.tmpFontAssetPath;
    #endif
            if (tmpPath != row.tmpFontAssetPath)
            {
                row.tmpFontAssetPath = tmpPath;
                _dirty = true;
            }

            if (GUILayout.Button("Delete", GUILayout.Width(48)))
            {
                _rows.RemoveAt(index);
                _dirty = true;
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void SaveToDisk()
        {
            var data = new PsdFontMappingData { entries = _rows.ToArray() };
            PsdFontMappingConfig.Save(data);
            _dirty = false;
            Debug.Log($"Font mapping saved: {PsdFontMappingConfig.ConfigPath}");
        }
    }
} // namespace PsdTools
