using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.AiNaming;
using PsdUiWorkbench.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdAiPreviewTests
    {
        private static readonly Color32 Red = new Color32(255, 0, 0, 255);
        private static readonly Color32 Green = new Color32(0, 255, 0, 255);
        private static readonly Color32 Blue = new Color32(0, 0, 255, 255);
        private static readonly Color32 Yellow = new Color32(255, 255, 0, 255);

        private sealed class AtlasFixture : IDisposable
        {
            public readonly Texture2D Texture;
            public readonly Sprite Sprite;

            public AtlasFixture()
            {
                Texture = new Texture2D(8, 8, TextureFormat.RGBA32, false)
                {
                    name = "AI预览测试图集",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                // 图集其他位置故意用洋红色，错误地采样整张 atlas 会立即被检测到。
                var pixels = Enumerable.Repeat(new Color32(255, 0, 255, 255), 64).ToArray();
                // Sprite rect 为 atlas 的 (3,2)-(5,4)，下方蓝/黄，上方红/绿。
                pixels[2 * 8 + 3] = Blue;
                pixels[2 * 8 + 4] = Yellow;
                pixels[3 * 8 + 3] = Red;
                pixels[3 * 8 + 4] = Green;
                Texture.SetPixels32(pixels);
                Sprite = Sprite.Create(Texture, new Rect(3, 2, 2, 2), new Vector2(0.25f, 0.75f),
                    37f, 0, SpriteMeshType.FullRect);
                Sprite.name = "偏心Pivot图集节点";
                Sprite.hideFlags = HideFlags.HideAndDontSave;
                // 测试开始前彻底丢弃 CPU 像素；被测实现不能依赖或重设 readable。
                Texture.Apply(false, true);
            }

            public void Dispose()
            {
                if (Sprite != null) UnityEngine.Object.DestroyImmediate(Sprite);
                if (Texture != null) UnityEngine.Object.DestroyImmediate(Texture);
            }
        }

        [SetUp]
        public void RequireGraphicsDevice()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("当前测试进程没有图形设备，跳过 GPU 预览验收。");
            Assert.That(Shader.Find("Sprites/Default"), Is.Not.Null, "缺少预览所需内置 Sprite shader。");
        }

        private static PsdDocumentSnapshot Document() => new PsdDocumentSnapshot("preview-fixture", 10, 8,
            new[]
            {
                new PsdNode(17, null, "Fixture", false, true, true, false, "fixtureSprite",
                    new PixelRect(2, 1, 4, 4), 0, PsdLayerKind.NonText)
            });

        private static Texture2D Decode(PsdAiImage image)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            try
            {
                Assert.That(ImageConversion.LoadImage(texture, Convert.FromBase64String(image.Base64Png), false), Is.True);
                return texture;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(texture);
                throw;
            }
        }

        private static void AssertPixel(Texture2D image, int x, int y, Color32 expected)
        {
            Color32 actual = image.GetPixel(x, y);
            string location = "PNG 像素 (" + x + "," + y + ")";
            Assert.That((int)actual.a, Is.EqualTo((int)expected.a).Within(2), location + " 透明度");
            // 透明像素的 RGB 不属于视觉语义，避免约束驱动对透明颜色的处理。
            if (expected.a == 0) return;
            Assert.That((int)actual.r, Is.EqualTo((int)expected.r).Within(2), location + " R");
            Assert.That((int)actual.g, Is.EqualTo((int)expected.g).Within(2), location + " G");
            Assert.That((int)actual.b, Is.EqualTo((int)expected.b).Within(2), location + " B");
        }

        [UnityTest]
        public IEnumerator UnreadableAtlasPreservesWholeAndSingleNodeCoordinatesAndTransparentBoundary()
        {
            using (var fixture = new AtlasFixture())
            {
                Assert.That(fixture.Texture.isReadable, Is.False);
                var previous = RenderTexture.active;
                var sentinel = new RenderTexture(3, 3, 0, RenderTextureFormat.ARGB32);
                IEnumerator capture = null;
                Texture2D whole = null, single = null;
                try
                {
                    Assert.That(sentinel.Create(), Is.True);
                    RenderTexture.active = sentinel;
                    PsdAiPreviewResult result = null;
                    capture = new PsdAiPreviewProvider().CaptureIncrementally(Document(), new[] { 17 },
                        new PsdVisibilityState(), id => id == "fixtureSprite" ? fixture.Sprite : null,
                        value => result = value);
                    int renderedFrames = 0;
                    while (true)
                    {
                        // 两帧之间 Editor 自身可能绘制其他窗口，每次推进前重新设置哨兵。
                        RenderTexture.active = sentinel;
                        if (!capture.MoveNext()) break;
                        renderedFrames++;
                        Assert.That(renderedFrames, Is.LessThanOrEqualTo(3), "整图、范围、单节点分别在独立帧生成。");
                        Assert.That(result, Is.Null, "所有预览生成前不能提前完成。");
                        Assert.That(RenderTexture.active == sentinel, Is.True);
                        Assert.That(fixture.Texture.isReadable, Is.False);
                        yield return null;
                    }
                    Assert.That(result, Is.Not.Null);
                    Assert.That(renderedFrames, Is.EqualTo(3));
                    Assert.That(RenderTexture.active == sentinel, Is.True);
                    whole = Decode(result.Images.Single(image => image.Label.StartsWith("整图", StringComparison.Ordinal)));
                    single = Decode(result.Images.Single(image => image.Label == "独立节点 n17"));
                    Assert.That(whole.width, Is.EqualTo(10));
                    Assert.That(whole.height, Is.EqualTo(8));
                    Assert.That(single.width, Is.EqualTo(4));
                    Assert.That(single.height, Is.EqualTo(4));

                    // 独立的已知 PNG 坐标：节点覆盖 x=2..5、y=3..6；不调用生产坐标换算。
                    // 上两行红/绿，下两行蓝/黄；节点四周每个像素都必须保持透明。
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 10; x++)
                        {
                            Color32 expected = new Color32(0, 0, 0, 0);
                            if (x >= 2 && x <= 5 && y >= 3 && y <= 6)
                                expected = y >= 5 ? (x <= 3 ? Red : Green) : (x <= 3 ? Blue : Yellow);
                            AssertPixel(whole, x, y, expected);
                        }
                    // 单节点无额外 padding，四个边角与已知 2x2 象限一致，不能翻转或错采 atlas。
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                            AssertPixel(single, x, y, y >= 2 ? (x < 2 ? Red : Green) : (x < 2 ? Blue : Yellow));
                    Assert.That(fixture.Texture.isReadable, Is.False);
                }
                finally
                {
                    (capture as IDisposable)?.Dispose();
                    if (whole != null) UnityEngine.Object.DestroyImmediate(whole);
                    if (single != null) UnityEngine.Object.DestroyImmediate(single);
                    RenderTexture.active = previous;
                    sentinel.Release();
                    UnityEngine.Object.DestroyImmediate(sentinel);
                }
            }
        }

        [Test]
        public void DisposeAfterFirstFrameDestroysOwnMaterialRestoresTargetAndDoesNotComplete()
        {
            using (var fixture = new AtlasFixture())
            {
                var previous = RenderTexture.active;
                var sentinel = new RenderTexture(3, 3, 0, RenderTextureFormat.ARGB32);
                IEnumerator capture = null;
                try
                {
                    Assert.That(sentinel.Create(), Is.True);
                    RenderTexture.active = sentinel;
                    var originalMaterials = new HashSet<int>(Resources.FindObjectsOfTypeAll<Material>().Select(material => material.GetInstanceID()));
                    int completions = 0;
                    capture = new PsdAiPreviewProvider().CaptureIncrementally(Document(), new[] { 17 },
                        new PsdVisibilityState(), id => id == "fixtureSprite" ? fixture.Sprite : null,
                        _ => completions++);
                    Assert.That(capture.MoveNext(), Is.True, "首帧只生成整图，尚未完成范围和单节点。");
                    Assert.That(completions, Is.Zero);
                    Assert.That(RenderTexture.active == sentinel, Is.True);
                    var createdMaterials = Resources.FindObjectsOfTypeAll<Material>()
                        .Where(material => !originalMaterials.Contains(material.GetInstanceID()) &&
                            material.shader == Shader.Find("Sprites/Default") && material.HasProperty("_MainTex") &&
                            material.GetTexture("_MainTex") == fixture.Texture && material.hideFlags == HideFlags.HideAndDontSave)
                        .ToArray();
                    Assert.That(createdMaterials.Length, Is.EqualTo(1), "准确识别本次捕获创建的临时材质。");
                    ((IDisposable)capture).Dispose();
                    foreach (var material in createdMaterials)
                        Assert.That(material == null, Is.True, "取消必须立即销毁本次材质。");
                    Assert.That(RenderTexture.active == sentinel, Is.True);
                    Assert.That(completions, Is.Zero);
                    Assert.That(fixture.Texture.isReadable, Is.False);
                    Assert.That(capture.MoveNext(), Is.False);
                    Assert.That(completions, Is.Zero, "取消后继续推进也不能调用 completed。");
                }
                finally
                {
                    (capture as IDisposable)?.Dispose();
                    RenderTexture.active = previous;
                    sentinel.Release();
                    UnityEngine.Object.DestroyImmediate(sentinel);
                }
            }
        }
        [Test]
        public void ExcludedNodeIsNeverResolvedOrAddedToPreviewImages()
        {
            var provider = new PsdAiPreviewProvider();
            var document = Document();
            int resolves = 0;
            PsdAiPreviewResult result = null;
            var capture = provider.CaptureIncrementally(document, new[] { 17 }, new PsdVisibilityState(),
                new PsdExportState(new[] { 17 }), _ => { resolves++; return null; }, value => result = value);
            try
            {
                Assert.That(capture.MoveNext(), Is.False);
                Assert.That(resolves, Is.Zero);
                Assert.That(result, Is.Not.Null);
                Assert.That(result.Images, Is.Empty);
                Assert.That(result.PreviewIds, Is.Empty);
            }
            finally { (capture as IDisposable)?.Dispose(); }
        }

    }
}
