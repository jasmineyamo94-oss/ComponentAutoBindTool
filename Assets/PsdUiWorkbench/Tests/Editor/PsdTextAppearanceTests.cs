using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using TMPro;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdTextAppearanceTests
    {
        [Test]
        public void AppearanceProbeMatchesBaselineAndDoesNotModifySharedMaterials()
        {
            const string path = "Assets/PsdUiWorkbench/Exports/战斗-主菜单界面_单字形共享材质.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var materials = prefab.GetComponentsInChildren<TextMeshProUGUI>(true).Select(t => t.fontSharedMaterial).Distinct().ToArray();
            var before = materials.Select(m => EditorJsonUtility.ToJson(m)).ToArray();
            int scenes = EditorSceneManager.previewSceneCount;
            string prefix = "appearance-test-" + Guid.NewGuid().ToString("N");
            try
            {
                string baseline = PsdTmpCandidateExport.Capture(path, PsdStrokeVisualDiagnosis.Folder + prefix + "direct.png");
                string actual = PsdTextAppearanceProbe.Capture(prefix + "probe");
                string thinner = PsdTextAppearanceProbe.Capture(prefix + "thinner", strokeScale: 0.9f);
                Assert.That(File.ReadAllBytes(actual), Is.EqualTo(File.ReadAllBytes(baseline)), "无参数探针必须与真实导出渲染一致。");
                Assert.That(File.ReadAllBytes(thinner), Is.Not.EqualTo(File.ReadAllBytes(baseline)), "负面对照应实际改变截图，不能验证空白输出。");
                Assert.That(materials.Select(m => EditorJsonUtility.ToJson(m)), Is.EqualTo(before));
                Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(scenes));
            }
            finally
            {
                foreach (string suffix in new[] {"direct", "probe", "thinner"})
                {
                    string file = PsdStrokeVisualDiagnosis.Folder + prefix + suffix + ".png";
                    if (File.Exists(file)) File.Delete(file);
                    if (File.Exists(file + ".meta")) File.Delete(file + ".meta");
                }
            }
        }
    }
}
