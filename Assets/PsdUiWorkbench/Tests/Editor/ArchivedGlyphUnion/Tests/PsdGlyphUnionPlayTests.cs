using System.Collections;
using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.Runtime;
using TMPro;
using UnityEditor;
using UnityEditor.TestTools;
using UnityEngine;
using UnityEngine.TestTools;

namespace PsdUiWorkbench.Tests
{
    [Explicit("已退役的邻字合成历史实验，不进入常规回归。")]
    public sealed class PsdGlyphUnionPlayTests
    {
        [UnityTest]
        public IEnumerator LiveChangesUpdateWithoutForceMeshAndReleaseAfterDisable()
        {
            yield return new EnterPlayMode();
            bool background = Application.runInBackground;
            Application.runInBackground = true;
            GameObject root = null;
            try
            {
                root = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PsdGlyphUnionRuntimeTests.Source));
                var text = root.GetComponentsInChildren<TextMeshProUGUI>(true).Single(t => t.text == "Energy Level");
                for (var at = text.transform; at != null; at = at.parent) at.gameObject.SetActive(true);
                var original = text.fontSharedMaterial;
                var component = text.gameObject.AddComponent<PsdTmpGlyphUnion>();
                component.Configure(Shader.Find(PsdTmpGlyphUnion.ShaderName));
                int frame = Time.frameCount;
                yield return null; yield return null;
                Assert.That(Time.frameCount, Is.GreaterThan(frame), "需要真实推进运行帧。");
                Assert.That(component.ActiveGlyphCount, Is.EqualTo(11));
                var material = text.fontSharedMaterial;
                var texture = material.GetTexture("_PsdGlyphData");
                foreach (string value in new[] {"Hell", "16", "Energy Level"})
                {
                    text.text = value;
                    yield return null; yield return null;
                    Assert.That(component.Diagnostic, Is.Null);
                    Assert.That(component.ActiveGlyphCount, Is.EqualTo(value.Count(c => !char.IsWhiteSpace(c))));
                    Assert.That(text.fontSharedMaterial, Is.SameAs(material));
                    Assert.That(material.GetTexture("_PsdGlyphData"), Is.SameAs(texture));
                }
                text.fontSize += 3; text.characterSpacing += 4;
                yield return null; yield return null;
                Assert.That(component.ActiveGlyphCount, Is.EqualTo(11));
                component.enabled = false;
                yield return null;
                Assert.That(material == null && texture == null, Is.True, "运行时对象应在帧末释放。");
                Assert.That(text.fontSharedMaterial, Is.SameAs(original));
                text.text = "Hell";
                component.enabled = true;
                yield return null; yield return null;
                Assert.That(component.ActiveGlyphCount, Is.EqualTo(4));
                material = text.fontSharedMaterial;
                texture = material.GetTexture("_PsdGlyphData");
                UnityEngine.Object.Destroy(root); root = null;
                yield return null;
                Assert.That(material == null && texture == null, Is.True);
            }
            finally
            {
                if (root != null) UnityEngine.Object.Destroy(root);
                Application.runInBackground = background;
            }
            yield return new ExitPlayMode();
        }
    }
}
