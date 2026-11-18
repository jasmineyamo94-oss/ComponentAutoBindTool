using System.Collections;
using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.Infrastructure;
using TMPro;
using UnityEditor;
using UnityEditor.TestTools;
using UnityEngine;
using UnityEngine.TestTools;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdPlainTmpPlayTests
    {
        [UnityTest]
        public IEnumerator PlainTmpEditsLongAndMultilineTextWithoutWorkbenchRuntimeResources()
        {
            yield return new EnterPlayMode();
            bool background = Application.runInBackground;
            Application.runInBackground = true;
            GameObject root = null;
            try
            {
                root = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/PsdUiWorkbench/Exports/战斗-主菜单界面_单Pass字形校验.prefab"));
                var text = root.GetComponentsInChildren<TextMeshProUGUI>(true).Single(t => t.text == "Energy Level");
                for (var at = text.transform; at != null; at = at.parent) at.gameObject.SetActive(true);
                var material = text.fontSharedMaterial;
                int frame = Time.frameCount;
                foreach (string value in new[] {"Hell", "16", string.Concat(Enumerable.Repeat("Hell ", 12)), "Energy\nLevel"})
                {
                    text.text = value;
                    yield return null; yield return null;
                    Assert.That(Time.frameCount, Is.GreaterThan(frame));
                    Assert.That(text.textInfo.characterCount, Is.EqualTo(value.Length));
                    Assert.That(text.fontSharedMaterial, Is.SameAs(material), "普通改字不应创建逐段材质实例。");
                    Assert.That(material.shader.name, Is.EqualTo(PsdTmpOuterStroke.ShaderName));
                    Assert.That(material.HasProperty("_PsdGlyphData"), Is.False);
                    Assert.That(root.GetComponentsInChildren<MonoBehaviour>(true).Any(c => c != null && c.GetType().Namespace?.StartsWith("PsdUiWorkbench") == true), Is.False);
                }
                Assert.That(text.textInfo.lineCount, Is.EqualTo(2));
            }
            finally
            {
                if (root != null) UnityEngine.Object.Destroy(root);
                Application.runInBackground = background;
            }
            yield return null;
            yield return new ExitPlayMode();
        }
    }
}
