using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.AiNaming;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Infrastructure;
using PsdUiWorkbench.Presentation;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdAiWorkbenchTests
    {
        private const string SourcePath = "Assets/Psd/战斗-主菜单界面.psd";
        private sealed class Client : IPsdAiNamingClient
        {
            public IEnumerator Analyze(PsdAiNamingSnapshot snapshot, PsdAiRequestSettings settings, Action<IReadOnlyList<PsdAiSuggestion>> completed)
            {
                yield return null;
                completed(snapshot.NodeKeys.Select(key => new PsdAiSuggestion(key, "EnergyLevel", "能量等级说明", false)).ToArray());
            }
            public void Dispose() { }
        }
        private sealed class Preview : IPsdAiPreviewProvider
        {
            public PsdAiPreviewResult Capture(PsdDocumentSnapshot document, IReadOnlyList<int> targets, PsdVisibilityState visibility, Func<string, Sprite> resolve)
                => new PsdAiPreviewResult(Array.Empty<PsdAiImage>(), Array.Empty<int>());
        }
        private static byte[] Bytes(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

        [Test]
        public void OpeningAndClosingAiPanelDoesNotCreateConfigurationOrChangeDocument()
        {
            string namePath = PsdNodeNameRepository.GetConfigPath(AssetDatabase.AssetPathToGUID(SourcePath));
            byte[] settingsBefore = Bytes(PsdAiNamingSettings.DefaultAssetPath), namesBefore = Bytes(namePath);
            var window = ScriptableObject.CreateInstance<PsdWorkbenchWindow>();
            try
            {
                window.CreateGUI();
                window.OpenAsset(SourcePath);
                long version = window.Session.ContextVersion;
                window.ShowAiNaming();
                Assert.That(window.rootVisualElement.Q("aiHost").style.display.value, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(window.AiNaming.IsBusy, Is.False, "打开面板不得主动请求模型。");
                window.ShowAiNaming(false);
                Assert.That(window.rootVisualElement.Q("aiHost").style.display.value, Is.EqualTo(DisplayStyle.None));
                Assert.That(window.Session.ContextVersion, Is.EqualTo(version));
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
            Assert.That(Bytes(PsdAiNamingSettings.DefaultAssetPath), Is.EqualTo(settingsBefore));
            Assert.That(Bytes(namePath), Is.EqualTo(namesBefore));
        }

        [UnityTest]
        public IEnumerator ApprovedAiNameSurvivesUndoReopenReimportAndBothExportsWithoutChangingText()
        {
            string folder = "Assets/PsdUiWorkbench/Tests/AiWorkflow_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/PsdUiWorkbench/Tests", Path.GetFileName(folder));
            string copy = folder + "/copy.psd", guid = null;
            PsdWorkbenchWindow window = null;
            PsdAiNamingController controller = null;
            var settings = ScriptableObject.CreateInstance<PsdAiNamingSettings>();
            settings.Provider = PsdAiProvider.Zhipu;
            settings.Model = "glm-4.6v";
            try
            {
                Assert.That(AssetDatabase.CopyAsset(SourcePath, copy), Is.True);
                PsdImporterMigration.Switch(copy, true);
                guid = AssetDatabase.AssetPathToGUID(copy);
                byte[] psdBefore = Bytes(copy), metaBefore = Bytes(copy + ".meta");
                window = ScriptableObject.CreateInstance<PsdWorkbenchWindow>();
                window.ShowUtility();
                yield return null;
                window.OpenAsset(copy);
                yield return Idle(window);
                int id = window.Session.Document.Nodes.Single(n => n.Name == "Energy Level").Id;
                var original = window.Session.Document.Find(id);
                var source = new UnityPsdDocumentSource();
                source.Load(guid);
                controller = new PsdAiNamingController(window.Session, source.Resolve, settings: settings,
                    client: new Client(), previewProvider: new Preview());
                controller.Start(new[] { id }, false);
                double deadline = EditorApplication.timeSinceStartup + 5;
                while (controller.IsBusy && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.That(controller.IsBusy, Is.False);
                Assert.That(controller.Rows.Single().Selected, Is.False, "模型建议不能自动应用。");
                Assert.That(window.Session.GetNodeName(id), Is.EqualTo("Energy Level"));
                controller.Rows.Single().Selected = true;
                Assert.That(controller.ApplySelected(), Is.True, controller.Error);
                Assert.That(window.Session.GetNodeName(id), Is.EqualTo("EnergyLevel"));
                Assert.That(window.Session.Document.Find(id).Name, Is.EqualTo("Energy Level"));
                Undo.PerformUndo();
                yield return null;
                Assert.That(window.Session.GetNodeName(id), Is.EqualTo("Energy Level"));
                Undo.PerformRedo();
                yield return null;
                Assert.That(window.Session.GetNodeName(id), Is.EqualTo("EnergyLevel"));
                controller.Dispose(); controller = null;
                window.Close();
                window = ScriptableObject.CreateInstance<PsdWorkbenchWindow>();
                window.ShowUtility();
                yield return null;
                window.OpenAsset(copy);
                Assert.That(window.Session.GetNodeName(id), Is.EqualTo("EnergyLevel"));
                AssetDatabase.ImportAsset(copy, ImportAssetOptions.ForceUpdate);
                yield return null;
                yield return Idle(window);
                Assert.That(window.Session.GetNodeName(id), Is.EqualTo("EnergyLevel"));
                window.SelectLayer(id);
                var tree = window.rootVisualElement.Q<PsdLayerTree>("layers");
                tree.ScrollToItemById(id);
                yield return null;
                var toggle = tree.Query<Toggle>("visibility").ToList().Single(t => t.userData is int layerId && layerId == id);
                Assert.That(toggle.parent.Q<Label>("name").text, Is.EqualTo("EnergyLevel"));
                yield return Idle(window);
                window.SelectDocumentRoot();
                int rootId = tree.DocumentRootId;
                Assert.That(tree.selectedIds, Is.EqualTo(new[] { rootId }));
                Assert.That(window.DocumentRootSelected, Is.True);
                Assert.That(window.Session.SelectedIds, Is.Empty, "虚拟根不应伪装为真实 PSD 图层。");
                Assert.That(window.rootVisualElement.Q<PsdAiNamingPanel>().Q<Label>("selection").text,
                    Does.Contain("文档根节点"));
                Assert.That(window.SetDocumentRootName("BattleMainMenu"), Is.True);
                tree.ScrollToItemById(rootId);
                yield return null;
                var rootToggle = tree.Query<Toggle>("visibility").ToList().Single(t => t.userData is int layerId && layerId == rootId);
                Assert.That(rootToggle.parent.Q<Label>("name").text, Is.EqualTo("BattleMainMenu"));
                AssetDatabase.CreateFolder(folder, "Images");
                AssetDatabase.CreateFolder(folder, "Texts");
                var image = window.ExportPrefabTo(folder + "/Images/BattleMainMenu.prefab");
                yield return Idle(window);
                var text = window.ExportTextPrefabTo(folder + "/Texts/BattleMainMenu.prefab");
                var imageRoot = AssetDatabase.LoadAssetAtPath<GameObject>(image.AssetPath);
                var textRoot = AssetDatabase.LoadAssetAtPath<GameObject>(text.AssetPath);
                Assert.That(imageRoot.name, Is.EqualTo("BattleMainMenu"));
                Assert.That(textRoot.name, Is.EqualTo("BattleMainMenu"));
                Assert.That(imageRoot.GetComponentsInChildren<Transform>(true).Count(t => t.name == "EnergyLevel"), Is.EqualTo(1));
                var named = textRoot.GetComponentsInChildren<RectTransform>(true).Single(t => t.name == "EnergyLevel");
                Assert.That(named.GetComponentInChildren<TextMeshProUGUI>(true).text, Is.EqualTo("Energy Level"));
                Assert.That(textRoot.GetComponentsInChildren<TextMeshProUGUI>(true).Length, Is.EqualTo(17));
                var corners = new Vector3[4];
                named.GetWorldCorners(corners);
                var topLeft = textRoot.transform.GetChild(0).InverseTransformPoint(corners[1]);
                Assert.That(topLeft.x + 540, Is.EqualTo(original.Bounds.X).Within(0.01f));
                Assert.That(1200 - topLeft.y, Is.EqualTo(original.Bounds.Y).Within(0.01f));
                Assert.That(Bytes(copy), Is.EqualTo(psdBefore));
                Assert.That(Bytes(copy + ".meta"), Is.EqualTo(metaBefore));
            }
            finally
            {
                controller?.Dispose();
                UnityEngine.Object.DestroyImmediate(settings);
                if (window != null) window.Close();
                if (guid != null)
                {
                    string path = PsdNodeNameRepository.GetConfigPath(guid);
                    var config = AssetDatabase.LoadMainAssetAtPath(path);
                    if (config != null) Undo.ClearUndo(config);
                    AssetDatabase.DeleteAsset(path);
                }
                AssetDatabase.DeleteAsset(folder);
            }
        }

        private static IEnumerator Idle(PsdWorkbenchWindow window)
        {
            double deadline = EditorApplication.timeSinceStartup + 10;
            while ((window.IsBusy || EditorApplication.isCompiling || EditorApplication.isUpdating) &&
                EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.That(window.IsBusy, Is.False);
            Assert.That(EditorApplication.isCompiling, Is.False);
            Assert.That(EditorApplication.isUpdating, Is.False);
        }
    }
}
