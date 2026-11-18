using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Infrastructure;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace PsdUiWorkbench.Tests
{
    // 真实 PSD 审计数据的验证入口；不把离线 JSON 当成生产 Importer 缓存。
    public static class PsdTmpCandidateExport
    {
        public const string SourcePath = "Assets/Psd/战斗-主菜单界面.psd";
        private const string FontPath = "Assets/AAGame/UI/Fonts/Toxia_FRE.ttf";
        private const string InventoryPath = "Assets/PsdUiWorkbench/Documentation/PsdTextAudit/inventory.json";

        [Serializable]
        public sealed class Report
        {
            public string prefabPath, outputFolder, fontPath;
            public int textLayers, textComponents, placeholders, images, sourceGlyphs;
            public bool sourceUnchanged, metaUnchanged, defaultFontUnchanged, visibilityUnchanged;
        }

        public static IReadOnlyDictionary<int, PsdTextSource> ReadSources()
        {
            var input = JsonUtility.FromJson<PsdTextReferenceValidation.Inventory>(File.ReadAllText(InventoryPath));
            if (Hash(SourcePath) != input.sha256 || Hash(SourcePath + ".meta") != input.metaSha256)
                throw new InvalidOperationException("源 PSD 或 .meta 已变化，需要重新生成当前验证样本的审计数据。");
            return input.jobs.ToDictionary(job => job.id, job =>
            {
                var spans = new List<PsdTextSourceSpan>();
                foreach (var segment in job.segments)
                {
                    int length = Math.Min(segment.length, job.text.Length - segment.start);
                    if (length <= 0) continue;
                    var style = new PsdTextStyle(segment.font, job.fontSizeUi, job.fauxBold, job.tracking,
                        ColorOf(job.color), job.strokePixels, ColorOf(job.strokeColor));
                    spans.Add(new PsdTextSourceSpan(segment.start, length, style));
                }
                return new PsdTextSource(job.id, job.text, job.originX, job.originY,
                    job.justification == 2 ? PsdTextAlignment.Center : PsdTextAlignment.Left, spans);
            });
        }

        public static Report Export(string parentFolder = "Assets/PsdUiWorkbench/Exports", PsdVisibilityState visibility = null)
        {
            var texts = ReadSources();
            var source = new UnityPsdDocumentSource();
            string guid = AssetDatabase.AssetPathToGUID(SourcePath);
            var loaded = source.Load(guid);
            if (loaded.Document == null) throw new InvalidOperationException(loaded.Diagnostic);
            string before = Hash(SourcePath), meta = Hash(SourcePath + ".meta");
            string defaultPath = AssetDatabase.GetAssetPath(TMP_Settings.defaultFontAsset), defaultHash = Hash(defaultPath);
            string configPath = PsdVisibilityRepository.GetConfigPath(guid);
            string config = File.Exists(configPath) ? Hash(configPath) : null;
            if (visibility == null)
            {
                using var repository = new PsdVisibilityRepository();
                visibility = repository.Load(guid);
            }
            var plan = PsdPrefabPlanner.Build(loaded.Document, visibility);
            EnsureFolder(parentFolder);
            string folder = AssetDatabase.GenerateUniqueAssetPath(parentFolder + "/战斗主菜单 TMP候选");
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parentFolder, Path.GetFileName(folder)))) throw new IOException("候选输出目录创建失败。");
            bool complete = false;
            try
            {
                string fontPath = folder + "/TOXIA Candidate.asset";
                var font = CreateFont(fontPath, texts.Values);
                var fonts = new Dictionary<string, TMP_FontAsset> { { "TOXIA", font } };
                var result = PsdTmpPrefabExporter.Export(plan, texts, fonts, TMP_Settings.defaultFontAsset,
                    folder + "/战斗-主菜单界面_TMP候选.prefab", source.Resolve, PsdTmpRenderingMode.Standard);
                var report = new Report
                {
                    prefabPath = result.AssetPath, outputFolder = folder, fontPath = fontPath,
                    textLayers = result.TextLayers, textComponents = result.TextComponents, placeholders = result.PlaceholderRuns,
                    images = result.ImageCount, sourceGlyphs = font.glyphTable.Count,
                    sourceUnchanged = Hash(SourcePath) == before, metaUnchanged = Hash(SourcePath + ".meta") == meta,
                    defaultFontUnchanged = Hash(defaultPath) == defaultHash,
                    visibilityUnchanged = (File.Exists(configPath) ? Hash(configPath) : null) == config
                };
                if (!report.sourceUnchanged || !report.metaUnchanged || !report.defaultFontUnchanged || !report.visibilityUnchanged)
                    throw new InvalidOperationException("候选导出改变了源资源或显隐配置。");
                complete = true;
                return report;
            }
            finally { if (!complete) AssetDatabase.DeleteAsset(folder); }
        }

        public static string Run()
        {
            var report = Export();
            File.WriteAllText("Assets/PsdUiWorkbench/Documentation/PsdTextAudit/tmp-candidate.json", JsonUtility.ToJson(report, true));
            EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(report.prefabPath));
            return JsonUtility.ToJson(report);
        }

        public static string Capture(string prefabPath, string output = "Assets/PsdUiWorkbench/Documentation/PsdTextAudit/tmp-candidate-render.png", Action<GameObject> prepare = null)
        {
            if (!output.StartsWith("Assets/PsdUiWorkbench/Documentation/", StringComparison.Ordinal) || output.Contains("..") || !output.EndsWith(".png", StringComparison.Ordinal))
                throw new ArgumentException("验证截图需保存在工作台说明目录内。");
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject root = null, cameraObject = null;
            RenderTexture target = null;
            Texture2D readback = null;
            var previous = RenderTexture.active;
            try
            {
                target = new RenderTexture(1080, 2400, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear); target.Create();
                cameraObject = new GameObject("候选截图相机", typeof(Camera));
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, scene);
                var camera = cameraObject.GetComponent<Camera>(); camera.scene = scene;
                camera.targetTexture = target; camera.orthographic = true; camera.orthographicSize = 1200;
                camera.transform.position = new Vector3(0, 0, -10); camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 1);
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (asset == null) throw new ArgumentException("候选 Prefab 不存在。");
                root = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene); root.SetActive(false);
                var canvas = root.GetComponent<Canvas>(); var scaler = root.GetComponent<UnityEngine.UI.CanvasScaler>();
                canvas.enabled = false; scaler.enabled = false;
                canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = 1;
                root.transform.localScale = Vector3.one; root.SetActive(true); canvas.enabled = true; scaler.enabled = true;
                prepare?.Invoke(root);
                Canvas.ForceUpdateCanvases();
                foreach (var text in root.GetComponentsInChildren<TextMeshProUGUI>(true)) text.ForceMeshUpdate(true, true);
                Canvas.ForceUpdateCanvases(); camera.Render();
                RenderTexture.active = target;
                readback = new Texture2D(1080, 2400, TextureFormat.RGBA32, false, true);
                readback.ReadPixels(new Rect(0, 0, 1080, 2400), 0, 0); readback.Apply();
                File.WriteAllBytes(output, readback.EncodeToPNG());
                return output;
            }
            finally
            {
                RenderTexture.active = previous;
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
                if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                if (readback != null) UnityEngine.Object.DestroyImmediate(readback);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static TMP_FontAsset CreateFont(string path, IEnumerable<PsdTextSource> sources)
        {
            var original = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            var required = string.Concat(sources.SelectMany(s => s.Spans.Where(r => r.Style.FontKey == "TOXIA").Select(r => s.Text.Substring(r.Start, r.Length))));
            string ascii = new string(Enumerable.Range(32, 95).Where(c => original.HasCharacter((char)c)).Select(c => (char)c).ToArray());
            var font = TMP_FontAsset.CreateFontAsset(original, 96, 24, GlyphRenderMode.SDFAA, 2048, 2048, AtlasPopulationMode.Dynamic, false);
            if (font == null) throw new InvalidOperationException("候选字体创建失败。");
            try
            {
                if (!font.TryAddCharacters(new string((required + ascii).Distinct().ToArray()), out string missing))
                    throw new InvalidOperationException("候选字体缺字：" + missing);
                font.atlasTexture.Apply(false, false);
                font.atlasPopulationMode = AtlasPopulationMode.Static;
                font.name = "TOXIA Workbench Candidate";
                font.material.name = "TOXIA Candidate Default";
                AssetDatabase.CreateAsset(font, path);
                AssetDatabase.AddObjectToAsset(font.material, font);
                foreach (var atlas in font.atlasTextures)
                {
                    atlas.name = "TOXIA Candidate Atlas"; AssetDatabase.AddObjectToAsset(atlas, font);
                }
                EditorUtility.SetDirty(font); AssetDatabase.SaveAssetIfDirty(font);
                return font;
            }
            catch { if (!EditorUtility.IsPersistent(font)) UnityEngine.Object.DestroyImmediate(font); throw; }
        }

        private static void EnsureFolder(string folder)
        {
            if (!folder.StartsWith("Assets/PsdUiWorkbench/", StringComparison.Ordinal) || folder.Contains("..") || folder.Contains('\\'))
                throw new ArgumentException("候选验证输出必须在工作台目录内。");
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(folder)))) throw new IOException("输出父目录创建失败。");
        }
        private static PsdTextColor ColorOf(float[] c) => new PsdTextColor(c[0], c[1], c[2], c[3]);
        private static string Hash(string path)
        {
            using var sha = SHA256.Create(); using var stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
