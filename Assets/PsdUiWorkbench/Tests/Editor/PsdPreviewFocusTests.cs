using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.Core;
using PsdUiWorkbench.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdPreviewFocusTests
    {
        public sealed class PreviewHostWindow : EditorWindow { }

        private static Vector2 Center(PixelRect bounds)
            => new Vector2(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

        private static Vector2 InPreview(PsdPreview preview, Vector2 documentPoint)
            => preview.WorldToLocal(preview.Children().First().LocalToWorld(documentPoint));

        private static void AssertCentered(PsdPreview preview, PixelRect bounds)
        {
            var center = InPreview(preview, Center(bounds));
            Assert.That(center.x, Is.EqualTo(preview.contentRect.center.x).Within(1f));
            Assert.That(center.y, Is.EqualTo(preview.contentRect.center.y).Within(1f));
        }

        private static void SendF(VisualElement target, EventModifiers modifiers = EventModifiers.None)
        {
            using (var evt = KeyDownEvent.GetPooled(new Event
            {
                type = EventType.KeyDown, keyCode = KeyCode.F, character = 'f', modifiers = modifiers
            }))
            {
                evt.target = target;
                target.SendEvent(evt);
            }
        }

        [UnityTest]
        public IEnumerator FrameCentersTargetKeepsItVisibleAndPreservesDocumentPickingCoordinates()
        {
            var host = ScriptableObject.CreateInstance<PreviewHostWindow>();
            host.minSize = new Vector2(480, 320);
            host.ShowUtility();
            host.position = new Rect(200, 200, 640, 400);
            yield return null;
            try
            {
                host.rootVisualElement.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(
                    "Assets/PsdUiWorkbench/Editor/Presentation/PsdWorkbenchWindow.uss"));
                var preview = new PsdPreview();
                host.rootVisualElement.Add(preview);
                var target = new PixelRect(900, 2200, 80, 50);
                preview.SetDocument(new PsdDocumentSnapshot("memory", 2000, 3000, new[]
                {
                    new PsdNode(1, null, "Target", false, true, true, false, "sprite", target, 0)
                }), _ => null);
                yield return null;
                preview.Fit();
                Assert.That(preview.Frame(target), Is.True);
                yield return null;
                AssertCentered(preview, target);
                var topLeft = InPreview(preview, new Vector2(target.X, target.Y));
                var bottomRight = InPreview(preview, new Vector2(target.X + target.Width, target.Y + target.Height));
                Assert.That(topLeft.x, Is.GreaterThan(preview.contentRect.xMin));
                Assert.That(topLeft.y, Is.GreaterThan(preview.contentRect.yMin));
                Assert.That(bottomRight.x, Is.LessThan(preview.contentRect.xMax));
                Assert.That(bottomRight.y, Is.LessThan(preview.contentRect.yMax));
                Vector2? picked = null;
                preview.Picked += (x, y) => picked = new Vector2(x, y);
                using (var click = PointerDownEvent.GetPooled(new Event
                {
                    type = EventType.MouseDown, button = 0,
                    mousePosition = preview.LocalToWorld(preview.contentRect.center)
                }))
                {
                    click.target = preview;
                    preview.SendEvent(click);
                }
                Assert.That(picked.HasValue, Is.True);
                Assert.That(picked.Value.x, Is.EqualTo(Center(target).x).Within(0.1f));
                Assert.That(picked.Value.y, Is.EqualTo(Center(target).y).Within(0.1f));
                var canvas = preview.Children().First();
                float left = canvas.style.left.value.value, top = canvas.style.top.value.value;
                foreach (var invalid in new[] { default(PixelRect), new PixelRect(float.NaN, 0, 1, 1),
                    new PixelRect(0, 0, float.PositiveInfinity, 1), new PixelRect(float.MaxValue, 0, float.MaxValue, 1) })
                    Assert.That(preview.Frame(invalid), Is.False);
                Assert.That(canvas.style.left.value.value, Is.EqualTo(left));
                Assert.That(canvas.style.top.value.value, Is.EqualTo(top));
            }
            finally { host.Close(); }
        }

        [UnityTest]
        public IEnumerator WindowShortcutFramesSelectionButDoesNotStealTextOrModifiedKeys()
        {
            var window = ScriptableObject.CreateInstance<PsdWorkbenchWindow>();
            window.ShowUtility();
            yield return null;
            try
            {
                window.OpenAsset("Assets/Psd/战斗-主菜单界面.psd");
                var document = window.Session.Document;
                var node = document.Nodes.Single(n => n.Name == "Energy Level");
                var preview = window.rootVisualElement.Q<PsdPreview>();
                var tree = window.rootVisualElement.Q<PsdLayerTree>();
                window.SelectLayer(node.Id);
                yield return null;
                yield return null;
                long version = window.Session.ContextVersion;
                var visibility = window.Session.CaptureVisibility();
                var export = window.Session.CaptureExport();
                preview.Fit();
                yield return null;
                SendF(tree);
                yield return null;
                AssertCentered(preview, node.Bounds);
                preview.Fit();
                yield return null;
                var before = InPreview(preview, Center(node.Bounds));
                var field = new TextField();
                tree.hierarchy.Add(field);
                yield return null;
                SendF(field);
                yield return null;
                Assert.That(InPreview(preview, Center(node.Bounds)), Is.EqualTo(before));
                field.RemoveFromHierarchy();
                SendF(tree, EventModifiers.Control);
                yield return null;
                Assert.That(InPreview(preview, Center(node.Bounds)), Is.EqualTo(before));
                SendF(preview);
                yield return null;
                AssertCentered(preview, node.Bounds);
                int groupId = node.ParentId.Value;
                int otherId = document.DrawNodes.First(n => n.Id != node.Id).Id;
                window.SelectLayers(new[] { groupId, otherId });
                var union = PixelRect.Union(document.GetBounds(groupId), document.GetBounds(otherId));
                Assert.That(window.FocusSelected(), Is.True);
                yield return null;
                AssertCentered(preview, union);
                Assert.That(window.Session.ContextVersion, Is.EqualTo(version));
                Assert.That(window.Session.CaptureVisibility().HiddenLayerIds,
                    Is.EqualTo(visibility.HiddenLayerIds));
                Assert.That(window.Session.CaptureVisibility().DocumentVisible,
                    Is.EqualTo(visibility.DocumentVisible));
                Assert.That(window.Session.CaptureExport().ExcludedLayerIds,
                    Is.EqualTo(export.ExcludedLayerIds));
                window.SelectLayer(null);
                var unchanged = InPreview(preview, Center(union));
                Assert.That(window.FocusSelected(), Is.False);
                SendF(tree);
                yield return null;
                Assert.That(InPreview(preview, Center(union)), Is.EqualTo(unchanged));
            }
            finally { window.Close(); }
        }
    }
}
