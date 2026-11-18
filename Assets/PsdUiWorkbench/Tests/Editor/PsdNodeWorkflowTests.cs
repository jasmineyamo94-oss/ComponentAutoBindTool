using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.Presentation;
using UnityEngine;
using UnityEngine.UIElements;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdNodeWorkflowTests
    {
        [Test]
        public void WindowKeepsMultipleLayerSelectionAcrossTreeRefresh()
        {
            var window = ScriptableObject.CreateInstance<PsdWorkbenchWindow>();
            try
            {
                window.CreateGUI();
                window.OpenAsset("Assets/Psd/战斗-主菜单界面.psd");
                var ids = window.Session.Document.DrawNodes.Take(2).Select(n => n.Id).ToArray();
                var tree = window.rootVisualElement.Q<PsdLayerTree>("layers");
                tree.ExpandAll();
                tree.SetSelectionById(ids);
                Assert.That(tree.selectedIds, Is.EquivalentTo(ids), "窗口同步不能把节点树的多选重新压回单选。");
                tree.RefreshItems();
                Assert.That(tree.selectedIds, Is.EquivalentTo(ids));
            }
            finally { Object.DestroyImmediate(window); }
        }

        [Test]
        public void ReopeningWindowRestoresMultiSelectionAndPrimaryLayer()
        {
            var window = ScriptableObject.CreateInstance<PsdWorkbenchWindow>();
            try
            {
                window.CreateGUI();
                window.OpenAsset("Assets/Psd/战斗-主菜单界面.psd");
                var ids = window.Session.Document.DrawNodes.Take(3).Select(n => n.Id).ToArray();
                window.SelectLayers(ids, ids[1]);
                Object.DestroyImmediate(window);
                window = ScriptableObject.CreateInstance<PsdWorkbenchWindow>();
                window.CreateGUI();
                window.OpenAsset("Assets/Psd/战斗-主菜单界面.psd");
                Assert.That(window.Session.SelectedIds, Is.EqualTo(ids));
                Assert.That(window.SelectedLayerId, Is.EqualTo(ids[1]));
                Assert.That(window.rootVisualElement.Q<PsdLayerTree>("layers").selectedIds, Is.EquivalentTo(ids));
            }
            finally { Object.DestroyImmediate(window); }
        }
    }
}
