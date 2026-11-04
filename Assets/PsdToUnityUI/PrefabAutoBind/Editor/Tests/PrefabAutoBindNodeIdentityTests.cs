using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PrefabAutoBind.Tests
{
    public sealed class PrefabAutoBindNodeIdentityTests
    {
        private string tempFolder;
        private readonly List<UnityEngine.Object> transientObjects = new List<UnityEngine.Object>();

        [SetUp]
        public void SetUp()
        {
            tempFolder = "Assets/__PrefabAutoBindTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", tempFolder.Substring("Assets/".Length));
        }

        [TearDown]
        public void TearDown()
        {
            DeleteGeneratedConfigAssets();

            for (var index = 0; index < transientObjects.Count; index++)
            {
                if (transientObjects[index] != null)
                {
                    UnityEngine.Object.DestroyImmediate(transientObjects[index]);
                }
            }

            transientObjects.Clear();
            AssetDatabase.DeleteAsset(tempFolder);
        }

        [Test]
        public void GetOrCreateConfig_CreatesAssetInConfigFolderBesideEditor()
        {
            var prefab = CreateNamedChildrenPrefab("CentralConfig.prefab", "Child");

            var config = PrefabAutoBindEditorUtility.GetOrCreateConfig(prefab);
            var configPath = AssetDatabase.GetAssetPath(config);

            Assert.That(
                Path.GetDirectoryName(configPath).Replace('\\', '/'),
                Is.EqualTo(PrefabAutoBindEditorUtility.GetConfigDirectoryPath()));
            Assert.That(configPath, Does.Not.StartWith(tempFolder + "/"));
        }

        [Test]
        public void BuildConfigDirectoryPath_FollowsMovedUtilityFolder()
        {
            const string movedUtilityPath =
                "Assets/PsdToUnityUI/PrefabAutoBind/Editor/PrefabAutoBindEditorUtility.cs";

            Assert.That(
                PrefabAutoBindEditorUtility.BuildConfigDirectoryPath(movedUtilityPath),
                Is.EqualTo("Assets/PsdToUnityUI/PrefabAutoBind/Config"));
        }

        [Test]
        public void GetOrCreateConfig_MovesLegacyAssetAndPreservesGuidAndData()
        {
            var prefab = CreateNamedChildrenPrefab("LegacyConfig.prefab", "Child");
            var legacyPath = tempFolder + "/LegacyConfig.PrefabAutoBind.asset";
            var legacyConfig = ScriptableObject.CreateInstance<PrefabAutoBindConfig>();
            legacyConfig.SchemaVersion = PrefabAutoBindConfig.CurrentSchemaVersion;
            legacyConfig.Prefab = prefab;
            legacyConfig.ClassName = "PreservedClassName";
            AssetDatabase.CreateAsset(legacyConfig, legacyPath);
            AssetDatabase.SaveAssets();
            var legacyGuid = AssetDatabase.AssetPathToGUID(legacyPath);

            var migrated = PrefabAutoBindEditorUtility.GetOrCreateConfig(prefab);
            var migratedPath = AssetDatabase.GetAssetPath(migrated);

            Assert.That(migrated, Is.SameAs(legacyConfig));
            Assert.That(migrated.ClassName, Is.EqualTo("PreservedClassName"));
            Assert.That(AssetDatabase.AssetPathToGUID(migratedPath), Is.EqualTo(legacyGuid));
            Assert.That(Path.GetDirectoryName(migratedPath).Replace('\\', '/'),
                Is.EqualTo(PrefabAutoBindEditorUtility.GetConfigDirectoryPath()));
            Assert.That(AssetDatabase.LoadMainAssetAtPath(legacyPath), Is.Null);
        }

        [Test]
        public void GetOrCreateConfig_AddsPrefabGuidWhenReadableNameCollides()
        {
            AssetDatabase.CreateFolder(tempFolder, "First");
            AssetDatabase.CreateFolder(tempFolder, "Second");
            var sharedName = "Shared_" + Guid.NewGuid().ToString("N");
            var firstPrefabPath = tempFolder + "/First/" + sharedName + ".prefab";
            var secondPrefabPath = tempFolder + "/Second/" + sharedName + ".prefab";
            var firstPrefab = CreateNamedChildrenPrefabAtPath(firstPrefabPath, "FirstChild");
            var secondPrefab = CreateNamedChildrenPrefabAtPath(secondPrefabPath, "SecondChild");

            var firstConfig = PrefabAutoBindEditorUtility.GetOrCreateConfig(firstPrefab);
            var secondConfig = PrefabAutoBindEditorUtility.GetOrCreateConfig(secondPrefab);
            var firstConfigPath = AssetDatabase.GetAssetPath(firstConfig);
            var secondConfigPath = AssetDatabase.GetAssetPath(secondConfig);

            Assert.That(Path.GetFileName(firstConfigPath),
                Is.EqualTo(sharedName + ".PrefabAutoBind.asset"));
            Assert.That(Path.GetFileName(secondConfigPath),
                Is.EqualTo(
                    sharedName + "." + AssetDatabase.AssetPathToGUID(secondPrefabPath) +
                    ".PrefabAutoBind.asset"));
            Assert.That(firstConfig.Prefab, Is.SameAs(firstPrefab));
            Assert.That(secondConfig.Prefab, Is.SameAs(secondPrefab));
            Assert.That(secondConfig, Is.Not.SameAs(firstConfig));
        }

        [Test]
        public void SyncNodes_UsesNodeIdForDuplicatePaths()
        {
            var prefab = CreateDuplicateSiblingPrefab("Duplicate.prefab");
            var snapshots = PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab);
            var duplicates = FindSnapshotsByPath(snapshots, "Duplicate");

            Assert.That(duplicates, Has.Count.EqualTo(2));
            Assert.That(duplicates[0].NodeId, Is.Not.EqualTo(duplicates[1].NodeId));

            var config = CreateConfig(prefab);
            PrefabAutoBindEditorUtility.SyncNodes(config, snapshots);
            var first = PrefabAutoBindEditorUtility.FindNodeConfig(config, duplicates[0].NodeId);
            var second = PrefabAutoBindEditorUtility.FindNodeConfig(config, duplicates[1].NodeId);
            first.NodeName = "FirstOnly";

            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));

            Assert.That(
                PrefabAutoBindEditorUtility.FindNodeConfig(config, duplicates[0].NodeId).NodeName,
                Is.EqualTo("Duplicate"));
            Assert.That(
                PrefabAutoBindEditorUtility.FindNodeConfig(config, duplicates[1].NodeId).NodeName,
                Is.EqualTo("Duplicate"));
            Assert.That(first, Is.Not.SameAs(second));
        }

        [Test]
        public void SyncNodes_TracksMovedAndRenamedPrefabNode()
        {
            var prefabPath = tempFolder + "/Move.prefab";
            var root = new GameObject("Root");
            var child = new GameObject("Child");
            child.transform.SetParent(root.transform, false);
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var config = CreateConfig(prefab);
            var initialSnapshots = PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab);
            var initialChild = FindSnapshotByPath(initialSnapshots, "Child");
            PrefabAutoBindEditorUtility.SyncNodes(config, initialSnapshots);
            PrefabAutoBindEditorUtility.FindNodeConfig(config, initialChild.NodeId).NodeName = "KeepMe";

            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var movedChild = contents.transform.GetChild(0);
                var parent = new GameObject("Parent");
                parent.transform.SetParent(contents.transform, false);
                movedChild.name = "Renamed";
                movedChild.SetParent(parent.transform, false);
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var movedSnapshots = PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab);
            var movedChildSnapshot = FindSnapshotByPath(movedSnapshots, "Parent/Renamed");
            Assert.That(movedChildSnapshot.NodeId, Is.EqualTo(initialChild.NodeId));
            PrefabAutoBindEditorUtility.SyncNodes(config, movedSnapshots);
            Assert.That(
                PrefabAutoBindEditorUtility.FindNodeConfig(config, movedChildSnapshot.NodeId).NodeName,
                Is.EqualTo("Renamed"));

            contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var parent = contents.transform.Find("Parent");
                UnityEngine.Object.DestroyImmediate(parent.GetChild(0).gameObject);
                var replacement = new GameObject("Renamed");
                replacement.transform.SetParent(parent, false);
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var recreatedSnapshots = PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab);
            var recreatedChild = FindSnapshotByPath(recreatedSnapshots, "Parent/Renamed");
            Assert.That(recreatedChild.NodeId, Is.Not.EqualTo(initialChild.NodeId));
            PrefabAutoBindEditorUtility.SyncNodes(config, recreatedSnapshots);
            Assert.That(
                PrefabAutoBindEditorUtility.FindNodeConfig(config, recreatedChild.NodeId).NodeName,
                Is.EqualTo("Renamed"));
        }

        [Test]
        public void BuildNodeSnapshots_DistinguishesRepeatedNestedPrefabInstances()
        {
            var nestedRoot = new GameObject("Nested");
            var nestedChild = new GameObject("NestedChild");
            nestedChild.transform.SetParent(nestedRoot.transform, false);
            var nestedPath = tempFolder + "/Nested.prefab";
            var nestedPrefab = PrefabUtility.SaveAsPrefabAsset(nestedRoot, nestedPath);
            UnityEngine.Object.DestroyImmediate(nestedRoot);

            var outerRoot = new GameObject("Outer");
            var firstInstance = (GameObject)PrefabUtility.InstantiatePrefab(nestedPrefab);
            var secondInstance = (GameObject)PrefabUtility.InstantiatePrefab(nestedPrefab);
            firstInstance.transform.SetParent(outerRoot.transform, false);
            secondInstance.transform.SetParent(outerRoot.transform, false);
            var outerPath = tempFolder + "/Outer.prefab";
            PrefabUtility.SaveAsPrefabAsset(outerRoot, outerPath);
            UnityEngine.Object.DestroyImmediate(outerRoot);

            var outerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(outerPath);
            var snapshots = PrefabAutoBindEditorUtility.BuildNodeSnapshots(outerPrefab);
            var nestedChildren = FindSnapshotsByDisplayName(snapshots, "NestedChild");

            Assert.That(nestedChildren, Has.Count.EqualTo(2));
            Assert.That(nestedChildren[0].NodeId, Is.Not.EqualTo(nestedChildren[1].NodeId));
            Assert.That(nestedChildren[0].NodeId.IsValid, Is.True);
            Assert.That(nestedChildren[1].NodeId.IsValid, Is.True);
        }

        [Test]
        public void GetOrCreateConfig_MigratesDuplicateLegacyPathsInOrder()
        {
            var prefab = CreateDuplicateSiblingPrefab("Migration.prefab");
            var prefabPath = AssetDatabase.GetAssetPath(prefab);
            var configPath = PrefabAutoBindEditorUtility.GetConfigAssetPath(prefabPath);
            var legacyConfig = ScriptableObject.CreateInstance<PrefabAutoBindConfig>();
            legacyConfig.SchemaVersion = 2;
            legacyConfig.Prefab = prefab;
            legacyConfig.Nodes.Add(new PrefabAutoBindNodeConfig
            {
                NodePath = string.Empty,
                NodeName = "LegacyRoot",
            });
            legacyConfig.Nodes.Add(new PrefabAutoBindNodeConfig
            {
                NodePath = "Duplicate",
                NodeName = "LegacyFirst",
            });
            legacyConfig.Nodes.Add(new PrefabAutoBindNodeConfig
            {
                NodePath = "Duplicate",
                NodeName = "LegacySecond",
            });
            AssetDatabase.CreateAsset(legacyConfig, configPath);
            AssetDatabase.SaveAssets();

            var migrated = PrefabAutoBindEditorUtility.GetOrCreateConfig(prefab);
            var duplicateNodes = FindNodesByPath(migrated, "Duplicate");

            Assert.That(migrated.SchemaVersion, Is.EqualTo(3));
            Assert.That(duplicateNodes, Has.Count.EqualTo(2));
            Assert.That(duplicateNodes[0].NodeName, Is.EqualTo("Duplicate"));
            Assert.That(duplicateNodes[1].NodeName, Is.EqualTo("Duplicate"));
            Assert.That(duplicateNodes[0].NodeId, Is.Not.EqualTo(duplicateNodes[1].NodeId));
        }

        [Test]
        public void AssetContextMenu_ValidatesOnlyActiveProjectPrefab()
        {
            var previousSelection = Selection.activeObject;
            var prefab = CreateNamedChildrenPrefab("ContextMenu.prefab", "Child");
            var sceneObject = new GameObject("SceneObject");
            transientObjects.Add(sceneObject);

            try
            {
                Selection.activeObject = prefab;

                Assert.That(PrefabAutoBindWindow.TryGetSelectedPrefab(out var selectedPrefab), Is.True);
                Assert.That(selectedPrefab, Is.SameAs(prefab));

                Selection.activeObject = sceneObject;
                Assert.That(PrefabAutoBindWindow.TryGetSelectedPrefab(out selectedPrefab), Is.False);
                Assert.That(selectedPrefab, Is.Null);

                var transientConfig = ScriptableObject.CreateInstance<PrefabAutoBindConfig>();
                var configScript = MonoScript.FromScriptableObject(transientConfig);
                UnityEngine.Object.DestroyImmediate(transientConfig);
                Selection.activeObject = configScript;
                Assert.That(PrefabAutoBindWindow.TryGetSelectedPrefab(out selectedPrefab), Is.False);
                Assert.That(selectedPrefab, Is.Null);
            }
            finally
            {
                Selection.activeObject = previousSelection;
            }
        }

        [Test]
        public void AssetContextMenu_OpensWindowCreatesConfigAndSwitchesPrefab()
        {
            var firstPrefab = CreateNamedChildrenPrefab("ContextFirst.prefab", "FirstChild");
            var secondPrefab = CreateNamedChildrenPrefab("ContextSecond.prefab", "SecondChild");
            var firstConfigPath = PrefabAutoBindEditorUtility.GetConfigAssetPath(
                AssetDatabase.GetAssetPath(firstPrefab));
            var secondConfig = PrefabAutoBindEditorUtility.GetOrCreateConfig(secondPrefab);
            PrefabAutoBindWindow window = null;

            try
            {
                Assert.That(AssetDatabase.LoadAssetAtPath<PrefabAutoBindConfig>(firstConfigPath), Is.Null);

                window = PrefabAutoBindWindow.OpenPrefab(firstPrefab);

                Assert.That(window, Is.Not.Null);
                Assert.That(GetCurrentWindowConfig(window).Prefab, Is.SameAs(firstPrefab));
                Assert.That(
                    AssetDatabase.LoadAssetAtPath<PrefabAutoBindConfig>(firstConfigPath),
                    Is.Not.Null);

                var reusedWindow = PrefabAutoBindWindow.OpenPrefab(secondPrefab);

                Assert.That(reusedWindow, Is.SameAs(window));
                Assert.That(GetCurrentWindowConfig(reusedWindow), Is.SameAs(secondConfig));
            }
            finally
            {
                if (window != null)
                {
                    window.Close();
                }
            }
        }

        [Test]
        public void ComponentEventCatalog_SupportsTextMeshProControls()
        {
            var inputEvents = PrefabAutoBindComponentEventCatalog.GetSupportedEvents(
                PrefabAutoBindEditorUtility.BuildStoredTypeName(typeof(TMP_InputField)));

            Assert.That(inputEvents.Count, Is.EqualTo(3));
            Assert.That(inputEvents[0].Kind, Is.EqualTo(PrefabAutoBindComponentEventKind.ValueChanged));
            Assert.That(inputEvents[0].EventPropertyName, Is.EqualTo("onValueChanged"));
            Assert.That(inputEvents[0].ListenerParameterTypeName, Is.EqualTo("string"));
            Assert.That(inputEvents[1].Kind, Is.EqualTo(PrefabAutoBindComponentEventKind.EndEdit));
            Assert.That(inputEvents[1].EventPropertyName, Is.EqualTo("onEndEdit"));
            Assert.That(inputEvents[1].ListenerParameterTypeName, Is.EqualTo("string"));
            Assert.That(inputEvents[2].Kind, Is.EqualTo(PrefabAutoBindComponentEventKind.Submit));
            Assert.That(inputEvents[2].EventPropertyName, Is.EqualTo("onSubmit"));
            Assert.That(inputEvents[2].ListenerParameterTypeName, Is.EqualTo("string"));

            var dropdownEvents = PrefabAutoBindComponentEventCatalog.GetSupportedEvents(
                PrefabAutoBindEditorUtility.BuildStoredTypeName(typeof(TMP_Dropdown)));

            Assert.That(dropdownEvents.Count, Is.EqualTo(1));
            Assert.That(dropdownEvents[0].Kind, Is.EqualTo(PrefabAutoBindComponentEventKind.ValueChanged));
            Assert.That(dropdownEvents[0].EventPropertyName, Is.EqualTo("onValueChanged"));
            Assert.That(dropdownEvents[0].ListenerParameterTypeName, Is.EqualTo("int"));
        }

        [Test]
        public void BindByFindPartial_GeneratesTextMeshProEvents()
        {
            var root = new GameObject("Root");
            var input = new GameObject("Input", typeof(RectTransform), typeof(TMP_InputField));
            var dropdown = new GameObject("Dropdown", typeof(RectTransform), typeof(TMP_Dropdown));
            input.transform.SetParent(root.transform, false);
            dropdown.transform.SetParent(root.transform, false);
            var prefabPath = tempFolder + "/TextMeshProEvents.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);

            var config = CreateConfig(prefab);
            var settings = CreateBindByFindSettings();
            settings.AddEventStatementTemplate = "AddUIEvent(${event_obj}.${event_name}, ${handler_name});";
            settings.RemoveEventStatementTemplate = "RemoveUIEvent(${event_obj}.${event_name}, ${handler_name});";
            config.ClassName = "TextMeshProEventsView";
            config.Settings = settings;
            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));

            var inputComponent = new PrefabAutoBindComponentConfig
            {
                ComponentTypeName = PrefabAutoBindEditorUtility.BuildStoredTypeName(typeof(TMP_InputField)),
                ComponentIndex = 0,
                FieldName = "messageInput",
            };
            inputComponent.Events.Add(PrefabAutoBindComponentEventKind.ValueChanged);
            inputComponent.Events.Add(PrefabAutoBindComponentEventKind.EndEdit);
            inputComponent.Events.Add(PrefabAutoBindComponentEventKind.Submit);
            FindNodesByPath(config, "Input")[0].Components.Add(inputComponent);

            var dropdownComponent = new PrefabAutoBindComponentConfig
            {
                ComponentTypeName = PrefabAutoBindEditorUtility.BuildStoredTypeName(typeof(TMP_Dropdown)),
                ComponentIndex = 0,
                FieldName = "choiceDropdown",
            };
            dropdownComponent.Events.Add(PrefabAutoBindComponentEventKind.ValueChanged);
            FindNodesByPath(config, "Dropdown")[0].Components.Add(dropdownComponent);

            var partial = BuildRootPartialForTest(config);

            StringAssert.Contains("using TMPro;", partial);
            StringAssert.Contains("private TMP_InputField messageInput;", partial);
            StringAssert.Contains("private TMP_Dropdown choiceDropdown;", partial);
            StringAssert.Contains("AddUIEvent(messageInput.onValueChanged, OnmessageInputonValueChanged);", partial);
            StringAssert.Contains("AddUIEvent(messageInput.onEndEdit, OnmessageInputonEndEdit);", partial);
            StringAssert.Contains("AddUIEvent(messageInput.onSubmit, OnmessageInputonSubmit);", partial);
            StringAssert.Contains("AddUIEvent(choiceDropdown.onValueChanged, OnchoiceDropdownonValueChanged);", partial);
            StringAssert.Contains("RemoveUIEvent(messageInput.onValueChanged, OnmessageInputonValueChanged);", partial);
            StringAssert.Contains("RemoveUIEvent(messageInput.onEndEdit, OnmessageInputonEndEdit);", partial);
            StringAssert.Contains("RemoveUIEvent(messageInput.onSubmit, OnmessageInputonSubmit);", partial);
            StringAssert.Contains("RemoveUIEvent(choiceDropdown.onValueChanged, OnchoiceDropdownonValueChanged);", partial);
            StringAssert.Contains("partial void OnmessageInputonValueChanged(string value);", partial);
            StringAssert.Contains("partial void OnmessageInputonEndEdit(string value);", partial);
            StringAssert.Contains("partial void OnmessageInputonSubmit(string value);", partial);
            StringAssert.Contains("partial void OnchoiceDropdownonValueChanged(int value);", partial);
            Assert.That(partial, Does.Not.Contain("${"));
        }

        [Test]
        public void DirectReferencePartial_RendersEventStatementsWithoutFindStatements()
        {
            var root = new GameObject("Root");
            var buttonObject = new GameObject(
                "ActionButton",
                typeof(RectTransform),
                typeof(UnityEngine.UI.Button));
            buttonObject.transform.SetParent(root.transform, false);
            var prefabPath = tempFolder + "/DirectReferenceStatements.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);

            var config = CreateConfig(prefab);
            config.ClassName = "DirectReferenceStatementsView";
            config.Settings = AssetDatabase.LoadAssetAtPath<PrefabAutoBindSettings>(
                "Assets/AutoBindDemo/DirectReference.PrefabAutoBindSettings.asset");
            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));

            var button = new PrefabAutoBindComponentConfig
            {
                ComponentTypeName = PrefabAutoBindEditorUtility.BuildStoredTypeName(
                    typeof(UnityEngine.UI.Button)),
                ComponentIndex = 0,
                FieldName = "actionButton",
            };
            button.Events.Add(PrefabAutoBindComponentEventKind.Click);
            FindNodesByPath(config, "ActionButton")[0].Components.Add(button);

            var partial = BuildRootPartialForTest(config);

            StringAssert.Contains("AddUIEvent(actionButton.onClick, OnactionButtononClick);", partial);
            StringAssert.Contains("RemoveUIEvent(actionButton.onClick, OnactionButtononClick);", partial);
            Assert.That(partial, Does.Not.Contain("FindUI<"));
            Assert.That(partial, Does.Not.Contain("${"));
        }

        [Test]
        public void DirectReferenceResolver_UsesHierarchyIndicesForDuplicatePaths()
        {
            var prefab = CreateDuplicateSiblingPrefab("DirectReference.prefab");
            var prefabPath = AssetDatabase.GetAssetPath(prefab);
            var duplicates = FindSnapshotsByPath(
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab),
                "Duplicate");
            var second = duplicates[1];

            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var resolved = PrefabAutoBindDirectReferenceBinder.ResolveNodeTransform(
                    contents.transform,
                    second.HierarchyIndices);

                Assert.That(resolved, Is.SameAs(contents.transform.GetChild(1)));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        [Test]
        public void RenamePrefabNodes_RenamesSingleNodeAndRefreshesSnapshotPath()
        {
            var prefab = CreateNamedChildrenPrefab("RenameSingle.prefab", "Child");
            var prefabPath = AssetDatabase.GetAssetPath(prefab);
            var config = CreateConfig(prefab);
            var snapshots = PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab);
            var childSnapshot = FindSnapshotByPath(snapshots, "Child");

            Assert.That(
                PrefabAutoBindEditorUtility.TryRenamePrefabNodes(
                    config,
                    new[] { childSnapshot },
                    "Renamed Child (1)",
                    out var errorMessage),
                Is.True,
                errorMessage);

            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Assert.That(contents.transform.GetChild(0).name, Is.EqualTo("Renamed Child (1)"));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            config.Prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var renamedSnapshots = PrefabAutoBindEditorUtility.BuildNodeSnapshots(config.Prefab);
            var renamedSnapshot = FindSnapshotByPath(renamedSnapshots, "Renamed Child (1)");
            PrefabAutoBindEditorUtility.SyncNodes(config, renamedSnapshots);

            Assert.That(renamedSnapshot.DisplayName, Is.EqualTo("Renamed Child (1)"));
            Assert.That(
                FindNodesByPath(config, "Renamed Child (1)")[0].NodeName,
                Is.EqualTo("Renamed Child (1)"));
        }

        [Test]
        public void RenamePrefabNodes_RenamesMultipleNodesToSameName()
        {
            var prefab = CreateNamedChildrenPrefab("RenameMultiple.prefab", "First", "Second");
            var prefabPath = AssetDatabase.GetAssetPath(prefab);
            var config = CreateConfig(prefab);
            var snapshots = PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab);
            var firstSnapshot = FindSnapshotByPath(snapshots, "First");
            var secondSnapshot = FindSnapshotByPath(snapshots, "Second");

            Assert.That(
                PrefabAutoBindEditorUtility.TryRenamePrefabNodes(
                    config,
                    new[] { firstSnapshot, secondSnapshot },
                    "Shared Name",
                    out var errorMessage),
                Is.True,
                errorMessage);

            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Assert.That(contents.transform.GetChild(0).name, Is.EqualTo("Shared Name"));
                Assert.That(contents.transform.GetChild(1).name, Is.EqualTo("Shared Name"));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            config.Prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var renamedSnapshots = PrefabAutoBindEditorUtility.BuildNodeSnapshots(config.Prefab);
            Assert.That(FindSnapshotsByPath(renamedSnapshots, "Shared Name"), Has.Count.EqualTo(2));
        }

        [Test]
        public void RenamePrefabNodes_RejectsInvalidHierarchyWithoutSaving()
        {
            var prefab = CreateNamedChildrenPrefab("RenameInvalid.prefab", "Child");
            var prefabPath = AssetDatabase.GetAssetPath(prefab);
            var config = CreateConfig(prefab);
            var invalidSnapshot = new PrefabAutoBindNodeSnapshot(
                new PrefabAutoBindNodeId(1, 0),
                default,
                "Missing",
                "Missing",
                new[] { 99 },
                new List<PrefabAutoBindAvailableComponent>());

            Assert.That(
                PrefabAutoBindEditorUtility.TryRenamePrefabNodes(
                    config,
                    new[] { invalidSnapshot },
                    "Should Not Save",
                    out var errorMessage),
                Is.False);
            StringAssert.Contains("could not resolve prefab node", errorMessage);

            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Assert.That(contents.transform.GetChild(0).name, Is.EqualTo("Child"));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        [Test]
        public void BindByFindValidation_IgnoresUnboundDuplicatePaths()
        {
            var prefab = CreateDuplicateSiblingPrefab("BindByFind.prefab");
            var config = CreateConfig(prefab);
            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            var settings = ScriptableObject.CreateInstance<PrefabAutoBindSettings>();
            transientObjects.Add(settings);
            config.Settings = settings;
            var duplicateNodes = FindNodesByPath(config, "Duplicate");
            duplicateNodes[0].Components.Add(new PrefabAutoBindComponentConfig
            {
                ComponentTypeName = typeof(Transform).AssemblyQualifiedName,
                ComponentIndex = 0,
                FieldName = "firstDuplicate",
            });

            settings.ExportMode = PrefabAutoBindExportMode.BindByFind;
            Assert.That(
                PrefabAutoBindCodeGenerator.TryValidateBindByFindNodePaths(config, out var errorMessage),
                Is.True);
            Assert.That(errorMessage, Is.Null);

            duplicateNodes[1].Components.Add(new PrefabAutoBindComponentConfig
            {
                ComponentTypeName = typeof(Transform).AssemblyQualifiedName,
                ComponentIndex = 0,
                FieldName = "secondDuplicate",
            });
            Assert.That(
                PrefabAutoBindCodeGenerator.TryValidateBindByFindNodePaths(config, out errorMessage),
                Is.False);
            StringAssert.Contains("exported node path", errorMessage);

            settings.ExportMode = PrefabAutoBindExportMode.DirectReference;
            Assert.That(
                PrefabAutoBindCodeGenerator.TryValidateBindByFindNodePaths(config, out errorMessage),
                Is.True);
            Assert.That(errorMessage, Is.Null);
        }

        [Test]
        public void PendingBindQueue_DeduplicatesAndRemovesConfigGuids()
        {
            var queue = new PrefabAutoBindPendingBindQueue();

            Assert.That(queue.Enqueue("config-guid"), Is.True);
            Assert.That(queue.Enqueue("config-guid"), Is.False);
            Assert.That(queue.Enqueue(string.Empty), Is.False);
            Assert.That(queue.Count, Is.EqualTo(1));
            Assert.That(queue.GetAt(0), Is.EqualTo("config-guid"));

            Assert.That(queue.Remove("config-guid"), Is.True);
            Assert.That(queue.Remove("config-guid"), Is.False);
            Assert.That(queue.Count, Is.Zero);
        }

        [Test]
        public void PendingBindScheduler_RejectsMissingConfigGuid()
        {
            Assert.That(
                PrefabAutoBindDirectReferenceBindScheduler.TryResolveConfig(
                    "00000000000000000000000000000000",
                    out var config,
                    out var errorMessage),
                Is.False);
            Assert.That(config, Is.Null);
            StringAssert.Contains("was deleted", errorMessage);
        }

        [Test]
        public void WindowReload_DoesNotOpenOrFocusWindowDuringPlayMode()
        {
            Assert.That(
                PrefabAutoBindWindowReloadBootstrap.ShouldOpenWindowAfterReload(1, false),
                Is.False);
            Assert.That(
                PrefabAutoBindWindowReloadBootstrap.ShouldOpenWindowAfterReload(0, true),
                Is.False);
            Assert.That(
                PrefabAutoBindWindowReloadBootstrap.ShouldOpenWindowAfterReload(0, false),
                Is.True);
        }

        [Test]
        public void WindowFooter_ShowsEnglishExportButtonsSideBySide()
        {
            var window = ScriptableObject.CreateInstance<PrefabAutoBindWindow>();
            transientObjects.Add(window);
            var buildFooter = typeof(PrefabAutoBindWindow).GetMethod(
                "BuildFooter",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(buildFooter, Is.Not.Null);

            var footer = buildFooter.Invoke(window, null) as UnityEngine.UIElements.VisualElement;
            Assert.That(footer, Is.Not.Null);
            var exportButton = footer.Q<UnityEngine.UIElements.Button>("exportButton");
            var regenerateAllButton = footer.Q<UnityEngine.UIElements.Button>("regenerateAllButton");

            Assert.That(exportButton, Is.Not.Null);
            Assert.That(regenerateAllButton, Is.Not.Null);
            Assert.That(exportButton.text, Is.EqualTo("Export"));
            Assert.That(regenerateAllButton.text, Is.EqualTo("Regenerate All"));
            Assert.That(exportButton.parent, Is.SameAs(regenerateAllButton.parent));
            Assert.That(
                exportButton.parent.style.flexDirection.value,
                Is.EqualTo(UnityEngine.UIElements.FlexDirection.Row));
            Assert.That(exportButton.style.flexGrow.value, Is.EqualTo(1f));
            Assert.That(regenerateAllButton.style.flexGrow.value, Is.EqualTo(1f));
            Assert.That(exportButton.style.flexBasis.value.value, Is.EqualTo(0f));
            Assert.That(regenerateAllButton.style.flexBasis.value.value, Is.EqualTo(0f));
            Assert.That(PrefabAutoBindWindow.RegenerateAllDialogTitle, Is.EqualTo("Regenerate All Scripts?"));
            Assert.That(
                PrefabAutoBindWindow.RegenerateAllDialogMessage,
                Is.EqualTo(
                    "This will overwrite all existing logic files and regenerate all .Bind.cs files. " +
                    "Any manual changes in the logic files will be permanently lost.\n\nContinue?"));
        }

        [Test]
        public void Export_OverwriteModeRegeneratesRootAndChildViewLogic()
        {
            var root = new GameObject("Root");
            var childViewObject = new GameObject("ChildView");
            var buttonObject = new GameObject(
                "ActionButton",
                typeof(RectTransform),
                typeof(UnityEngine.UI.Button));
            childViewObject.transform.SetParent(root.transform, false);
            buttonObject.transform.SetParent(childViewObject.transform, false);
            var prefabPath = tempFolder + "/RegenerateAll.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);

            var config = CreateConfig(prefab);
            var settings = CreateRegenerateAllTestSettings();
            config.ClassName = "RegenerateRootView";
            config.Settings = settings;
            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));

            var childView = FindNodesByPath(config, "ChildView")[0];
            childView.NodeKind = PrefabAutoBindNodeKind.View;
            childView.ViewClassName = "RegenerateChildView";
            childView.Settings = settings;

            var button = new PrefabAutoBindComponentConfig
            {
                ComponentTypeName = PrefabAutoBindEditorUtility.BuildStoredTypeName(
                    typeof(UnityEngine.UI.Button)),
                ComponentIndex = 0,
                FieldName = "actionButton",
            };
            button.Events.Add(PrefabAutoBindComponentEventKind.Click);
            FindNodesByPath(config, "ChildView/ActionButton")[0].Components.Add(button);

            var rootLogicPath = settings.LogicOutputDirectory + "/RegenerateRootView.cs";
            var childLogicPath = settings.LogicOutputDirectory + "/RegenerateChildView.cs";
            var rootBindPath = settings.PartialOutputDirectory + "/RegenerateRootView.Bind.cs";
            var childBindPath = settings.PartialOutputDirectory + "/RegenerateChildView.Bind.cs";

            var initialResult = PrefabAutoBindCodeGenerator.Export(config);
            Assert.That(initialResult.Success, Is.True, initialResult.Message);
            StringAssert.Contains("Created Logic:", initialResult.Message);
            StringAssert.Contains(rootLogicPath, initialResult.Message);
            StringAssert.Contains(childLogicPath, initialResult.Message);
            Assert.That(File.Exists(rootLogicPath), Is.True);
            Assert.That(File.Exists(childLogicPath), Is.True);
            Assert.That(File.Exists(rootBindPath), Is.True);
            Assert.That(File.Exists(childBindPath), Is.True);

            File.AppendAllText(rootLogicPath, "// KEEP ROOT CUSTOM LOGIC\n");
            File.AppendAllText(childLogicPath, "// KEEP CHILD CUSTOM LOGIC\n");

            var normalResult = PrefabAutoBindCodeGenerator.Export(config);
            Assert.That(normalResult.Success, Is.True, normalResult.Message);
            StringAssert.Contains("Skipped existing Logic:", normalResult.Message);
            StringAssert.Contains("KEEP ROOT CUSTOM LOGIC", File.ReadAllText(rootLogicPath));
            StringAssert.Contains("KEEP CHILD CUSTOM LOGIC", File.ReadAllText(childLogicPath));

            var overwriteResult = PrefabAutoBindCodeGenerator.Export(config, true);
            Assert.That(overwriteResult.Success, Is.True, overwriteResult.Message);
            StringAssert.Contains("Overwritten Logic:", overwriteResult.Message);
            StringAssert.Contains(rootLogicPath, overwriteResult.Message);
            StringAssert.Contains(childLogicPath, overwriteResult.Message);
            StringAssert.DoesNotContain("Skipped existing Logic:", overwriteResult.Message);

            var rootLogic = File.ReadAllText(rootLogicPath);
            var childLogic = File.ReadAllText(childLogicPath);
            StringAssert.DoesNotContain("KEEP ROOT CUSTOM LOGIC", rootLogic);
            StringAssert.DoesNotContain("KEEP CHILD CUSTOM LOGIC", childLogic);
            StringAssert.Contains("partial class RegenerateRootView", rootLogic);
            StringAssert.Contains("partial class RegenerateChildView", childLogic);
            StringAssert.Contains("partial void OnactionButtononClick()", childLogic);
            StringAssert.Contains("Overwritten Bind:", overwriteResult.Message);
        }

        [Test]
        public void DirectReferenceBindPlan_RebuildsHierarchyAndFieldBindings()
        {
            var prefab = CreateDuplicateSiblingPrefab("DirectReferencePlan.prefab");
            var config = CreateConfig(prefab);
            var settings = ScriptableObject.CreateInstance<PrefabAutoBindSettings>();
            transientObjects.Add(settings);
            config.ClassName = "DirectReferencePlanView";
            config.Settings = settings;
            settings.ExportMode = PrefabAutoBindExportMode.DirectReference;

            var snapshots = PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab);
            PrefabAutoBindEditorUtility.SyncNodes(config, snapshots);
            var duplicateNodes = FindNodesByPath(config, "Duplicate");
            duplicateNodes[1].Components.Add(new PrefabAutoBindComponentConfig
            {
                ComponentTypeName = typeof(Transform).AssemblyQualifiedName,
                ComponentIndex = 0,
                FieldName = "secondDuplicate",
            });

            Assert.That(
                PrefabAutoBindCodeGenerator.TryBuildDirectReferenceBindUnits(
                    config,
                    out var prefabPath,
                    out var units,
                    out var errorMessage),
                Is.True,
                errorMessage);
            Assert.That(prefabPath, Is.EqualTo(AssetDatabase.GetAssetPath(prefab)));
            Assert.That(units, Has.Count.EqualTo(1));
            Assert.That(units[0].Members, Has.Count.EqualTo(1));
            Assert.That(units[0].Members[0].FieldName, Is.EqualTo("secondDuplicate"));
            Assert.That(units[0].Members[0].HierarchyIndices, Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void DirectReferenceBindPlan_GroupsChildViewsIntoArrayContainer()
        {
            var prefab = CreateDuplicateSiblingPrefab("ChildViewArrayContainer.prefab");
            var config = CreateConfig(prefab);
            var settings = ScriptableObject.CreateInstance<PrefabAutoBindSettings>();
            transientObjects.Add(settings);
            config.ClassName = "ChildViewArrayRoot";
            config.Settings = settings;
            settings.ExportMode = PrefabAutoBindExportMode.DirectReference;

            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            var duplicateNodes = FindNodesByPath(config, "Duplicate");
            ConfigureViewNode(
                duplicateNodes[0],
                settings,
                "ItemView",
                PrefabAutoBindContainerKind.Array,
                "childViews");
            ConfigureViewNode(
                duplicateNodes[1],
                settings,
                "ItemView",
                PrefabAutoBindContainerKind.Array,
                "childViews");

            Assert.That(
                PrefabAutoBindCodeGenerator.TryBuildDirectReferenceBindUnits(
                    config,
                    out _,
                    out var units,
                    out var errorMessage),
                Is.True,
                errorMessage);

            var rootUnit = units[0];
            Assert.That(rootUnit.Containers, Has.Count.EqualTo(1));
            Assert.That(rootUnit.Containers[0].FieldName, Is.EqualTo("childViews"));
            Assert.That(rootUnit.Containers[0].ContainerKind, Is.EqualTo(PrefabAutoBindContainerKind.Array));
            Assert.That(rootUnit.Containers[0].Members, Has.Count.EqualTo(2));
            Assert.That(rootUnit.Containers[0].Members[0].Kind, Is.EqualTo(PrefabAutoBindDirectReferenceBinder.BindMemberKind.ChildView));
            Assert.That(rootUnit.Containers[0].Members[0].ChildClassName, Is.EqualTo("ItemView"));
            Assert.That(rootUnit.Containers[0].Members[0].HierarchyIndices, Is.EqualTo(new[] { 0 }));
            Assert.That(rootUnit.Containers[0].Members[1].Kind, Is.EqualTo(PrefabAutoBindDirectReferenceBinder.BindMemberKind.ChildView));
            Assert.That(rootUnit.Containers[0].Members[1].ChildClassName, Is.EqualTo("ItemView"));
            Assert.That(rootUnit.Containers[0].Members[1].HierarchyIndices, Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void DirectReferenceBindPlan_GroupsChildViewsIntoListContainer()
        {
            var prefab = CreateDuplicateSiblingPrefab("ChildViewListContainer.prefab");
            var config = CreateConfig(prefab);
            var settings = ScriptableObject.CreateInstance<PrefabAutoBindSettings>();
            transientObjects.Add(settings);
            config.ClassName = "ChildViewListRoot";
            config.Settings = settings;
            settings.ExportMode = PrefabAutoBindExportMode.DirectReference;

            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            var duplicateNodes = FindNodesByPath(config, "Duplicate");
            ConfigureViewNode(
                duplicateNodes[0],
                settings,
                "ItemView",
                PrefabAutoBindContainerKind.List,
                "childViews");
            ConfigureViewNode(
                duplicateNodes[1],
                settings,
                "ItemView",
                PrefabAutoBindContainerKind.List,
                "childViews");

            Assert.That(
                PrefabAutoBindCodeGenerator.TryBuildDirectReferenceBindUnits(
                    config,
                    out _,
                    out var units,
                    out var errorMessage),
                Is.True,
                errorMessage);

            var rootUnit = units[0];
            Assert.That(rootUnit.Containers, Has.Count.EqualTo(1));
            Assert.That(rootUnit.Containers[0].FieldName, Is.EqualTo("childViews"));
            Assert.That(rootUnit.Containers[0].ContainerKind, Is.EqualTo(PrefabAutoBindContainerKind.List));
            Assert.That(rootUnit.Containers[0].Members, Has.Count.EqualTo(2));
            Assert.That(rootUnit.Containers[0].Members[0].Kind, Is.EqualTo(PrefabAutoBindDirectReferenceBinder.BindMemberKind.ChildView));
            Assert.That(rootUnit.Containers[0].Members[1].Kind, Is.EqualTo(PrefabAutoBindDirectReferenceBinder.BindMemberKind.ChildView));
        }

        [Test]
        public void DirectReferenceBindPlan_KeepsPlainChildViewAndComponentContainersCompatible()
        {
            var prefab = CreateDuplicateSiblingPrefab("PlainChildViewAndComponentContainer.prefab");
            var config = CreateConfig(prefab);
            var settings = ScriptableObject.CreateInstance<PrefabAutoBindSettings>();
            transientObjects.Add(settings);
            config.ClassName = "PlainChildViewRoot";
            config.Settings = settings;
            settings.ExportMode = PrefabAutoBindExportMode.DirectReference;

            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            var duplicateNodes = FindNodesByPath(config, "Duplicate");
            ConfigureViewNode(
                duplicateNodes[0],
                settings,
                "ItemView",
                PrefabAutoBindContainerKind.None,
                string.Empty);
            duplicateNodes[1].Components.Add(new PrefabAutoBindComponentConfig
            {
                ComponentTypeName = typeof(Transform).AssemblyQualifiedName,
                ComponentIndex = 0,
                FieldName = "duplicateTransform",
                ContainerKind = PrefabAutoBindContainerKind.Array,
                ContainerName = "transforms",
            });

            Assert.That(
                PrefabAutoBindCodeGenerator.TryBuildDirectReferenceBindUnits(
                    config,
                    out _,
                    out var units,
                    out var errorMessage),
                Is.True,
                errorMessage);

            var rootUnit = units[0];
            Assert.That(rootUnit.Members, Has.Count.EqualTo(2));
            Assert.That(rootUnit.Members[0].Kind, Is.EqualTo(PrefabAutoBindDirectReferenceBinder.BindMemberKind.ChildView));
            Assert.That(rootUnit.Members[0].Container, Is.Null);
            Assert.That(rootUnit.Containers, Has.Count.EqualTo(1));
            Assert.That(rootUnit.Containers[0].FieldName, Is.EqualTo("transforms"));
            Assert.That(rootUnit.Containers[0].ContainerKind, Is.EqualTo(PrefabAutoBindContainerKind.Array));
            Assert.That(rootUnit.Containers[0].ComponentType, Is.EqualTo(typeof(Transform)));
            Assert.That(rootUnit.Containers[0].Members[0].Kind, Is.EqualTo(PrefabAutoBindDirectReferenceBinder.BindMemberKind.Component));
            Assert.That(rootUnit.Containers[0].Members[0].HierarchyIndices, Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void PasteConfig_CopiesViewSubtreeToCompatibleViewTarget()
        {
            var prefab = CreateCopyConfigPrefab("CopyConfigCompatible.prefab", true, true);
            var config = CreateConfig(prefab);
            var settings = ScriptableObject.CreateInstance<PrefabAutoBindSettings>();
            transientObjects.Add(settings);
            config.Settings = settings;
            var snapshots = SyncAndGetSnapshots(config);
            ConfigureCopyConfigSource(config, settings);

            var source = FindNodesByPath(config, "Source")[0];
            var target = FindNodesByPath(config, "Target")[0];
            var targetIcon = FindNodesByPath(config, "Target/Icon")[0];
            target.NodeKind = PrefabAutoBindNodeKind.View;

            Assert.That(
                PrefabAutoBindWindow.TryCopyViewConfigToTargets(
                    config,
                    snapshots,
                    source,
                    new[] { target },
                    out var errorMessage),
                Is.True,
                errorMessage);

            Assert.That(target.NodePath, Is.EqualTo("Target"));
            Assert.That(target.NodeKind, Is.EqualTo(PrefabAutoBindNodeKind.View));
            Assert.That(target.NodeName, Is.EqualTo("Target"));
            Assert.That(target.ViewClassName, Is.EqualTo("CopyItemView"));
            Assert.That(target.Settings, Is.SameAs(settings));
            Assert.That(target.ViewContainerKind, Is.EqualTo(PrefabAutoBindContainerKind.List));
            Assert.That(target.ViewContainerName, Is.EqualTo("copyItems"));
            Assert.That(targetIcon.NodePath, Is.EqualTo("Target/Icon"));
            Assert.That(targetIcon.NodeName, Is.EqualTo("Icon"));
            Assert.That(targetIcon.Components, Has.Count.EqualTo(1));
            Assert.That(targetIcon.Components[0].FieldName, Is.EqualTo("iconRenderer"));
            Assert.That(targetIcon.Components[0].AccessModifier, Is.EqualTo(PrefabAutoBindFieldAccessModifier.Protected));
            Assert.That(targetIcon.Components[0].ContainerKind, Is.EqualTo(PrefabAutoBindContainerKind.Array));
            Assert.That(targetIcon.Components[0].ContainerName, Is.EqualTo("renderers"));
            Assert.That(targetIcon.Components[0].Events, Has.Count.EqualTo(0));
            Assert.That(targetIcon.Components[0], Is.Not.SameAs(FindNodesByPath(config, "Source/Icon")[0].Components[0]));
        }

        [Test]
        public void PasteConfig_CopiesViewSubtreeToMultipleCompatibleViewTargets()
        {
            var prefab = CreateCopyConfigPrefab(
                "CopyConfigMultipleCompatible.prefab",
                true,
                true,
                true,
                true,
                true);
            var config = CreateConfig(prefab);
            var settings = ScriptableObject.CreateInstance<PrefabAutoBindSettings>();
            transientObjects.Add(settings);
            config.Settings = settings;
            var snapshots = SyncAndGetSnapshots(config);
            ConfigureCopyConfigSource(config, settings);

            var source = FindNodesByPath(config, "Source")[0];
            var sourceIcon = FindNodesByPath(config, "Source/Icon")[0];
            var firstTarget = FindNodesByPath(config, "Target")[0];
            var secondTarget = FindNodesByPath(config, "TargetB")[0];
            var firstTargetIcon = FindNodesByPath(config, "Target/Icon")[0];
            var secondTargetIcon = FindNodesByPath(config, "TargetB/Icon")[0];
            firstTarget.NodeKind = PrefabAutoBindNodeKind.View;
            secondTarget.NodeKind = PrefabAutoBindNodeKind.View;

            Assert.That(
                PrefabAutoBindWindow.TryCopyViewConfigToTargets(
                    config,
                    snapshots,
                    source,
                    new[] { firstTarget, secondTarget },
                    out var errorMessage),
                Is.True,
                errorMessage);

            Assert.That(firstTarget.ViewClassName, Is.EqualTo("CopyItemView"));
            Assert.That(secondTarget.ViewClassName, Is.EqualTo("CopyItemView"));
            Assert.That(firstTarget.Settings, Is.SameAs(settings));
            Assert.That(secondTarget.Settings, Is.SameAs(settings));
            Assert.That(firstTargetIcon.Components[0].FieldName, Is.EqualTo("iconRenderer"));
            Assert.That(secondTargetIcon.Components[0].FieldName, Is.EqualTo("iconRenderer"));
            Assert.That(firstTargetIcon.Components[0], Is.Not.SameAs(sourceIcon.Components[0]));
            Assert.That(secondTargetIcon.Components[0], Is.Not.SameAs(sourceIcon.Components[0]));
            Assert.That(secondTargetIcon.Components[0], Is.Not.SameAs(firstTargetIcon.Components[0]));
        }

        [Test]
        public void MultiSelection_SwitchesFieldsToViewsAndEnablesPasteConfig()
        {
            var prefab = CreateCopyConfigPrefab(
                "CopyConfigSwitchMultiple.prefab",
                true,
                true,
                true,
                true,
                true);
            var config = CreateConfig(prefab);
            var settings = ScriptableObject.CreateInstance<PrefabAutoBindSettings>();
            transientObjects.Add(settings);
            config.Settings = settings;
            var snapshots = SyncAndGetSnapshots(config);
            ConfigureCopyConfigSource(config, settings);

            var source = FindNodesByPath(config, "Source")[0];
            var firstTarget = FindNodesByPath(config, "Target")[0];
            var secondTarget = FindNodesByPath(config, "TargetB")[0];
            var targets = new[] { firstTarget, secondTarget };
            var selectedIds = new HashSet<PrefabAutoBindNodeId>
            {
                firstTarget.NodeId,
                secondTarget.NodeId,
            };
            var window = CreateConfiguredWindow(config, snapshots, source, selectedIds);

            var trySetNodeKinds = typeof(PrefabAutoBindWindow).GetMethod(
                "TrySetNodeKinds",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(trySetNodeKinds, Is.Not.Null);
            Assert.That(
                trySetNodeKinds.Invoke(
                    window,
                    new object[] { targets, PrefabAutoBindNodeKind.View }),
                Is.True);

            Assert.That(firstTarget.NodeKind, Is.EqualTo(PrefabAutoBindNodeKind.View));
            Assert.That(secondTarget.NodeKind, Is.EqualTo(PrefabAutoBindNodeKind.View));
            Assert.That(firstTarget.Settings, Is.SameAs(settings));
            Assert.That(secondTarget.Settings, Is.SameAs(settings));
            Assert.That(GetSelectedNodeIds(window), Is.EquivalentTo(selectedIds));

            var pasteButton = BuildMultiPasteButton(window, targets);
            Assert.That(pasteButton, Is.Not.Null);
            Assert.That(pasteButton.enabledSelf, Is.True);

            Assert.That(
                PrefabAutoBindWindow.TryCopyViewConfigToTargets(
                    config,
                    snapshots,
                    source,
                    targets,
                    out var errorMessage),
                Is.True,
                errorMessage);
            Assert.That(firstTarget.ViewClassName, Is.EqualTo("CopyItemView"));
            Assert.That(secondTarget.ViewClassName, Is.EqualTo("CopyItemView"));
            Assert.That(GetSelectedNodeIds(window), Is.EquivalentTo(selectedIds));
        }

        [Test]
        public void MultiPasteButton_RequiresValidSourceAndViewTargets()
        {
            var prefab = CreateCopyConfigPrefab(
                "CopyConfigButtonState.prefab",
                true,
                true,
                true,
                true,
                true);
            var config = CreateConfig(prefab);
            var settings = ScriptableObject.CreateInstance<PrefabAutoBindSettings>();
            transientObjects.Add(settings);
            config.Settings = settings;
            var snapshots = SyncAndGetSnapshots(config);
            ConfigureCopyConfigSource(config, settings);

            var source = FindNodesByPath(config, "Source")[0];
            var firstTarget = FindNodesByPath(config, "Target")[0];
            var secondTarget = FindNodesByPath(config, "TargetB")[0];
            firstTarget.NodeKind = PrefabAutoBindNodeKind.View;
            secondTarget.NodeKind = PrefabAutoBindNodeKind.View;
            var targets = new[] { firstTarget, secondTarget };
            var window = CreateConfiguredWindow(config, snapshots, null, null);

            Assert.That(BuildMultiPasteButton(window, targets).enabledSelf, Is.False);

            SetPrivateWindowField(window, "copiedViewConfig", config);
            SetPrivateWindowField(window, "copiedViewNodeId", source.NodeId);
            Assert.That(BuildMultiPasteButton(window, targets).enabledSelf, Is.True);
            Assert.That(BuildMultiPasteButton(window, new[] { source, firstTarget }).enabledSelf, Is.False);

            secondTarget.NodeKind = PrefabAutoBindNodeKind.Field;
            Assert.That(BuildMultiPasteButton(window, targets).enabledSelf, Is.False);

            secondTarget.NodeKind = PrefabAutoBindNodeKind.View;
            source.NodeKind = PrefabAutoBindNodeKind.Field;
            Assert.That(BuildMultiPasteButton(window, targets).enabledSelf, Is.False);
        }

        [Test]
        public void PasteConfig_RejectsFieldTargetAndKeepsConfigUnchanged()
        {
            var prefab = CreateCopyConfigPrefab("CopyConfigFieldTarget.prefab", true, true);
            var config = CreateConfig(prefab);
            var settings = ScriptableObject.CreateInstance<PrefabAutoBindSettings>();
            transientObjects.Add(settings);
            config.Settings = settings;
            var snapshots = SyncAndGetSnapshots(config);
            ConfigureCopyConfigSource(config, settings);

            var source = FindNodesByPath(config, "Source")[0];
            var target = FindNodesByPath(config, "Target")[0];

            Assert.That(
                PrefabAutoBindWindow.TryCopyViewConfigToTargets(
                    config,
                    snapshots,
                    source,
                    new[] { target },
                    out var errorMessage),
                Is.False);

            StringAssert.Contains("target node must be a view node", errorMessage);
            Assert.That(target.NodeKind, Is.EqualTo(PrefabAutoBindNodeKind.Field));
            Assert.That(target.ViewClassName, Is.Null.Or.Empty);
            Assert.That(target.Components, Is.Empty);
        }

        [Test]
        public void PasteConfig_RejectsTargetWithMissingChildAndKeepsConfigUnchanged()
        {
            var prefab = CreateCopyConfigPrefab("CopyConfigMissingChild.prefab", false, false);
            var config = CreateConfig(prefab);
            var settings = ScriptableObject.CreateInstance<PrefabAutoBindSettings>();
            transientObjects.Add(settings);
            config.Settings = settings;
            var snapshots = SyncAndGetSnapshots(config);
            ConfigureCopyConfigSource(config, settings);

            var source = FindNodesByPath(config, "Source")[0];
            var target = FindNodesByPath(config, "Target")[0];
            target.NodeKind = PrefabAutoBindNodeKind.View;

            Assert.That(
                PrefabAutoBindWindow.TryCopyViewConfigToTargets(
                    config,
                    snapshots,
                    source,
                    new[] { target },
                    out var errorMessage),
                Is.False);

            StringAssert.Contains("child count mismatch", errorMessage);
            Assert.That(target.NodeKind, Is.EqualTo(PrefabAutoBindNodeKind.View));
            Assert.That(target.ViewClassName, Is.Null.Or.Empty);
            Assert.That(target.Components, Is.Empty);
        }

        [Test]
        public void PasteConfig_RejectsTargetWithMissingComponentAndKeepsConfigUnchanged()
        {
            var prefab = CreateCopyConfigPrefab("CopyConfigMissingComponent.prefab", true, false);
            var config = CreateConfig(prefab);
            var settings = ScriptableObject.CreateInstance<PrefabAutoBindSettings>();
            transientObjects.Add(settings);
            config.Settings = settings;
            var snapshots = SyncAndGetSnapshots(config);
            ConfigureCopyConfigSource(config, settings);

            var source = FindNodesByPath(config, "Source")[0];
            var target = FindNodesByPath(config, "Target")[0];
            var targetIcon = FindNodesByPath(config, "Target/Icon")[0];
            target.NodeKind = PrefabAutoBindNodeKind.View;

            Assert.That(
                PrefabAutoBindWindow.TryCopyViewConfigToTargets(
                    config,
                    snapshots,
                    source,
                    new[] { target },
                    out var errorMessage),
                Is.False);

            StringAssert.Contains("missing component", errorMessage);
            Assert.That(target.NodeKind, Is.EqualTo(PrefabAutoBindNodeKind.View));
            Assert.That(targetIcon.Components, Is.Empty);
        }

        [Test]
        public void PasteConfig_IsAtomicWhenOneOfMultipleTargetsFails()
        {
            var prefab = CreateCopyConfigPrefab(
                "CopyConfigAtomic.prefab",
                true,
                true,
                true,
                false,
                false);
            var config = CreateConfig(prefab);
            var settings = ScriptableObject.CreateInstance<PrefabAutoBindSettings>();
            transientObjects.Add(settings);
            config.Settings = settings;
            var snapshots = SyncAndGetSnapshots(config);
            ConfigureCopyConfigSource(config, settings);

            var source = FindNodesByPath(config, "Source")[0];
            var compatibleTarget = FindNodesByPath(config, "Target")[0];
            var compatibleTargetIcon = FindNodesByPath(config, "Target/Icon")[0];
            var failingTarget = FindNodesByPath(config, "TargetB")[0];
            compatibleTarget.NodeKind = PrefabAutoBindNodeKind.View;
            failingTarget.NodeKind = PrefabAutoBindNodeKind.View;

            Assert.That(
                PrefabAutoBindWindow.TryCopyViewConfigToTargets(
                    config,
                    snapshots,
                    source,
                    new[] { compatibleTarget, failingTarget },
                    out var errorMessage),
                Is.False);

            StringAssert.Contains("No nodes were changed", errorMessage);
            Assert.That(compatibleTarget.NodeKind, Is.EqualTo(PrefabAutoBindNodeKind.View));
            Assert.That(compatibleTarget.ViewClassName, Is.Null.Or.Empty);
            Assert.That(compatibleTargetIcon.Components, Is.Empty);
            Assert.That(failingTarget.NodeKind, Is.EqualTo(PrefabAutoBindNodeKind.View));
        }

        [Test]
        public void BindByFindBindPlan_DoesNotCreateDeferredUnits()
        {
            var prefab = CreateDuplicateSiblingPrefab("BindByFindPlan.prefab");
            var config = CreateConfig(prefab);
            var settings = ScriptableObject.CreateInstance<PrefabAutoBindSettings>();
            transientObjects.Add(settings);
            config.ClassName = "BindByFindPlanView";
            config.Settings = settings;
            settings.ExportMode = PrefabAutoBindExportMode.BindByFind;

            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            var duplicateNodes = FindNodesByPath(config, "Duplicate");
            duplicateNodes[0].Components.Add(new PrefabAutoBindComponentConfig
            {
                ComponentTypeName = typeof(Transform).AssemblyQualifiedName,
                ComponentIndex = 0,
                FieldName = "firstDuplicate",
            });

            Assert.That(
                PrefabAutoBindCodeGenerator.TryBuildDirectReferenceBindUnits(
                    config,
                    out _,
                    out var units,
                    out var errorMessage),
                Is.True,
                errorMessage);
            Assert.That(units, Is.Empty);
        }

        [Test]
        public void BindByFindBindPlan_IncludesMonoBehaviourUnitsForPostCompileAttach()
        {
            var prefab = CreateNamedChildrenPrefab("BindByFindMonoPlan.prefab", "Bound");
            var config = CreateConfig(prefab);
            var settings = CreateBindByFindSettings("MonoBehaviour");
            config.ClassName = "BindByFindMonoRoot";
            config.Settings = settings;

            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            FindNodesByPath(config, "Bound")[0].Components.Add(new PrefabAutoBindComponentConfig
            {
                ComponentTypeName = typeof(Transform).AssemblyQualifiedName,
                ComponentIndex = 0,
                FieldName = "boundTransform",
            });

            Assert.That(
                PrefabAutoBindCodeGenerator.TryBuildDirectReferenceBindUnits(
                    config,
                    out _,
                    out var units,
                    out var errorMessage),
                Is.True,
                errorMessage);
            Assert.That(units, Has.Count.EqualTo(1));
            Assert.That(units[0].ClassName, Is.EqualTo("BindByFindMonoRoot"));
            Assert.That(units[0].AssignSerializedFields, Is.False);
        }

        [Test]
        public void BindByFindPartial_CreatesPlainChildViewAndPassesFoundGameObject()
        {
            var prefab = CreateNamedChildrenPrefab("PlainChildViewFind.prefab", "Child");
            var config = CreateConfig(prefab);
            config.ClassName = "PlainChildRoot";
            config.Settings = CreateBindByFindSettings();

            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            ConfigureViewNode(
                FindNodesByPath(config, "Child")[0],
                CreateBindByFindSettings(),
                "PlainChildView",
                PrefabAutoBindContainerKind.None,
                string.Empty);

            var partial = BuildRootPartialForTest(config);

            StringAssert.Contains("Child = new PlainChildView();", partial);
            StringAssert.Contains("Child.Bind(root.transform.Find(\"Child\").gameObject);", partial);
            Assert.That(partial, Does.Not.Contain("GetComponent<PlainChildView>"));
        }

        [Test]
        public void BindByFindPartial_UsesPlainChildViewBindTemplateFromChildSettings()
        {
            var prefab = CreateNamedChildrenPrefab("PlainChildViewCustomBind.prefab", "Child");
            var config = CreateConfig(prefab);
            config.ClassName = "PlainChildCustomRoot";
            config.Settings = CreateBindByFindSettings();
            var childSettings = CreateBindByFindSettings(
                plainChildViewBindTemplate:
                "${obj_name} = new ${class_name}();\n" +
                "${obj_ref}.Init(${obj_path});");

            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            ConfigureViewNode(
                FindNodesByPath(config, "Child")[0],
                childSettings,
                "PlainChildView",
                PrefabAutoBindContainerKind.None,
                string.Empty);

            var partial = BuildRootPartialForTest(config);

            StringAssert.Contains("Child = new PlainChildView();", partial);
            StringAssert.Contains("Child.Init(root.transform.Find(\"Child\"));", partial);
            Assert.That(partial, Does.Not.Contain("Child.Bind("));
        }

        [Test]
        public void BindByFindPartial_UsesPlainChildViewBindTemplateForMonoBehaviourChildView()
        {
            var prefab = CreateNamedChildrenPrefab("MonoChildViewFind.prefab", "Child");
            var config = CreateConfig(prefab);
            config.ClassName = "MonoChildRoot";
            config.Settings = CreateBindByFindSettings();

            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            var childSettings = CreateBindByFindSettings(
                bindTemplateInheritsMonoBehaviour: true,
                plainChildViewBindTemplate:
                "${obj_name} = ${obj_path}.GetComponent<${class_name}>();\n" +
                "${obj_ref}.Initialize();");
            ConfigureViewNode(
                FindNodesByPath(config, "Child")[0],
                childSettings,
                "MonoChildView",
                PrefabAutoBindContainerKind.None,
                string.Empty);

            var partial = BuildRootPartialForTest(config);

            StringAssert.Contains("Child = root.transform.Find(\"Child\").GetComponent<MonoChildView>();", partial);
            StringAssert.Contains("Child.Initialize();", partial);
            Assert.That(partial, Does.Not.Contain("new MonoChildView"));
        }

        [Test]
        public void BindByFindPartial_UsesPlainChildViewBindTemplateForMonoBehaviourChildViewList()
        {
            var prefab = CreateNamedChildrenPrefab("MonoChildViewListFind.prefab", "First", "Second");
            var config = CreateConfig(prefab);
            config.ClassName = "MonoChildListRoot";
            config.Settings = CreateBindByFindSettings();
            var childSettings = CreateBindByFindSettings(
                bindTemplateInheritsMonoBehaviour: true,
                plainChildViewBindTemplate:
                "${obj_name} = ${obj_path}.GetComponent<${class_name}>();\n" +
                "${obj_ref}.Initialize();");

            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            ConfigureViewNode(
                FindNodesByPath(config, "First")[0],
                childSettings,
                "MonoItemView",
                PrefabAutoBindContainerKind.List,
                "items");
            ConfigureViewNode(
                FindNodesByPath(config, "Second")[0],
                childSettings,
                "MonoItemView",
                PrefabAutoBindContainerKind.List,
                "items");

            var partial = BuildRootPartialForTest(config);

            StringAssert.Contains(
                "var temp_First = root.transform.Find(\"First\").GetComponent<MonoItemView>();",
                partial);
            StringAssert.Contains("temp_First.Initialize();", partial);
            StringAssert.Contains("items.Add(temp_First);", partial);
            Assert.That(partial, Does.Not.Contain("var temp_First.Initialize"));
        }

        [Test]
        public void BindByFindPartial_DeclaresListTemporaryForPlainChildViews()
        {
            var prefab = CreateNamedChildrenPrefab("PlainChildViewListFind.prefab", "First", "Second");
            var config = CreateConfig(prefab);
            config.ClassName = "PlainChildListRoot";
            config.Settings = CreateBindByFindSettings();
            var childSettings = CreateBindByFindSettings();

            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            ConfigureViewNode(
                FindNodesByPath(config, "First")[0],
                childSettings,
                "PlainItemView",
                PrefabAutoBindContainerKind.List,
                "items");
            ConfigureViewNode(
                FindNodesByPath(config, "Second")[0],
                childSettings,
                "PlainItemView",
                PrefabAutoBindContainerKind.List,
                "items");

            var partial = BuildRootPartialForTest(config);

            StringAssert.Contains("items = new List<PlainItemView>(2);", partial);
            StringAssert.Contains("var temp_First = new PlainItemView();", partial);
            StringAssert.Contains("temp_First.Bind(root.transform.Find(\"First\").gameObject);", partial);
            StringAssert.Contains("items.Add(temp_First);", partial);
            StringAssert.Contains("var temp_Second = new PlainItemView();", partial);
            StringAssert.Contains("temp_Second.Bind(root.transform.Find(\"Second\").gameObject);", partial);
            StringAssert.Contains("items.Add(temp_Second);", partial);
        }

        [Test]
        public void BindByFindPartial_UsesPlainChildViewBindTemplateForListTemporaries()
        {
            var prefab = CreateNamedChildrenPrefab("PlainChildViewListCustomBind.prefab", "First");
            var config = CreateConfig(prefab);
            config.ClassName = "PlainChildListCustomRoot";
            config.Settings = CreateBindByFindSettings();
            var childSettings = CreateBindByFindSettings(
                plainChildViewBindTemplate:
                "${obj_name} = Create${class_name}(${obj_path});\n" +
                "${obj_ref}.Init(${obj_path});");

            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            ConfigureViewNode(
                FindNodesByPath(config, "First")[0],
                childSettings,
                "PlainItemView",
                PrefabAutoBindContainerKind.List,
                "items");

            var partial = BuildRootPartialForTest(config);

            StringAssert.Contains("var temp_First = CreatePlainItemView(root.transform.Find(\"First\"));", partial);
            StringAssert.Contains("temp_First.Init(root.transform.Find(\"First\"));", partial);
            StringAssert.Contains("items.Add(temp_First);", partial);
            Assert.That(partial, Does.Not.Contain("temp_First.Bind("));
        }

        [Test]
        public void BindByFindValidation_RejectsPlainChildViewBindTemplateMissingRequiredTokens()
        {
            var prefab = CreateNamedChildrenPrefab("PlainChildViewInvalidBind.prefab", "Child");
            var config = CreateConfig(prefab);
            config.ClassName = "PlainChildInvalidRoot";
            config.Settings = CreateBindByFindSettings();
            var childSettings = CreateBindByFindSettings(
                plainChildViewBindTemplate:
                "${obj_name} = new ${class_name}();");

            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            ConfigureViewNode(
                FindNodesByPath(config, "Child")[0],
                childSettings,
                "PlainChildView",
                PrefabAutoBindContainerKind.None,
                string.Empty);

            Assert.That(
                TryValidateExportTemplatesForTest(config, out var errorMessage),
                Is.False);
            StringAssert.Contains("Plain Child View Bind", errorMessage);
            StringAssert.Contains("${obj_path}", errorMessage);
        }

        [Test]
        public void BindByFindValidation_ValidatesPlainChildViewBindTemplateForMonoBehaviourChildView()
        {
            var prefab = CreateNamedChildrenPrefab("MonoChildViewInvalidBind.prefab", "Child");
            var config = CreateConfig(prefab);
            config.ClassName = "MonoChildInvalidRoot";
            config.Settings = CreateBindByFindSettings();
            var childSettings = CreateBindByFindSettings(
                bindTemplateInheritsMonoBehaviour: true,
                plainChildViewBindTemplate:
                "${obj_name} = ${obj_path}.GetComponent<MonoChildView>();");

            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            ConfigureViewNode(
                FindNodesByPath(config, "Child")[0],
                childSettings,
                "MonoChildView",
                PrefabAutoBindContainerKind.None,
                string.Empty);

            Assert.That(
                TryValidateExportTemplatesForTest(config, out var errorMessage),
                Is.False);
            StringAssert.Contains("Plain Child View Bind", errorMessage);
            StringAssert.Contains("${class_name}", errorMessage);
        }

        [TestCase("findfunction", "find_statements")]
        [TestCase("register_event", "add_event_statements")]
        [TestCase("remove_event", "remove_event_statements")]
        public void BindTemplateValidation_RejectsRemovedStatementToken(
            string removedPlaceholder,
            string replacementPlaceholder)
        {
            var prefab = CreateNamedChildrenPrefab(
                "Removed" + removedPlaceholder.Replace("_", string.Empty) + ".prefab",
                "Child");
            var config = CreateConfig(prefab);
            config.Settings = CreateBindByFindSettings();
            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            AddChildTransformBinding(config);
            config.Settings.BindTemplate = config.Settings.BindTemplate.Replace(
                "${" + replacementPlaceholder + "}",
                "${" + removedPlaceholder + "}");

            Assert.That(
                TryValidateExportTemplatesForTest(config, out var errorMessage),
                Is.False);
            StringAssert.Contains("${" + removedPlaceholder + "}", errorMessage);
            StringAssert.Contains("${" + replacementPlaceholder + "}", errorMessage);
        }

        [Test]
        public void BindByFindValidation_RequiresFindStatementsToken()
        {
            var prefab = CreateNamedChildrenPrefab("MissingFindStatements.prefab", "Child");
            var config = CreateConfig(prefab);
            config.Settings = CreateBindByFindSettings();
            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            AddChildTransformBinding(config);
            config.Settings.BindTemplate = config.Settings.BindTemplate.Replace(
                "        ${find_statements}\n",
                string.Empty);

            Assert.That(
                TryValidateExportTemplatesForTest(config, out var errorMessage),
                Is.False);
            StringAssert.Contains("Template must contain ${find_statements}.", errorMessage);
        }

        [Test]
        public void BindByFindValidation_RequiresRootObjectToken()
        {
            var prefab = CreateNamedChildrenPrefab("MissingRootObject.prefab", "Child");
            var config = CreateConfig(prefab);
            config.Settings = CreateBindByFindSettings();
            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            AddChildTransformBinding(config);
            config.Settings.BindTemplate = config.Settings.BindTemplate.Replace(
                "${root_object}",
                "root");

            Assert.That(
                TryValidateExportTemplatesForTest(config, out var errorMessage),
                Is.False);
            StringAssert.Contains("Template must contain ${root_object}.", errorMessage);
        }

        [TestCase("gameObject.transform")]
        [TestCase("two words")]
        [TestCase("class")]
        public void BindByFindValidation_RequiresRootObjectNameIdentifier(string rootObjectName)
        {
            var prefab = CreateNamedChildrenPrefab("InvalidRootObject.prefab", "Child");
            var config = CreateConfig(prefab);
            config.Settings = CreateBindByFindSettings();
            config.Settings.RootObjectName = rootObjectName;
            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            AddChildTransformBinding(config);

            Assert.That(
                TryValidateExportTemplatesForTest(config, out var errorMessage),
                Is.False);
            StringAssert.Contains("Root Object Name must be a valid C# identifier.", errorMessage);
        }

        [Test]
        public void BindByFindGeneration_RendersRootObjectNameInSignatureAndFindStatements()
        {
            var prefab = CreateNamedChildrenPrefab("RootObjectRendering.prefab", "Child");
            var config = CreateConfig(prefab);
            config.Settings = CreateBindByFindSettings();
            config.Settings.RootObjectName = "_bindRoot";
            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            AddChildTransformBinding(config);

            var partial = BuildRootPartialForTest(config);

            StringAssert.Contains("Bind(GameObject _bindRoot)", partial);
            StringAssert.Contains("childTransform = _bindRoot.transform.Find(\"Child\")", partial);
            Assert.That(partial, Does.Not.Contain("${root_object}"));
        }

        [Test]
        public void BindTemplateValidation_RequiresStatementTokensOnStandaloneLines()
        {
            var prefab = CreateNamedChildrenPrefab("InlineFindStatements.prefab", "Child");
            var config = CreateConfig(prefab);
            config.Settings = CreateBindByFindSettings();
            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            AddChildTransformBinding(config);
            config.Settings.BindTemplate = config.Settings.BindTemplate.Replace(
                "${find_statements}",
                "var invalid = ${find_statements};");

            Assert.That(
                TryValidateExportTemplatesForTest(config, out var errorMessage),
                Is.False);
            StringAssert.Contains(
                "${find_statements} in Template must appear on a line by itself.",
                errorMessage);
        }

        [Test]
        public void DirectReferenceValidation_DoesNotRequireFindStatementsToken()
        {
            var prefab = CreateNamedChildrenPrefab("DirectReferenceNoFindStatements.prefab", "Child");
            var config = CreateConfig(prefab);
            var settings = ScriptableObject.CreateInstance<PrefabAutoBindSettings>();
            transientObjects.Add(settings);
            settings.ExportMode = PrefabAutoBindExportMode.DirectReference;
            settings.BindTemplate = settings.BindTemplate.Replace(
                "        ${find_statements}\n",
                string.Empty);
            settings.BindTemplate = settings.BindTemplate.Replace("${root_object}", "directRoot");
            config.Settings = settings;
            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            AddChildTransformBinding(config);

            Assert.That(
                TryValidateExportTemplatesForTest(config, out var errorMessage),
                Is.True,
                errorMessage);
        }

        [Test]
        public void ClearDescendantComponentBindings_ClearsOnlyTheSelectedSubtree()
        {
            var root = new GameObject("Root");
            var parent = new GameObject("Parent");
            var child = new GameObject("Child");
            var grandchild = new GameObject("Grandchild");
            var sibling = new GameObject("Sibling");
            parent.transform.SetParent(root.transform, false);
            child.transform.SetParent(parent.transform, false);
            grandchild.transform.SetParent(child.transform, false);
            sibling.transform.SetParent(root.transform, false);
            var prefabPath = tempFolder + "/ClearDescendants.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);

            var config = CreateConfig(prefab);
            PrefabAutoBindEditorUtility.SyncNodes(
                config,
                PrefabAutoBindEditorUtility.BuildNodeSnapshots(prefab));
            var parentNode = FindNodesByPath(config, "Parent")[0];
            var childNode = FindNodesByPath(config, "Parent/Child")[0];
            var grandchildNode = FindNodesByPath(config, "Parent/Child/Grandchild")[0];
            var siblingNode = FindNodesByPath(config, "Sibling")[0];
            childNode.Components.Add(new PrefabAutoBindComponentConfig());
            grandchildNode.Components.Add(new PrefabAutoBindComponentConfig());
            siblingNode.Components.Add(new PrefabAutoBindComponentConfig());

            var clearedBindingCount =
                PrefabAutoBindEditorUtility.ClearDescendantComponentBindings(config, parentNode);

            Assert.That(clearedBindingCount, Is.EqualTo(2));
            Assert.That(childNode.Components, Is.Empty);
            Assert.That(grandchildNode.Components, Is.Empty);
            Assert.That(siblingNode.Components, Has.Count.EqualTo(1));
        }

        private GameObject CreateDuplicateSiblingPrefab(string fileName)
        {
            var root = new GameObject("Root");
            var first = new GameObject("Duplicate");
            var second = new GameObject("Duplicate");
            first.transform.SetParent(root.transform, false);
            second.transform.SetParent(root.transform, false);
            var path = tempFolder + "/" + fileName;
            PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        private GameObject CreateNamedChildrenPrefab(string fileName, params string[] childNames)
        {
            return CreateNamedChildrenPrefabAtPath(tempFolder + "/" + fileName, childNames);
        }

        private static GameObject CreateNamedChildrenPrefabAtPath(string path, params string[] childNames)
        {
            var root = new GameObject("Root");
            for (var index = 0; index < childNames.Length; index++)
            {
                var child = new GameObject(childNames[index]);
                child.transform.SetParent(root.transform, false);
            }

            PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        private void DeleteGeneratedConfigAssets()
        {
            var configGuids = AssetDatabase.FindAssets("t:PrefabAutoBindConfig");
            for (var index = 0; index < configGuids.Length; index++)
            {
                var configPath = AssetDatabase.GUIDToAssetPath(configGuids[index]);
                var config = AssetDatabase.LoadAssetAtPath<PrefabAutoBindConfig>(configPath);
                var prefabPath = config != null && config.Prefab != null
                    ? AssetDatabase.GetAssetPath(config.Prefab)
                    : string.Empty;
                if (prefabPath.StartsWith(tempFolder + "/", StringComparison.Ordinal))
                {
                    AssetDatabase.DeleteAsset(configPath);
                }
            }
        }

        private PrefabAutoBindConfig CreateConfig(GameObject prefab)
        {
            var config = ScriptableObject.CreateInstance<PrefabAutoBindConfig>();
            config.SchemaVersion = PrefabAutoBindConfig.CurrentSchemaVersion;
            config.Prefab = prefab;
            transientObjects.Add(config);
            return config;
        }

        private static void AddChildTransformBinding(PrefabAutoBindConfig config)
        {
            FindNodesByPath(config, "Child")[0].Components.Add(new PrefabAutoBindComponentConfig
            {
                ComponentTypeName = typeof(Transform).AssemblyQualifiedName,
                ComponentIndex = 0,
                FieldName = "childTransform",
            });
        }

        private static PrefabAutoBindConfig GetCurrentWindowConfig(PrefabAutoBindWindow window)
        {
            var field = typeof(PrefabAutoBindWindow).GetField(
                "currentConfig",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return field.GetValue(window) as PrefabAutoBindConfig;
        }

        private PrefabAutoBindWindow CreateConfiguredWindow(
            PrefabAutoBindConfig config,
            List<PrefabAutoBindNodeSnapshot> snapshots,
            PrefabAutoBindNodeConfig copiedSource,
            HashSet<PrefabAutoBindNodeId> selectedIds)
        {
            var window = ScriptableObject.CreateInstance<PrefabAutoBindWindow>();
            transientObjects.Add(window);
            SetPrivateWindowField(window, "currentConfig", config);
            SetPrivateWindowField(window, "nodeSnapshots", snapshots);
            SetPrivateWindowField(window, "nodeSummaryLabel", new Label());
            if (copiedSource != null)
            {
                SetPrivateWindowField(window, "copiedViewConfig", config);
                SetPrivateWindowField(window, "copiedViewNodeId", copiedSource.NodeId);
            }

            if (selectedIds != null)
            {
                SetPrivateWindowField(window, "selectedNodeIds", selectedIds);
            }

            return window;
        }

        private static Button BuildMultiPasteButton(
            PrefabAutoBindWindow window,
            IReadOnlyList<PrefabAutoBindNodeConfig> nodeConfigs)
        {
            var buildMultiViewDetailPanel = typeof(PrefabAutoBindWindow).GetMethod(
                "BuildMultiViewDetailPanel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(buildMultiViewDetailPanel, Is.Not.Null);
            var panel = buildMultiViewDetailPanel.Invoke(window, new object[] { nodeConfigs }) as VisualElement;
            Assert.That(panel, Is.Not.Null);
            return panel.Q<Button>("multiPasteConfigButton");
        }

        private static HashSet<PrefabAutoBindNodeId> GetSelectedNodeIds(PrefabAutoBindWindow window)
        {
            var field = typeof(PrefabAutoBindWindow).GetField(
                "selectedNodeIds",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return field.GetValue(window) as HashSet<PrefabAutoBindNodeId>;
        }

        private static void SetPrivateWindowField(
            PrefabAutoBindWindow window,
            string fieldName,
            object value)
        {
            var field = typeof(PrefabAutoBindWindow).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Window field not found: " + fieldName);
            field.SetValue(window, value);
        }

        private PrefabAutoBindSettings CreateRegenerateAllTestSettings()
        {
            var settings = CreateBindByFindSettings("PrefabAutoBind.BindByFindViewBase");
            settings.GeneratedNamespace = "PrefabAutoBind.Tests.Generated";
            settings.LogicOutputDirectory = tempFolder + "/Logic";
            settings.PartialOutputDirectory = tempFolder + "/Generated";
            settings.AddEventStatementTemplate = "AddUIEvent(${event_obj}.${event_name}, ${handler_name});";
            settings.RemoveEventStatementTemplate = "RemoveUIEvent(${event_obj}.${event_name}, ${handler_name});";
            settings.BindTemplate =
                "${usings}\n" +
                "\n" +
                "public partial class ${class_name} : PrefabAutoBind.BindByFindViewBase\n" +
                "{\n" +
                "    ${fields}\n" +
                "\n" +
                "    protected override void BindUI(GameObject ${root_object})\n" +
                "    {\n" +
                "        ${find_statements}\n" +
                "    }\n" +
                "\n" +
                "    protected override void AddUIEvents()\n" +
                "    {\n" +
                "        ${add_event_statements}\n" +
                "    }\n" +
                "\n" +
                "    protected override void RemoveUIEvents()\n" +
                "    {\n" +
                "        ${remove_event_statements}\n" +
                "    }\n" +
                "\n" +
                "    ${event_handlers}\n" +
                "}\n";
            return settings;
        }

        private PrefabAutoBindSettings CreateBindByFindSettings(
            string parentClassName = null,
            bool bindTemplateInheritsMonoBehaviour = false,
            string plainChildViewBindTemplate = null)
        {
            var settings = ScriptableObject.CreateInstance<PrefabAutoBindSettings>();
            transientObjects.Add(settings);
            settings.ExportMode = PrefabAutoBindExportMode.BindByFind;
            settings.ParentClassName = parentClassName ?? string.Empty;
            settings.FindStatementTemplate = "${obj_name} = ${parent_obj}.transform.Find(\"${path}\").GetComponent<${component}>();";
            settings.BindTemplate = bindTemplateInheritsMonoBehaviour
                ? PrefabAutoBindSettings.DefaultBindTemplate.Replace(
                    "public partial class ${class_name}",
                    "public partial class ${class_name} : MonoBehaviour")
                : PrefabAutoBindSettings.DefaultBindTemplate;
            if (plainChildViewBindTemplate != null)
            {
                settings.PlainChildViewBindTemplate = plainChildViewBindTemplate;
            }

            return settings;
        }

        private static string BuildRootPartialForTest(PrefabAutoBindConfig config)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var generatorType = typeof(PrefabAutoBindCodeGenerator);
            var tryBuildExportGraph = generatorType.GetMethod("TryBuildExportGraph", flags);
            var buildPartialFile = generatorType.GetMethod("BuildPartialFile", flags);
            Assert.That(tryBuildExportGraph, Is.Not.Null);
            Assert.That(buildPartialFile, Is.Not.Null);

            var arguments = new object[] { config, null, null };
            Assert.That((bool)tryBuildExportGraph.Invoke(null, arguments), Is.True, arguments[2] as string);

            var graph = arguments[1];
            var rootUnit = graph.GetType()
                .GetField("RootUnit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .GetValue(graph);
            return (string)buildPartialFile.Invoke(null, new[] { rootUnit });
        }

        private static bool TryValidateExportTemplatesForTest(
            PrefabAutoBindConfig config,
            out string errorMessage)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var generatorType = typeof(PrefabAutoBindCodeGenerator);
            var tryBuildExportGraph = generatorType.GetMethod("TryBuildExportGraph", flags);
            var tryValidateExportTemplates = generatorType.GetMethod("TryValidateExportTemplates", flags);
            Assert.That(tryBuildExportGraph, Is.Not.Null);
            Assert.That(tryValidateExportTemplates, Is.Not.Null);

            var buildArguments = new object[] { config, null, null };
            if (!(bool)tryBuildExportGraph.Invoke(null, buildArguments))
            {
                errorMessage = buildArguments[2] as string;
                return false;
            }

            var validateArguments = new[] { buildArguments[1], null };
            var result = (bool)tryValidateExportTemplates.Invoke(null, validateArguments);
            errorMessage = validateArguments[1] as string;
            return result;
        }

        private List<PrefabAutoBindNodeSnapshot> SyncAndGetSnapshots(PrefabAutoBindConfig config)
        {
            var snapshots = PrefabAutoBindEditorUtility.BuildNodeSnapshots(config.Prefab);
            PrefabAutoBindEditorUtility.SyncNodes(config, snapshots);
            return snapshots;
        }

        private GameObject CreateCopyConfigPrefab(
            string fileName,
            bool targetHasIcon,
            bool targetIconHasRenderer,
            bool includeSecondTarget = false,
            bool secondTargetHasIcon = false,
            bool secondTargetIconHasRenderer = false)
        {
            var root = new GameObject("Root");
            CreateCopyConfigBranch(root.transform, "Source", true, true);
            CreateCopyConfigBranch(root.transform, "Target", targetHasIcon, targetIconHasRenderer);
            if (includeSecondTarget)
            {
                CreateCopyConfigBranch(root.transform, "TargetB", secondTargetHasIcon, secondTargetIconHasRenderer);
            }

            var path = tempFolder + "/" + fileName;
            PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        private static void CreateCopyConfigBranch(
            Transform parent,
            string branchName,
            bool hasIcon,
            bool iconHasRenderer)
        {
            var branch = new GameObject(branchName);
            branch.transform.SetParent(parent, false);
            if (!hasIcon)
            {
                return;
            }

            var icon = new GameObject("Icon");
            icon.transform.SetParent(branch.transform, false);
            if (iconHasRenderer)
            {
                icon.AddComponent<CanvasRenderer>();
            }
        }

        private static void ConfigureCopyConfigSource(
            PrefabAutoBindConfig config,
            PrefabAutoBindSettings settings)
        {
            var source = FindNodesByPath(config, "Source")[0];
            var sourceIcon = FindNodesByPath(config, "Source/Icon")[0];
            ConfigureViewNode(
                source,
                settings,
                "CopyItemView",
                PrefabAutoBindContainerKind.List,
                "copyItems");
            sourceIcon.Components.Add(new PrefabAutoBindComponentConfig
            {
                ComponentTypeName = PrefabAutoBindEditorUtility.BuildStoredTypeName(typeof(CanvasRenderer)),
                ComponentIndex = 0,
                FieldName = "iconRenderer",
                AccessModifier = PrefabAutoBindFieldAccessModifier.Protected,
                ContainerKind = PrefabAutoBindContainerKind.Array,
                ContainerName = "renderers",
            });
        }

        private static void ConfigureViewNode(
            PrefabAutoBindNodeConfig node,
            PrefabAutoBindSettings settings,
            string viewClassName,
            PrefabAutoBindContainerKind containerKind,
            string containerName)
        {
            node.NodeKind = PrefabAutoBindNodeKind.View;
            node.ViewClassName = viewClassName;
            node.Settings = settings;
            node.ViewContainerKind = containerKind;
            node.ViewContainerName = containerName;
        }

        private static PrefabAutoBindNodeSnapshot FindSnapshotByPath(
            IReadOnlyList<PrefabAutoBindNodeSnapshot> snapshots,
            string path)
        {
            for (var index = 0; index < snapshots.Count; index++)
            {
                if (snapshots[index].Path == path)
                {
                    return snapshots[index];
                }
            }

            Assert.Fail("Snapshot path not found: " + path);
            return null;
        }

        private static List<PrefabAutoBindNodeSnapshot> FindSnapshotsByPath(
            IReadOnlyList<PrefabAutoBindNodeSnapshot> snapshots,
            string path)
        {
            var result = new List<PrefabAutoBindNodeSnapshot>();
            for (var index = 0; index < snapshots.Count; index++)
            {
                if (snapshots[index].Path == path)
                {
                    result.Add(snapshots[index]);
                }
            }

            return result;
        }

        private static List<PrefabAutoBindNodeSnapshot> FindSnapshotsByDisplayName(
            IReadOnlyList<PrefabAutoBindNodeSnapshot> snapshots,
            string displayName)
        {
            var result = new List<PrefabAutoBindNodeSnapshot>();
            for (var index = 0; index < snapshots.Count; index++)
            {
                if (snapshots[index].DisplayName == displayName)
                {
                    result.Add(snapshots[index]);
                }
            }

            return result;
        }

        private static List<PrefabAutoBindNodeConfig> FindNodesByPath(
            PrefabAutoBindConfig config,
            string path)
        {
            var result = new List<PrefabAutoBindNodeConfig>();
            for (var index = 0; index < config.Nodes.Count; index++)
            {
                if (config.Nodes[index].NodePath == path)
                {
                    result.Add(config.Nodes[index]);
                }
            }

            return result;
        }
    }
}
