using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using PsdUiWorkbench.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace PsdUiWorkbench.AiNaming
{
    public sealed class PsdAiPreviewProvider : IPsdAiExportAwarePreviewProvider
    {
        public PsdAiPreviewResult Capture(PsdDocumentSnapshot document, IReadOnlyList<int> targets,
            PsdVisibilityState visibility, Func<string, Sprite> resolveSprite)
            => throw new PsdAiNamingException("真实 GPU 预览必须使用 CaptureIncrementally 按帧生成。");

        public IEnumerator CaptureIncrementally(PsdDocumentSnapshot document, IReadOnlyList<int> targets,
            PsdVisibilityState visibility, Func<string, Sprite> resolveSprite, Action<PsdAiPreviewResult> completed)
            => CaptureIncrementally(document, targets, visibility, new PsdExportState(), resolveSprite, completed);

        public IEnumerator CaptureIncrementally(PsdDocumentSnapshot document, IReadOnlyList<int> targets,
            PsdVisibilityState visibility, PsdExportState export, Func<string, Sprite> resolveSprite,
            Action<PsdAiPreviewResult> completed)
        {
            var images = new List<PsdAiImage>();
            var previewIds = new List<int>();
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                completed(new PsdAiPreviewResult(images, previewIds));
                yield break;
            }
            var sprites = new Dictionary<int, Sprite>();
            foreach (var node in document.DrawNodes.Where(n => export.Allows(document, n.Id)))
            {
                var sprite = resolveSprite(node.SpriteId);
                if (sprite != null) sprites[node.Id] = sprite;
            }
            if (sprites.Count == 0)
            {
                completed(new PsdAiPreviewResult(images, previewIds));
                yield break;
            }
            var shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                completed(new PsdAiPreviewResult(images, previewIds));
                yield break;
            }
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var hidden = new HashSet<int>(visibility.HiddenLayerIds);
                var visible = document.DrawNodes.Where(n => sprites.ContainsKey(n.Id) &&
                    IsVisible(document, n, visibility.DocumentVisible, hidden)).ToArray();
                if (visible.Length > 0)
                {
                    images.Add(new PsdAiImage("整图：当前显隐，透明背景，无棋盘格", Render(visible, sprites,
                        new PixelRect(0, 0, document.Width, document.Height), 1280, material)));
                    yield return null;
                }
                var scope = new HashSet<int>(targets);
                var local = document.DrawNodes.Where(n => sprites.ContainsKey(n.Id) && InScope(document, n, scope)).ToArray();
                if (local.Length > 0)
                {
                    PixelRect bounds = default;
                    foreach (var node in local) bounds = PixelRect.Union(bounds, node.Bounds);
                    images.Add(new PsdAiImage("目标范围：忽略显隐，不表示独立子层图像", Render(local, sprites, bounds, 1280, material)));
                    yield return null;
                }
                foreach (int id in targets)
                {
                    // 合并子层不在 DrawNodes 中；不得使用合并父层冒充独立图像。
                    var node = document.DrawNodes.FirstOrDefault(n => n.Id == id);
                    if (node == null || !sprites.ContainsKey(id)) continue;
                    int limit = targets.Count == 1 ? 1280 : 512;
                    images.Add(new PsdAiImage("独立节点 " + PsdAiNamingController.Key(id), Render(new[] { node }, sprites, node.Bounds, limit, material)));
                    previewIds.Add(id);
                    yield return null;
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
            completed(new PsdAiPreviewResult(images, previewIds));
        }
        private static bool InScope(PsdDocumentSnapshot document, PsdNode node, HashSet<int> targets)
        {
            for (var current = node; current != null; current = current.ParentId.HasValue ? document.Find(current.ParentId.Value) : null)
                if (targets.Contains(current.Id)) return true;
            return false;
        }
        private static bool IsVisible(PsdDocumentSnapshot document, PsdNode node, bool documentVisible, HashSet<int> hidden)
        {
            if (!documentVisible) return false;
            for (var current = node; current != null; current = current.ParentId.HasValue ? document.Find(current.ParentId.Value) : null)
                if (hidden.Contains(current.Id)) return false;
            return true;
        }
        private static byte[] Render(IReadOnlyList<PsdNode> nodes, Dictionary<int, Sprite> sprites, PixelRect bounds, int maxSize, Material material)
        {
            if (!bounds.IsValid) throw new PsdAiNamingException("预览边界无效。");
            float scale = Mathf.Min(1f, maxSize / Mathf.Max(bounds.Width, bounds.Height));
            int width = Mathf.Max(1, Mathf.CeilToInt(bounds.Width * scale));
            int height = Mathf.Max(1, Mathf.CeilToInt(bounds.Height * scale));
            var previous = RenderTexture.active;
            RenderTexture temporary = null;
            Texture2D readback = null;
            bool pushed = false;
            try
            {
                temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                RenderTexture.active = temporary;
                GL.Clear(true, true, Color.clear);
                GL.PushMatrix(); pushed = true;
                GL.LoadPixelMatrix(0, width, height, 0);
                foreach (var node in nodes)
                {
                    var sprite = sprites[node.Id];
                    material.mainTexture = sprite.texture;
                    if (!material.SetPass(0)) throw new PsdAiNamingException("无法绘制独立预览。");
                    var vertices = sprite.vertices;
                    var uv = sprite.uv;
                    var triangles = sprite.triangles;
                    var rect = sprite.rect;
                    GL.Begin(GL.TRIANGLES);
                    try
                    {
                        GL.Color(Color.white);
                        foreach (var index in triangles)
                        {
                            var point = vertices[index] * sprite.pixelsPerUnit + sprite.pivot;
                            float x = node.Bounds.X + point.x / rect.width * node.Bounds.Width;
                            float y = node.Bounds.Y + (1f - point.y / rect.height) * node.Bounds.Height;
                            GL.TexCoord2(uv[index].x, uv[index].y);
                            GL.Vertex3((x - bounds.X) / bounds.Width * width, (y - bounds.Y) / bounds.Height * height, 0);
                        }
                    }
                    finally { GL.End(); }
                }
                GL.PopMatrix(); pushed = false;
                readback = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readback.Apply(false, false);
                return readback.EncodeToPNG();
            }
            finally
            {
                if (pushed) GL.PopMatrix();
                RenderTexture.active = previous;
                if (readback != null) UnityEngine.Object.DestroyImmediate(readback);
                if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
            }
        }
    }
}
