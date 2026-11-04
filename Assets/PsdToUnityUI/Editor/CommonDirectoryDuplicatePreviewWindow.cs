using System.IO;
using UnityEditor;
using UnityEngine;

namespace PsdTools
{
    /// <summary>
    /// Side-by-side preview of two images and their resolution: for “common directory name clash” three-way choice,
    /// or “common dedup hit” two-way choice (keep common directory / replace with current export).
    /// </summary>
    public sealed class CommonDirectoryDuplicatePreviewWindow : EditorWindow
    {
        private enum WindowMode
        {
            DuplicateCollision,
            CommonDedup
        }

        public enum UserChoice
        {
            Overwrite,
            CancelExport,
            UseNewName
        }

        public enum CommonDedupUserChoice
        {
            ReuseCommonDirectoryImage,
            ReplaceWithCurrentImage
        }

        private WindowMode _mode;
        private Texture2D _leftTex;
        private bool _ownLeftTex;
        private Texture2D _rightTex;
        private string _leftPathOrHint;
        private string _rightCaption;
        private string _leftCaption;
        private string _fileName;
        private string _winnerLayerName;
        private int _groupMemberCount;
        private UserChoice? _choice;
        private CommonDedupUserChoice? _commonDedupChoice;
        private string _dedupHelpText;

        /// <summary>Blocking modal window until the user clicks a button or closes (close = cancel export).</summary>
        public static UserChoice ShowModal(string existingFullPath, Texture2D newImagePreview, string fileName,
            string winnerLayerName, int groupMemberCount)
        {
            var win = CreateInstance<CommonDirectoryDuplicatePreviewWindow>();
            win._mode = WindowMode.DuplicateCollision;
            win.InitDuplicate(existingFullPath, newImagePreview, fileName, winnerLayerName, groupMemberCount);
            win.ShowModalUtility();
            return win._choice ?? UserChoice.CancelExport;
        }

        /// <summary>When common dedup hits and the current export has higher resolution: left = common dir image, right = this export. Closing keeps the common directory asset.</summary>
        public static CommonDedupUserChoice ShowModalCommonDedup(string commonImageFullPath, Texture2D currentExportPreview,
            string winnerLayerName, int groupMemberCount)
        {
            var win = CreateInstance<CommonDirectoryDuplicatePreviewWindow>();
            win._mode = WindowMode.CommonDedup;
            win.InitCommonDedup(commonImageFullPath, currentExportPreview, winnerLayerName, groupMemberCount);
            win.ShowModalUtility();
            return win._commonDedupChoice ?? CommonDedupUserChoice.ReuseCommonDirectoryImage;
        }

        private void InitDuplicate(string existingFullPath, Texture2D newImagePreview, string fileName, string winnerLayerName,
            int groupMemberCount)
        {
            titleContent = new GUIContent("Common directory — duplicate filename");
            minSize = new Vector2(680, 480);

            _leftCaption = "Already in common directory";
            _rightCaption = "About to write (this export)";
            _leftPathOrHint = existingFullPath;
            _rightTex = newImagePreview;
            _fileName = fileName;
            _winnerLayerName = winnerLayerName;
            _groupMemberCount = groupMemberCount;
            _choice = null;
            _dedupHelpText = null;

            _ownLeftTex = false;
            _leftTex = null;
            TryLoadTextureFromFile(existingFullPath, out _leftTex, out _ownLeftTex);
        }

        private void InitCommonDedup(string commonImageFullPath, Texture2D currentExportPreview, string winnerLayerName,
            int groupMemberCount)
        {
            titleContent = new GUIContent("Common directory dedup");
            minSize = new Vector2(680, 480);

            _leftCaption = "Common directory (existing asset)";
            _rightCaption = "This export";
            _leftPathOrHint = commonImageFullPath;
            _rightTex = currentExportPreview;
            _fileName = Path.GetFileName(commonImageFullPath) ?? "";
            _winnerLayerName = winnerLayerName;
            _groupMemberCount = groupMemberCount;
            _commonDedupChoice = null;
            _dedupHelpText =
                "The common directory already has a pixel-identical image. This export has higher resolution; you can replace the file on disk or keep referencing the existing asset.";

            _ownLeftTex = false;
            _leftTex = null;
            TryLoadTextureFromFile(commonImageFullPath, out _leftTex, out _ownLeftTex);
        }

        private static void TryLoadTextureFromFile(string path, out Texture2D tex, out bool ownTex)
        {
            tex = null;
            ownTex = false;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return;
                byte[] bytes = File.ReadAllBytes(path);
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (t.LoadImage(bytes))
                {
                    tex = t;
                    ownTex = true;
                }
                else
                {
                    DestroyImmediate(t);
                }
            }
            catch
            {
                // ignored
            }
        }

        private void OnDestroy()
        {
            if (_ownLeftTex && _leftTex != null)
                DestroyImmediate(_leftTex);
        }

        private static void DrawPreviewColumn(string title, Texture2D tex)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Width(320));
            GUILayout.Label(title, EditorStyles.boldLabel);

            if (tex != null)
            {
                GUILayout.Label($"Resolution: {tex.width} × {tex.height}", EditorStyles.miniLabel);
                float maxSide = 240f;
                float scale = maxSide / Mathf.Max(1f, Mathf.Max(tex.width, tex.height));
                float pw = tex.width * scale;
                float ph = tex.height * scale;
                Rect r = GUILayoutUtility.GetRect(pw, ph, GUILayout.ExpandWidth(false));
                EditorGUI.DrawPreviewTexture(r, tex, null, ScaleMode.ScaleToFit);
            }
            else
            {
                GUILayout.Label("(Preview could not be loaded)", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        private void OnGUI()
        {
            if (_mode == WindowMode.DuplicateCollision)
                DrawDuplicateBody();
            else
                DrawCommonDedupBody();
        }

        private void DrawDuplicateBody()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Target file", _fileName, EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("Full path", string.IsNullOrEmpty(_leftPathOrHint) ? "" : _leftPathOrHint,
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("Representative layer", _winnerLayerName);
            if (_groupMemberCount > 1)
                EditorGUILayout.HelpBox($"{_groupMemberCount} nodes in this group share the same final asset path as the representative.", MessageType.Info);

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            DrawPreviewColumn(_leftCaption, _leftTex);
            GUILayout.Space(12);
            DrawPreviewColumn(_rightCaption, _rightTex);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField(
                "Choose: Overwrite replaces the file on disk; Use new name picks a non-conflicting name (e.g. xxx_1.png), and all nodes in the group reference that new file.",
                EditorStyles.wordWrappedMiniLabel);

            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            const int bw = 110;
            float bh = Mathf.Max(22f, EditorGUIUtility.singleLineHeight + 6f);
            if (GUILayout.Button("Overwrite", GUILayout.Width(bw), GUILayout.Height(bh)))
            {
                _choice = UserChoice.Overwrite;
                Close();
            }

            if (GUILayout.Button("Use new name", GUILayout.Width(bw), GUILayout.Height(bh)))
            {
                _choice = UserChoice.UseNewName;
                Close();
            }

            if (GUILayout.Button("Cancel export", GUILayout.Width(bw), GUILayout.Height(bh)))
            {
                _choice = UserChoice.CancelExport;
                Close();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(6);
        }

        private void DrawCommonDedupBody()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Representative layer", _winnerLayerName);
            if (_groupMemberCount > 1)
                EditorGUILayout.HelpBox($"{_groupMemberCount} nodes in this group share the same final asset path as the representative.", MessageType.Info);
            EditorGUILayout.LabelField("Common directory file", _fileName, EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("Full path", string.IsNullOrEmpty(_leftPathOrHint) ? "" : _leftPathOrHint,
                EditorStyles.wordWrappedMiniLabel);

            if (!string.IsNullOrEmpty(_dedupHelpText))
                EditorGUILayout.HelpBox(_dedupHelpText, MessageType.Info);

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            DrawPreviewColumn(_leftCaption, _leftTex);
            GUILayout.Space(12);
            DrawPreviewColumn(_rightCaption, _rightTex);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("“Use common directory image”: nothing is written; prefab keeps the existing path. “Use current image (replace)”: this export overwrites the PNG in the common directory.",
                EditorStyles.wordWrappedMiniLabel);

            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            float bh = Mathf.Max(22f, EditorGUIUtility.singleLineHeight + 6f);
            int bw1 = 180;
            int bw2 = 180;
            if (GUILayout.Button("Use common directory image", GUILayout.Width(bw1), GUILayout.Height(bh)))
            {
                _commonDedupChoice = CommonDedupUserChoice.ReuseCommonDirectoryImage;
                Close();
            }

            if (GUILayout.Button("Use current image (replace)", GUILayout.Width(bw2), GUILayout.Height(bh)))
            {
                _commonDedupChoice = CommonDedupUserChoice.ReplaceWithCurrentImage;
                Close();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(6);
        }
    }
} // namespace PsdTools
