using System;
using NUnit.Framework;
using PsdUiWorkbench.Infrastructure;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using Object = UnityEngine.Object;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdOuterOutlineTests
    {
        [TestCase(0)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(5)]
        public void OuterOutlineRendersRequestedPixelGrowthWithoutChangingFill(int pixels)
        {
            int scenes = EditorSceneManager.previewSceneCount;
            using (var rig = new RenderRig())
            {
                rig.Configure(0);
                var baseline = rig.Render();
                rig.Configure(pixels);
                var outlined = rig.Render();
                AssertSameChannel(baseline, outlined, 0, "外描边不能扩张或侵蚀红色填充。");
                Assert.That(rig.Material.passCount, Is.EqualTo(1));
                Assert.That(rig.Text.textInfo.materialCount, Is.EqualTo(1), "单 TMP 不应生成额外描边材质。");
                Assert.That(rig.Text.textInfo.characterCount, Is.EqualTo(1));
                Assert.That(rig.Material.GetFloat("_FaceDilate"), Is.Zero);
                float paddingPixels = ShaderUtilities.GetPadding(rig.Material, false, true) * rig.GlyphScale;
                Assert.That(paddingPixels, Is.GreaterThanOrEqualTo(pixels), "不依赖 extraPadding 的 TMP 网格也须容纳完整外扩。");
                var inner = Bounds(baseline);
                var outer = Bounds(outlined);
                foreach (int growth in new[] { inner.xMin - outer.xMin, outer.xMax - inner.xMax,
                    inner.yMin - outer.yMin, outer.yMax - inner.yMax })
                    Assert.That(growth, Is.EqualTo(pixels).Within(1), "实际图像外扩须匹配 PSD 像素，允许 1 px 栅格误差。");
                int greenCoverage = 0;
                foreach (var color in outlined) greenCoverage += color.g;
                if (pixels == 0) Assert.That(greenCoverage, Is.Zero);
                else Assert.That(greenCoverage, Is.GreaterThan(1000));
                AssertNotFrameClipped(outlined);
            }
            Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(scenes));
        }

        [Test]
        public void OuterOutlineKeepsEightyPercentOutlineAlphaIndependentOfTransparentFace()
        {
            using var rig = new RenderRig();
            rig.Configure(5);
            rig.Material.SetColor("_OutlineColor", new Color(0, 1, 0, 0.8f));
            var opaqueFace = rig.Render();
            rig.Material.SetColor("_FaceColor", new Color(1, 0, 0, 0));
            var transparentFace = rig.Render();
            AssertSameChannel(opaqueFace, transparentFace, 1, "填充 alpha 不能混入外环颜色或 alpha。");
            int solidRing = 0, solidFace = 0, maximumOutline = 0;
            for (int i = 0; i < opaqueFace.Length; i++)
            {
                if (opaqueFace[i].g > 195 && opaqueFace[i].r < 2)
                {
                    solidRing++;
                    // 抗锯齿边缘只覆盖部分外环，颜色与 alpha 相等，但不必每点都达到 204。
                    maximumOutline = Math.Max(maximumOutline, transparentFace[i].g);
                    Assert.That(transparentFace[i].a, Is.EqualTo(transparentFace[i].g).Within(3));
                    Assert.That(transparentFace[i].r, Is.Zero);
                    Assert.That(transparentFace[i].b, Is.Zero);
                }
                if (opaqueFace[i].r == 255)
                {
                    solidFace++;
                    Assert.That(transparentFace[i].a, Is.LessThanOrEqualTo(2), "透明字面内部不得被描边填满。");
                }
            }
            Assert.That(solidRing, Is.GreaterThan(20));
            Assert.That(maximumOutline, Is.EqualTo(204).Within(3), "满覆盖外环保持 80% alpha。");
            Assert.That(solidFace, Is.GreaterThan(20));
        }

        [Test]
        public void OuterOutlineFillDoesNotFollowTmpScaleRatioChanges()
        {
            using var rig = new RenderRig();
            rig.Configure(0);
            var baseline = rig.Render();
            float ratioBefore = rig.Material.GetFloat("_ScaleRatioA");
            // 模拟材质面板把宽度调到 RatioA 的饱和分支；适配器本身会拒绝这样的 PSD 请求。
            rig.Material.SetFloat("_OutlineWidth", 1.2f);
            rig.Material.SetColor("_OutlineColor", Color.clear);
            rig.Material.EnableKeyword("OUTLINE_ON");
            var changedRatio = rig.Render();
            Assert.That(rig.Material.GetFloat("_ScaleRatioA"), Is.LessThan(ratioBefore));
            AssertSameChannel(baseline, changedRatio, 0, "填充字重不能随 TMP 的描边比例更新而改变。");
        }

        [Test]
        public void CenterModeMatchesStandardTmpRendering()
        {
            using var rig = new RenderRig();
            rig.Configure(3);
            rig.Material.SetFloat("_OutlineMode", 0);
            rig.Material.SetFloat("_PsdWidthConvention", 0);
            var center = rig.Render();
            rig.Material.shader = Shader.Find("TextMeshPro/Mobile/Distance Field");
            var standard = rig.Render();
            for (int channel = 0; channel < 4; channel++)
                AssertSameChannel(standard, center, channel, "Center 分支必须保留标准 TMP 行为。");
        }

        [Test]
        public void OuterOutlineRejectsInvalidAndInsufficientSdfBeforeMutatingMaterial()
        {
            var material = new Material(Shader.Find("TextMeshPro/Mobile/Distance Field"));
            try
            {
                material.SetFloat("_GradientScale", 25);
                string before = EditorJsonUtility.ToJson(material);
                Assert.Throws<ArgumentException>(() => PsdTmpOuterStroke.ConfigureOuterOutline(material, float.NaN, 0.5f, 0.375f));
                Assert.Throws<ArgumentException>(() => PsdTmpOuterStroke.ConfigureOuterOutline(material, -1, 0.5f, 0.375f));
                Assert.Throws<ArgumentException>(() => PsdTmpOuterStroke.ConfigureOuterOutline(material, 3, 0, 0.375f));
                Assert.Throws<ArgumentException>(() => PsdTmpOuterStroke.ConfigureOuterOutline(material, 3, 0.5f, float.PositiveInfinity));
                Assert.Throws<InvalidOperationException>(() => PsdTmpOuterStroke.ConfigureOuterOutline(material, 100, 0.5f, 0.375f));
                // width 本身未超 1，但加上最大字重已经没有足够距离场余量。
                Assert.Throws<InvalidOperationException>(() => PsdTmpOuterStroke.ConfigureOuterOutline(material, 11.5f, 0.5f, 0.375f));
                Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(before));
                material.SetFloat("_GradientScale", 1);
                before = EditorJsonUtility.ToJson(material);
                Assert.Throws<InvalidOperationException>(() => PsdTmpOuterStroke.ConfigureOuterOutline(material, 0, 0.5f, 0.375f));
                Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(before));
            }
            finally { Object.DestroyImmediate(material); }
        }

        private static void AssertSameChannel(Color32[] expected, Color32[] actual, int channel, string message)
        {
            long error = 0, coverage = 0;
            int maxError = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                int a = Channel(expected[i], channel), b = Channel(actual[i], channel);
                int difference = Math.Abs(a - b);
                error += difference;
                coverage += a;
                maxError = Math.Max(maxError, difference);
            }
            Assert.That(maxError, Is.LessThanOrEqualTo(5), message);
            Assert.That(error / (double)Math.Max(1, coverage), Is.LessThan(0.005), message);
        }

        private static byte Channel(Color32 color, int channel)
        {
            switch (channel)
            {
                case 0: return color.r;
                case 1: return color.g;
                case 2: return color.b;
                default: return color.a;
            }
        }

        private static RectInt Bounds(Color32[] pixels)
        {
            int left = RenderRig.Width, right = -1, bottom = RenderRig.Height, top = -1;
            for (int y = 0; y < RenderRig.Height; y++)
            for (int x = 0; x < RenderRig.Width; x++)
            {
                var color = pixels[y * RenderRig.Width + x];
                if (color.r + color.g < 128) continue;
                left = Math.Min(left, x); right = Math.Max(right, x);
                bottom = Math.Min(bottom, y); top = Math.Max(top, y);
            }
            Assert.That(right, Is.GreaterThan(left), "真实渲染不能为空。");
            return new RectInt(left, bottom, right - left + 1, top - bottom + 1);
        }

        private static void AssertNotFrameClipped(Color32[] pixels)
        {
            for (int x = 0; x < RenderRig.Width; x++)
            {
                Assert.That(pixels[x].a, Is.Zero);
                Assert.That(pixels[(RenderRig.Height - 1) * RenderRig.Width + x].a, Is.Zero);
            }
            for (int y = 0; y < RenderRig.Height; y++)
            {
                Assert.That(pixels[y * RenderRig.Width].a, Is.Zero);
                Assert.That(pixels[y * RenderRig.Width + RenderRig.Width - 1].a, Is.Zero);
            }
        }

        // 复用现有 render probe 的预览场景思路；不读导出 Prefab、不写图像或字体资产。
        private sealed class RenderRig : IDisposable
        {
            public const int Width = 256, Height = 192;
            private UnityEngine.SceneManagement.Scene _scene;
            private GameObject _root, _cameraObject;
            private Camera _camera;
            private RenderTexture _target;
            private Texture2D _readback;
            private TMP_FontAsset _font;
            private Material _fontMaterial;
            public TextMeshProUGUI Text { get; private set; }
            public Material Material { get; private set; }
            public float GlyphScale => Text.textInfo.characterInfo[0].scale;

            public RenderRig()
            {
                try
                {
                    var shader = Shader.Find(PsdTmpOuterStroke.UserShaderName);
                    Assert.That(shader, Is.Not.Null);
                    Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
                    var source = AssetDatabase.LoadAssetAtPath<Font>("Assets/AAGame/UI/Fonts/Toxia_FRE.ttf");
                    Assert.That(source, Is.Not.Null, "需要测试字体，但不能修改或生成持久字体资产。");
                    _font = TMP_FontAsset.CreateFontAsset(source, 96, 24, GlyphRenderMode.SDFAA, 512, 512, AtlasPopulationMode.Dynamic, false);
                    Assert.That(_font, Is.Not.Null);
                    _font.normalStyle = 0;
                    _font.boldStyle = 0;
                    _fontMaterial = _font.material;
                    Assert.That(_font.TryAddCharacters("0", out string missing), Is.True, missing);
                    _font.atlasTexture.Apply(false, false);
                    _scene = EditorSceneManager.NewPreviewScene();
                    _root = new GameObject("正式外描边测试", typeof(RectTransform));
                    _root.SetActive(false);
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(_root, _scene);
                    var canvas = _root.AddComponent<Canvas>();
                    canvas.renderMode = RenderMode.WorldSpace;
                    ((RectTransform)_root.transform).sizeDelta = new Vector2(Width, Height);
                    var textObject = new GameObject("单字形", typeof(RectTransform));
                    textObject.transform.SetParent(_root.transform, false);
                    Text = textObject.AddComponent<TextMeshProUGUI>();
                    Material = new Material(_fontMaterial);
                    Material.SetFloat("_Sharpness", 0);
                    Material.SetFloat("_PerspectiveFilter", 0);
                    Material.DisableKeyword("UNDERLAY_ON");
                    Material.DisableKeyword("UNDERLAY_INNER");
                    Text.font = _font;
                    Text.fontSharedMaterial = Material;
                    Text.fontSize = 48;
                    Text.fontStyle = FontStyles.Bold;
                    Text.text = "0";
                    Text.color = Color.white;
                    Text.alignment = TextAlignmentOptions.Center;
                    Text.enableAutoSizing = false;
                    Text.textWrappingMode = TextWrappingModes.NoWrap;
                    Text.overflowMode = TextOverflowModes.Overflow;
                    // Shader 字重独立于字体几何字重；固定留白还需容纳描边为零时的粗体扩张。
                    Text.extraPadding = true;
                    Text.raycastTarget = false;
                    Text.rectTransform.sizeDelta = new Vector2(Width - 40, Height - 40);
                    _target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    _target.antiAliasing = 1;
                    _target.Create();
                    _cameraObject = new GameObject("测试相机", typeof(Camera));
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(_cameraObject, _scene);
                    _camera = _cameraObject.GetComponent<Camera>();
                    _camera.scene = _scene;
                    _camera.orthographic = true;
                    _camera.orthographicSize = Height / 2f;
                    _camera.transform.position = new Vector3(0, 0, -10);
                    _camera.clearFlags = CameraClearFlags.SolidColor;
                    _camera.backgroundColor = Color.clear;
                    _camera.targetTexture = _target;
                    _camera.nearClipPlane = 0.1f;
                    _camera.farClipPlane = 100;
                    canvas.worldCamera = _camera;
                    _readback = new Texture2D(Width, Height, TextureFormat.RGBA32, false, true);
                    _root.SetActive(true);
                    Text.ForceMeshUpdate();
                }
                catch { Dispose(); throw; }
            }

            public void Configure(float pixels)
            {
                PsdTmpOuterStroke.ConfigureOuterOutline(Material, pixels, GlyphScale, 0.375f);
                Material.SetColor("_FaceColor", Color.red);
                Material.SetColor("_OutlineColor", Color.green);
            }

            public Color32[] Render()
            {
                var previous = RenderTexture.active;
                try
                {
                    ShaderUtilities.UpdateShaderRatios(Material);
                    Text.UpdateMeshPadding();
                    Text.ForceMeshUpdate();
                    Canvas.ForceUpdateCanvases();
                    _camera.Render();
                    RenderTexture.active = _target;
                    _readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                    _readback.Apply();
                    return _readback.GetPixels32();
                }
                finally { RenderTexture.active = previous; }
            }

            public void Dispose()
            {
                if (_root != null) Object.DestroyImmediate(_root);
                if (_cameraObject != null) Object.DestroyImmediate(_cameraObject);
                if (_target != null) { _target.Release(); Object.DestroyImmediate(_target); }
                if (_readback != null) Object.DestroyImmediate(_readback);
                if (Material != null) Object.DestroyImmediate(Material);
                if (_font != null)
                {
                    foreach (var texture in _font.atlasTextures)
                        if (texture != null && !EditorUtility.IsPersistent(texture)) Object.DestroyImmediate(texture);
                    Object.DestroyImmediate(_font);
                }
                if (_fontMaterial != null && !EditorUtility.IsPersistent(_fontMaterial)) Object.DestroyImmediate(_fontMaterial);
                if (_scene.IsValid()) EditorSceneManager.ClosePreviewScene(_scene);
            }
        }
    }
}
