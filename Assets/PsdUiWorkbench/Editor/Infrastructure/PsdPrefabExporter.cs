using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PsdUiWorkbench.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PsdUiWorkbench.Infrastructure
{
    public sealed class PsdPrefabExportResult
    {
        public string AssetPath { get; }
        public int NodeCount { get; }
        public int ImageCount { get; }
        public string Diagnostic { get; }
        internal PsdPrefabExportResult(string path, PsdPrefabPlan plan)
        {
            AssetPath = path; NodeCount = plan.Nodes.Count; ImageCount = plan.ImageCount;
            Diagnostic = string.Join("\n", plan.Skipped.Select(n => $"跳过 {n.Name}（ID {n.Id}）：{n.Reason}"));
        }
    }

    public static class PsdPrefabExporter
    {
        public static PsdPrefabExportResult Export(PsdPrefabPlan plan, string assetPath, Func<string, Sprite> resolveSprite)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (resolveSprite == null) throw new ArgumentNullException(nameof(resolveSprite));
            string projectRoot = Path.GetFullPath("Assets");
            string full = string.IsNullOrWhiteSpace(assetPath) ? "" : Path.GetFullPath(assetPath);
            if (string.IsNullOrWhiteSpace(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal) || !full.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(assetPath), ".prefab", StringComparison.OrdinalIgnoreCase) || assetPath.Contains('\\'))
                throw new ArgumentException("请选择 Assets 目录内的 .prefab 路径。", nameof(assetPath));
            var sprites = new Dictionary<int, Sprite>();
            foreach (var node in plan.Nodes.Where(n => n.IsImage))
            {
                var sprite = resolveSprite(node.SpriteId);
                if (sprite == null || !EditorUtility.IsPersistent(sprite))
                    throw new InvalidOperationException($"无法解析持久化 Sprite：{node.Name}（ID {node.Id}）。");
                sprites.Add(node.Id, sprite);
            }
            string directory = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            EnsureFolder(directory);
            string output = AssetDatabase.GenerateUniqueAssetPath(assetPath);
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject root = null;
            bool saved = false;
            try
            {
                root = new GameObject(plan.RootName, typeof(RectTransform));
                root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root, scene);
                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.pixelPerfect = false;
                var scaler = root.AddComponent<UnityEngine.UI.CanvasScaler>();
                scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(plan.Width, plan.Height);
                scaler.screenMatchMode = UnityEngine.UI.CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;
                scaler.referencePixelsPerUnit = 100;
                root.AddComponent<UnityEngine.UI.GraphicRaycaster>();
                var rootRect = (RectTransform)root.transform;
                rootRect.localScale = Vector3.one;
                rootRect.localRotation = Quaternion.identity;
                rootRect.anchorMin = rootRect.anchorMax = rootRect.pivot = new Vector2(0.5f, 0.5f);
                rootRect.sizeDelta = new Vector2(plan.Width, plan.Height);
                rootRect.anchoredPosition = Vector2.zero;
                var content = NewRect("Content", root.transform, plan.Width, plan.Height, 0, 0);
                var transforms = new Dictionary<int, RectTransform>();
                foreach (var node in plan.Nodes)
                {
                    Transform parent = node.ParentId.HasValue ? transforms[node.ParentId.Value] : content;
                    var rect = NewRect(node.Name, parent, node.Bounds.Width, node.Bounds.Height, node.LocalX, node.LocalY);
                    rect.SetSiblingIndex(node.SiblingIndex);
                    transforms.Add(node.Id, rect);
                    if (node.IsImage)
                    {
                        var image = rect.gameObject.AddComponent<UnityEngine.UI.Image>();
                        image.sprite = sprites[node.Id];
                        image.type = UnityEngine.UI.Image.Type.Simple;
                        image.color = Color.white;
                        image.preserveAspect = false;
                        image.useSpriteMesh = false;
                        image.raycastTarget = false;
                    }
                    rect.gameObject.SetActive(node.ActiveSelf);
                }
                content.gameObject.SetActive(plan.ContentActive);
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, output, out bool success);
                if (!success || prefab == null) throw new IOException("Prefab 保存失败：" + output);
                prefab.SetActive(true);
                PrefabUtility.SavePrefabAsset(prefab, out bool activated);
                if (!activated) throw new IOException("Prefab 根节点激活状态保存失败：" + output);
                saved = true;
                return new PsdPrefabExportResult(output, plan);
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                EditorSceneManager.ClosePreviewScene(scene);
                if (!saved && (File.Exists(output) || File.Exists(output + ".meta"))) AssetDatabase.DeleteAsset(output);
            }
        }
        private static RectTransform NewRect(string name, Transform parent, float width, float height, float x, float y)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(x, y);
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
            return rect;
        }
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(path)))) throw new IOException("无法创建导出目录：" + path);
        }
    }
}
