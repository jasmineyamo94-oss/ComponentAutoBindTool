using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace PrefabAutoBind
{
    public sealed class PrefabAutoBindWindow : EditorWindow
    {
        private const string PersistedConfigPathKey = "PrefabAutoBind.Window.ConfigPath";
        private const string PersistedSelectedNodeIdKey = "PrefabAutoBind.Window.SelectedNodeId";
        private const string PersistedSelectedNodeIdsKey = "PrefabAutoBind.Window.SelectedNodeIds";
        private const string OpenSelectedPrefabMenuPath =
            "Assets/Prefab Auto Bind/Open in AutoBind Editor";
        private const string CreateDirectReferenceSettingsMenuPath =
            "Assets/Prefab Auto Bind/Create DirectReference Settings";
        private const string CreateBindByFindSettingsMenuPath =
            "Assets/Prefab Auto Bind/Create BindByFind Settings";
        internal const string DirectReferenceSettingsFileName =
            "DirectReference.PrefabAutoBindSettings.asset";
        internal const string BindByFindSettingsFileName =
            "BindByFind.PrefabAutoBindSettings.asset";
        internal const string RegenerateAllButtonText = "Regenerate All";
        internal const string RegenerateAllDialogTitle = "Regenerate All Scripts?";
        internal const string RegenerateAllDialogMessage =
            "This will overwrite all existing logic files and regenerate all .Bind.cs files. " +
            "Any manual changes in the logic files will be permanently lost.\n\nContinue?";

        [Serializable]
        private sealed class PersistedSelection
        {
            public PrefabAutoBindNodeId anchorNodeId;
            public List<PrefabAutoBindNodeId> nodeIds = new List<PrefabAutoBindNodeId>();
        }

        private sealed class MultiComponentBinding
        {
            public string TypeName;
            public int ComponentIndex;
            public readonly List<PrefabAutoBindNodeConfig> Nodes = new List<PrefabAutoBindNodeConfig>();
            public readonly List<PrefabAutoBindComponentConfig> Components = new List<PrefabAutoBindComponentConfig>();
        }

        private sealed class ViewConfigCopyPlan
        {
            public PrefabAutoBindNodeConfig TargetRoot;
            public readonly List<ViewConfigCopyPair> Pairs = new List<ViewConfigCopyPair>();
        }

        private readonly struct ViewConfigCopyPair
        {
            public ViewConfigCopyPair(PrefabAutoBindNodeConfig source, PrefabAutoBindNodeConfig target)
            {
                Source = source;
                Target = target;
            }

            public PrefabAutoBindNodeConfig Source { get; }

            public PrefabAutoBindNodeConfig Target { get; }
        }

        private PrefabAutoBindConfig currentConfig;
        private List<PrefabAutoBindNodeSnapshot> nodeSnapshots = new List<PrefabAutoBindNodeSnapshot>();
        private readonly Dictionary<PrefabAutoBindNodeId, bool> expandedNodeStates =
            new Dictionary<PrefabAutoBindNodeId, bool>();
        private readonly Dictionary<PrefabAutoBindNodeId, bool> nodeHasChildren =
            new Dictionary<PrefabAutoBindNodeId, bool>();
        private readonly Dictionary<PrefabAutoBindNodeId, PrefabAutoBindNodeId> parentNodeIds =
            new Dictionary<PrefabAutoBindNodeId, PrefabAutoBindNodeId>();
        private PrefabAutoBindNodeId selectedNodeId;
        [NonSerialized] private HashSet<PrefabAutoBindNodeId> selectedNodeIds =
            new HashSet<PrefabAutoBindNodeId>();
        [NonSerialized] private PrefabAutoBindConfig copiedViewConfig;
        [NonSerialized] private PrefabAutoBindNodeId copiedViewNodeId;
        [SerializeField] private string persistedConfigPath;
        [SerializeField] private PrefabAutoBindNodeId persistedSelectedNodeId;
        [SerializeField] private List<PrefabAutoBindNodeId> persistedSelectedNodeIds =
            new List<PrefabAutoBindNodeId>();

        private ObjectField prefabField;
        private TextField classNameField;
        private ObjectField settingsField;
        private Label nodeSummaryLabel;
        private ScrollView nodeTreeScrollView;
        private ScrollView detailScrollView;
        private Label logicPreviewLabel;
        private Label partialPreviewLabel;

        private static Color WindowBackgroundColor => EditorGUIUtility.isProSkin
            ? new Color(0.22f, 0.22f, 0.22f)
            : new Color(0.76f, 0.76f, 0.76f);

        private static Color PanelBackgroundColor => EditorGUIUtility.isProSkin
            ? new Color(0.24f, 0.24f, 0.24f)
            : new Color(0.82f, 0.82f, 0.82f);

        private static Color InsetBackgroundColor => EditorGUIUtility.isProSkin
            ? new Color(0.18f, 0.18f, 0.18f)
            : new Color(0.9f, 0.9f, 0.9f);

        private static Color BorderColor => EditorGUIUtility.isProSkin
            ? new Color(0.12f, 0.12f, 0.12f)
            : new Color(0.63f, 0.63f, 0.63f);

        private static Color SecondaryTextColor => EditorGUIUtility.isProSkin
            ? new Color(0.78f, 0.78f, 0.78f)
            : new Color(0.28f, 0.28f, 0.28f);

        private static Color PrimaryTextColor => EditorGUIUtility.isProSkin
            ? new Color(0.86f, 0.86f, 0.86f)
            : new Color(0.12f, 0.12f, 0.12f);

        private static Color SelectionBackgroundColor => EditorGUIUtility.isProSkin
            ? new Color(0.24f, 0.37f, 0.59f)
            : new Color(0.24f, 0.49f, 0.9f);

        private static Color SelectedTextColor => Color.white;

        private static Color TagBackgroundColor => EditorGUIUtility.isProSkin
            ? new Color(0.3f, 0.3f, 0.3f)
            : new Color(0.72f, 0.72f, 0.72f);

        private static Color ViewTagBackgroundColor => EditorGUIUtility.isProSkin
            ? new Color(0.2f, 0.43f, 0.33f)
            : new Color(0.56f, 0.81f, 0.67f);

        private static Color TagTextColor => EditorGUIUtility.isProSkin
            ? new Color(0.92f, 0.92f, 0.92f)
            : new Color(0.12f, 0.12f, 0.12f);

        [MenuItem("Tools/Prefab Auto Bind/Open Window")]
        public static void OpenWindow()
        {
            GetOrOpenWindow();
        }

        [MenuItem(OpenSelectedPrefabMenuPath, false, 2000)]
        private static void OpenSelectedPrefabFromAssetsMenu()
        {
            TryOpenSelectedPrefab();
        }

        [MenuItem(OpenSelectedPrefabMenuPath, true)]
        private static bool ValidateOpenSelectedPrefabFromAssetsMenu()
        {
            return TryGetSelectedPrefab(out _);
        }

        [MenuItem(CreateDirectReferenceSettingsMenuPath, false, 2001)]
        private static void CreateDirectReferenceSettingsFromAssetsMenu()
        {
            ProjectWindowUtil.CreateAsset(
                PrefabAutoBindSettings.CreateDirectReferencePreset(),
                DirectReferenceSettingsFileName);
        }

        [MenuItem(CreateBindByFindSettingsMenuPath, false, 2002)]
        private static void CreateBindByFindSettingsFromAssetsMenu()
        {
            ProjectWindowUtil.CreateAsset(
                PrefabAutoBindSettings.CreateBindByFindPreset(),
                BindByFindSettingsFileName);
        }

        internal static PrefabAutoBindWindow TryOpenSelectedPrefab()
        {
            return TryGetSelectedPrefab(out var prefab)
                ? OpenPrefab(prefab)
                : null;
        }

        internal static PrefabAutoBindWindow OpenPrefab(GameObject prefab)
        {
            if (!IsPrefabAsset(prefab))
            {
                return null;
            }

            var window = GetOrOpenWindow();
            window.LoadPrefab(prefab);
            window.Focus();
            return window;
        }

        internal static bool TryGetSelectedPrefab(out GameObject prefab)
        {
            prefab = Selection.activeObject as GameObject;
            if (IsPrefabAsset(prefab))
            {
                return true;
            }

            prefab = null;
            return false;
        }

        private static bool IsPrefabAsset(GameObject prefab)
        {
            if (prefab == null)
            {
                return false;
            }

            var assetPath = AssetDatabase.GetAssetPath(prefab);
            return !string.IsNullOrWhiteSpace(assetPath) &&
                   assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        }

        internal static PrefabAutoBindWindow GetOrOpenWindow()
        {
            var window = GetWindow<PrefabAutoBindWindow>();
            window.titleContent = new GUIContent("Prefab Auto Bind");
            window.minSize = new Vector2(1080f, 640f);
            return window;
        }

        [DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            var windows = Resources.FindObjectsOfTypeAll<PrefabAutoBindWindow>();
            for (var index = 0; index < windows.Length; index++)
            {
                windows[index].RequestPersistedStateRestore();
            }
        }

        private void OnEnable()
        {
            Selection.selectionChanged -= OnSelectionChanged;
            Selection.selectionChanged += OnSelectionChanged;
            QueueStateRestore();
        }

        private void OnDisable()
        {
            Selection.selectionChanged -= OnSelectionChanged;
            EditorApplication.delayCall -= RestoreWindowStateIfNeeded;
        }

        private void CreateGUI()
        {
            BuildWindowUi();
            TryLoadSelectionPrefab();
            QueueStateRestore();
        }

        private void BuildWindowUi()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.flexDirection = FlexDirection.Column;
            rootVisualElement.style.paddingLeft = 12f;
            rootVisualElement.style.paddingRight = 12f;
            rootVisualElement.style.paddingTop = 12f;
            rootVisualElement.style.paddingBottom = 12f;
            rootVisualElement.style.backgroundColor = WindowBackgroundColor;
            rootVisualElement.UnregisterCallback<DragUpdatedEvent>(OnWindowDragUpdated);
            rootVisualElement.UnregisterCallback<DragPerformEvent>(OnWindowDragPerform);
            RegisterWindowDropTarget();

            rootVisualElement.Add(BuildToolbar());
            rootVisualElement.Add(BuildContentArea());
        }

        private void OnSelectionChanged()
        {
            if (prefabField == null || classNameField == null || settingsField == null)
            {
                return;
            }

            TryLoadSelectionPrefab();
        }

        private void QueueStateRestore()
        {
            EditorApplication.delayCall -= RestoreWindowStateIfNeeded;
            EditorApplication.delayCall += RestoreWindowStateIfNeeded;
        }

        private void RestoreWindowStateIfNeeded()
        {
            EditorApplication.delayCall -= RestoreWindowStateIfNeeded;

            if (prefabField == null || classNameField == null || settingsField == null)
            {
                QueueStateRestore();
                return;
            }

            if (currentConfig != null && currentConfig.Prefab != null)
            {
                return;
            }

            TryLoadSelectionPrefab();
            if (currentConfig == null)
            {
                TryRestorePersistedWindowState();
            }
        }

        internal void RequestPersistedStateRestore()
        {
            currentConfig = null;
            nodeSnapshots.Clear();
            expandedNodeStates.Clear();
            nodeHasChildren.Clear();
            selectedNodeId = default;
            SelectedNodeIds.Clear();
            ClearCopiedViewConfig();
            BuildWindowUi();
            QueueStateRestore();
        }

        private VisualElement BuildToolbar()
        {
            var toolbar = new VisualElement();
            toolbar.style.flexDirection = FlexDirection.Column;
            toolbar.style.marginBottom = 10f;

            var pickerRow = new VisualElement();
            pickerRow.style.flexDirection = FlexDirection.Row;
            pickerRow.style.alignItems = Align.Center;
            pickerRow.style.marginBottom = 8f;

            var dragHint = new Label("Drag prefab here or select");
            dragHint.style.width = 220f;
            dragHint.style.flexShrink = 0f;
            dragHint.style.marginRight = 8f;
            dragHint.style.color = SecondaryTextColor;

            var prefabLabel = new Label("Prefab");
            prefabLabel.style.width = 52f;
            prefabLabel.style.flexShrink = 0f;
            prefabLabel.style.marginRight = 8f;

            prefabField = new ObjectField
            {
                objectType = typeof(GameObject),
                allowSceneObjects = false,
            };
            prefabField.style.flexGrow = 1f;
            prefabField.style.flexShrink = 1f;
            prefabField.style.flexBasis = 0f;
            prefabField.style.minWidth = 0f;
            prefabField.style.marginRight = 8f;
            prefabField.RegisterValueChangedCallback(OnPrefabChanged);

            pickerRow.Add(dragHint);
            pickerRow.Add(prefabLabel);
            pickerRow.Add(prefabField);
            RegisterPrefabDropTarget(pickerRow);

            classNameField = new TextField("Class Name")
            {
                value = string.Empty,
                isDelayed = true,
            };
            classNameField.style.width = 220f;
            classNameField.RegisterValueChangedCallback(evt =>
            {
                if (currentConfig == null)
                {
                    return;
                }

                currentConfig.ClassName = PrefabAutoBindEditorUtility.MakeSafeIdentifier(
                    evt.newValue,
                    PrefabAutoBindEditorUtility.BuildDefaultClassName(currentConfig.Prefab));
                classNameField.SetValueWithoutNotify(currentConfig.ClassName);
                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                UpdatePreviewLabels();
            });

            settingsField = new ObjectField("Settings")
            {
                objectType = typeof(PrefabAutoBindSettings),
                allowSceneObjects = false,
            };
            settingsField.style.flexGrow = 1f;
            settingsField.style.flexShrink = 1f;
            settingsField.style.minWidth = 280f;
            settingsField.RegisterValueChangedCallback(evt =>
            {
                if (currentConfig == null)
                {
                    return;
                }

                currentConfig.Settings = evt.newValue as PrefabAutoBindSettings;
                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                UpdatePreviewLabels();
                RebuildDetailPane();
            });

            var refreshButton = new Button(RefreshSnapshots)
            {
                text = "Refresh",
            };

            var configRow = new VisualElement();
            configRow.style.flexDirection = FlexDirection.Row;
            configRow.style.alignItems = Align.Center;
            configRow.Add(classNameField);
            configRow.Add(settingsField);
            configRow.Add(refreshButton);

            toolbar.Add(pickerRow);
            toolbar.Add(configRow);
            return toolbar;
        }

        private VisualElement BuildContentArea()
        {
            var splitView = new TwoPaneSplitView(1, 210, TwoPaneSplitViewOrientation.Vertical);
            splitView.style.flexGrow = 1f;

            splitView.Add(BuildMainArea());
            splitView.Add(BuildFooter());
            return splitView;
        }

        private VisualElement BuildMainArea()
        {
            var splitView = new TwoPaneSplitView(0, 360, TwoPaneSplitViewOrientation.Horizontal);
            splitView.style.flexGrow = 1f;

            splitView.Add(BuildNodeListPane());
            splitView.Add(BuildDetailPane());
            return splitView;
        }

        private VisualElement BuildNodeListPane()
        {
            var container = CreateCard();
            container.style.marginRight = 8f;
            RegisterRejectedDragTarget(container);

            nodeSummaryLabel = new Label("Prefab Nodes 0 | Bindings 0");
            nodeSummaryLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            nodeSummaryLabel.style.marginBottom = 8f;

            nodeTreeScrollView = new ScrollView(ScrollViewMode.Vertical);
            nodeTreeScrollView.style.flexGrow = 1f;
            RegisterRejectedDragTarget(nodeTreeScrollView);

            container.Add(nodeSummaryLabel);
            container.Add(nodeTreeScrollView);
            return container;
        }

        private VisualElement BuildDetailPane()
        {
            var container = CreateCard();
            detailScrollView = new ScrollView(ScrollViewMode.Vertical);
            detailScrollView.style.flexGrow = 1f;
            container.Add(detailScrollView);
            RebuildDetailPane();
            return container;
        }

        private VisualElement BuildFooter()
        {
            var footerScrollView = new ScrollView(ScrollViewMode.Vertical);
            footerScrollView.style.marginTop = 10f;
            footerScrollView.style.flexGrow = 1f;
            footerScrollView.style.flexShrink = 1f;

            var footer = CreateCard();
            footer.style.marginTop = 0f;
            footer.style.flexGrow = 0f;
            footer.style.flexShrink = 0f;

            logicPreviewLabel = new Label();
            partialPreviewLabel = new Label();
            logicPreviewLabel.style.marginTop = 6f;
            partialPreviewLabel.style.marginTop = 4f;
            logicPreviewLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            partialPreviewLabel.style.unityFontStyleAndWeight = FontStyle.Bold;

            var exportButton = new Button(Export)
            {
                name = "exportButton",
                text = "Export",
            };
            exportButton.style.height = 34f;
            exportButton.style.flexGrow = 1f;
            exportButton.style.flexShrink = 1f;
            exportButton.style.flexBasis = 0f;
            exportButton.style.minWidth = 0f;
            exportButton.style.marginRight = 4f;

            var regenerateAllButton = new Button(RegenerateAll)
            {
                name = "regenerateAllButton",
                text = RegenerateAllButtonText,
            };
            regenerateAllButton.style.height = 34f;
            regenerateAllButton.style.flexGrow = 1f;
            regenerateAllButton.style.flexShrink = 1f;
            regenerateAllButton.style.flexBasis = 0f;
            regenerateAllButton.style.minWidth = 0f;
            regenerateAllButton.style.marginLeft = 4f;

            var exportButtonRow = new VisualElement();
            exportButtonRow.style.flexDirection = FlexDirection.Row;
            exportButtonRow.style.marginTop = 10f;
            exportButtonRow.Add(exportButton);
            exportButtonRow.Add(regenerateAllButton);

            footer.Add(logicPreviewLabel);
            footer.Add(partialPreviewLabel);
            footer.Add(exportButtonRow);

            footerScrollView.Add(footer);
            return footerScrollView;
        }

        private void OnPrefabChanged(ChangeEvent<UnityEngine.Object> evt)
        {
            LoadPrefab(evt.newValue as GameObject);
        }

        private void LoadPrefab(GameObject prefab)
        {
            if (prefab == null)
            {
                currentConfig = null;
                nodeSnapshots.Clear();
                expandedNodeStates.Clear();
                nodeHasChildren.Clear();
                selectedNodeId = default;
                SelectedNodeIds.Clear();
                ClearCopiedViewConfig();
                ClearPersistedWindowState();
                RefreshViewState();
                return;
            }

            if (!IsPrefabAsset(prefab))
            {
                EditorUtility.DisplayDialog("Prefab Auto Bind", "Select a prefab asset rather than a scene object or another asset.", "OK");
                prefabField.SetValueWithoutNotify(null);
                return;
            }

            var nextConfig = PrefabAutoBindEditorUtility.GetOrCreateConfig(prefab);
            if (nextConfig != currentConfig)
            {
                ClearCopiedViewConfig();
            }

            currentConfig = nextConfig;
            expandedNodeStates.Clear();
            nodeHasChildren.Clear();
            prefabField.SetValueWithoutNotify(prefab);
            classNameField.SetValueWithoutNotify(currentConfig.ClassName);
            settingsField.SetValueWithoutNotify(currentConfig.Settings);
            RefreshSnapshots();
        }

        private void RefreshSnapshots()
        {
            if (currentConfig == null || currentConfig.Prefab == null)
            {
                nodeSnapshots.Clear();
                RefreshViewState();
                return;
            }

            nodeSnapshots = PrefabAutoBindEditorUtility.BuildNodeSnapshots(currentConfig.Prefab);
            PrefabAutoBindEditorUtility.SyncNodes(currentConfig, nodeSnapshots);
            RefreshTreeMetadata();
            PrefabAutoBindEditorUtility.SaveConfig(currentConfig);

            PruneAndInitializeSelection();

            PersistWindowState();

            RefreshViewState();
        }

        private void RefreshViewState()
        {
            RebuildNodeTree();
            UpdateNodeSummary();
            UpdatePreviewLabels();
            RebuildDetailPane();
        }

        private void RebuildDetailPane()
        {
            detailScrollView.Clear();
            if (currentConfig == null || currentConfig.Prefab == null)
            {
                detailScrollView.Add(new HelpBox("Select a prefab asset to inspect its hierarchy and binding targets.", HelpBoxMessageType.Info));
                return;
            }

            var selectedSnapshots = GetSelectedSnapshots();
            if (selectedSnapshots.Count > 1)
            {
                BuildMultiNodeDetail(selectedSnapshots);
                return;
            }

            var snapshot = GetSelectedSnapshot();
            if (snapshot == null)
            {
                detailScrollView.Add(new HelpBox("Select a node on the left to edit its exported bindings.", HelpBoxMessageType.Info));
                return;
            }

            var nodeConfig = PrefabAutoBindEditorUtility.FindNodeConfig(currentConfig, snapshot.NodeId);
            if (nodeConfig == null)
            {
                detailScrollView.Add(new HelpBox("Node configuration could not be resolved. Try Refresh.", HelpBoxMessageType.Warning));
                return;
            }

            var title = new Label(nodeConfig.NodeName);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 15f;
            detailScrollView.Add(title);

            var pathLabel = new Label(string.IsNullOrEmpty(snapshot.Path) ? "Path: (Root)" : "Path: " + snapshot.Path);
            pathLabel.style.color = SecondaryTextColor;
            pathLabel.style.marginBottom = 8f;
            detailScrollView.Add(pathLabel);

            var aliasField = new TextField("Node Name")
            {
                value = nodeConfig.NodeName,
                isDelayed = true,
            };
            aliasField.RegisterValueChangedCallback(evt =>
            {
                if (!PrefabAutoBindEditorUtility.TryRenamePrefabNodes(
                        currentConfig,
                        new[] { snapshot },
                        evt.newValue,
                        out var errorMessage))
                {
                    EditorUtility.DisplayDialog("Rename Node", errorMessage, "OK");
                    aliasField.SetValueWithoutNotify(nodeConfig.NodeName);
                    return;
                }

                RefreshSnapshots();
            });
            detailScrollView.Add(aliasField);

            var nodeKindChoices = new List<string>
            {
                "field",
                "view",
            };

            var nodeKindField = new PopupField<string>("Node Type", nodeKindChoices, nodeConfig.NodeKind == PrefabAutoBindNodeKind.View ? 1 : 0);
            nodeKindField.RegisterValueChangedCallback(evt =>
            {
                var nextKind = string.Equals(evt.newValue, "view", StringComparison.Ordinal)
                    ? PrefabAutoBindNodeKind.View
                    : PrefabAutoBindNodeKind.Field;
                if (nextKind == nodeConfig.NodeKind)
                {
                    return;
                }

                if (!TrySetNodeKind(nodeConfig, nextKind))
                {
                    nodeKindField.SetValueWithoutNotify(nodeConfig.NodeKind == PrefabAutoBindNodeKind.View ? "view" : "field");
                    return;
                }

                RefreshViewState();
            });
            if (!snapshot.ParentNodeId.IsValid)
            {
                nodeKindField.SetEnabled(false);
                nodeKindField.tooltip = "The prefab root node is always exported by the main class and cannot be changed to view.";
            }

            detailScrollView.Add(nodeKindField);

            if (nodeConfig.NodeKind == PrefabAutoBindNodeKind.View)
            {
                detailScrollView.Add(BuildViewDetailPanel(nodeConfig));
                return;
            }

            var addButton = new Button(() => ShowAddComponentMenu(snapshot, nodeConfig))
            {
                text = "Add Component",
            };
            addButton.style.marginTop = 6f;
            addButton.style.marginBottom = 8f;
            detailScrollView.Add(addButton);

            if (nodeConfig.Components.Count == 0)
            {
                detailScrollView.Add(new HelpBox("No components selected for export on this node yet.", HelpBoxMessageType.None));
                return;
            }

            for (var index = 0; index < nodeConfig.Components.Count; index++)
            {
                var component = nodeConfig.Components[index];
                detailScrollView.Add(BuildComponentEditor(nodeConfig, component, index == 0));
            }
        }

        private void BuildMultiNodeDetail(IReadOnlyList<PrefabAutoBindNodeSnapshot> snapshots)
        {
            var nodeConfigs = GetNodeConfigs(snapshots);
            if (nodeConfigs.Count != snapshots.Count)
            {
                detailScrollView.Add(new HelpBox("Some node configurations could not be resolved. Try Refresh.", HelpBoxMessageType.Warning));
                return;
            }

            var title = new Label(nodeConfigs.Count + " Nodes Selected");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 15f;
            detailScrollView.Add(title);

            var pathLabel = new Label("Multiple paths");
            pathLabel.style.color = SecondaryTextColor;
            pathLabel.style.marginBottom = 8f;
            detailScrollView.Add(pathLabel);

            var hasCommonNodeName = TryGetCommonValue(nodeConfigs, node => node.NodeName, out var commonNodeName);
            var aliasField = new TextField("Node Name")
            {
                value = hasCommonNodeName ? commonNodeName : string.Empty,
                isDelayed = true,
                showMixedValue = !hasCommonNodeName,
            };
            aliasField.RegisterValueChangedCallback(evt =>
            {
                if (!PrefabAutoBindEditorUtility.TryRenamePrefabNodes(
                        currentConfig,
                        snapshots,
                        evt.newValue,
                        out var errorMessage))
                {
                    EditorUtility.DisplayDialog("Rename Nodes", errorMessage, "OK");
                    aliasField.showMixedValue = !hasCommonNodeName;
                    aliasField.SetValueWithoutNotify(hasCommonNodeName ? commonNodeName : string.Empty);
                    return;
                }

                aliasField.showMixedValue = false;
                RefreshSnapshots();
            });
            detailScrollView.Add(aliasField);

            var hasCommonNodeKind = TryGetCommonValue(nodeConfigs, node => node.NodeKind, out var commonNodeKind);
            var nodeKindChoices = hasCommonNodeKind
                ? new List<string> { "field", "view" }
                : new List<string> { "--", "field", "view" };
            var selectedChoice = hasCommonNodeKind
                ? commonNodeKind == PrefabAutoBindNodeKind.View ? "view" : "field"
                : "--";
            var nodeKindField = new PopupField<string>("Node Type", nodeKindChoices, selectedChoice);
            nodeKindField.RegisterValueChangedCallback(evt =>
            {
                if (string.Equals(evt.newValue, "--", StringComparison.Ordinal))
                {
                    return;
                }

                var nextKind = string.Equals(evt.newValue, "view", StringComparison.Ordinal)
                    ? PrefabAutoBindNodeKind.View
                    : PrefabAutoBindNodeKind.Field;
                if (!TrySetNodeKinds(nodeConfigs, nextKind))
                {
                    nodeKindField.SetValueWithoutNotify(selectedChoice);
                    return;
                }

                RefreshViewState();
            });
            detailScrollView.Add(nodeKindField);

            if (!hasCommonNodeKind)
            {
                detailScrollView.Add(new HelpBox(
                    "Selected nodes contain both field and view types. Set a common Node Type to edit type-specific properties.",
                    HelpBoxMessageType.Info));
                return;
            }

            if (commonNodeKind == PrefabAutoBindNodeKind.View)
            {
                detailScrollView.Add(BuildMultiViewDetailPanel(nodeConfigs));
                return;
            }

            detailScrollView.Add(BuildMultiFieldDetailPanel(snapshots, nodeConfigs));
        }

        private void CopyViewConfig(PrefabAutoBindNodeConfig sourceNode)
        {
            if (currentConfig == null || sourceNode == null || sourceNode.NodeKind != PrefabAutoBindNodeKind.View)
            {
                return;
            }

            copiedViewConfig = currentConfig;
            copiedViewNodeId = sourceNode.NodeId;
            ShowNotification(new GUIContent("Copied view config."));
            RebuildDetailPane();
        }

        private void PasteViewConfig(PrefabAutoBindNodeConfig targetNode)
        {
            PasteViewConfig(new[] { targetNode });
        }

        private void PasteViewConfig(IReadOnlyList<PrefabAutoBindNodeConfig> targetNodes)
        {
            if (!TryResolveCopiedViewConfig(out var sourceNode, out var errorMessage))
            {
                EditorUtility.DisplayDialog("Paste Config", errorMessage, "OK");
                RebuildDetailPane();
                return;
            }

            if (!TryCopyViewConfigToTargets(
                    currentConfig,
                    nodeSnapshots,
                    sourceNode,
                    targetNodes,
                    out errorMessage))
            {
                EditorUtility.DisplayDialog("Paste Config", errorMessage, "OK");
                return;
            }

            PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
            RefreshViewState();
        }

        private bool TryResolveCopiedViewConfig(
            out PrefabAutoBindNodeConfig sourceNode,
            out string errorMessage)
        {
            sourceNode = null;
            errorMessage = null;

            if (currentConfig == null || copiedViewConfig == null || copiedViewConfig != currentConfig)
            {
                errorMessage = "Paste Config failed: no view config has been copied for this prefab.";
                return false;
            }

            sourceNode = PrefabAutoBindEditorUtility.FindNodeConfig(currentConfig, copiedViewNodeId);
            if (sourceNode == null || sourceNode.NodeKind != PrefabAutoBindNodeKind.View)
            {
                ClearCopiedViewConfig();
                errorMessage = "Paste Config failed: the copied source view is no longer available.";
                return false;
            }

            return true;
        }

        private bool CanPasteViewConfigTo(PrefabAutoBindNodeConfig targetNode)
        {
            return CanPasteViewConfigTo(new[] { targetNode });
        }

        private bool CanPasteViewConfigTo(IReadOnlyList<PrefabAutoBindNodeConfig> targetNodes)
        {
            if (currentConfig == null ||
                copiedViewConfig == null ||
                copiedViewConfig != currentConfig ||
                targetNodes == null ||
                targetNodes.Count == 0)
            {
                return false;
            }

            var sourceNode = PrefabAutoBindEditorUtility.FindNodeConfig(currentConfig, copiedViewNodeId);
            if (sourceNode == null || sourceNode.NodeKind != PrefabAutoBindNodeKind.View)
            {
                return false;
            }

            for (var index = 0; index < targetNodes.Count; index++)
            {
                var targetNode = targetNodes[index];
                if (targetNode == null ||
                    targetNode.NodeKind != PrefabAutoBindNodeKind.View ||
                    sourceNode.NodeId == targetNode.NodeId)
                {
                    return false;
                }
            }

            return true;
        }

        private void ClearCopiedViewConfig()
        {
            copiedViewConfig = null;
            copiedViewNodeId = default;
        }

        internal static bool TryCopyViewConfigToTargets(
            PrefabAutoBindConfig config,
            IReadOnlyList<PrefabAutoBindNodeSnapshot> snapshots,
            PrefabAutoBindNodeConfig sourceNode,
            IReadOnlyList<PrefabAutoBindNodeConfig> targetNodes,
            out string errorMessage)
        {
            errorMessage = null;
            if (config == null || sourceNode == null)
            {
                errorMessage = "Paste Config failed: missing source config.";
                return false;
            }

            if (sourceNode.NodeKind != PrefabAutoBindNodeKind.View)
            {
                errorMessage = "Paste Config failed: the source node must be a view node.";
                return false;
            }

            if (!sourceNode.ParentNodeId.IsValid)
            {
                errorMessage = "Paste Config failed: the prefab root cannot be used as the source view.";
                return false;
            }

            if (targetNodes == null || targetNodes.Count == 0)
            {
                errorMessage = "Paste Config failed: select at least one target view node.";
                return false;
            }

            var snapshotsById = BuildSnapshotLookup(snapshots);
            var plans = new List<ViewConfigCopyPlan>(targetNodes.Count);
            var failureMessages = new List<string>();
            for (var targetIndex = 0; targetIndex < targetNodes.Count; targetIndex++)
            {
                var targetNode = targetNodes[targetIndex];
                if (!TryBuildViewConfigCopyPlan(
                        config,
                        snapshotsById,
                        sourceNode,
                        targetNode,
                        plans,
                        out var plan,
                        out var failureMessage))
                {
                    failureMessages.Add(failureMessage);
                    if (failureMessages.Count == 3)
                    {
                        break;
                    }

                    continue;
                }

                plans.Add(plan);
            }

            if (failureMessages.Count > 0)
            {
                errorMessage = "Paste Config failed. No nodes were changed.\n\n" +
                               string.Join("\n", failureMessages);
                return false;
            }

            for (var planIndex = 0; planIndex < plans.Count; planIndex++)
            {
                var plan = plans[planIndex];
                for (var pairIndex = 0; pairIndex < plan.Pairs.Count; pairIndex++)
                {
                    CopyNodeConfig(plan.Pairs[pairIndex].Source, plan.Pairs[pairIndex].Target);
                }
            }

            return true;
        }

        private static bool TryBuildViewConfigCopyPlan(
            PrefabAutoBindConfig config,
            IReadOnlyDictionary<PrefabAutoBindNodeId, PrefabAutoBindNodeSnapshot> snapshotsById,
            PrefabAutoBindNodeConfig sourceNode,
            PrefabAutoBindNodeConfig targetNode,
            IReadOnlyList<ViewConfigCopyPlan> existingPlans,
            out ViewConfigCopyPlan plan,
            out string failureMessage)
        {
            plan = null;
            failureMessage = null;
            var targetPath = FormatNodePath(targetNode);
            if (targetNode == null)
            {
                failureMessage = "Target <missing>: node config could not be resolved.";
                return false;
            }

            if (targetNode.NodeId == sourceNode.NodeId)
            {
                failureMessage = "Target '" + targetPath + "': source node cannot also be a target.";
                return false;
            }

            if (!targetNode.ParentNodeId.IsValid)
            {
                failureMessage = "Target '" + targetPath + "': prefab root cannot be used as a paste target.";
                return false;
            }

            if (targetNode.NodeKind != PrefabAutoBindNodeKind.View)
            {
                failureMessage = "Target '" + targetPath + "': target node must be a view node.";
                return false;
            }

            if (IsSameOrDescendantOf(config, targetNode, sourceNode))
            {
                failureMessage = "Target '" + targetPath + "': target cannot be inside the source view subtree.";
                return false;
            }

            if (IsSameOrDescendantOf(config, sourceNode, targetNode))
            {
                failureMessage = "Target '" + targetPath + "': target cannot contain the source view subtree.";
                return false;
            }

            for (var planIndex = 0; planIndex < existingPlans.Count; planIndex++)
            {
                var existingTarget = existingPlans[planIndex].TargetRoot;
                if (IsSameOrDescendantOf(config, targetNode, existingTarget) ||
                    IsSameOrDescendantOf(config, existingTarget, targetNode))
                {
                    failureMessage = "Target '" + targetPath + "': selected targets must not overlap each other.";
                    return false;
                }
            }

            plan = new ViewConfigCopyPlan
            {
                TargetRoot = targetNode,
            };

            if (!TryCollectViewConfigCopyPairs(
                    config,
                    snapshotsById,
                    sourceNode,
                    targetNode,
                    true,
                    plan.Pairs,
                    out failureMessage))
            {
                plan = null;
                return false;
            }

            return true;
        }

        private static bool TryCollectViewConfigCopyPairs(
            PrefabAutoBindConfig config,
            IReadOnlyDictionary<PrefabAutoBindNodeId, PrefabAutoBindNodeSnapshot> snapshotsById,
            PrefabAutoBindNodeConfig sourceNode,
            PrefabAutoBindNodeConfig targetNode,
            bool isRoot,
            List<ViewConfigCopyPair> pairs,
            out string failureMessage)
        {
            failureMessage = null;
            var targetPath = FormatNodePath(targetNode);
            if (sourceNode == null || targetNode == null)
            {
                failureMessage = "Target '" + targetPath + "': source and target structure could not be resolved.";
                return false;
            }

            if (!isRoot)
            {
                var sourceDisplayName = GetDisplayName(snapshotsById, sourceNode);
                var targetDisplayName = GetDisplayName(snapshotsById, targetNode);
                if (!string.Equals(sourceDisplayName, targetDisplayName, StringComparison.Ordinal))
                {
                    failureMessage = "Target '" + targetPath + "': child name mismatch. Expected '" +
                                     sourceDisplayName + "', got '" + targetDisplayName + "'.";
                    return false;
                }
            }

            if (!TargetHasRequiredComponents(snapshotsById, targetNode, sourceNode, out var missingComponent))
            {
                failureMessage = "Target '" + targetPath + "': missing component '" + missingComponent + "'.";
                return false;
            }

            pairs.Add(new ViewConfigCopyPair(sourceNode, targetNode));

            var sourceChildren = GetDirectChildren(config, sourceNode);
            var targetChildren = GetDirectChildren(config, targetNode);
            if (sourceChildren.Count != targetChildren.Count)
            {
                failureMessage = "Target '" + targetPath + "': child count mismatch. Expected " +
                                 sourceChildren.Count + ", got " + targetChildren.Count + ".";
                return false;
            }

            for (var childIndex = 0; childIndex < sourceChildren.Count; childIndex++)
            {
                if (!TryCollectViewConfigCopyPairs(
                        config,
                        snapshotsById,
                        sourceChildren[childIndex],
                        targetChildren[childIndex],
                        false,
                        pairs,
                        out failureMessage))
                {
                    return false;
                }
            }

            return true;
        }

        private static Dictionary<PrefabAutoBindNodeId, PrefabAutoBindNodeSnapshot> BuildSnapshotLookup(
            IReadOnlyList<PrefabAutoBindNodeSnapshot> snapshots)
        {
            var result = new Dictionary<PrefabAutoBindNodeId, PrefabAutoBindNodeSnapshot>();
            if (snapshots == null)
            {
                return result;
            }

            for (var index = 0; index < snapshots.Count; index++)
            {
                result[snapshots[index].NodeId] = snapshots[index];
            }

            return result;
        }

        private static bool TargetHasRequiredComponents(
            IReadOnlyDictionary<PrefabAutoBindNodeId, PrefabAutoBindNodeSnapshot> snapshotsById,
            PrefabAutoBindNodeConfig targetNode,
            PrefabAutoBindNodeConfig sourceNode,
            out string missingComponent)
        {
            missingComponent = null;
            if (!snapshotsById.TryGetValue(targetNode.NodeId, out var targetSnapshot))
            {
                missingComponent = "node snapshot";
                return false;
            }

            for (var componentIndex = 0; componentIndex < sourceNode.Components.Count; componentIndex++)
            {
                var sourceComponent = sourceNode.Components[componentIndex];
                var found = false;
                for (var availableIndex = 0; availableIndex < targetSnapshot.AvailableComponents.Count; availableIndex++)
                {
                    var available = targetSnapshot.AvailableComponents[availableIndex];
                    if (available.ComponentIndex == sourceComponent.ComponentIndex &&
                        string.Equals(available.TypeName, sourceComponent.ComponentTypeName, StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }

                if (found)
                {
                    continue;
                }

                missingComponent =
                    PrefabAutoBindEditorUtility.GetShortTypeName(sourceComponent.ComponentTypeName) +
                    " #" + (sourceComponent.ComponentIndex + 1);
                return false;
            }

            return true;
        }

        private static List<PrefabAutoBindNodeConfig> GetDirectChildren(
            PrefabAutoBindConfig config,
            PrefabAutoBindNodeConfig parentNode)
        {
            var result = new List<PrefabAutoBindNodeConfig>();
            if (config == null || parentNode == null)
            {
                return result;
            }

            for (var index = 0; index < config.Nodes.Count; index++)
            {
                var node = config.Nodes[index];
                if (node != null && node.ParentNodeId == parentNode.NodeId)
                {
                    result.Add(node);
                }
            }

            return result;
        }

        private static bool IsSameOrDescendantOf(
            PrefabAutoBindConfig config,
            PrefabAutoBindNodeConfig node,
            PrefabAutoBindNodeConfig ancestor)
        {
            var current = node;
            while (current != null && current.NodeId.IsValid)
            {
                if (current.NodeId == ancestor.NodeId)
                {
                    return true;
                }

                current = PrefabAutoBindEditorUtility.FindNodeConfig(config, current.ParentNodeId);
            }

            return false;
        }

        private static string GetDisplayName(
            IReadOnlyDictionary<PrefabAutoBindNodeId, PrefabAutoBindNodeSnapshot> snapshotsById,
            PrefabAutoBindNodeConfig node)
        {
            return node != null &&
                   snapshotsById.TryGetValue(node.NodeId, out var snapshot) &&
                   !string.IsNullOrWhiteSpace(snapshot.DisplayName)
                ? snapshot.DisplayName
                : node != null
                    ? node.NodeName ?? string.Empty
                    : string.Empty;
        }

        private static string FormatNodePath(PrefabAutoBindNodeConfig node)
        {
            if (node == null)
            {
                return "<missing>";
            }

            return string.IsNullOrWhiteSpace(node.NodePath) ? "<root>" : node.NodePath;
        }

        private static void CopyNodeConfig(
            PrefabAutoBindNodeConfig source,
            PrefabAutoBindNodeConfig target)
        {
            target.NodeKind = source.NodeKind;
            target.ViewClassName = source.ViewClassName;
            target.ViewContainerKind = source.ViewContainerKind;
            target.ViewContainerName = source.ViewContainerName;
            target.Settings = source.Settings;

            target.Components.Clear();
            for (var componentIndex = 0; componentIndex < source.Components.Count; componentIndex++)
            {
                target.Components.Add(CloneComponentConfig(source.Components[componentIndex]));
            }
        }

        private static PrefabAutoBindComponentConfig CloneComponentConfig(
            PrefabAutoBindComponentConfig source)
        {
            var clone = new PrefabAutoBindComponentConfig
            {
                ComponentTypeName = source.ComponentTypeName,
                ComponentIndex = source.ComponentIndex,
                FieldName = source.FieldName,
                AccessModifier = source.AccessModifier,
                ContainerKind = source.ContainerKind,
                ContainerName = source.ContainerName,
            };

            for (var eventIndex = 0; eventIndex < source.Events.Count; eventIndex++)
            {
                clone.Events.Add(source.Events[eventIndex]);
            }

            return clone;
        }

        private bool TrySetNodeKinds(
            IReadOnlyList<PrefabAutoBindNodeConfig> nodeConfigs,
            PrefabAutoBindNodeKind nextKind)
        {
            if (nextKind == PrefabAutoBindNodeKind.View)
            {
                for (var index = 0; index < nodeConfigs.Count; index++)
                {
                    if (nodeConfigs[index].ParentNodeId.IsValid)
                    {
                        continue;
                    }

                    EditorUtility.DisplayDialog(
                        "Switch Node Type",
                        "The selection contains the prefab root. The root cannot be switched to view, so no nodes were changed.",
                        "OK");
                    return false;
                }

                var clearedBindingCount = 0;
                for (var index = 0; index < nodeConfigs.Count; index++)
                {
                    if (nodeConfigs[index].NodeKind != PrefabAutoBindNodeKind.View)
                    {
                        clearedBindingCount += nodeConfigs[index].Components.Count;
                    }
                }

                if (clearedBindingCount > 0 &&
                    !EditorUtility.DisplayDialog(
                        "Switch Nodes to View",
                        "Switching the selected nodes to view will clear " + clearedBindingCount +
                        " component binding(s). Continue?",
                        "Switch to View",
                        "Cancel"))
                {
                    return false;
                }
            }

            for (var index = 0; index < nodeConfigs.Count; index++)
            {
                var nodeConfig = nodeConfigs[index];
                if (nodeConfig.NodeKind == nextKind)
                {
                    continue;
                }

                var previousKind = nodeConfig.NodeKind;
                if (nextKind == PrefabAutoBindNodeKind.View)
                {
                    nodeConfig.Components.Clear();
                }

                nodeConfig.NodeKind = nextKind;
                if (previousKind == PrefabAutoBindNodeKind.View &&
                    nextKind == PrefabAutoBindNodeKind.Field)
                {
                    PrefabAutoBindEditorUtility.ClearDescendantComponentBindings(
                        currentConfig,
                        nodeConfig);
                }

                if (nextKind == PrefabAutoBindNodeKind.View)
                {
                    nodeConfig.ViewClassName = string.IsNullOrWhiteSpace(nodeConfig.ViewClassName)
                        ? PrefabAutoBindEditorUtility.BuildDefaultViewClassName(currentConfig, nodeConfig)
                        : PrefabAutoBindEditorUtility.MakeSafeIdentifier(nodeConfig.ViewClassName, "View");
                    nodeConfig.Settings = currentConfig.Settings;
                }
            }

            PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
            UpdateNodeSummary();
            return true;
        }

        private bool TrySetNodeKind(PrefabAutoBindNodeConfig nodeConfig, PrefabAutoBindNodeKind nextKind)
        {
            if (nodeConfig == null)
            {
                return false;
            }

            if (nextKind == PrefabAutoBindNodeKind.View && !nodeConfig.ParentNodeId.IsValid)
            {
                EditorUtility.DisplayDialog(
                    "Switch Node Type",
                    "The prefab root node is always exported using the top-level Class / Settings configuration and cannot be changed to view.",
                    "OK");
                return false;
            }

            if (nextKind == PrefabAutoBindNodeKind.View && nodeConfig.Components.Count > 0)
            {
                var confirmed = EditorUtility.DisplayDialog(
                    "Switch Node Type",
                    "Switching this node to view will clear the current component bindings on this node. Descendants will belong to the generated view class instead. Continue?",
                    "Switch to View",
                    "Cancel");
                if (!confirmed)
                {
                    return false;
                }

                nodeConfig.Components.Clear();
            }

            var previousKind = nodeConfig.NodeKind;
            nodeConfig.NodeKind = nextKind;
            if (previousKind == PrefabAutoBindNodeKind.View &&
                nextKind == PrefabAutoBindNodeKind.Field)
            {
                PrefabAutoBindEditorUtility.ClearDescendantComponentBindings(
                    currentConfig,
                    nodeConfig);
            }

            if (nextKind == PrefabAutoBindNodeKind.View)
            {
                nodeConfig.ViewClassName = string.IsNullOrWhiteSpace(nodeConfig.ViewClassName)
                    ? PrefabAutoBindEditorUtility.BuildDefaultViewClassName(currentConfig, nodeConfig)
                    : PrefabAutoBindEditorUtility.MakeSafeIdentifier(nodeConfig.ViewClassName, "View");
                nodeConfig.Settings = currentConfig.Settings;
            }

            PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
            UpdateNodeSummary();
            return true;
        }

        private VisualElement BuildViewDetailPanel(PrefabAutoBindNodeConfig nodeConfig)
        {
            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Column;
            container.style.marginTop = 8f;

            container.Add(new HelpBox(
                "This node becomes a separate view class. Duplicate View Class names are validated for compatibility on export.",
                HelpBoxMessageType.Info));

            if (string.IsNullOrWhiteSpace(nodeConfig.ViewClassName))
            {
                nodeConfig.ViewClassName = PrefabAutoBindEditorUtility.BuildDefaultViewClassName(currentConfig, nodeConfig);
                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
            }

            container.Add(BuildViewConfigClipboardRow(nodeConfig));

            var viewClassField = new TextField("View Class")
            {
                value = nodeConfig.ViewClassName,
                isDelayed = true,
            };
            viewClassField.RegisterValueChangedCallback(evt =>
            {
                nodeConfig.ViewClassName = PrefabAutoBindEditorUtility.MakeSafeIdentifier(
                    evt.newValue,
                    PrefabAutoBindEditorUtility.BuildDefaultViewClassName(currentConfig, nodeConfig));
                viewClassField.SetValueWithoutNotify(nodeConfig.ViewClassName);
                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                RebuildNodeTree();
            });
            container.Add(viewClassField);

            var viewSettingsField = new ObjectField("Settings")
            {
                objectType = typeof(PrefabAutoBindSettings),
                allowSceneObjects = false,
                value = nodeConfig.Settings,
            };
            viewSettingsField.RegisterValueChangedCallback(evt =>
            {
                nodeConfig.Settings = evt.newValue as PrefabAutoBindSettings;
                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                RebuildDetailPane();
            });
            container.Add(viewSettingsField);

            var containerRow = new VisualElement();
            containerRow.style.flexDirection = FlexDirection.Row;
            containerRow.style.alignItems = Align.Center;
            containerRow.style.marginTop = 6f;

            var containerKindField = new EnumField("Container", nodeConfig.ViewContainerKind);
            containerKindField.style.width = 180f;
            containerKindField.style.flexShrink = 0f;
            containerKindField.style.marginRight = 8f;
            containerKindField.RegisterValueChangedCallback(evt =>
            {
                nodeConfig.ViewContainerKind = (PrefabAutoBindContainerKind)evt.newValue;
                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                RebuildDetailPane();
            });
            containerRow.Add(containerKindField);

            if (nodeConfig.ViewContainerKind != PrefabAutoBindContainerKind.None)
            {
                var containerNameField = new TextField("Name")
                {
                    value = nodeConfig.ViewContainerName ?? string.Empty,
                    isDelayed = true,
                };
                containerNameField.style.flexGrow = 1f;
                containerNameField.style.flexShrink = 1f;
                containerNameField.style.flexBasis = 0f;
                containerNameField.style.minWidth = 0f;
                containerNameField.RegisterValueChangedCallback(evt =>
                {
                    nodeConfig.ViewContainerName = string.IsNullOrWhiteSpace(evt.newValue)
                        ? string.Empty
                        : PrefabAutoBindEditorUtility.MakeSafeIdentifier(evt.newValue, "container");
                    containerNameField.SetValueWithoutNotify(nodeConfig.ViewContainerName);
                    PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                });
                containerRow.Add(containerNameField);
            }

            container.Add(containerRow);
            return container;
        }

        private VisualElement BuildViewConfigClipboardRow(PrefabAutoBindNodeConfig nodeConfig)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 4f;
            row.style.marginBottom = 8f;

            var copyButton = new Button(() => CopyViewConfig(nodeConfig))
            {
                text = "CopyConfig",
            };
            copyButton.style.flexGrow = 1f;
            copyButton.style.height = 28f;
            copyButton.tooltip = "Copy this view node config for Paste Config.";

            var pasteButton = new Button(() => PasteViewConfig(nodeConfig))
            {
                text = "Paste Config",
            };
            pasteButton.style.flexGrow = 1f;
            pasteButton.style.height = 28f;
            pasteButton.style.marginLeft = 8f;
            pasteButton.tooltip = "Paste a copied view node config into this view node.";
            pasteButton.SetEnabled(CanPasteViewConfigTo(nodeConfig));

            row.Add(copyButton);
            row.Add(pasteButton);
            return row;
        }

        private VisualElement BuildMultiViewDetailPanel(IReadOnlyList<PrefabAutoBindNodeConfig> nodeConfigs)
        {
            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Column;
            container.style.marginTop = 8f;

            container.Add(new HelpBox(
                "All selected nodes are view nodes. Mixed values are shown as --.",
                HelpBoxMessageType.Info));

            var pasteButton = new Button(() => PasteViewConfig(nodeConfigs))
            {
                name = "multiPasteConfigButton",
                text = "Paste Config",
                tooltip = "Paste a copied view node config into all selected view nodes.",
            };
            pasteButton.style.height = 28f;
            pasteButton.style.marginTop = 4f;
            pasteButton.style.marginBottom = 8f;
            pasteButton.SetEnabled(CanPasteViewConfigTo(nodeConfigs));
            container.Add(pasteButton);

            var hasCommonViewClass = TryGetCommonValue(
                nodeConfigs,
                node => node.ViewClassName,
                out var commonViewClass);
            var viewClassField = new TextField("View Class")
            {
                value = hasCommonViewClass ? commonViewClass : string.Empty,
                isDelayed = true,
                showMixedValue = !hasCommonViewClass,
            };
            viewClassField.RegisterValueChangedCallback(evt =>
            {
                var nextClassName = PrefabAutoBindEditorUtility.MakeSafeIdentifier(evt.newValue, "View");
                for (var index = 0; index < nodeConfigs.Count; index++)
                {
                    nodeConfigs[index].ViewClassName = nextClassName;
                }

                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                RebuildNodeTree();
                RebuildDetailPane();
            });
            container.Add(viewClassField);

            var hasCommonSettings = TryGetCommonValue(
                nodeConfigs,
                node => node.Settings,
                out var commonSettings);
            var multiSettingsField = new ObjectField("Settings")
            {
                objectType = typeof(PrefabAutoBindSettings),
                allowSceneObjects = false,
                value = hasCommonSettings ? commonSettings : null,
                showMixedValue = !hasCommonSettings,
            };
            multiSettingsField.RegisterValueChangedCallback(evt =>
            {
                var nextSettings = evt.newValue as PrefabAutoBindSettings;
                for (var index = 0; index < nodeConfigs.Count; index++)
                {
                    nodeConfigs[index].Settings = nextSettings;
                }

                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                RebuildDetailPane();
            });
            container.Add(multiSettingsField);

            var containerRow = new VisualElement();
            containerRow.style.flexDirection = FlexDirection.Row;
            containerRow.style.alignItems = Align.Center;
            containerRow.style.marginTop = 6f;

            var hasCommonContainerKind = TryGetCommonValue(
                nodeConfigs,
                node => node.ViewContainerKind,
                out var commonContainerKind);
            var containerChoices = hasCommonContainerKind
                ? new List<string> { "None", "Array", "List" }
                : new List<string> { "--", "None", "Array", "List" };
            var containerKindField = new PopupField<string>(
                "Container",
                containerChoices,
                hasCommonContainerKind ? commonContainerKind.ToString() : "--");
            containerKindField.style.width = 180f;
            containerKindField.style.flexShrink = 0f;
            containerKindField.style.marginRight = 8f;
            containerKindField.RegisterValueChangedCallback(evt =>
            {
                if (!Enum.TryParse(evt.newValue, out PrefabAutoBindContainerKind nextContainerKind))
                {
                    return;
                }

                for (var index = 0; index < nodeConfigs.Count; index++)
                {
                    nodeConfigs[index].ViewContainerKind = nextContainerKind;
                }

                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                RebuildDetailPane();
            });
            containerRow.Add(containerKindField);

            var hasContainer = false;
            for (var index = 0; index < nodeConfigs.Count; index++)
            {
                if (nodeConfigs[index].ViewContainerKind == PrefabAutoBindContainerKind.None)
                {
                    continue;
                }

                hasContainer = true;
                break;
            }

            if (hasContainer)
            {
                var hasCommonContainerName = TryGetCommonValue(
                    nodeConfigs,
                    node => node.ViewContainerName ?? string.Empty,
                    out var commonContainerName);
                var containerNameField = new TextField("Name")
                {
                    value = hasCommonContainerName ? commonContainerName : string.Empty,
                    isDelayed = true,
                    showMixedValue = !hasCommonContainerName,
                };
                containerNameField.style.flexGrow = 1f;
                containerNameField.style.flexShrink = 1f;
                containerNameField.style.flexBasis = 0f;
                containerNameField.style.minWidth = 0f;
                containerNameField.RegisterValueChangedCallback(evt =>
                {
                    var nextContainerName = string.IsNullOrWhiteSpace(evt.newValue)
                        ? string.Empty
                        : PrefabAutoBindEditorUtility.MakeSafeIdentifier(evt.newValue, "container");
                    for (var index = 0; index < nodeConfigs.Count; index++)
                    {
                        nodeConfigs[index].ViewContainerName = nextContainerName;
                    }

                    PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                    RebuildDetailPane();
                });
                containerRow.Add(containerNameField);
            }

            container.Add(containerRow);
            return container;
        }

#if false // Pre-settings UI retained only as migration-era reference.
        private VisualElement CreateMultiNodeDirectoryOverrideRow(
            string labelText,
            IReadOnlyList<PrefabAutoBindNodeConfig> nodeConfigs,
            Func<PrefabAutoBindNodeConfig, bool> getInherit,
            Action<PrefabAutoBindNodeConfig, bool> setInherit,
            Func<PrefabAutoBindNodeConfig, string> getOverride,
            Action<PrefabAutoBindNodeConfig, string> setOverride,
            Func<PrefabAutoBindNodeConfig, string> getInherited,
            Func<PrefabAutoBindNodeConfig, string> getEffective,
            string fallbackDirectory)
        {
            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Column;
            container.style.marginTop = 6f;
            container.style.marginBottom = 2f;

            var hasCommonInherit = TryGetCommonValue(nodeConfigs, getInherit, out var commonInherit);
            var inheritChoices = hasCommonInherit
                ? new List<string> { "On", "Off" }
                : new List<string> { "--", "On", "Off" };
            var inheritField = new PopupField<string>(
                "Legacy Override",
                inheritChoices,
                hasCommonInherit ? (commonInherit ? "On" : "Off") : "--");
            inheritField.RegisterValueChangedCallback(evt =>
            {
                if (string.Equals(evt.newValue, "--", StringComparison.Ordinal))
                {
                    return;
                }

                var nextInherit = string.Equals(evt.newValue, "On", StringComparison.Ordinal);
                for (var index = 0; index < nodeConfigs.Count; index++)
                {
                    var nodeConfig = nodeConfigs[index];
                    setInherit(nodeConfig, nextInherit);
                    if (!nextInherit && string.IsNullOrWhiteSpace(getOverride(nodeConfig)))
                    {
                        setOverride(nodeConfig, getInherited(nodeConfig));
                    }
                }

                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                RebuildDetailPane();
            });
            container.Add(inheritField);

            var canEditOverride = hasCommonInherit && !commonInherit;
            var valueSelector = canEditOverride ? getOverride : getEffective;
            var hasCommonDirectory = TryGetCommonValue(nodeConfigs, valueSelector, out var commonDirectory);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 2f;

            var label = new Label(labelText);
            label.style.width = 70f;
            label.style.minWidth = 70f;
            label.style.flexShrink = 0f;
            label.style.marginRight = 8f;

            var field = new TextField
            {
                value = hasCommonDirectory ? commonDirectory : string.Empty,
                isDelayed = true,
                showMixedValue = !hasCommonDirectory,
            };
            field.style.flexGrow = 1f;
            field.style.flexShrink = 1f;
            field.style.flexBasis = 0f;
            field.style.minWidth = 0f;
            field.style.marginRight = 8f;
            field.SetEnabled(canEditOverride);
            field.RegisterValueChangedCallback(evt =>
            {
                if (string.IsNullOrWhiteSpace(evt.newValue))
                {
                    EditorUtility.DisplayDialog(
                        "Invalid Directory",
                        "A bulk directory override cannot be empty. No nodes were changed.",
                        "OK");
                    RebuildDetailPane();
                    return;
                }

                var normalized = PrefabAutoBindEditorUtility.NormalizeDirectory(evt.newValue, fallbackDirectory);
                for (var index = 0; index < nodeConfigs.Count; index++)
                {
                    setOverride(nodeConfigs[index], normalized);
                }

                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                RebuildDetailPane();
            });

            var pickButton = new Button(() =>
            {
                var initialDirectory = hasCommonDirectory ? commonDirectory : fallbackDirectory;
                PickFolder(initialDirectory, projectRelative =>
                {
                    for (var index = 0; index < nodeConfigs.Count; index++)
                    {
                        setOverride(nodeConfigs[index], projectRelative);
                    }

                    PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                    RebuildDetailPane();
                });
            })
            {
                text = "Pick",
            };
            pickButton.style.width = 96f;
            pickButton.style.minWidth = 96f;
            pickButton.style.flexShrink = 0f;
            pickButton.SetEnabled(canEditOverride);

            row.Add(label);
            row.Add(field);
            row.Add(pickButton);
            container.Add(row);

            var hintLabel = new Label(canEditOverride
                ? "Using node overrides"
                : hasCommonInherit && commonInherit
                    ? "Resolved from parent"
                    : "Legacy directory override");
            hintLabel.style.marginTop = 2f;
            hintLabel.style.color = SecondaryTextColor;
            container.Add(hintLabel);
            return container;
        }

        private VisualElement CreateNodeDirectoryOverrideRow(
            string labelText,
            bool inheritValue,
            string inheritedValue,
            string effectiveValue,
            Action<bool> onInheritChanged,
            Action<string> onValueChanged,
            Action onPick)
        {
            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Column;
            container.style.marginTop = 6f;
            container.style.marginBottom = 2f;

            var inheritToggle = new Toggle("Legacy Override")
            {
                value = inheritValue,
            };
            inheritToggle.RegisterValueChangedCallback(evt => onInheritChanged(evt.newValue));
            container.Add(inheritToggle);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 2f;

            var label = new Label(labelText);
            label.style.width = 70f;
            label.style.minWidth = 70f;
            label.style.flexShrink = 0f;
            label.style.marginRight = 8f;

            var field = new TextField
            {
                value = inheritValue ? inheritedValue : effectiveValue,
                isDelayed = true,
            };
            field.style.flexGrow = 1f;
            field.style.flexShrink = 1f;
            field.style.flexBasis = 0f;
            field.style.minWidth = 0f;
            field.style.marginRight = 8f;
            field.SetEnabled(!inheritValue);
            field.RegisterValueChangedCallback(evt => onValueChanged(evt.newValue));

            var pickButton = new Button(() => onPick())
            {
                text = "Pick",
            };
            pickButton.style.width = 96f;
            pickButton.style.minWidth = 96f;
            pickButton.style.flexShrink = 0f;
            pickButton.SetEnabled(!inheritValue);

            var hintLabel = new Label(inheritValue ? "Resolved from parent: " + inheritedValue : "Using node override");
            hintLabel.style.marginTop = 2f;
            hintLabel.style.color = SecondaryTextColor;

            RegisterRejectedDragTarget(row);
            RegisterRejectedDragTarget(field);
            RegisterRejectedDragTarget(pickButton);

            row.Add(label);
            row.Add(field);
            row.Add(pickButton);

            container.Add(row);
            container.Add(hintLabel);
            return container;
        }

#endif

        private VisualElement BuildMultiFieldDetailPanel(
            IReadOnlyList<PrefabAutoBindNodeSnapshot> snapshots,
            IReadOnlyList<PrefabAutoBindNodeConfig> nodeConfigs)
        {
            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Column;

            var addButton = new Button(() => ShowAddComponentMenu(snapshots, nodeConfigs))
            {
                text = "Add Component",
            };
            addButton.style.marginTop = 6f;
            addButton.style.marginBottom = 8f;
            container.Add(addButton);

            var commonComponents = GetCommonComponentBindings(nodeConfigs);
            if (commonComponents.Count == 0)
            {
                container.Add(new HelpBox(
                    "The selected nodes have no component bindings in common.",
                    HelpBoxMessageType.None));
                return container;
            }

            for (var index = 0; index < commonComponents.Count; index++)
            {
                container.Add(BuildMultiComponentEditor(commonComponents[index]));
            }

            return container;
        }

        private List<MultiComponentBinding> GetCommonComponentBindings(
            IReadOnlyList<PrefabAutoBindNodeConfig> nodeConfigs)
        {
            var result = new List<MultiComponentBinding>();
            if (nodeConfigs.Count == 0)
            {
                return result;
            }

            var firstNode = nodeConfigs[0];
            for (var componentIndex = 0; componentIndex < firstNode.Components.Count; componentIndex++)
            {
                var firstComponent = firstNode.Components[componentIndex];
                if (firstComponent == null)
                {
                    continue;
                }

                var binding = new MultiComponentBinding
                {
                    TypeName = firstComponent.ComponentTypeName,
                    ComponentIndex = firstComponent.ComponentIndex,
                };
                binding.Nodes.Add(firstNode);
                binding.Components.Add(firstComponent);

                var isCommon = true;
                for (var nodeIndex = 1; nodeIndex < nodeConfigs.Count; nodeIndex++)
                {
                    var component = FindComponentBinding(
                        nodeConfigs[nodeIndex],
                        firstComponent.ComponentTypeName,
                        firstComponent.ComponentIndex);
                    if (component == null)
                    {
                        isCommon = false;
                        break;
                    }

                    binding.Nodes.Add(nodeConfigs[nodeIndex]);
                    binding.Components.Add(component);
                }

                if (isCommon)
                {
                    result.Add(binding);
                }
            }

            return result;
        }

        private VisualElement BuildMultiComponentEditor(MultiComponentBinding binding)
        {
            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Column;
            container.style.marginBottom = 6f;
            container.style.paddingLeft = 8f;
            container.style.paddingRight = 8f;
            container.style.paddingTop = 6f;
            container.style.paddingBottom = 6f;
            ApplySurfaceStyle(container, InsetBackgroundColor, 4f);

            var headerRow = new VisualElement();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.alignItems = Align.Center;

            var componentDisplayName = PrefabAutoBindEditorUtility.GetShortTypeName(binding.TypeName);
            var matchingTypeCount = 0;
            for (var index = 0; index < binding.Nodes[0].Components.Count; index++)
            {
                if (string.Equals(
                        binding.Nodes[0].Components[index].ComponentTypeName,
                        binding.TypeName,
                        StringComparison.Ordinal))
                {
                    matchingTypeCount++;
                }
            }

            if (matchingTypeCount > 1)
            {
                componentDisplayName += " #" + (binding.ComponentIndex + 1);
            }

            var typeLabel = new Label(componentDisplayName);
            typeLabel.style.flexGrow = 1f;
            typeLabel.style.unityFontStyleAndWeight = FontStyle.Bold;

            var removeButton = new Button(() =>
            {
                if (!EditorUtility.DisplayDialog(
                        "Remove Component Bindings",
                        "Remove this component binding from all " + binding.Nodes.Count + " selected nodes?",
                        "Remove",
                        "Cancel"))
                {
                    return;
                }

                for (var index = 0; index < binding.Nodes.Count; index++)
                {
                    binding.Nodes[index].Components.Remove(binding.Components[index]);
                }

                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                RebuildNodeTree();
                RebuildDetailPane();
                UpdateNodeSummary();
            })
            {
                text = "Remove",
            };
            removeButton.style.marginLeft = 8f;

            headerRow.Add(typeLabel);
            headerRow.Add(removeButton);
            container.Add(headerRow);

            var editorRow = new VisualElement();
            editorRow.style.flexDirection = FlexDirection.Row;
            editorRow.style.alignItems = Align.Center;
            editorRow.style.marginTop = 6f;

            var hasCommonFieldName = TryGetCommonValue(
                binding.Components,
                component => component.FieldName,
                out var commonFieldName);
            var fieldName = new TextField("Field")
            {
                value = hasCommonFieldName ? commonFieldName : string.Empty,
                isDelayed = true,
                showMixedValue = !hasCommonFieldName,
            };
            fieldName.style.flexGrow = 1f;
            fieldName.style.flexShrink = 1f;
            fieldName.style.flexBasis = 0f;
            fieldName.style.minWidth = 0f;
            fieldName.style.marginRight = 8f;
            fieldName.RegisterValueChangedCallback(evt =>
            {
                var nextFieldName = PrefabAutoBindEditorUtility.MakeSafeIdentifier(evt.newValue, "node");
                if (!CanAssignFieldName(binding, nextFieldName, out var errorMessage))
                {
                    EditorUtility.DisplayDialog("Field Name Conflict", errorMessage, "OK");
                    RebuildDetailPane();
                    return;
                }

                for (var index = 0; index < binding.Components.Count; index++)
                {
                    binding.Components[index].FieldName = nextFieldName;
                }

                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                RebuildNodeTree();
                RebuildDetailPane();
            });

            var hasCommonAccess = TryGetCommonValue(
                binding.Components,
                component => component.AccessModifier,
                out var commonAccess);
            var accessChoices = hasCommonAccess
                ? new List<string> { "Private", "Public", "Protected", "Internal" }
                : new List<string> { "--", "Private", "Public", "Protected", "Internal" };
            var accessField = new PopupField<string>(
                "Access",
                accessChoices,
                hasCommonAccess ? commonAccess.ToString() : "--");
            accessField.style.width = 180f;
            accessField.style.flexShrink = 0f;
            accessField.RegisterValueChangedCallback(evt =>
            {
                if (!Enum.TryParse(evt.newValue, out PrefabAutoBindFieldAccessModifier nextAccess))
                {
                    return;
                }

                for (var index = 0; index < binding.Components.Count; index++)
                {
                    binding.Components[index].AccessModifier = nextAccess;
                }

                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                RebuildDetailPane();
            });

            editorRow.Add(fieldName);
            editorRow.Add(accessField);
            container.Add(editorRow);

            var containerRow = new VisualElement();
            containerRow.style.flexDirection = FlexDirection.Row;
            containerRow.style.alignItems = Align.Center;
            containerRow.style.marginTop = 6f;

            var hasCommonContainerKind = TryGetCommonValue(
                binding.Components,
                component => component.ContainerKind,
                out var commonContainerKind);
            var containerChoices = hasCommonContainerKind
                ? new List<string> { "None", "Array", "List" }
                : new List<string> { "--", "None", "Array", "List" };
            var containerKindField = new PopupField<string>(
                "Container",
                containerChoices,
                hasCommonContainerKind ? commonContainerKind.ToString() : "--");
            containerKindField.style.width = 180f;
            containerKindField.style.flexShrink = 0f;
            containerKindField.style.marginRight = 8f;
            containerKindField.RegisterValueChangedCallback(evt =>
            {
                if (!Enum.TryParse(evt.newValue, out PrefabAutoBindContainerKind nextContainerKind))
                {
                    return;
                }

                for (var index = 0; index < binding.Components.Count; index++)
                {
                    binding.Components[index].ContainerKind = nextContainerKind;
                }

                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                RebuildDetailPane();
            });
            containerRow.Add(containerKindField);

            var hasContainer = false;
            for (var index = 0; index < binding.Components.Count; index++)
            {
                if (binding.Components[index].ContainerKind == PrefabAutoBindContainerKind.None)
                {
                    continue;
                }

                hasContainer = true;
                break;
            }

            if (hasContainer)
            {
                var hasCommonContainerName = TryGetCommonValue(
                    binding.Components,
                    component => component.ContainerName ?? string.Empty,
                    out var commonContainerName);
                var containerNameField = new TextField("Name")
                {
                    value = hasCommonContainerName ? commonContainerName : string.Empty,
                    isDelayed = true,
                    showMixedValue = !hasCommonContainerName,
                };
                containerNameField.style.flexGrow = 1f;
                containerNameField.style.flexShrink = 1f;
                containerNameField.style.flexBasis = 0f;
                containerNameField.style.minWidth = 0f;
                containerNameField.RegisterValueChangedCallback(evt =>
                {
                    var nextContainerName = string.IsNullOrWhiteSpace(evt.newValue)
                        ? string.Empty
                        : PrefabAutoBindEditorUtility.MakeSafeIdentifier(evt.newValue, "container");
                    for (var index = 0; index < binding.Components.Count; index++)
                    {
                        binding.Components[index].ContainerName = nextContainerName;
                    }

                    PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                    RebuildDetailPane();
                });
                containerRow.Add(containerNameField);
            }

            container.Add(containerRow);
            container.Add(BuildMultiComponentEventSection(binding));
            return container;
        }

        private bool CanAssignFieldName(
            MultiComponentBinding binding,
            string candidate,
            out string errorMessage)
        {
            var targetComponents = new HashSet<PrefabAutoBindComponentConfig>(binding.Components);
            var targetOwners = new HashSet<PrefabAutoBindNodeId>();
            for (var index = 0; index < binding.Nodes.Count; index++)
            {
                var ownerId = PrefabAutoBindEditorUtility.GetOwningExportRootId(currentConfig, binding.Nodes[index]);
                if (targetOwners.Add(ownerId))
                {
                    continue;
                }

                errorMessage =
                    "Field Name '" + candidate +
                    "' would be duplicated by selected components in the same exported class. No components were changed.";
                return false;
            }

            for (var nodeIndex = 0; nodeIndex < currentConfig.Nodes.Count; nodeIndex++)
            {
                var node = currentConfig.Nodes[nodeIndex];
                if (node == null || node.NodeKind == PrefabAutoBindNodeKind.View)
                {
                    continue;
                }

                var ownerId = PrefabAutoBindEditorUtility.GetOwningExportRootId(currentConfig, node);
                if (!targetOwners.Contains(ownerId))
                {
                    continue;
                }

                for (var componentIndex = 0; componentIndex < node.Components.Count; componentIndex++)
                {
                    var component = node.Components[componentIndex];
                    if (component == null || targetComponents.Contains(component))
                    {
                        continue;
                    }

                    if (!string.Equals(component.FieldName, candidate, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    errorMessage =
                        "Field Name '" + candidate +
                        "' is already used in one of the affected exported classes. No components were changed.";
                    return false;
                }
            }

            errorMessage = null;
            return true;
        }

        private VisualElement BuildMultiComponentEventSection(MultiComponentBinding binding)
        {
            var supportedEvents = PrefabAutoBindComponentEventCatalog.GetSupportedEvents(binding.TypeName);
            if (supportedEvents.Count == 0)
            {
                return new VisualElement();
            }

            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Column;
            container.style.marginTop = 8f;

            var headerRow = new VisualElement();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.alignItems = Align.Center;

            var title = new Label("Events");
            title.style.flexGrow = 1f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;

            var addEventButton = new Button(() => ShowAddEventMenu(binding))
            {
                text = "Add Event",
            };
            var allEventsCommon = true;
            for (var index = 0; index < supportedEvents.Count; index++)
            {
                if (HasEventOnAllComponents(binding, supportedEvents[index].Kind))
                {
                    continue;
                }

                allEventsCommon = false;
                break;
            }

            addEventButton.SetEnabled(!allEventsCommon);
            headerRow.Add(title);
            headerRow.Add(addEventButton);
            container.Add(headerRow);

            var commonEvents = new List<PrefabAutoBindComponentEventKind>();
            for (var index = 0; index < binding.Components[0].Events.Count; index++)
            {
                var eventKind = binding.Components[0].Events[index];
                if (HasEventOnAllComponents(binding, eventKind))
                {
                    commonEvents.Add(eventKind);
                }
            }

            if (commonEvents.Count == 0)
            {
                var emptyLabel = new Label("No events are selected on every node.");
                emptyLabel.style.marginTop = 4f;
                emptyLabel.style.color = SecondaryTextColor;
                container.Add(emptyLabel);
                return container;
            }

            var hasCommonFieldName = TryGetCommonValue(
                binding.Components,
                component => component.FieldName,
                out var commonFieldName);
            var hasCommonEventHandlerNameTemplate = TryGetCommonValue(
                binding.Nodes,
                GetEventHandlerNameTemplate,
                out var commonEventHandlerNameTemplate);
            for (var index = 0; index < commonEvents.Count; index++)
            {
                var eventKind = commonEvents[index];
                if (PrefabAutoBindComponentEventCatalog.TryGetSupportedEvent(
                        binding.TypeName,
                        eventKind,
                        out var supportedEvent))
                {
                    var handlerMethodName = hasCommonFieldName && hasCommonEventHandlerNameTemplate
                        ? PrefabAutoBindTemplateRenderer.RenderEventHandlerName(
                            commonEventHandlerNameTemplate,
                            commonFieldName,
                            supportedEvent.EventPropertyName)
                        : "--";
                    container.Add(BuildMultiComponentEventRow(
                        binding,
                        eventKind,
                        supportedEvent.DisplayName,
                        handlerMethodName));
                    continue;
                }

                container.Add(BuildMultiComponentEventRow(
                    binding,
                    eventKind,
                    eventKind.ToString(),
                    "Unsupported event mapping"));
            }

            return container;
        }

        private VisualElement BuildMultiComponentEventRow(
            MultiComponentBinding binding,
            PrefabAutoBindComponentEventKind eventKind,
            string eventDisplayName,
            string handlerMethodName)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 4f;

            var labelContainer = new VisualElement();
            labelContainer.style.flexDirection = FlexDirection.Column;
            labelContainer.style.flexGrow = 1f;
            labelContainer.style.flexShrink = 1f;
            labelContainer.style.minWidth = 0f;

            var eventLabel = new Label(eventDisplayName);
            eventLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            var handlerLabel = new Label(handlerMethodName);
            handlerLabel.style.color = SecondaryTextColor;
            labelContainer.Add(eventLabel);
            labelContainer.Add(handlerLabel);

            var removeButton = new Button(() =>
            {
                for (var index = 0; index < binding.Components.Count; index++)
                {
                    binding.Components[index].Events.Remove(eventKind);
                }

                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                RebuildDetailPane();
            })
            {
                text = "Remove",
            };
            removeButton.style.marginLeft = 8f;

            row.Add(labelContainer);
            row.Add(removeButton);
            return row;
        }

        private void ShowAddEventMenu(MultiComponentBinding binding)
        {
            var supportedEvents = PrefabAutoBindComponentEventCatalog.GetSupportedEvents(binding.TypeName);
            var menu = new GenericMenu();
            var addedAny = false;

            for (var index = 0; index < supportedEvents.Count; index++)
            {
                var supportedEvent = supportedEvents[index];
                if (HasEventOnAllComponents(binding, supportedEvent.Kind))
                {
                    menu.AddDisabledItem(new GUIContent(supportedEvent.DisplayName));
                    continue;
                }

                addedAny = true;
                var eventKind = supportedEvent.Kind;
                menu.AddItem(
                    new GUIContent(supportedEvent.DisplayName),
                    false,
                    () => AddComponentEvent(binding, eventKind));
            }

            if (!addedAny)
            {
                menu.AddDisabledItem(new GUIContent("No more events for these components"));
            }

            menu.ShowAsContext();
        }

        private void AddComponentEvent(
            MultiComponentBinding binding,
            PrefabAutoBindComponentEventKind eventKind)
        {
            for (var index = 0; index < binding.Components.Count; index++)
            {
                if (!binding.Components[index].Events.Contains(eventKind))
                {
                    binding.Components[index].Events.Add(eventKind);
                }
            }

            PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
            RebuildDetailPane();
        }

        private static bool HasEventOnAllComponents(
            MultiComponentBinding binding,
            PrefabAutoBindComponentEventKind eventKind)
        {
            for (var index = 0; index < binding.Components.Count; index++)
            {
                if (!binding.Components[index].Events.Contains(eventKind))
                {
                    return false;
                }
            }

            return true;
        }

        private void ShowAddComponentMenu(
            IReadOnlyList<PrefabAutoBindNodeSnapshot> snapshots,
            IReadOnlyList<PrefabAutoBindNodeConfig> nodeConfigs)
        {
            var menu = new GenericMenu();
            var commonAvailableComponents = GetCommonAvailableComponents(snapshots);
            var addedAny = false;

            for (var index = 0; index < commonAvailableComponents.Count; index++)
            {
                var available = commonAvailableComponents[index];
                var selectedOnAll = true;
                for (var nodeIndex = 0; nodeIndex < nodeConfigs.Count; nodeIndex++)
                {
                    if (HasSelectedComponent(nodeConfigs[nodeIndex], available))
                    {
                        continue;
                    }

                    selectedOnAll = false;
                    break;
                }

                if (selectedOnAll)
                {
                    menu.AddDisabledItem(new GUIContent(available.DisplayName));
                    continue;
                }

                addedAny = true;
                var capturedAvailable = available;
                menu.AddItem(
                    new GUIContent(available.DisplayName),
                    false,
                    () => AddComponentBindings(nodeConfigs, capturedAvailable));
            }

            if (!addedAny)
            {
                menu.AddDisabledItem(new GUIContent("No common components to add"));
            }

            menu.ShowAsContext();
        }

        private static List<PrefabAutoBindAvailableComponent> GetCommonAvailableComponents(
            IReadOnlyList<PrefabAutoBindNodeSnapshot> snapshots)
        {
            var result = new List<PrefabAutoBindAvailableComponent>();
            if (snapshots.Count == 0)
            {
                return result;
            }

            for (var componentIndex = 0; componentIndex < snapshots[0].AvailableComponents.Count; componentIndex++)
            {
                var available = snapshots[0].AvailableComponents[componentIndex];
                var isCommon = true;
                for (var snapshotIndex = 1; snapshotIndex < snapshots.Count; snapshotIndex++)
                {
                    if (ContainsAvailableComponent(snapshots[snapshotIndex], available))
                    {
                        continue;
                    }

                    isCommon = false;
                    break;
                }

                if (isCommon)
                {
                    result.Add(available);
                }
            }

            return result;
        }

        private void AddComponentBindings(
            IReadOnlyList<PrefabAutoBindNodeConfig> nodeConfigs,
            PrefabAutoBindAvailableComponent available)
        {
            for (var index = 0; index < nodeConfigs.Count; index++)
            {
                var nodeConfig = nodeConfigs[index];
                if (HasSelectedComponent(nodeConfig, available))
                {
                    continue;
                }

                var binding = new PrefabAutoBindComponentConfig
                {
                    ComponentTypeName = available.TypeName,
                    ComponentIndex = available.ComponentIndex,
                    AccessModifier = PrefabAutoBindFieldAccessModifier.Private,
                    ContainerKind = PrefabAutoBindContainerKind.None,
                    ContainerName = string.Empty,
                };
                binding.FieldName = PrefabAutoBindEditorUtility.BuildDefaultFieldName(
                    currentConfig,
                    nodeConfig,
                    available.TypeName,
                    nodeConfig.Components.Count == 0,
                    binding);
                nodeConfig.Components.Add(binding);
            }

            PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
            RebuildNodeTree();
            RebuildDetailPane();
            UpdateNodeSummary();
        }

        private static bool ContainsAvailableComponent(
            PrefabAutoBindNodeSnapshot snapshot,
            PrefabAutoBindAvailableComponent available)
        {
            for (var index = 0; index < snapshot.AvailableComponents.Count; index++)
            {
                var candidate = snapshot.AvailableComponents[index];
                if (candidate.ComponentIndex == available.ComponentIndex &&
                    string.Equals(candidate.TypeName, available.TypeName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static PrefabAutoBindComponentConfig FindComponentBinding(
            PrefabAutoBindNodeConfig nodeConfig,
            string componentTypeName,
            int componentIndex)
        {
            for (var index = 0; index < nodeConfig.Components.Count; index++)
            {
                var component = nodeConfig.Components[index];
                if (component.ComponentIndex == componentIndex &&
                    string.Equals(component.ComponentTypeName, componentTypeName, StringComparison.Ordinal))
                {
                    return component;
                }
            }

            return null;
        }

        private VisualElement BuildComponentEditor(
            PrefabAutoBindNodeConfig nodeConfig,
            PrefabAutoBindComponentConfig component,
            bool isFirstComponent)
        {
            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Column;
            container.style.marginBottom = 6f;
            container.style.paddingLeft = 8f;
            container.style.paddingRight = 8f;
            container.style.paddingTop = 6f;
            container.style.paddingBottom = 6f;
            ApplySurfaceStyle(container, InsetBackgroundColor, 4f);

            var headerRow = new VisualElement();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.alignItems = Align.Center;

            var editorRow = new VisualElement();
            editorRow.style.flexDirection = FlexDirection.Row;
            editorRow.style.alignItems = Align.Center;
            editorRow.style.marginTop = 6f;

            var containerRow = new VisualElement();
            containerRow.style.flexDirection = FlexDirection.Row;
            containerRow.style.alignItems = Align.Center;
            containerRow.style.marginTop = 6f;

            var typeLabel = new Label(PrefabAutoBindEditorUtility.GetShortTypeName(component.ComponentTypeName));
            typeLabel.style.flexGrow = 1f;
            typeLabel.style.unityFontStyleAndWeight = FontStyle.Bold;

            var fieldName = new TextField("Field")
            {
                value = component.FieldName,
                isDelayed = true,
            };
            fieldName.style.flexGrow = 1f;
            fieldName.style.flexShrink = 1f;
            fieldName.style.flexBasis = 0f;
            fieldName.style.minWidth = 0f;
            fieldName.style.marginRight = 8f;
            fieldName.RegisterValueChangedCallback(evt =>
            {
                component.FieldName = PrefabAutoBindEditorUtility.MakeSafeIdentifier(
                    evt.newValue,
                    PrefabAutoBindEditorUtility.BuildDefaultFieldName(
                        currentConfig,
                        nodeConfig,
                        component.ComponentTypeName,
                        isFirstComponent,
                        component));
                fieldName.SetValueWithoutNotify(component.FieldName);
                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                RebuildNodeTree();
                RebuildDetailPane();
            });

            var accessField = new EnumField("Access", component.AccessModifier);
            accessField.style.width = 180f;
            accessField.style.flexShrink = 0f;
            accessField.RegisterValueChangedCallback(evt =>
            {
                component.AccessModifier = (PrefabAutoBindFieldAccessModifier)evt.newValue;
                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
            });

            var containerKindField = new EnumField("Container", component.ContainerKind);
            containerKindField.style.width = 180f;
            containerKindField.style.flexShrink = 0f;
            containerKindField.style.marginRight = 8f;
            containerKindField.RegisterValueChangedCallback(evt =>
            {
                component.ContainerKind = (PrefabAutoBindContainerKind)evt.newValue;
                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                RebuildDetailPane();
            });

            var containerNameField = new TextField("Name")
            {
                value = component.ContainerName ?? string.Empty,
                isDelayed = true,
            };
            containerNameField.style.flexGrow = 1f;
            containerNameField.style.flexShrink = 1f;
            containerNameField.style.flexBasis = 0f;
            containerNameField.style.minWidth = 0f;
            containerNameField.RegisterValueChangedCallback(evt =>
            {
                component.ContainerName = string.IsNullOrWhiteSpace(evt.newValue)
                    ? string.Empty
                    : PrefabAutoBindEditorUtility.MakeSafeIdentifier(evt.newValue, "container");
                containerNameField.SetValueWithoutNotify(component.ContainerName);
                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
            });

            var removeButton = new Button(() =>
            {
                nodeConfig.Components.Remove(component);
                PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
                RebuildNodeTree();
                RebuildDetailPane();
                UpdateNodeSummary();
            })
            {
                text = "Remove",
            };
            removeButton.style.marginLeft = 8f;

            headerRow.Add(typeLabel);
            headerRow.Add(removeButton);

            editorRow.Add(fieldName);
            editorRow.Add(accessField);

            containerRow.Add(containerKindField);
            if (component.ContainerKind != PrefabAutoBindContainerKind.None)
            {
                containerRow.Add(containerNameField);
            }

            container.Add(headerRow);
            container.Add(editorRow);
            container.Add(containerRow);
            container.Add(BuildComponentEventSection(nodeConfig, component));
            return container;
        }

        private VisualElement BuildComponentEventSection(
            PrefabAutoBindNodeConfig nodeConfig,
            PrefabAutoBindComponentConfig component)
        {
            var supportedEvents = PrefabAutoBindComponentEventCatalog.GetSupportedEvents(component.ComponentTypeName);
            if (supportedEvents.Count == 0)
            {
                return new VisualElement();
            }

            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Column;
            container.style.marginTop = 8f;

            var headerRow = new VisualElement();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.alignItems = Align.Center;

            var title = new Label("Events");
            title.style.flexGrow = 1f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;

            var addEventButton = new Button(() => ShowAddEventMenu(component))
            {
                text = "Add Event",
            };
            addEventButton.SetEnabled(component.Events.Count < supportedEvents.Count);

            headerRow.Add(title);
            headerRow.Add(addEventButton);
            container.Add(headerRow);

            if (component.Events.Count == 0)
            {
                var emptyLabel = new Label("No events selected for this component.");
                emptyLabel.style.marginTop = 4f;
                emptyLabel.style.color = SecondaryTextColor;
                container.Add(emptyLabel);
                return container;
            }

            for (var index = 0; index < component.Events.Count; index++)
            {
                var eventKind = component.Events[index];
                if (PrefabAutoBindComponentEventCatalog.TryGetSupportedEvent(component.ComponentTypeName, eventKind, out var supportedEvent))
                {
                    container.Add(BuildComponentEventRow(
                        component,
                        eventKind,
                        supportedEvent.DisplayName,
                        PrefabAutoBindTemplateRenderer.RenderEventHandlerName(
                            GetEventHandlerNameTemplate(nodeConfig),
                            component.FieldName,
                            supportedEvent.EventPropertyName)));
                    continue;
                }

                container.Add(BuildComponentEventRow(component, eventKind, eventKind.ToString(), "Unsupported event mapping"));
            }

            return container;
        }

        private string GetEventHandlerNameTemplate(PrefabAutoBindNodeConfig nodeConfig)
        {
            var settings = PrefabAutoBindEditorUtility.GetSettingsForNode(currentConfig, nodeConfig);
            return settings != null && !string.IsNullOrWhiteSpace(settings.EventHandlerNameTemplate)
                ? settings.EventHandlerNameTemplate
                : PrefabAutoBindSettings.DefaultEventHandlerNameTemplate;
        }

        private VisualElement BuildComponentEventRow(
            PrefabAutoBindComponentConfig component,
            PrefabAutoBindComponentEventKind eventKind,
            string eventDisplayName,
            string handlerMethodName)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 4f;

            var labelContainer = new VisualElement();
            labelContainer.style.flexDirection = FlexDirection.Column;
            labelContainer.style.flexGrow = 1f;
            labelContainer.style.flexShrink = 1f;
            labelContainer.style.minWidth = 0f;

            var eventLabel = new Label(eventDisplayName);
            eventLabel.style.unityFontStyleAndWeight = FontStyle.Bold;

            var handlerLabel = new Label(handlerMethodName);
            handlerLabel.style.color = SecondaryTextColor;

            labelContainer.Add(eventLabel);
            labelContainer.Add(handlerLabel);

            var removeButton = new Button(() => RemoveComponentEvent(component, eventKind))
            {
                text = "Remove",
            };
            removeButton.style.marginLeft = 8f;

            row.Add(labelContainer);
            row.Add(removeButton);
            return row;
        }

        private void ShowAddEventMenu(PrefabAutoBindComponentConfig component)
        {
            var supportedEvents = PrefabAutoBindComponentEventCatalog.GetSupportedEvents(component.ComponentTypeName);
            var menu = new GenericMenu();
            var addedAny = false;

            for (var index = 0; index < supportedEvents.Count; index++)
            {
                var supportedEvent = supportedEvents[index];
                if (component.Events.Contains(supportedEvent.Kind))
                {
                    menu.AddDisabledItem(new GUIContent(supportedEvent.DisplayName));
                    continue;
                }

                addedAny = true;
                menu.AddItem(new GUIContent(supportedEvent.DisplayName), false, () => AddComponentEvent(component, supportedEvent.Kind));
            }

            if (!addedAny)
            {
                menu.AddDisabledItem(new GUIContent("No more events for this component"));
            }

            menu.ShowAsContext();
        }

        private void AddComponentEvent(PrefabAutoBindComponentConfig component, PrefabAutoBindComponentEventKind eventKind)
        {
            if (component.Events.Contains(eventKind))
            {
                return;
            }

            component.Events.Add(eventKind);
            PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
            RebuildDetailPane();
        }

        private void RemoveComponentEvent(PrefabAutoBindComponentConfig component, PrefabAutoBindComponentEventKind eventKind)
        {
            if (!component.Events.Remove(eventKind))
            {
                return;
            }

            PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
            RebuildDetailPane();
        }

        private void ShowAddComponentMenu(PrefabAutoBindNodeSnapshot snapshot, PrefabAutoBindNodeConfig nodeConfig)
        {
            var menu = new GenericMenu();
            var addedAny = false;

            for (var index = 0; index < snapshot.AvailableComponents.Count; index++)
            {
                var available = snapshot.AvailableComponents[index];
                if (HasSelectedComponent(nodeConfig, available))
                {
                    menu.AddDisabledItem(new GUIContent(available.DisplayName));
                    continue;
                }

                addedAny = true;
                menu.AddItem(new GUIContent(available.DisplayName), false, () => AddComponentBinding(nodeConfig, available));
            }

            if (!addedAny)
            {
                menu.AddDisabledItem(new GUIContent("No more components on this node"));
            }

            menu.ShowAsContext();
        }

        private void AddComponentBinding(PrefabAutoBindNodeConfig nodeConfig, PrefabAutoBindAvailableComponent available)
        {
            var binding = new PrefabAutoBindComponentConfig
            {
                ComponentTypeName = available.TypeName,
                ComponentIndex = available.ComponentIndex,
                AccessModifier = PrefabAutoBindFieldAccessModifier.Private,
                ContainerKind = PrefabAutoBindContainerKind.None,
                ContainerName = string.Empty,
            };

            binding.FieldName = PrefabAutoBindEditorUtility.BuildDefaultFieldName(
                currentConfig,
                nodeConfig,
                available.TypeName,
                nodeConfig.Components.Count == 0,
                binding);
            nodeConfig.Components.Add(binding);
            PrefabAutoBindEditorUtility.SaveConfig(currentConfig);
            RebuildNodeTree();
            RebuildDetailPane();
            UpdateNodeSummary();
        }

        private void UpdateNodeSummary()
        {
            var bindingCount = 0;
            if (currentConfig != null)
            {
                for (var nodeIndex = 0; nodeIndex < currentConfig.Nodes.Count; nodeIndex++)
                {
                    bindingCount += currentConfig.Nodes[nodeIndex].Components.Count;
                }
            }

            nodeSummaryLabel.text = "Prefab Nodes " + nodeSnapshots.Count + " | Bindings " + bindingCount;
        }

        private void RebuildNodeTree()
        {
            if (nodeTreeScrollView == null)
            {
                return;
            }

            nodeTreeScrollView.Clear();
            if (nodeSnapshots.Count == 0)
            {
                nodeTreeScrollView.Add(new Label("No prefab nodes."));
                return;
            }

            for (var index = 0; index < nodeSnapshots.Count; index++)
            {
                var snapshot = nodeSnapshots[index];
                if (!IsNodeVisible(snapshot.NodeId))
                {
                    continue;
                }

                nodeTreeScrollView.Add(CreateTreeRow(snapshot));
            }
        }

        private VisualElement CreateTemplateField(
            string label,
            Func<string> getValue,
            Action<string> setValue,
            float minHeight = 42f,
            bool preserveWhitespace = false)
        {
            var container = new VisualElement();
            container.style.marginTop = 6f;

            var caption = new Label(label);
            caption.style.unityFontStyleAndWeight = FontStyle.Bold;
            container.Add(caption);

            var field = new TextField
            {
                isDelayed = true,
                multiline = true,
            };
            field.style.minHeight = minHeight;
            field.style.whiteSpace = WhiteSpace.Normal;
            field.RegisterValueChangedCallback(evt =>
            {
                var value = string.IsNullOrWhiteSpace(evt.newValue)
                    ? string.Empty
                    : preserveWhitespace
                        ? evt.newValue
                        : evt.newValue.Trim();
                setValue(value);
                field.SetValueWithoutNotify(getValue());
            });
            container.Add(field);
            return container;
        }

        private static string BuildBindTemplateTokenHelpText()
        {
            return "Bind Template tokens:\n"
                + "${class_name}, ${namespace}, ${parent_class}\n"
                + "${root_object} \u2190 Root Object Name\n"
                + "${usings}, ${fields}, ${find_statements}\n"
                + "${add_event_statements}, ${remove_event_statements}, ${event_handlers}\n"
                + "${find_statements} \u2190 Find Statement Template + Plain Child View Bind Template\n"
                + "${add_event_statements} \u2190 Add Event Statement Template\n"
                + "${remove_event_statements} \u2190 Remove Event Statement Template\n"
                + "Plain Child View Bind Template controls every BindByFind child view, including MonoBehaviour views.\n"
                + "Tokens: ${obj_name}, ${obj_ref}, ${class_name}, ${obj_path} (Transform expression)\n"
                + "Multiline tokens must be on their own line. Root Object Name also becomes the initial ${parent_obj}.";
        }

        private void UpdateFindSettingsVisibility()
        {
            // Find settings now live in the selected Settings asset.
        }

        private void UpdatePreviewLabels()
        {
            if (currentConfig == null)
            {
                logicPreviewLabel.text = "Logic Preview: -";
                partialPreviewLabel.text = "Bind Preview: -";
                return;
            }

            settingsField?.SetValueWithoutNotify(currentConfig.Settings);

            var className = string.IsNullOrWhiteSpace(currentConfig.ClassName)
                ? "NewPrefab"
                : currentConfig.ClassName;
            if (currentConfig.Settings == null)
            {
                logicPreviewLabel.text = "Logic Preview: - (select Settings)";
                partialPreviewLabel.text = "Bind Preview: - (select Settings)";
                return;
            }

            logicPreviewLabel.text = "Logic Preview: " + PrefabAutoBindEditorUtility.CombineProjectPath(
                PrefabAutoBindEditorUtility.NormalizeDirectory(
                    currentConfig.Settings.LogicOutputDirectory,
                    "Assets/Scripts/Logic"),
                className + ".cs");
            partialPreviewLabel.text = "Bind Preview: " + PrefabAutoBindEditorUtility.CombineProjectPath(
                PrefabAutoBindEditorUtility.NormalizeDirectory(
                    currentConfig.Settings.PartialOutputDirectory,
                    "Assets/Scripts/Logic/Generated"),
                className + ".Bind.cs");
        }

        private void Export()
        {
            Export(false);
        }

        private void RegenerateAll()
        {
            if (currentConfig == null)
            {
                EditorUtility.DisplayDialog("Prefab Auto Bind", "Select a prefab first.", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    RegenerateAllDialogTitle,
                    RegenerateAllDialogMessage,
                    RegenerateAllButtonText,
                    "Cancel"))
            {
                return;
            }

            Export(true);
        }

        private void Export(bool overwriteExistingLogicFiles)
        {
            if (currentConfig == null)
            {
                EditorUtility.DisplayDialog("Prefab Auto Bind", "Select a prefab first.", "OK");
                return;
            }

            PersistWindowState();

            var result = PrefabAutoBindCodeGenerator.Export(
                currentConfig,
                overwriteExistingLogicFiles);
            if (result.Pending)
            {
                Debug.Log(result.Message);
                ShowNotification(new GUIContent("Scripts generated. Waiting for Unity to compile them before automatic binding."));
            }
            else
            {
                EditorUtility.DisplayDialog(result.Success ? "Export Complete" : "Export Failed", result.Message, "OK");
            }

            RefreshSnapshots();
        }

        private void PickFolder(TextField targetField, bool isLogicDirectory)
        {
            PickFolder(targetField.value, projectRelative =>
            {
                if (currentConfig == null || currentConfig.Settings == null)
                {
                    return;
                }

                if (isLogicDirectory)
                {
                    currentConfig.Settings.LogicOutputDirectory = projectRelative;
                }
                else
                {
                    currentConfig.Settings.PartialOutputDirectory = projectRelative;
                }

                EditorUtility.SetDirty(currentConfig.Settings);
                AssetDatabase.SaveAssets();
                UpdatePreviewLabels();
            });
        }

        private void PickFolder(string currentValue, Action<string> applyPickedPath)
        {
            var initialDirectory = PrefabAutoBindEditorUtility.GetProjectRoot();
            if (!string.IsNullOrWhiteSpace(currentValue))
            {
                var normalized = currentValue.Replace('\\', '/');
                if (normalized.StartsWith("Assets"))
                {
                    initialDirectory = Path.GetFullPath(
                        Path.Combine(
                            PrefabAutoBindEditorUtility.GetProjectRoot(),
                            normalized));
                }
                else if (Path.IsPathRooted(normalized))
                {
                    initialDirectory = normalized;
                }
            }

            var absolute = EditorUtility.OpenFolderPanel("Select export folder", initialDirectory, string.Empty);
            if (string.IsNullOrWhiteSpace(absolute))
            {
                return;
            }

            var projectRelative = PrefabAutoBindEditorUtility.ToProjectRelativePath(absolute);
            if (string.IsNullOrWhiteSpace(projectRelative))
            {
                EditorUtility.DisplayDialog("Prefab Auto Bind", "The export directory must be inside the current project.", "OK");
                return;
            }

            applyPickedPath?.Invoke(projectRelative);
        }

        private VisualElement CreateDirectoryRow(string labelText, TextField field, string buttonText, bool isLogicDirectory)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 6f;
            row.style.flexShrink = 0f;

            var label = new Label(labelText);
            label.style.width = 70f;
            label.style.minWidth = 70f;
            label.style.flexShrink = 0f;
            label.style.marginRight = 8f;

            var pickButton = new Button(() => PickFolder(field, isLogicDirectory))
            {
                text = buttonText,
            };
            pickButton.style.width = 120f;
            pickButton.style.minWidth = 120f;
            pickButton.style.flexShrink = 0f;

            RegisterRejectedDragTarget(row);
            RegisterRejectedDragTarget(field);
            RegisterRejectedDragTarget(pickButton);

            row.Add(label);
            row.Add(field);
            row.Add(pickButton);
            return row;
        }

        private TextField CreateDirectoryField(bool isLogicDirectory)
        {
            var field = new TextField
            {
                isDelayed = true,
            };
            field.style.flexGrow = 1f;
            field.style.flexShrink = 1f;
            field.style.flexBasis = 0f;
            field.style.minWidth = 0f;
            field.style.marginRight = 8f;
            field.tooltip = "Input a project-relative path such as Assets/Scripts/Logic, or use the pick button.";

            field.RegisterValueChangedCallback(evt =>
            {
                if (currentConfig == null || currentConfig.Settings == null)
                {
                    return;
                }

                if (isLogicDirectory)
                {
                    currentConfig.Settings.LogicOutputDirectory = PrefabAutoBindEditorUtility.NormalizeDirectory(
                        evt.newValue,
                        "Assets/Scripts/Logic");
                    field.SetValueWithoutNotify(currentConfig.Settings.LogicOutputDirectory);
                }
                else
                {
                    currentConfig.Settings.PartialOutputDirectory = PrefabAutoBindEditorUtility.NormalizeDirectory(
                        evt.newValue,
                        "Assets/Scripts/Logic/Generated");
                    field.SetValueWithoutNotify(currentConfig.Settings.PartialOutputDirectory);
                }

                EditorUtility.SetDirty(currentConfig.Settings);
                AssetDatabase.SaveAssets();
                UpdatePreviewLabels();
            });

            return field;
        }

        private void RegisterRejectedDragTarget(VisualElement target)
        {
            target.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                evt.StopPropagation();
            });
            target.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                evt.StopPropagation();
            }, TrickleDown.TrickleDown);

            target.RegisterCallback<DragPerformEvent>(evt =>
            {
                evt.StopPropagation();
                evt.PreventDefault();
            });
            target.RegisterCallback<DragPerformEvent>(evt =>
            {
                evt.StopPropagation();
                evt.PreventDefault();
            }, TrickleDown.TrickleDown);
        }

        private void TryLoadSelectionPrefab()
        {
            if (Selection.activeObject is GameObject prefab && IsPrefabAsset(prefab))
            {
                LoadPrefab(prefab);
            }
        }

        private void TryRestorePersistedWindowState()
        {
            var reloadState = PrefabAutoBindWindowReloadState.instance;
            var configPath = !string.IsNullOrWhiteSpace(reloadState.ConfigPath)
                ? reloadState.ConfigPath
                : string.IsNullOrWhiteSpace(persistedConfigPath)
                    ? SessionState.GetString(PersistedConfigPathKey, string.Empty)
                    : persistedConfigPath;
            if (string.IsNullOrWhiteSpace(configPath))
            {
                return;
            }

            var config = AssetDatabase.LoadAssetAtPath<PrefabAutoBindConfig>(configPath);
            if (config == null || config.Prefab == null)
            {
                ClearPersistedWindowState();
                return;
            }

            selectedNodeId = reloadState.SelectedNodeId.IsValid
                ? reloadState.SelectedNodeId
                : persistedSelectedNodeId;
            SelectedNodeIds.Clear();
            RestoreSelectedNodeIds(reloadState.SelectedNodeIds);
            if (SelectedNodeIds.Count == 0)
            {
                RestoreSelectedNodeIds(persistedSelectedNodeIds);
            }

            if (SelectedNodeIds.Count == 0)
            {
                RestoreSessionSelection();
            }

            if (SelectedNodeIds.Count == 0 && selectedNodeId.IsValid)
            {
                SelectedNodeIds.Add(selectedNodeId);
            }

            LoadPrefab(config.Prefab);
        }

        private void PersistWindowState()
        {
            if (currentConfig == null)
            {
                ClearPersistedWindowState();
                return;
            }

            var configPath = AssetDatabase.GetAssetPath(currentConfig);
            if (string.IsNullOrWhiteSpace(configPath))
            {
                return;
            }

            persistedConfigPath = configPath;
            persistedSelectedNodeId = selectedNodeId;
            if (persistedSelectedNodeIds == null)
            {
                persistedSelectedNodeIds = new List<PrefabAutoBindNodeId>();
            }

            persistedSelectedNodeIds.Clear();
            var orderedSelectedNodeIds = GetOrderedSelectedNodeIds();
            persistedSelectedNodeIds.AddRange(orderedSelectedNodeIds);
            PrefabAutoBindWindowReloadState.instance.SaveSelection(
                configPath,
                selectedNodeId,
                orderedSelectedNodeIds);
            SessionState.SetString(PersistedConfigPathKey, configPath);
            SessionState.SetString(
                PersistedSelectedNodeIdsKey,
                JsonUtility.ToJson(new PersistedSelection
                {
                    anchorNodeId = selectedNodeId,
                    nodeIds = orderedSelectedNodeIds,
                }));
        }

        private void ClearPersistedWindowState()
        {
            persistedConfigPath = string.Empty;
            persistedSelectedNodeId = default;
            persistedSelectedNodeIds?.Clear();
            SelectedNodeIds.Clear();
            PrefabAutoBindWindowReloadState.instance.ClearSelection();
            SessionState.EraseString(PersistedConfigPathKey);
            SessionState.EraseString(PersistedSelectedNodeIdKey);
            SessionState.EraseString(PersistedSelectedNodeIdsKey);
            SessionState.EraseString("PrefabAutoBind.Window.SelectedNodePath");
            SessionState.EraseString("PrefabAutoBind.Window.SelectedNodePaths");
        }

        private HashSet<PrefabAutoBindNodeId> SelectedNodeIds
        {
            get
            {
                if (selectedNodeIds == null)
                {
                    selectedNodeIds = new HashSet<PrefabAutoBindNodeId>();
                }

                return selectedNodeIds;
            }
        }

        private void RestoreSelectedNodeIds(IReadOnlyList<PrefabAutoBindNodeId> nodeIds)
        {
            if (nodeIds == null)
            {
                return;
            }

            for (var index = 0; index < nodeIds.Count; index++)
            {
                if (nodeIds[index].IsValid)
                {
                    SelectedNodeIds.Add(nodeIds[index]);
                }
            }
        }

        private void RestoreSessionSelection()
        {
            var json = SessionState.GetString(PersistedSelectedNodeIdsKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            try
            {
                var selection = JsonUtility.FromJson<PersistedSelection>(json);
                if (selection == null)
                {
                    return;
                }

                if (!selectedNodeId.IsValid)
                {
                    selectedNodeId = selection.anchorNodeId;
                }

                RestoreSelectedNodeIds(selection.nodeIds);
            }
            catch (ArgumentException)
            {
                SessionState.EraseString(PersistedSelectedNodeIdsKey);
            }
        }

        private List<PrefabAutoBindNodeId> GetOrderedSelectedNodeIds()
        {
            var result = new List<PrefabAutoBindNodeId>(SelectedNodeIds.Count);
            for (var index = 0; index < nodeSnapshots.Count; index++)
            {
                var nodeId = nodeSnapshots[index].NodeId;
                if (SelectedNodeIds.Contains(nodeId))
                {
                    result.Add(nodeId);
                }
            }

            return result;
        }

        private void PruneAndInitializeSelection()
        {
            var validNodeIds = new HashSet<PrefabAutoBindNodeId>();
            for (var index = 0; index < nodeSnapshots.Count; index++)
            {
                validNodeIds.Add(nodeSnapshots[index].NodeId);
            }

            SelectedNodeIds.RemoveWhere(nodeId => !validNodeIds.Contains(nodeId));
            if (SelectedNodeIds.Count == 0 && nodeSnapshots.Count > 0)
            {
                selectedNodeId = nodeSnapshots[0].NodeId;
                SelectedNodeIds.Add(selectedNodeId);
                return;
            }

            if (selectedNodeId.IsValid && SelectedNodeIds.Contains(selectedNodeId))
            {
                return;
            }

            selectedNodeId = default;
            for (var index = 0; index < nodeSnapshots.Count; index++)
            {
                if (!SelectedNodeIds.Contains(nodeSnapshots[index].NodeId))
                {
                    continue;
                }

                selectedNodeId = nodeSnapshots[index].NodeId;
                break;
            }
        }

        private bool IsNodeSelected(PrefabAutoBindNodeId nodeId)
        {
            return nodeId.IsValid && SelectedNodeIds.Contains(nodeId);
        }

        private void UpdateNodeSelection(
            PrefabAutoBindNodeId nodeId,
            bool selectRange,
            bool toggleSelection)
        {
            if (selectRange && TrySelectVisibleRange(nodeId, toggleSelection))
            {
                return;
            }

            if (toggleSelection)
            {
                if (!SelectedNodeIds.Add(nodeId))
                {
                    SelectedNodeIds.Remove(nodeId);
                    if (selectedNodeId == nodeId)
                    {
                        var orderedSelection = GetOrderedSelectedNodeIds();
                        selectedNodeId = orderedSelection.Count > 0 ? orderedSelection[0] : default;
                    }
                }
                else
                {
                    selectedNodeId = nodeId;
                }

                return;
            }

            SelectedNodeIds.Clear();
            SelectedNodeIds.Add(nodeId);
            selectedNodeId = nodeId;
        }

        private bool TrySelectVisibleRange(PrefabAutoBindNodeId nodeId, bool addToSelection)
        {
            if (!selectedNodeId.IsValid)
            {
                return false;
            }

            var visibleNodeIds = new List<PrefabAutoBindNodeId>();
            for (var index = 0; index < nodeSnapshots.Count; index++)
            {
                if (IsNodeVisible(nodeSnapshots[index].NodeId))
                {
                    visibleNodeIds.Add(nodeSnapshots[index].NodeId);
                }
            }

            var anchorIndex = visibleNodeIds.IndexOf(selectedNodeId);
            var targetIndex = visibleNodeIds.IndexOf(nodeId);
            if (anchorIndex < 0 || targetIndex < 0)
            {
                return false;
            }

            if (!addToSelection)
            {
                SelectedNodeIds.Clear();
            }

            var firstIndex = Math.Min(anchorIndex, targetIndex);
            var lastIndex = Math.Max(anchorIndex, targetIndex);
            for (var index = firstIndex; index <= lastIndex; index++)
            {
                SelectedNodeIds.Add(visibleNodeIds[index]);
            }

            return true;
        }

        private bool HasSelectedComponent(PrefabAutoBindNodeConfig nodeConfig, PrefabAutoBindAvailableComponent available)
        {
            for (var index = 0; index < nodeConfig.Components.Count; index++)
            {
                var component = nodeConfig.Components[index];
                if (component.ComponentIndex == available.ComponentIndex &&
                    component.ComponentTypeName == available.TypeName)
                {
                    return true;
                }
            }

            return false;
        }

        private VisualElement CreateTreeRow(PrefabAutoBindNodeSnapshot snapshot)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.minHeight = 24f;
            row.style.marginBottom = 1f;
            row.style.paddingTop = 4f;
            row.style.paddingBottom = 4f;
            row.style.paddingRight = 6f;
            row.style.paddingLeft = 6f + (snapshot.Depth * 16f);
            row.style.borderBottomLeftRadius = 3f;
            row.style.borderBottomRightRadius = 3f;
            row.style.borderTopLeftRadius = 3f;
            row.style.borderTopRightRadius = 3f;
            var isSelected = IsNodeSelected(snapshot.NodeId);
            row.style.backgroundColor = isSelected
                ? SelectionBackgroundColor
                : Color.clear;

            var hasChildren = HasChildren(snapshot.NodeId);
            if (hasChildren)
            {
                var toggleButton = new Button(() =>
                {
                    expandedNodeStates[snapshot.NodeId] = !IsExpanded(snapshot.NodeId);
                    RebuildNodeTree();
                })
                {
                    text = IsExpanded(snapshot.NodeId) ? "▾" : "▸",
                };
                toggleButton.style.width = 18f;
                toggleButton.style.minWidth = 18f;
                toggleButton.style.height = 18f;
                toggleButton.style.marginRight = 4f;
                toggleButton.style.paddingLeft = 0f;
                toggleButton.style.paddingRight = 0f;
                toggleButton.style.paddingTop = 0f;
                toggleButton.style.paddingBottom = 0f;
                row.Add(toggleButton);
            }
            else
            {
                var spacer = new VisualElement();
                spacer.style.width = 22f;
                spacer.style.minWidth = 22f;
                row.Add(spacer);
            }

            var nodeConfig = PrefabAutoBindEditorUtility.FindNodeConfig(currentConfig, snapshot.NodeId);

            var nameLabel = new Label(nodeConfig != null ? nodeConfig.NodeName : snapshot.DisplayName);
            nameLabel.style.flexGrow = 1f;
            nameLabel.style.flexShrink = 1f;
            nameLabel.style.minWidth = 0f;
            nameLabel.style.color = isSelected ? SelectedTextColor : PrimaryTextColor;
            nameLabel.style.unityFontStyleAndWeight = isSelected ? FontStyle.Bold : FontStyle.Normal;

            var tags = new VisualElement();
            tags.style.flexDirection = FlexDirection.Row;
            tags.style.flexWrap = Wrap.NoWrap;
            tags.style.flexShrink = 0f;
            if (nodeConfig != null)
            {
                if (nodeConfig.NodeKind == PrefabAutoBindNodeKind.View)
                {
                    tags.Add(CreatePillTag("VIEW", ViewTagBackgroundColor));
                }

                for (var componentIndex = 0; componentIndex < nodeConfig.Components.Count; componentIndex++)
                {
                    tags.Add(CreateComponentTag(nodeConfig.Components[componentIndex].ComponentTypeName));
                }
            }

            row.Add(nameLabel);
            row.Add(tags);
            row.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0)
                {
                    return;
                }

                UpdateNodeSelection(snapshot.NodeId, evt.shiftKey, evt.commandKey || evt.ctrlKey);
                PersistWindowState();
                RebuildNodeTree();
                RebuildDetailPane();
                evt.StopPropagation();
            });
            return row;
        }

        private VisualElement CreateComponentTag(string componentTypeName)
        {
            return CreatePillTag(PrefabAutoBindEditorUtility.BuildTagLabel(componentTypeName), TagBackgroundColor);
        }

        private VisualElement CreatePillTag(string text, Color backgroundColor)
        {
            var tag = new Label(text);
            tag.style.marginLeft = 4f;
            tag.style.paddingLeft = 6f;
            tag.style.paddingRight = 6f;
            tag.style.paddingTop = 2f;
            tag.style.paddingBottom = 2f;
            tag.style.fontSize = 10f;
            tag.style.backgroundColor = backgroundColor;
            tag.style.color = TagTextColor;
            tag.style.borderBottomLeftRadius = 9f;
            tag.style.borderBottomRightRadius = 9f;
            tag.style.borderTopLeftRadius = 9f;
            tag.style.borderTopRightRadius = 9f;
            return tag;
        }

        private void RefreshTreeMetadata()
        {
            nodeHasChildren.Clear();
            parentNodeIds.Clear();
            var validNodeIds = new HashSet<PrefabAutoBindNodeId>();
            for (var index = 0; index < nodeSnapshots.Count; index++)
            {
                var snapshot = nodeSnapshots[index];
                validNodeIds.Add(snapshot.NodeId);
                nodeHasChildren[snapshot.NodeId] = false;
                parentNodeIds[snapshot.NodeId] = snapshot.ParentNodeId;
            }

            for (var index = 0; index < nodeSnapshots.Count; index++)
            {
                var parentNodeId = nodeSnapshots[index].ParentNodeId;
                if (!parentNodeId.IsValid)
                {
                    continue;
                }

                nodeHasChildren[parentNodeId] = true;
            }

            var staleKeys = new List<PrefabAutoBindNodeId>();
            foreach (var key in expandedNodeStates.Keys)
            {
                if (!validNodeIds.Contains(key))
                {
                    staleKeys.Add(key);
                }
            }

            for (var index = 0; index < staleKeys.Count; index++)
            {
                expandedNodeStates.Remove(staleKeys[index]);
            }

            for (var index = 0; index < nodeSnapshots.Count; index++)
            {
                var snapshot = nodeSnapshots[index];
                if (HasChildren(snapshot.NodeId) && !expandedNodeStates.ContainsKey(snapshot.NodeId))
                {
                    expandedNodeStates[snapshot.NodeId] = true;
                }
            }
        }

        private bool HasChildren(PrefabAutoBindNodeId nodeId)
        {
            return nodeHasChildren.TryGetValue(nodeId, out var hasChildren) && hasChildren;
        }

        private bool IsExpanded(PrefabAutoBindNodeId nodeId)
        {
            return !expandedNodeStates.TryGetValue(nodeId, out var expanded) || expanded;
        }

        private bool IsNodeVisible(PrefabAutoBindNodeId nodeId)
        {
            if (!parentNodeIds.TryGetValue(nodeId, out var parentNodeId))
            {
                return true;
            }

            while (parentNodeId.IsValid)
            {
                if (HasChildren(parentNodeId) && !IsExpanded(parentNodeId))
                {
                    return false;
                }

                if (!parentNodeIds.TryGetValue(parentNodeId, out parentNodeId))
                {
                    break;
                }
            }

            return true;
        }

        private PrefabAutoBindNodeSnapshot GetSelectedSnapshot()
        {
            for (var index = 0; index < nodeSnapshots.Count; index++)
            {
                if (nodeSnapshots[index].NodeId == selectedNodeId)
                {
                    return nodeSnapshots[index];
                }
            }

            return null;
        }

        private List<PrefabAutoBindNodeSnapshot> GetSelectedSnapshots()
        {
            var result = new List<PrefabAutoBindNodeSnapshot>(SelectedNodeIds.Count);
            for (var index = 0; index < nodeSnapshots.Count; index++)
            {
                if (SelectedNodeIds.Contains(nodeSnapshots[index].NodeId))
                {
                    result.Add(nodeSnapshots[index]);
                }
            }

            return result;
        }

        private List<PrefabAutoBindNodeConfig> GetNodeConfigs(
            IReadOnlyList<PrefabAutoBindNodeSnapshot> snapshots)
        {
            var result = new List<PrefabAutoBindNodeConfig>(snapshots.Count);
            for (var index = 0; index < snapshots.Count; index++)
            {
                var nodeConfig = PrefabAutoBindEditorUtility.FindNodeConfig(currentConfig, snapshots[index].NodeId);
                if (nodeConfig != null)
                {
                    result.Add(nodeConfig);
                }
            }

            return result;
        }

        private static bool TryGetCommonValue<TSource, TValue>(
            IReadOnlyList<TSource> source,
            Func<TSource, TValue> selector,
            out TValue commonValue)
        {
            if (source == null || source.Count == 0)
            {
                commonValue = default;
                return false;
            }

            commonValue = selector(source[0]);
            var comparer = EqualityComparer<TValue>.Default;
            for (var index = 1; index < source.Count; index++)
            {
                if (!comparer.Equals(commonValue, selector(source[index])))
                {
                    return false;
                }
            }

            return true;
        }

        private static VisualElement CreateCard()
        {
            var element = new VisualElement();
            element.style.flexGrow = 1f;
            element.style.paddingLeft = 10f;
            element.style.paddingRight = 10f;
            element.style.paddingTop = 10f;
            element.style.paddingBottom = 10f;
            ApplySurfaceStyle(element, PanelBackgroundColor, 6f);
            return element;
        }

        private void RegisterPrefabDropTarget(VisualElement target)
        {
            target.style.paddingLeft = 8f;
            target.style.paddingRight = 8f;
            target.style.paddingTop = 8f;
            target.style.paddingBottom = 8f;
            ApplySurfaceStyle(target, InsetBackgroundColor, 6f);

            target.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                if (!TryGetDraggedPrefab(out _))
                {
                    return;
                }

                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                evt.StopPropagation();
            });

            target.RegisterCallback<DragPerformEvent>(evt =>
            {
                if (!TryGetDraggedPrefab(out var prefab))
                {
                    return;
                }

                DragAndDrop.AcceptDrag();
                LoadPrefab(prefab);
                evt.StopPropagation();
            });
        }

        private void RegisterWindowDropTarget()
        {
            rootVisualElement.RegisterCallback<DragUpdatedEvent>(OnWindowDragUpdated);
            rootVisualElement.RegisterCallback<DragPerformEvent>(OnWindowDragPerform);
        }

        private void OnWindowDragUpdated(DragUpdatedEvent evt)
        {
            if (!TryGetDraggedPrefab(out _))
            {
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            evt.StopPropagation();
        }

        private void OnWindowDragPerform(DragPerformEvent evt)
        {
            if (!TryGetDraggedPrefab(out var prefab))
            {
                return;
            }

            DragAndDrop.AcceptDrag();
            LoadPrefab(prefab);
            evt.StopPropagation();
        }

        private static bool TryGetDraggedPrefab(out GameObject prefab)
        {
            prefab = null;
            var references = DragAndDrop.objectReferences;
            if (references == null || references.Length == 0)
            {
                return false;
            }

            for (var index = 0; index < references.Length; index++)
            {
                var candidate = references[index] as GameObject;
                if (candidate == null)
                {
                    continue;
                }

                if (IsPrefabAsset(candidate))
                {
                    prefab = candidate;
                    return true;
                }
            }

            return false;
        }

        private static void ApplySurfaceStyle(VisualElement element, Color backgroundColor, float borderRadius)
        {
            element.style.backgroundColor = backgroundColor;
            element.style.borderBottomLeftRadius = borderRadius;
            element.style.borderBottomRightRadius = borderRadius;
            element.style.borderTopLeftRadius = borderRadius;
            element.style.borderTopRightRadius = borderRadius;
            element.style.borderLeftWidth = 1f;
            element.style.borderRightWidth = 1f;
            element.style.borderTopWidth = 1f;
            element.style.borderBottomWidth = 1f;
            element.style.borderLeftColor = BorderColor;
            element.style.borderRightColor = BorderColor;
            element.style.borderTopColor = BorderColor;
            element.style.borderBottomColor = BorderColor;
        }
    }
}
