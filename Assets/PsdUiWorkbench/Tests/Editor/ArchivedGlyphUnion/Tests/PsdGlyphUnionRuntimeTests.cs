using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.Runtime;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace PsdUiWorkbench.Tests
{
    [Explicit("已退役的邻字合成历史实验，不进入常规回归。")]
    public sealed class PsdGlyphUnionRuntimeTests
    {
        public const string Source = "Assets/PsdUiWorkbench/Exports/战斗-主菜单界面_单Pass字形校验.prefab";
        private UnityEngine.SceneManagement.Scene _scene;
        private GameObject _root;
        private TextMeshProUGUI _text;
        private Material _source;
        private string _before;
        private PsdTmpGlyphUnion _component;

        [SetUp]
        public void SetUp()
        {
            _scene = EditorSceneManager.NewPreviewScene();
            _root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Source), _scene);
            PrefabUtility.UnpackPrefabInstance(_root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            _root.GetComponent<Canvas>().enabled = false;
            _root.GetComponent<CanvasScaler>().enabled = false;
            ((RectTransform)_root.transform).sizeDelta = new Vector2(1080, 2400);
            _text = _root.GetComponentsInChildren<TextMeshProUGUI>(true).Single(t => t.text == "Energy Level");
            for (var at = _text.transform; at != null; at = at.parent) at.gameObject.SetActive(true);
            _source = _text.fontSharedMaterial;
            _before = EditorJsonUtility.ToJson(_source);
            _component = _text.gameObject.AddComponent<PsdTmpGlyphUnion>();
            _component.Configure(Shader.Find(PsdTmpGlyphUnion.ShaderName));
            _text.ForceMeshUpdate();
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
            EditorSceneManager.ClosePreviewScene(_scene);
            Assert.That(EditorJsonUtility.ToJson(_source), Is.EqualTo(_before), "组件不能改写基础材质资产。");
        }

        private void AssertCurrentLayout(int count)
        {
            Assert.That(_component.Diagnostic, Is.Null);
            Assert.That(_component.ActiveGlyphCount, Is.EqualTo(count));
            var material = _text.fontSharedMaterial;
            Assert.That(material.GetFloat("_PsdGlyphCount"), Is.EqualTo(count));
            var texture = (Texture2D)material.GetTexture("_PsdGlyphData");
            Assert.That(texture.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));
            int index = 0;
            foreach (var c in _text.textInfo.characterInfo.Take(_text.textInfo.characterCount).Where(c => c.isVisible))
            {
                var mesh = _text.textInfo.meshInfo[c.materialReferenceIndex];
                var p0 = mesh.uvs2[c.vertexIndex]; var p1 = mesh.uvs2[c.vertexIndex + 2];
                Assert.That(texture.GetPixel(index, 0), Is.EqualTo(new Color(p0.x, p0.y, p1.x, p1.y)));
                var uv0 = mesh.uvs0[c.vertexIndex]; var uv1 = mesh.uvs0[c.vertexIndex + 2];
                Assert.That(texture.GetPixel(index, 1), Is.EqualTo(new Color(uv0.x, uv0.y, uv1.x, uv1.y)));
                index++;
            }
        }

        [Test]
        public void TextAndLayoutChangesReuseObjectsAndSkipUnchangedUploads()
        {
            AssertCurrentLayout(11);
            var material = _text.fontSharedMaterial;
            var texture = material.GetTexture("_PsdGlyphData");
            int uploads = _component.LayoutUploadCount;
            _text.ForceMeshUpdate(); _text.ForceMeshUpdate();
            Assert.That(_component.LayoutUploadCount, Is.EqualTo(uploads));
            _text.text = "Hell"; _text.ForceMeshUpdate(); AssertCurrentLayout(4);
            _text.fontSize += 7; _text.characterSpacing += 8;
            _text.rectTransform.sizeDelta += new Vector2(40, 10);
            _text.alignment = TextAlignmentOptions.Center;
            _text.ForceMeshUpdate(); AssertCurrentLayout(4);
            Assert.That(_text.fontSharedMaterial, Is.SameAs(material));
            Assert.That(material.GetTexture("_PsdGlyphData"), Is.SameAs(texture));
            Assert.That(material.passCount, Is.EqualTo(1));
            Assert.That(typeof(PsdTmpGlyphUnion).GetMethod("Update", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic), Is.Null);
        }

        [Test]
        public void UnsupportedLayoutClearsCountAndRecoversWithoutStaleGlyphs()
        {
            _text.richText = true;
            foreach (string value in new[] {new string('W', 33), "Hell\nHell", "<color=red>H</color>ell", "<size=20>H</size>ell", "<i>Hell</i>"})
            {
                _text.text = value; _text.ForceMeshUpdate();
                Assert.That(_component.ActiveGlyphCount, Is.Zero, value);
                Assert.That(_component.Diagnostic, Is.Not.Null.And.Not.Empty);
                Assert.That(_text.fontSharedMaterial.GetFloat("_PsdGlyphCount"), Is.Zero);
                _text.text = "Hell"; _text.ForceMeshUpdate(); AssertCurrentLayout(4);
            }
            _text.text = ""; _text.ForceMeshUpdate();
            Assert.That(_text.textInfo.characterCount, Is.Zero);
            _text.text = "0"; _text.ForceMeshUpdate(); AssertCurrentLayout(1);
        }

        [Test]
        public void DisableReenableAndDestroyReleaseOwnedObjectsAndRestoreSource()
        {
            for (int i = 0; i < 3; i++)
            {
                var material = _text.fontSharedMaterial;
                var texture = material.GetTexture("_PsdGlyphData");
                _component.enabled = false;
                Assert.That(_text.fontSharedMaterial, Is.SameAs(_source));
                Assert.That(material == null && texture == null, Is.True);
                _text.text = i == 0 ? "Hell" : "16";
                _component.enabled = true; _text.ForceMeshUpdate();
                AssertCurrentLayout(i == 0 ? 4 : 2);
            }
            UnityEngine.Object.DestroyImmediate(_component);
            Assert.That(_text.fontSharedMaterial, Is.SameAs(_source));
        }

        [Test]
        public void TmpMaterialSetterDoesNotRestoreBorrowedLayoutAfterDisable()
        {
            _text.outlineColor = Color.red;
            _text.ForceMeshUpdate();
            AssertCurrentLayout(11);
            var tmpMaterial = _text.fontMaterial;
            _text.ForceMeshUpdate();
            Assert.That(_text.fontMaterial, Is.SameAs(tmpMaterial), "应复用 TMP 已缓存的副本，避免每次 getter 创建新材质。");
            _component.enabled = false;
            Assert.That(_text.fontSharedMaterial, Is.SameAs(_source), "TMP 属性 setter 产生的材质副本不能成为带临时纹理的恢复目标。");
            _component.enabled = true; _text.ForceMeshUpdate();
            _text.outlineColor = Color.green;
            // 同一帧禁用，不给 TMP 下一次网格回调的机会。
            _component.enabled = false;
            Assert.That(_text.fontSharedMaterial, Is.SameAs(_source));
        }

        [Test]
        public void FallbackAtlasDoesNotRetainPreviousUnionCount()
        {
            var originalFont = _text.font;
            var clone = UnityEngine.Object.Instantiate(originalFont);
            clone.fallbackFontAssetTable = new System.Collections.Generic.List<TMP_FontAsset> {TMP_Settings.defaultFontAsset};
            try
            {
                _text.font = clone;
                _text.text = "Energy ¡";
                _text.ForceMeshUpdate();
                Assert.That(_text.textInfo.materialCount, Is.GreaterThan(1), "需要实际出现回退图集。");
                Assert.That(_component.ActiveGlyphCount, Is.Zero);
                int copiedUnionMaterials = 0;
                foreach (var sub in _text.GetComponentsInChildren<TMP_SubMeshUI>())
                    if (sub.sharedMaterial.HasProperty("_PsdGlyphCount"))
                    {
                        copiedUnionMaterials++;
                        Assert.That(sub.sharedMaterial.GetFloat("_PsdGlyphCount"), Is.Zero);
                        Assert.That(sub.materialForRendering.GetFloat("_PsdGlyphCount"), Is.Zero);
                    }
                if (TMP_Settings.matchMaterialPreset) Assert.That(copiedUnionMaterials, Is.GreaterThan(0));
                _text.text = "Hell"; _text.ForceMeshUpdate(); AssertCurrentLayout(4);
            }
            finally
            {
                _text.font = originalFont;
                clone.atlasTextures = Array.Empty<Texture2D>();
                // 字库是浅副本，材质和图集均属于持久化源资产。
                clone.material = null;
                UnityEngine.Object.DestroyImmediate(clone);
            }
        }

        [Test]
        public void StencilMaterialReceivesNewCountAndExternalMaterialIsNotDestroyed()
        {
            var mask = new GameObject("Mask", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Mask));
            mask.transform.SetParent(_root.transform, false);
            _text.transform.SetParent(mask.transform, false);
            _root.GetComponent<Canvas>().enabled = true;
            Canvas.ForceUpdateCanvases();
            _text.RecalculateMasking();
            _text.text = "Hell"; _text.ForceMeshUpdate();
            AssertCurrentLayout(4);
            var masked = _text.materialForRendering;
            Assert.That(masked, Is.Not.SameAs(_text.fontSharedMaterial));
            Assert.That(masked.GetFloat("_PsdGlyphCount"), Is.EqualTo(4));
            _text.text = "16"; _text.ForceMeshUpdate();
            Assert.That(_text.materialForRendering.GetFloat("_PsdGlyphCount"), Is.EqualTo(2));
            var external = new Material(_source);
            try
            {
                _text.fontSharedMaterial = external;
                _text.ForceMeshUpdate(); AssertCurrentLayout(2);
                _component.enabled = false;
                Assert.That(_text.fontSharedMaterial, Is.SameAs(external));
                Assert.That(external != null, Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(external); }
        }

        // 同一段文字先修改，再与重新生成静态数据的 GPU 结果比较，捕捉过期 UV 和邻字残影。
        public static string CaptureChanged(string name, bool runtime)
        {
            var owned = new System.Collections.Generic.List<UnityEngine.Object>();
            try
            {
                return PsdTmpCandidateExport.Capture(Source, PsdStrokeVisualDiagnosis.Folder + name + ".png", root =>
                {
                    foreach (var text in root.GetComponentsInChildren<TextMeshProUGUI>(true).Where(t => t.name != "Placeholder"))
                    {
                        if (text.text != "Energy Level") { text.enabled = false; continue; }
                        if (runtime)
                        {
                            var component = text.gameObject.AddComponent<PsdTmpGlyphUnion>();
                            component.Configure(Shader.Find(PsdTmpGlyphUnion.ShaderName));
                            text.ForceMeshUpdate();
                        }
                        text.text = "Hell"; text.characterSpacing += 3; text.fontSize += 2;
                        if (!runtime)
                        {
                            var material = new Material(text.fontSharedMaterial) { shader = Shader.Find(PsdGlyphUnionPrototype.ShaderName) };
                            owned.Add(material); text.fontSharedMaterial = material;
                            text.horizontalMapping = TextureMappingOptions.Paragraph; text.verticalMapping = TextureMappingOptions.Paragraph;
                            text.ForceMeshUpdate();
                            var data = PsdGlyphUnionPrototype.BuildLayout(text, out int count); owned.Add(data);
                            material.SetTexture("_PsdGlyphData", data); material.SetFloat("_PsdGlyphCount", count);
                        }
                        text.ForceMeshUpdate();
                        if (runtime) Assert.That(text.GetComponent<PsdTmpGlyphUnion>().Diagnostic, Is.Null);
                    }
                });
            }
            finally { foreach (var obj in owned) if (obj != null) UnityEngine.Object.DestroyImmediate(obj); }
        }

        [Test]
        public void ChangedTextGpuMatchesFreshStaticLayout()
        {
            string prefix = "dynamic-test-" + Guid.NewGuid().ToString("N");
            try
            {
                byte[] a = File.ReadAllBytes(CaptureChanged(prefix + "runtime", true));
                byte[] b = File.ReadAllBytes(CaptureChanged(prefix + "static", false));
                Assert.That(a, Is.EqualTo(b), "运行时改字后应与按新布局生成的静态合成完全一致。");
            }
            finally
            {
                foreach (string suffix in new[] {"runtime", "static"})
                {
                    string path = PsdStrokeVisualDiagnosis.Folder + prefix + suffix + ".png";
                    if (File.Exists(path)) File.Delete(path);
                    if (File.Exists(path + ".meta")) File.Delete(path + ".meta");
                }
            }
        }
    }
}
