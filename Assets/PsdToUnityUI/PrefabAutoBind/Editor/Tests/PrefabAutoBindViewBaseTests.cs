using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PrefabAutoBind.Tests
{
    public sealed class PrefabAutoBindViewBaseTests
    {
        private GameObject root;

        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DirectReference_EnableDisable_RegistersOnceAndPreservesExternalListener()
        {
            root = new GameObject("DirectReferenceRoot");
            root.SetActive(false);
            var view = root.AddComponent<DirectReferenceViewBaseTestView>();
            var externalCallCount = 0;
            view.Clicked.AddListener(() => externalCallCount++);

            view.SimulateEnable();
            view.SimulateEnable();
            view.Clicked.Invoke();
            view.ValueChanged.Invoke(3.5f);

            Assert.That(view.GeneratedCallCount, Is.EqualTo(1));
            Assert.That(view.GeneratedValueTotal, Is.EqualTo(3.5f));
            Assert.That(externalCallCount, Is.EqualTo(1));

            view.SimulateDisable();
            view.Clicked.Invoke();
            view.ValueChanged.Invoke(2f);

            Assert.That(view.GeneratedCallCount, Is.EqualTo(1));
            Assert.That(view.GeneratedValueTotal, Is.EqualTo(3.5f));
            Assert.That(externalCallCount, Is.EqualTo(2));
        }

        [Test]
        public void BindByFind_FindsRootAndNestedComponents()
        {
            root = new GameObject("Root", typeof(RectTransform));
            var child = new GameObject("Child", typeof(RectTransform));
            child.transform.SetParent(root.transform, false);
            var view = new BindByFindViewBaseTestView { FindPath = "Child" };

            view.Bind(root);

            Assert.That(view.Found, Is.SameAs(child.GetComponent<RectTransform>()));

            view.Unbind();
            view.FindPath = string.Empty;
            view.Bind(root);

            Assert.That(view.Found, Is.SameAs(root.GetComponent<RectTransform>()));
        }

        [Test]
        public void BindByFind_RejectsNullRootMissingPathAndMissingComponent()
        {
            var view = new BindByFindViewBaseTestView { FindPath = string.Empty };
            Assert.Throws<ArgumentNullException>(() => view.Bind(null));

            root = new GameObject("Root", typeof(RectTransform));
            view.FindPath = "Missing";
            var pathException = Assert.Throws<InvalidOperationException>(() => view.Bind(root));
            StringAssert.Contains("Missing", pathException.Message);
            StringAssert.Contains(typeof(RectTransform).FullName, pathException.Message);

            var child = new GameObject("Child");
            child.transform.SetParent(root.transform, false);
            var missingComponentView = new MissingComponentBindByFindViewBaseTestView();
            var componentException = Assert.Throws<MissingComponentException>(() => missingComponentView.Bind(root));
            StringAssert.Contains("Child", componentException.Message);
            StringAssert.Contains(typeof(Button).FullName, componentException.Message);
        }

        [Test]
        public void BindByFind_RebindsWithoutDuplicatingOrRemovingExternalListener()
        {
            root = new GameObject("Root", typeof(RectTransform));
            var view = new BindByFindViewBaseTestView { FindPath = string.Empty };
            var externalCallCount = 0;
            view.Clicked.AddListener(() => externalCallCount++);

            view.Bind(root);
            view.Bind(root);
            view.Clicked.Invoke();

            Assert.That(view.GeneratedCallCount, Is.EqualTo(1));
            Assert.That(externalCallCount, Is.EqualTo(1));

            view.Unbind();
            view.Clicked.Invoke();

            Assert.That(view.GeneratedCallCount, Is.EqualTo(1));
            Assert.That(externalCallCount, Is.EqualTo(2));
        }

        [Test]
        public void BindByFind_UnbindCascadesToTrackedChildViews()
        {
            root = new GameObject("Root", typeof(RectTransform));
            var child = new BindByFindViewBaseTestView { FindPath = string.Empty };
            var parent = new BindByFindViewBaseTestView
            {
                FindPath = string.Empty,
                ChildToBind = child,
            };

            parent.Bind(root);
            child.Clicked.Invoke();
            Assert.That(child.GeneratedCallCount, Is.EqualTo(1));

            parent.Unbind();
            child.Clicked.Invoke();

            Assert.That(child.GeneratedCallCount, Is.EqualTo(1));
        }

        [Test]
        public void SettingsAssets_MatchTheirBaseClassesAndExportModes()
        {
            var bindByFindSettings = AssetDatabase.LoadAssetAtPath<PrefabAutoBindSettings>(
                "Assets/AutoBindDemo/BindByFind.PrefabAutoBindSettings.asset");
            var directReferenceSettings = AssetDatabase.LoadAssetAtPath<PrefabAutoBindSettings>(
                "Assets/AutoBindDemo/DirectReference.PrefabAutoBindSettings.asset");

            Assert.That(bindByFindSettings, Is.Not.Null);
            Assert.That(bindByFindSettings.ExportMode, Is.EqualTo(PrefabAutoBindExportMode.BindByFind));
            Assert.That(bindByFindSettings.ParentClassName, Is.EqualTo("PrefabAutoBind.BindByFindViewBase"));
            StringAssert.Contains("FindUI<${component}>", bindByFindSettings.FindStatementTemplate);
            StringAssert.Contains("TrackChildView", bindByFindSettings.PlainChildViewBindTemplate);
            StringAssert.Contains("${obj_ref}.Bind", bindByFindSettings.PlainChildViewBindTemplate);
            Assert.That(
                bindByFindSettings.AddEventStatementTemplate,
                Is.EqualTo("AddUIEvent(${event_obj}.${event_name}, ${handler_name});"));
            Assert.That(
                bindByFindSettings.RemoveEventStatementTemplate,
                Is.EqualTo("RemoveUIEvent(${event_obj}.${event_name}, ${handler_name});"));
            Assert.That(bindByFindSettings.RemoveEventStatementTemplate, Does.Not.Contain("RemoveAllListeners"));

            Assert.That(directReferenceSettings, Is.Not.Null);
            Assert.That(directReferenceSettings.ExportMode, Is.EqualTo(PrefabAutoBindExportMode.DirectReference));
            Assert.That(directReferenceSettings.ParentClassName, Is.EqualTo("PrefabAutoBind.DirectReferenceViewBase"));
            StringAssert.Contains("protected override void AddUIEvents()", directReferenceSettings.BindTemplate);
            StringAssert.Contains("protected override void RemoveUIEvents()", directReferenceSettings.BindTemplate);
            Assert.That(directReferenceSettings.BindTemplate, Does.Not.Contain("${find_statements}"));
            Assert.That(
                directReferenceSettings.AddEventStatementTemplate,
                Is.EqualTo("AddUIEvent(${event_obj}.${event_name}, ${handler_name});"));
            Assert.That(
                directReferenceSettings.RemoveEventStatementTemplate,
                Is.EqualTo("RemoveUIEvent(${event_obj}.${event_name}, ${handler_name});"));
            Assert.That(
                PrefabAutoBindEditorUtility.TryResolveMonoBehaviourBaseType(
                    directReferenceSettings.ParentClassName,
                    out var errorMessage),
                Is.True,
                errorMessage);
        }

        [Test]
        public void SettingsPresets_MatchCurrentModeAssetsAndCreateIndependentInstances()
        {
            var directSource = AssetDatabase.LoadAssetAtPath<PrefabAutoBindSettings>(
                "Assets/AutoBindDemo/DirectReference.PrefabAutoBindSettings.asset");
            var bindByFindSource = AssetDatabase.LoadAssetAtPath<PrefabAutoBindSettings>(
                "Assets/AutoBindDemo/BindByFind.PrefabAutoBindSettings.asset");
            var firstDirectPreset = PrefabAutoBindSettings.CreateDirectReferencePreset();
            var secondDirectPreset = PrefabAutoBindSettings.CreateDirectReferencePreset();
            var bindByFindPreset = PrefabAutoBindSettings.CreateBindByFindPreset();

            try
            {
                AssertSettingsEqual(directSource, firstDirectPreset);
                AssertSettingsEqual(directSource, secondDirectPreset);
                AssertSettingsEqual(bindByFindSource, bindByFindPreset);
                Assert.That(firstDirectPreset, Is.Not.SameAs(secondDirectPreset));
                Assert.That(
                    firstDirectPreset.BindTemplate,
                    Does.Contain("public partial class ${class_name}:${parent_class}"));
                Assert.That(
                    bindByFindPreset.BindTemplate,
                    Does.Contain("public partial class ${class_name}:${parent_class}"));
                Assert.That(firstDirectPreset.LogicOutputDirectory, Is.EqualTo("Assets/Scripts"));
                Assert.That(firstDirectPreset.PartialOutputDirectory, Is.EqualTo("Assets/Scripts"));
                Assert.That(bindByFindPreset.LogicOutputDirectory, Is.EqualTo("Assets/Scripts"));
                Assert.That(bindByFindPreset.PartialOutputDirectory, Is.EqualTo("Assets/Scripts"));

                firstDirectPreset.GeneratedNamespace = "Changed";
                Assert.That(secondDirectPreset.GeneratedNamespace, Is.EqualTo(directSource.GeneratedNamespace));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(firstDirectPreset);
                UnityEngine.Object.DestroyImmediate(secondDirectPreset);
                UnityEngine.Object.DestroyImmediate(bindByFindPreset);
            }
        }

        [Test]
        public void SettingsApi_UsesCurrentTemplateAndRootObjectNamesOnly()
        {
            var settingsType = typeof(PrefabAutoBindSettings);
            var configType = typeof(PrefabAutoBindConfig);

            Assert.That(settingsType.GetProperty("FindStatementTemplate"), Is.Not.Null);
            Assert.That(settingsType.GetProperty("AddEventStatementTemplate"), Is.Not.Null);
            Assert.That(settingsType.GetProperty("RemoveEventStatementTemplate"), Is.Not.Null);
            Assert.That(settingsType.GetProperty("EventHandlerNameTemplate"), Is.Not.Null);
            Assert.That(settingsType.GetProperty("RootObjectName"), Is.Not.Null);
            Assert.That(settingsType.GetField("DefaultFindStatementTemplate"), Is.Not.Null);
            Assert.That(settingsType.GetField("DefaultAddEventStatementTemplate"), Is.Not.Null);
            Assert.That(settingsType.GetField("DefaultRemoveEventStatementTemplate"), Is.Not.Null);
            Assert.That(settingsType.GetField("DefaultEventHandlerNameTemplate"), Is.Not.Null);
            Assert.That(settingsType.GetField("DefaultRootObjectName"), Is.Not.Null);

            Assert.That(settingsType.GetProperty("FindTemplate"), Is.Null);
            Assert.That(settingsType.GetProperty("EventTemplate"), Is.Null);
            Assert.That(settingsType.GetProperty("RemoveEventTemplate"), Is.Null);
            Assert.That(settingsType.GetProperty("EventNameRule"), Is.Null);
            Assert.That(settingsType.GetProperty("RootTransform"), Is.Null);
            Assert.That(settingsType.GetField("DefaultFindTemplate"), Is.Null);
            Assert.That(settingsType.GetField("DefaultEventTemplate"), Is.Null);
            Assert.That(settingsType.GetField("DefaultRemoveEventTemplate"), Is.Null);
            Assert.That(settingsType.GetField("DefaultEventNameRule"), Is.Null);
            Assert.That(settingsType.GetField("DefaultRootTransform"), Is.Null);
            Assert.That(configType.GetProperty("EventNameRule"), Is.Null);
            Assert.That(configType.GetField("DefaultEventNameRule"), Is.Null);
            Assert.That(configType.GetProperty("RootTransform"), Is.Null);
            Assert.That(configType.GetField("DefaultRootTransform"), Is.Null);
        }

        [Test]
        public void SettingsCreationMenus_HaveExpectedPathsPrioritiesAndFileNames()
        {
            AssertMenuItem(
                "CreateDirectReferenceSettingsFromAssetsMenu",
                "Assets/Prefab Auto Bind/Create DirectReference Settings",
                2001);
            AssertMenuItem(
                "CreateBindByFindSettingsFromAssetsMenu",
                "Assets/Prefab Auto Bind/Create BindByFind Settings",
                2002);
            Assert.That(
                PrefabAutoBindWindow.DirectReferenceSettingsFileName,
                Is.EqualTo("DirectReference.PrefabAutoBindSettings.asset"));
            Assert.That(
                PrefabAutoBindWindow.BindByFindSettingsFileName,
                Is.EqualTo("BindByFind.PrefabAutoBindSettings.asset"));
        }

        private static void AssertSettingsEqual(
            PrefabAutoBindSettings expected,
            PrefabAutoBindSettings actual)
        {
            Assert.That(expected, Is.Not.Null);
            Assert.That(actual, Is.Not.Null);
            Assert.That(actual.GeneratedNamespace, Is.EqualTo(expected.GeneratedNamespace));
            Assert.That(actual.ParentClassName, Is.EqualTo(expected.ParentClassName));
            Assert.That(actual.ExportMode, Is.EqualTo(expected.ExportMode));
            Assert.That(actual.LogicOutputDirectory, Is.EqualTo(expected.LogicOutputDirectory));
            Assert.That(actual.PartialOutputDirectory, Is.EqualTo(expected.PartialOutputDirectory));
            Assert.That(actual.BindTemplate, Is.EqualTo(expected.BindTemplate));
            Assert.That(actual.AddEventStatementTemplate, Is.EqualTo(expected.AddEventStatementTemplate));
            Assert.That(actual.RemoveEventStatementTemplate, Is.EqualTo(expected.RemoveEventStatementTemplate));
            Assert.That(
                actual.EventHandlerNameTemplate,
                Is.EqualTo(expected.EventHandlerNameTemplate));
            Assert.That(actual.RootObjectName, Is.EqualTo(expected.RootObjectName));
            Assert.That(actual.FindStatementTemplate, Is.EqualTo(expected.FindStatementTemplate));
            Assert.That(
                actual.PlainChildViewBindTemplate,
                Is.EqualTo(expected.PlainChildViewBindTemplate));
        }

        private static void AssertMenuItem(string methodName, string expectedPath, int expectedPriority)
        {
            var method = typeof(PrefabAutoBindWindow).GetMethod(
                methodName,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var menuItem = Attribute.GetCustomAttribute(method, typeof(MenuItem)) as MenuItem;
            Assert.That(menuItem, Is.Not.Null);
            Assert.That(menuItem.menuItem, Is.EqualTo(expectedPath));
            Assert.That(menuItem.priority, Is.EqualTo(expectedPriority));
            Assert.That(menuItem.validate, Is.False);
        }
    }

    public sealed class DirectReferenceViewBaseTestView : DirectReferenceViewBase
    {
        public readonly Button.ButtonClickedEvent Clicked = new Button.ButtonClickedEvent();
        public readonly Slider.SliderEvent ValueChanged = new Slider.SliderEvent();

        public int GeneratedCallCount { get; private set; }

        public float GeneratedValueTotal { get; private set; }

        public void SimulateEnable()
        {
            base.OnEnable();
        }

        public void SimulateDisable()
        {
            base.OnDisable();
        }

        protected override void AddUIEvents()
        {
            AddUIEvent(Clicked, OnClicked);
            AddUIEvent(ValueChanged, OnValueChanged);
        }

        protected override void RemoveUIEvents()
        {
            RemoveUIEvent(Clicked, OnClicked);
            RemoveUIEvent(ValueChanged, OnValueChanged);
        }

        private void OnClicked()
        {
            GeneratedCallCount++;
        }

        private void OnValueChanged(float value)
        {
            GeneratedValueTotal += value;
        }
    }

    internal class BindByFindViewBaseTestView : BindByFindViewBase
    {
        public readonly UnityEvent Clicked = new UnityEvent();

        public string FindPath { get; set; }

        public RectTransform Found { get; private set; }

        public int GeneratedCallCount { get; private set; }

        public BindByFindViewBaseTestView ChildToBind { get; set; }

        protected override void BindUI(GameObject bindRoot)
        {
            Found = FindUI<RectTransform>(bindRoot.transform, FindPath);
            if (ChildToBind != null)
            {
                TrackChildView(ChildToBind).Bind(bindRoot);
            }
        }

        protected override void AddUIEvents()
        {
            AddUIEvent(Clicked, OnClicked);
        }

        protected override void RemoveUIEvents()
        {
            RemoveUIEvent(Clicked, OnClicked);
        }

        private void OnClicked()
        {
            GeneratedCallCount++;
        }
    }

    internal sealed class MissingComponentBindByFindViewBaseTestView : BindByFindViewBase
    {
        protected override void BindUI(GameObject bindRoot)
        {
            FindUI<Button>(bindRoot.transform, "Child");
        }

        protected override void AddUIEvents()
        {
        }

        protected override void RemoveUIEvents()
        {
        }
    }
}
