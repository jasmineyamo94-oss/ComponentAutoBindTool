using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using PsdTools;
using PsdTools.Layers;
using System.CodeDom.Compiler;

namespace PsdTools
{
    public class PSDEditorWindow : EditorWindow
    {
        private PsdImage _psd;
        private string _psdPath;

        private Layer _selectedLayer;
        private Dictionary<Layer, bool> _foldoutStates = new Dictionary<Layer, bool>();
        private Vector2 _treeScrollPos;
        /// <summary>Last frame's left tree ScrollView view position.</summary>
        private Vector2 _lastLeftTreeScroll;
        /// <summary>Last frame's right property ScrollView view position.</summary>
        private Vector2 _lastRightPropScroll;
        /// <summary>Last frame's left tree ScrollView viewport height (below toolbar); matches DrawLeftPanel layout for keyboard scroll logic.</summary>
        private float _treeScrollViewportHeight;
        private Vector2 _propertyScrollPos;
        /// <summary>Right properties panel scroll position per layer (in memory only; lost when switching PSD or closing the window).</summary>
        private Dictionary<Layer, Vector2> _propertyScrollPosByLayer = new Dictionary<Layer, Vector2>();
        /// <summary>Stable hint per layer so the right ScrollView gets its own control id (avoids inheriting the previous layer's scroll state).</summary>
        private Dictionary<Layer, int> _propertyScrollGuiHintByLayer = new Dictionary<Layer, int>();
        private int _nextPropertyScrollGuiHint = 1;

        private string _editingName;
        /// <summary>Layer currently being inline-renamed in the left tree; F2 to start, Enter to commit, Esc to cancel.</summary>
        private Layer _inlineRenamingLayer;
        private string _inlineRenameBuffer;
        private int _inlineRenameFocusFramesLeft;
        private Layer _nameLongPressLayer;
        private double _nameLongPressStartTime;
        private Vector2 _nameLongPressStartGui;
        /// <summary>Long-press threshold met while LMB still down; release opens inline rename.</summary>
        private bool _nameLongPressHoldMet;
        /// <summary>Do not use Input.GetMouseButton for LMB in the editor; set true on node name MouseDown, false on global MouseUp.</summary>
        private bool _nameLongPressLeftButtonHeld;
        /// <summary>When _selectedLayer started being selected (updated only when selection changes); used to treat same-node clicks within 0.45s as normal selection.</summary>
        private double _layerBecameSelectedAt;
        private bool _editingVisible;
        private bool _compositeDirty;
        /// <summary>true = live composite preview when toggling layer visibility (composite from layers); false = always show PSD baked composite.</summary>
        private bool _liveComposite = false;
        /// <summary>Default TextMeshPro usage when opening a PSD with no config, or when type layers lack useTextMeshPro in config.</summary>
        private bool _defaultUseTMP = false;
        /// <summary>Default slice usage when opening a PSD with no config, or when layers lack sliceImage in config.</summary>
        private bool _defaultSliceImage = true;
        /// <summary>Bottom TMP batch toggle state: 0 = TMP mode (click switches to Legacy), 1 = Legacy (click switches to TMP), -1 = uninitialized.</summary>
        private int _tmpToggleState = -1;
        /// <summary>Bottom slice batch toggle state: 0 = Slice mode (click switches to raw export), 1 = Raw mode (click switches to Slice / nine-slice), -1 = uninitialized.</summary>
        private int _sliceToggleState = -1;
        /// <summary>When enabled, exported PNGs use NodeName_LayerId.png; when off, NodeName.png only (names must be unique within an export run).</summary>
        private bool _exportAutoImageNaming = true;
        /// <summary>When enabled, node name differences between PSD and existing Prefab are considered changes (default on).</summary>
        private bool _exportCompareNameDiff = true;
        /// <summary>true = left tree follows PSD layer order; false = Unity Prefab hierarchy order (inverse of PSD).</summary>
        private bool _usePsdNodeOrder = false;
        /// <summary>true = when a common-dir dedup match is smaller than the current image, prompt user to replace; false = skip replacement prompt.</summary>
        private bool _detectCommonDirLargerImage = false;

        private Texture2D _canvasBgTex;
        private Texture2D _layerRectTex;
        private Texture2D _selectionHighlightTex;
        /// <summary>Full PSD composite for the canvas background; wireframes draw on top.</summary>
        private Texture2D _psdCompositeTex;

        /// <summary>Top of right panel: rendered preview of the selected layer.</summary>
        private Texture2D _layerPreviewTex;
        /// <summary>Bump to rebuild layer preview after full composite refresh (e.g. visibility changes).</summary>
        private int _layerPreviewEpoch;
        private int _layerPreviewBuiltForEpoch = -1;
        private Layer _layerPreviewBuiltForLayer;

        /// <summary>Merge for export: when true, this node and children export as one image; children are not exported separately.</summary>
        private Dictionary<Layer, bool> _mergeExportByLayer = new Dictionary<Layer, bool>();
        /// <summary>Export Prefab: when true, PSD export also creates a separate Prefab for this node.</summary>
        private Dictionary<Layer, bool> _exportPrefabByLayer = new Dictionary<Layer, bool>();
        /// <summary>Use external Prefab: when true, no slice/export for this node; generation places the chosen prefab at this transform.</summary>
        private Dictionary<Layer, bool> _useExternalPrefabByLayer = new Dictionary<Layer, bool>();
        /// <summary>Asset path of referenced prefab (Assets/xxx.prefab); only when use external Prefab is true.</summary>
        private Dictionary<Layer, string> _externalPrefabPathByLayer = new Dictionary<Layer, string>();
        /// <summary>When referencing a Prefab, reuse this PSD node’s position (default true).</summary>
        private Dictionary<Layer, bool> _externalPrefabReusePositionByLayer = new Dictionary<Layer, bool>();
        /// <summary>When referencing a Prefab, reuse this node’s size (default false).</summary>
        private Dictionary<Layer, bool> _externalPrefabReuseSizeByLayer = new Dictionary<Layer, bool>();
        /// <summary>Participate in same-export dedup: when true, this node’s export is deduped against others in the run (default on).</summary>
        /// <summary>Participate in same-export dedup: when true, this node’s export is deduped against others in the run (default on).</summary>
        private Dictionary<Layer, bool> _participateLocalDedupByLayer = new Dictionary<Layer, bool>();
        /// <summary>Participate in common-directory dedup: config flag; export pipeline dedupes against common cache only when this is on and Save to common directory is off.</summary>
        private Dictionary<Layer, bool> _participateCommonDedupByLayer = new Dictionary<Layer, bool>();
        /// <summary>Slice for nine-slice: when true, export runs nine-slice; when false, export raw image.</summary>
        private Dictionary<Layer, bool> _sliceImageByLayer = new Dictionary<Layer, bool>();
        /// <summary>Primary node for local dedup group: if several are set, the virtual representative is the largest resolution (pixels + params from it); default off.</summary>
        private Dictionary<Layer, bool> _primaryDedupNodeByLayer = new Dictionary<Layer, bool>();
        /// <summary>Slice with per-layer custom nine-slice params (written to _export_config.json).</summary>
        private Dictionary<Layer, bool> _useCustomNineSliceParamsByLayer = new Dictionary<Layer, bool>();
        /// <summary>Custom nine-slice params (only when the layer is true in the dictionary above).</summary>
        private Dictionary<Layer, NineSliceLayerParams> _nineSliceParamsByLayer = new Dictionary<Layer, NineSliceLayerParams>();
        /// <summary>Use custom image: when true, use the assigned Sprite; skip slice, nine-slice, dedup, and prefab replacement.</summary>
        private Dictionary<Layer, bool> _useCustomImageByLayer = new Dictionary<Layer, bool>();
        /// <summary>Assets path of custom image (e.g. "Assets/xxx.png"); only when useCustomImage is true.</summary>
        private Dictionary<Layer, string> _customImagePathByLayer = new Dictionary<Layer, string>();
        /// <summary>UI component type attached on export (e.g. None, Button); written to _export_config.json.</summary>
        private Dictionary<Layer, string> _uiComponentTypeByLayer = new Dictionary<Layer, string>();
        /// <summary>ScrollBar Direction (left_to_right, etc.); only when type is ScrollBar.</summary>
        private Dictionary<Layer, string> _scrollBarDirectionByLayer = new Dictionary<Layer, string>();
        /// <summary>ScrollBar handle child name; assigned to Scrollbar.handleRect on export.</summary>
        private Dictionary<Layer, string> _scrollBarHandleChildNameByLayer = new Dictionary<Layer, string>();
        /// <summary>Slider Direction (left_to_right, etc.); only when type is Slider.</summary>
        private Dictionary<Layer, string> _sliderDirectionByLayer = new Dictionary<Layer, string>();
        /// <summary>Slider fillRect child name.</summary>
        private Dictionary<Layer, string> _sliderFillRectChildNameByLayer = new Dictionary<Layer, string>();
        /// <summary>Slider handleRect child name.</summary>
        private Dictionary<Layer, string> _sliderHandleRectChildNameByLayer = new Dictionary<Layer, string>();
        /// <summary>ScrollRect: existing Viewport child name; assigned directly to ScrollRect.viewport on export.</summary>
        private Dictionary<Layer, string> _scrollRectScrollBgChildNameByLayer = new Dictionary<Layer, string>();
        /// <summary>ScrollRect: Content child name; found by name and assigned to ScrollRect.content without reparenting.</summary>
        private Dictionary<Layer, string> _scrollRectContentChildNameByLayer = new Dictionary<Layer, string>();
        /// <summary>ScrollRect allows horizontal scrolling.</summary>
        private Dictionary<Layer, bool> _scrollRectHorizontalByLayer = new Dictionary<Layer, bool>();
        /// <summary>ScrollRect allows vertical scrolling.</summary>
        private Dictionary<Layer, bool> _scrollRectVerticalByLayer = new Dictionary<Layer, bool>();
        /// <summary>ScrollRect horizontal Scrollbar child name.</summary>
        private Dictionary<Layer, string> _scrollRectHorizontalScrollbarChildNameByLayer = new Dictionary<Layer, string>();
        /// <summary>ScrollRect vertical Scrollbar child name.</summary>
        private Dictionary<Layer, string> _scrollRectVerticalScrollbarChildNameByLayer = new Dictionary<Layer, string>();
        /// <summary>Toggle Graphic child name; assigned to Toggle.graphic on export.</summary>
        private Dictionary<Layer, string> _toggleGraphicChildNameByLayer = new Dictionary<Layer, string>();
        /// <summary>InputField Text / textComponent child name.</summary>
        private Dictionary<Layer, string> _inputFieldTextChildNameByLayer = new Dictionary<Layer, string>();
        /// <summary>InputField Placeholder child name.</summary>
        private Dictionary<Layer, string> _inputFieldPlaceholderChildNameByLayer = new Dictionary<Layer, string>();
        /// <summary>InputField(TMP) text viewport child name.</summary>
        private Dictionary<Layer, string> _inputFieldTextViewportChildNameByLayer = new Dictionary<Layer, string>();
        /// <summary>Dropdown Template child name (RectTransform).</summary>
        private Dictionary<Layer, string> _dropdownTemplateChildNameByLayer = new Dictionary<Layer, string>();
        /// <summary>Dropdown Caption Text child name.</summary>
        private Dictionary<Layer, string> _dropdownCaptionTextChildNameByLayer = new Dictionary<Layer, string>();
        /// <summary>Dropdown Item Text child name.</summary>
        private Dictionary<Layer, string> _dropdownItemTextChildNameByLayer = new Dictionary<Layer, string>();
        /// <summary>Type layers export with TextMeshPro; missing from dict means true.</summary>
        private Dictionary<Layer, bool> _useTextMeshProByLayer = new Dictionary<Layer, bool>();
        /// <summary>Export this node: false skips slice and Prefab node (default true); missing from dict means true.</summary>
        private Dictionary<Layer, bool> _exportedByLayer = new Dictionary<Layer, bool>();
        /// <summary>Layer names as they existed in the source PSD when it was opened, keyed by LayerId.</summary>
        private Dictionary<int, string> _psdLayerNameSnapshot;

        // ── Right panel Undo / Redo (custom snapshots, separate from Unity global Undo) ──
        private const int RightPanelUndoMaxSteps = 50;

        private sealed class RightPanelUndoStep
        {
            public MergeExportConfigData exportData;
            public string[] commonDirList;
            public bool hasSelectedLayer;
            public int selectedLayerId;
        }

        private readonly List<RightPanelUndoStep> _rightPanelUndoStack = new List<RightPanelUndoStep>();
        private readonly List<RightPanelUndoStep> _rightPanelRedoStack = new List<RightPanelUndoStep>();
        private bool _rightPanelUndoApplying;

        /// <summary>Common directories config path under Assets/PsdToUnityUI/EditorConfig.</summary>
        private static string CommonDirectoriesConfigPath => Path.Combine(Application.dataPath, "PsdToUnityUI", "EditorConfig", "PSD_CommonDirectories.json");
        /// <summary>Loaded common-directory list (from config after opening PSD) for the right-panel popup.</summary>
        private List<string> _commonDirList = new List<string>();

        // ── Canvas click selection ──
        private Vector2 _lastClickPsdPos = new Vector2(-99999, -99999);
        private List<Layer> _clickCandidates = new List<Layer>();
        private int _clickCandidateIndex;
        private const float CLICK_SAME_SPOT_THRESHOLD = 5f;
        /// <summary>Draw blue selection box on canvas for selected node; false when clicking outside canvas.</summary>
        private bool _canvasShowSelection;
        /// <summary>Canvas rect in window space; used at top of OnGUI to test canvas hits.</summary>
        private Rect _canvasRectWindow;

        private const float LEFT_PANEL_WIDTH = 260f;
        private const float RIGHT_PANEL_WIDTH = 280f;
        private const float BOTTOM_PANEL_HEIGHT = 120f;
        private const string PrefExportImageFolder = "PSDEditor_ExportImageAssetsFolder";
        private const string PrefExportPrefabFolder = "PSDEditor_ExportPrefabAssetsFolder";
        private const string PrefExportAutoImageNaming = "PSDEditor_ExportAutoImageNaming";
        private const string PrefExportCompareNameDiff = "PSDEditor_ExportCompareNameDiff";
        private const string PrefAutoNavigateAfterExport = "PSDEditor_AutoNavigateAfterExport";
        private const string PrefLiveComposite = "PSDEditor_LiveComposite";
        private const string PrefDefaultUseTMP = "PSDEditor_DefaultUseTMP";
        private const string PrefDefaultSliceImage = "PSDEditor_DefaultSliceImage";
        private const string PrefDetectCommonDirLargerImage = "PSDEditor_DetectCommonDirLargerImage";
        private const string PrefUsePsdNodeOrder = "PSDEditor_UsePsdNodeOrder";
        private const string PrefClearExportFolderBeforeExport = "PSDEditor_ClearExportFolderBeforeExport";
        private const string PrefRecentFiles = "PSDEditor_RecentFiles";
        private const int RecentFilesMaxCount = 10;
        /// <summary>Recently opened PSD file paths (newest first, max 10).</summary>
        private List<string> _recentFiles = new List<string>();
        private Vector2 _recentFilesScrollPos;
        private Vector2 _noPsdScrollPos;
        private bool _showSettingsFoldout;
        /// <summary>After export, automatically navigate Project window to the output folder.</summary>
        private bool _autoNavigateAfterExport = true;
        /// <summary>When true, the image export folder ({psdName}) is deleted before each export run so stale slices are removed.</summary>
        private bool _clearExportFolderBeforeExport = true;
        /// <summary>Assets root for exported PNGs; actual folder is root / {PSD name}.</summary>
        private string _exportImageAssetsFolderRelative = "Assets";
        /// <summary>Assets root for saved Prefabs (can match image root).</summary>
        private string _exportPrefabAssetsFolderRelative = "Assets";
        private const float TREE_ROW_HEIGHT = 20f;
        private const float INDENT_WIDTH = 18f;
        private const double NAME_LONG_PRESS_HOLD_SECONDS = 0.45;
        private const float NAME_LONG_PRESS_MAX_GUI_DRAG_PX = 10f;
        /// <summary>Match leaf row <c>GUILayout.Space(16)</c> width so <c>EditorGUILayout.Foldout</c> does not steal horizontal space.</summary>
        private const float TREE_FOLDOUT_WIDTH = 16f;

        [MenuItem("Tools/PSD/PSD Editor")]
        public static void ShowWindow()
        {
            var window = GetWindow<PSDEditorWindow>("PSD Editor");
            window.minSize = new Vector2(900, 500);
        }

        private void OnEnable()
        {
            string legacyExport = EditorPrefs.GetString("PSDEditor_ExportAssetsFolder", "");
            string img = EditorPrefs.GetString(PrefExportImageFolder, "");
            string pref = EditorPrefs.GetString(PrefExportPrefabFolder, "");
            if (string.IsNullOrEmpty(img) && !string.IsNullOrEmpty(legacyExport))
            {
                img = legacyExport;
                EditorPrefs.SetString(PrefExportImageFolder, img);
            }
            if (string.IsNullOrEmpty(pref) && !string.IsNullOrEmpty(legacyExport))
            {
                pref = legacyExport;
                EditorPrefs.SetString(PrefExportPrefabFolder, pref);
            }
            _exportImageAssetsFolderRelative = string.IsNullOrEmpty(img) ? "Assets" : img;
            _exportPrefabAssetsFolderRelative = string.IsNullOrEmpty(pref) ? "Assets" : pref;
            _exportAutoImageNaming = EditorPrefs.GetBool(PrefExportAutoImageNaming, true);
            _exportCompareNameDiff = EditorPrefs.GetBool(PrefExportCompareNameDiff, true);
            _autoNavigateAfterExport = EditorPrefs.GetBool(PrefAutoNavigateAfterExport, true);
            _liveComposite = EditorPrefs.GetBool(PrefLiveComposite, false);
            _defaultUseTMP = EditorPrefs.GetBool(PrefDefaultUseTMP, false);
            _defaultSliceImage = EditorPrefs.GetBool(PrefDefaultSliceImage, true);
            _detectCommonDirLargerImage = EditorPrefs.GetBool(PrefDetectCommonDirLargerImage, false);
            _usePsdNodeOrder = EditorPrefs.GetBool(PrefUsePsdNodeOrder, false);
            _clearExportFolderBeforeExport = EditorPrefs.GetBool(PrefClearExportFolderBeforeExport, true);
            _canvasBgTex = MakeSolidTexture(new Color(0.85f, 0.85f, 0.85f, 1f));
            _layerRectTex = MakeSolidTexture(new Color(0.2f, 0.5f, 1f, 0.25f));
            _selectionHighlightTex = MakeSolidTexture(new Color(0.2f, 0.4f, 0.8f, 0.3f));
            LoadRecentFiles();
            EditorApplication.update += OnEditorUpdateNameLongPress;
        }

        private const string MemLogPrefix = "[PSDEditor.Memory]";

        private static void LogManagedSnapshot(string message)
        {
            long managed = System.GC.GetTotalMemory(false);
            Debug.Log($"{MemLogPrefix} {message} | managedHeap≈{managed / 1048576L} MB (GC.GetTotalMemory(false))");
        }

        private void OnDisable()
        {
            LogManagedSnapshot("OnDisable start");
            EditorApplication.update -= OnEditorUpdateNameLongPress;

            DestroyTexture(ref _canvasBgTex);
            DestroyTexture(ref _layerRectTex);
            DestroyTexture(ref _selectionHighlightTex);
            DestroyTexture(ref _psdCompositeTex);
            DestroyTexture(ref _layerPreviewTex);

            // Release PSD data so memory can be reclaimed after the window closes.
            ReleasePsdData("OnDisable");
            // Export pipeline pins Layer → full PsdDocument in PSDAutoPrefab static maps; release on close.
            PSDAutoPrefab.ReleaseExportSessionStaticPins();
            LogManagedSnapshot("OnDisable after ReleasePsdData + ReleaseExportSessionStaticPins (before deferred GC)");
            // Defer GC: OnDisable runs inside Unity’s window stack; Mono Boehm conservative GC may treat
            // stale stack values as roots and “pin” nulled PSD blobs. After delayCall the closing stack is gone,
            // greatly reducing false pinning.
            EditorApplication.delayCall += () =>
            {
                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                System.GC.Collect();
                LogManagedSnapshot("OnDisable deferred GC completed");
            };
        }

        /// <summary>Close the current PSD and return to home screen.</summary>
        private void CloseCurrentPsd()
        {
            ReleasePsdData("UserClose");
            GUIUtility.ExitGUI();
        }

        /// <summary>Release all memory tied to the current PSD (layer maps, document).</summary>
        /// <param name="reason">Log label for Console (e.g. OnDisable, BeforeLoad).</param>
        private void ReleasePsdData(string reason = "")
        {
            bool hadPsd = _psd != null;
            int layerCount = hadPsd ? _psd.LayerCount : 0;
            string pathLog = string.IsNullOrEmpty(_psdPath) ? "(none)" : _psdPath;
            long managedBefore = System.GC.GetTotalMemory(false);

            // Drop decompression caches before nulling refs; otherwise large byte[] waits on GC and Task Manager looks unchanged.
            // Eagerly release heavyweight PSD byte[] (compressed pixels, TaggedBlock, etc.) so they leave the PsdDocument graph.
            // Even if conservative GC falsely pins PsdImage, those buffers have no incoming refs and can be collected alone.
            _psd?.ReleaseAllData();
            _psd = null;
            _psdPath = null;
            _selectedLayer = null;
            _inlineRenamingLayer = null;
            _inlineRenameBuffer = null;
            _nameLongPressLayer = null;
            _layerPreviewBuiltForLayer = null;
            _psdLayerNameSnapshot = null;
            _tmpToggleState = -1;
            _sliceToggleState = -1;

            _foldoutStates.Clear();
            _propertyScrollPosByLayer.Clear();
            _propertyScrollGuiHintByLayer.Clear();
            _mergeExportByLayer.Clear();
            _exportPrefabByLayer.Clear();
            _useExternalPrefabByLayer.Clear();
            _externalPrefabPathByLayer.Clear();
            _externalPrefabReusePositionByLayer.Clear();
            _externalPrefabReuseSizeByLayer.Clear();
            _participateLocalDedupByLayer.Clear();
            _participateCommonDedupByLayer.Clear();
            _sliceImageByLayer.Clear();
            _primaryDedupNodeByLayer.Clear();
            _useCustomNineSliceParamsByLayer.Clear();
            _nineSliceParamsByLayer.Clear();
            _useCustomImageByLayer.Clear();
            _customImagePathByLayer.Clear();
            _uiComponentTypeByLayer.Clear();
            _scrollBarDirectionByLayer.Clear();
            _scrollBarHandleChildNameByLayer.Clear();
            _sliderDirectionByLayer.Clear();
            _sliderFillRectChildNameByLayer.Clear();
            _sliderHandleRectChildNameByLayer.Clear();
            _scrollRectScrollBgChildNameByLayer.Clear();
            _scrollRectContentChildNameByLayer.Clear();
            _scrollRectHorizontalByLayer.Clear();
            _scrollRectVerticalByLayer.Clear();
            _scrollRectHorizontalScrollbarChildNameByLayer.Clear();
            _scrollRectVerticalScrollbarChildNameByLayer.Clear();
            _toggleGraphicChildNameByLayer.Clear();
            _inputFieldTextChildNameByLayer.Clear();
            _inputFieldPlaceholderChildNameByLayer.Clear();
            _inputFieldTextViewportChildNameByLayer.Clear();
            _dropdownTemplateChildNameByLayer.Clear();
            _dropdownCaptionTextChildNameByLayer.Clear();
            _dropdownItemTextChildNameByLayer.Clear();
            _useTextMeshProByLayer.Clear();
            _exportedByLayer.Clear();
            _clickCandidates.Clear();
            _commonDirList.Clear();
            _rightPanelUndoStack.Clear();
            _rightPanelRedoStack.Clear();

            long managedAfter = System.GC.GetTotalMemory(false);
            Debug.Log(
                $"{MemLogPrefix} ReleasePsdData reason={reason} hadPsd={hadPsd} layerCount={layerCount} path={pathLog} " +
                $"managedBefore≈{managedBefore / 1048576L} MB managedAfterClear≈{managedAfter / 1048576L} MB " +
                $"(If before/after are close, large blocks leave the heap after GC; in Memory Profiler check snapshots for PsdDocument.)");
        }

        private void OnGUI()
        {
            if (_psd == null)
            {
                DrawNoPsdUI();
                return;
            }

            HandleRightPanelUndoRedoHotkeys();

            Event eMu = Event.current;
            if (eMu.type == EventType.MouseUp && eMu.button == 0)
            {
                if (_nameLongPressLayer != null)
                {
                    bool openRename = false;
                    // Long-press met on release: not limited by the post-select 0.45s click rule.
                    if (_nameLongPressHoldMet)
                        openRename = true;
                    // Short click after node selected >0.45s: open rename (same-node click within 0.45s does not use this branch)
                    else if (_nameLongPressLayer == _selectedLayer
                             && EditorApplication.timeSinceStartup - _layerBecameSelectedAt >= NAME_LONG_PRESS_HOLD_SECONDS
                             && EditorApplication.timeSinceStartup - _nameLongPressStartTime < NAME_LONG_PRESS_HOLD_SECONDS)
                        openRename = true;

                    if (openRename)
                    {
                        BeginOrRefocusInlineRename(_nameLongPressLayer);
                        Repaint();
                    }
                }

                _nameLongPressLeftButtonHeld = false;
                _nameLongPressLayer = null;
                _nameLongPressHoldMet = false;
            }

            TryHandleLayerRenameHotkey();
            HandleInlineRenameKeyboard();
            TryHandleRightPanelTextFieldConfirm();
            TryHandleLayerArrowNavigationHotkey();

            Rect fullRect = new Rect(0, 0, position.width, position.height);
            Rect topRect = new Rect(0, 0, fullRect.width, fullRect.height - BOTTOM_PANEL_HEIGHT);
            Rect bottomRect = new Rect(0, topRect.yMax, fullRect.width, BOTTOM_PANEL_HEIGHT);

            float centerWidth = Mathf.Max(100f, topRect.width - LEFT_PANEL_WIDTH - RIGHT_PANEL_WIDTH);
            Rect leftRect = new Rect(0, 0, LEFT_PANEL_WIDTH, topRect.height);
            Rect centerRect = new Rect(LEFT_PANEL_WIDTH, 0, centerWidth, topRect.height);
            Rect rightRect = new Rect(LEFT_PANEL_WIDTH + centerWidth, 0, RIGHT_PANEL_WIDTH, topRect.height);

            // Click outside canvas and right properties → hide canvas selection box
            // (tree node clicks in DrawLayerNode set it true again)
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
            {
                Vector2 mp = Event.current.mousePosition;
                bool inCanvas = _canvasRectWindow.width > 0 && _canvasRectWindow.Contains(mp);
                bool inRightPanel = rightRect.Contains(mp);
                if (!inCanvas && !inRightPanel)
                    _canvasShowSelection = false;
            }

            DrawLeftPanel(leftRect);
            DrawCenterPanel(centerRect);
            DrawRightPanel(rightRect);
            DrawBottomPanel(bottomRect);

            DrawPanelBorders(leftRect, centerRect, rightRect, bottomRect);
        }

        // ─────────────────────── No PSD loaded ───────────────────────

        private void DrawNoPsdUI()
        {
            _noPsdScrollPos = EditorGUILayout.BeginScrollView(_noPsdScrollPos);
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginVertical(GUILayout.Width(320));
            GUILayout.Label("No PSD loaded", EditorStyles.centeredGreyMiniLabel);
            GUILayout.Space(8);
            if (GUILayout.Button("Open PSD…", GUILayout.Height(36)))
            {
                OpenPsdFile();
            }

            // ── Recent Files ──
            var validRecent = _recentFiles.FindAll(p => !string.IsNullOrEmpty(p));
            if (validRecent.Count > 0)
            {
                GUILayout.Space(10);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Recent Files", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(40)))
                {
                    _recentFiles.Clear();
                    SaveRecentFiles();
                }
                EditorGUILayout.EndHorizontal();

                float rowH = 24f;
                float maxListH = rowH * Mathf.Min(validRecent.Count, 10) + 4f;
                _recentFilesScrollPos = EditorGUILayout.BeginScrollView(
                    _recentFilesScrollPos, GUILayout.Height(maxListH));

                string toRemove = null;
                foreach (string filePath in validRecent)
                {
                    bool exists = File.Exists(filePath);
                    EditorGUILayout.BeginHorizontal();

                    string displayName = System.IO.Path.GetFileName(filePath);
                    GUIStyle linkStyle = new GUIStyle(EditorStyles.label);
                    linkStyle.normal.textColor = exists
                        ? new Color(0.3f, 0.6f, 1f)
                        : new Color(0.55f, 0.55f, 0.55f);

                    GUIContent label = new GUIContent(displayName, filePath);
                    if (GUILayout.Button(label, linkStyle, GUILayout.Height(rowH)))
                    {
                        if (exists)
                            LoadPsdFromPath(filePath);
                        else
                            EditorUtility.DisplayDialog("File not found",
                                $"File not found:\n{filePath}", "OK");
                    }

                    GUIStyle closeStyle = new GUIStyle(EditorStyles.miniButton);
                    if (GUILayout.Button("×", closeStyle, GUILayout.Width(20), GUILayout.Height(rowH)))
                        toRemove = filePath;

                    EditorGUILayout.EndHorizontal();
                }
                if (toRemove != null) RemoveRecentFile(toRemove);

                EditorGUILayout.EndScrollView();
            }

            GUILayout.Space(10);
            _showSettingsFoldout = EditorGUILayout.Foldout(_showSettingsFoldout, "Advanced Settings", true, EditorStyles.foldout);
            if (_showSettingsFoldout)
            {
                EditorGUI.indentLevel++;
                bool newLiveComposite = EditorGUILayout.ToggleLeft("Live composite preview (rebuild when layer visibility changes)", _liveComposite);
                if (newLiveComposite != _liveComposite)
                {
                    _liveComposite = newLiveComposite;
                    EditorPrefs.SetBool(PrefLiveComposite, _liveComposite);
                }
                EditorGUILayout.HelpBox(
                    _liveComposite
                        ? "On: preview follows layer visibility; compositing is slower."
                        : "Off: shows the PSD’s baked composite; fast, but visibility toggles won’t show in preview.",
                    MessageType.Info);

                GUILayout.Space(6);
                bool newDefaultUseTMP = EditorGUILayout.ToggleLeft("Default to TextMeshPro (type layers with no config or no useTextMeshPro field)", _defaultUseTMP);
                if (newDefaultUseTMP != _defaultUseTMP)
                {
                    _defaultUseTMP = newDefaultUseTMP;
                    EditorPrefs.SetBool(PrefDefaultUseTMP, _defaultUseTMP);
                }
                EditorGUILayout.HelpBox(
                    _defaultUseTMP
                        ? "On (default): PSDs without config, or type layers missing useTextMeshPro, default to TextMeshPro."
                        : "Off: those cases default to Legacy Text.",
                    MessageType.Info);

                GUILayout.Space(6);
                bool newDefaultSliceImage = EditorGUILayout.ToggleLeft("Default to Slice / nine-slice (layers with no config or no sliceImage field)", _defaultSliceImage);
                if (newDefaultSliceImage != _defaultSliceImage)
                {
                    _defaultSliceImage = newDefaultSliceImage;
                    EditorPrefs.SetBool(PrefDefaultSliceImage, _defaultSliceImage);
                }
                EditorGUILayout.HelpBox(
                    _defaultSliceImage
                        ? "On (default): PSDs without config, or layers missing sliceImage, default to Slice / nine-slice."
                        : "Off: those cases default to raw export with no nine-slice.",
                    MessageType.Info);

                GUILayout.Space(6);
                bool newUsePsdNodeOrder = EditorGUILayout.ToggleLeft("Use PSD node order in tree", _usePsdNodeOrder);
                if (newUsePsdNodeOrder != _usePsdNodeOrder)
                {
                    _usePsdNodeOrder = newUsePsdNodeOrder;
                    EditorPrefs.SetBool(PrefUsePsdNodeOrder, _usePsdNodeOrder);
                }
                EditorGUILayout.HelpBox(
                    _usePsdNodeOrder
                        ? "On (default): left tree follows PSD layer order."
                        : "Off: left tree follows Unity Prefab hierarchy (inverse of PSD; first PSD layer appears last).",
                    MessageType.Info);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndScrollView();
        }

        private void OpenPsdFile()
        {
            string path = EditorUtility.OpenFilePanel("Choose PSD file", "", "psd");
            if (string.IsNullOrEmpty(path)) return;
            LoadPsdFromPath(path);
        }

        // ─────────────────────── Recent Files ───────────────────────

        private void LoadRecentFiles()
        {
            string raw = EditorPrefs.GetString(PrefRecentFiles, "");
            if (string.IsNullOrEmpty(raw))
            {
                _recentFiles = new List<string>();
                return;
            }
            _recentFiles = new List<string>(raw.Split('|'));
            _recentFiles.RemoveAll(p => string.IsNullOrEmpty(p));
        }

        private void SaveRecentFiles()
        {
            EditorPrefs.SetString(PrefRecentFiles, string.Join("|", _recentFiles));
        }

        private void AddRecentFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            _recentFiles.Remove(path);
            _recentFiles.Insert(0, path);
            if (_recentFiles.Count > RecentFilesMaxCount)
                _recentFiles.RemoveRange(RecentFilesMaxCount, _recentFiles.Count - RecentFilesMaxCount);
            SaveRecentFiles();
        }

        private void RemoveRecentFile(string path)
        {
            _recentFiles.Remove(path);
            SaveRecentFiles();
            Repaint();
        }

        /// <summary>Reload the current PSD path from disk without a file picker.</summary>
        private void ReloadCurrentPsd()
        {
            if (string.IsNullOrEmpty(_psdPath) || !File.Exists(_psdPath))
            {
                EditorUtility.DisplayDialog("Cannot reload", "No valid PSD path or the file does not exist.", "OK");
                return;
            }

            LoadPsdFromPath(_psdPath);
        }

        private void LoadPsdFromPath(string path)
        {
            // Release previous PSD so old + new documents don’t double peak memory.
            ReleasePsdData("BeforeLoad");
            DestroyTexture(ref _psdCompositeTex);
            DestroyTexture(ref _layerPreviewTex);
            // Force GC so old PSD buffers are freed before loading the new file.
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();
            LogManagedSnapshot("LoadPsdFromPath after GC (before PsdImage.Open)");

            try
            {
                _psd = PsdImage.Open(path);
                _psdPath = path;
                _selectedLayer = null;
                _inlineRenamingLayer = null;
                _inlineRenameBuffer = null;
                _inlineRenameFocusFramesLeft = 0;
                _nameLongPressLayer = null;
                _nameLongPressHoldMet = false;
                _nameLongPressLeftButtonHeld = false;
                _layerBecameSelectedAt = 0;
                _propertyScrollPos = Vector2.zero;
                _propertyScrollPosByLayer.Clear();
                _propertyScrollGuiHintByLayer.Clear();
                _nextPropertyScrollGuiHint = 1;
                _foldoutStates.Clear();
                _mergeExportByLayer.Clear();
                _exportPrefabByLayer.Clear();
                _useExternalPrefabByLayer.Clear();
                _externalPrefabPathByLayer.Clear();
                _externalPrefabReusePositionByLayer.Clear();
                _externalPrefabReuseSizeByLayer.Clear();
                _participateLocalDedupByLayer.Clear();
                _participateCommonDedupByLayer.Clear();
                _sliceImageByLayer.Clear();
                _primaryDedupNodeByLayer.Clear();
                _useCustomNineSliceParamsByLayer.Clear();
                _nineSliceParamsByLayer.Clear();
                _useCustomImageByLayer.Clear();
                _customImagePathByLayer.Clear();
                _uiComponentTypeByLayer.Clear();
                _scrollBarDirectionByLayer.Clear();
                _scrollBarHandleChildNameByLayer.Clear();
                _sliderDirectionByLayer.Clear();
                _sliderFillRectChildNameByLayer.Clear();
                _sliderHandleRectChildNameByLayer.Clear();
                _scrollRectScrollBgChildNameByLayer.Clear();
                _scrollRectContentChildNameByLayer.Clear();
                _scrollRectHorizontalByLayer.Clear();
                _scrollRectVerticalByLayer.Clear();
                _scrollRectHorizontalScrollbarChildNameByLayer.Clear();
                _scrollRectVerticalScrollbarChildNameByLayer.Clear();
                _toggleGraphicChildNameByLayer.Clear();
                _inputFieldTextChildNameByLayer.Clear();
                _inputFieldPlaceholderChildNameByLayer.Clear();
                _inputFieldTextViewportChildNameByLayer.Clear();
                _dropdownTemplateChildNameByLayer.Clear();
                _dropdownCaptionTextChildNameByLayer.Clear();
                _dropdownItemTextChildNameByLayer.Clear();
                _useTextMeshProByLayer.Clear();
                _clickCandidates.Clear();
                LoadCommonDirectories();
                _clickCandidateIndex = 0;
                _lastClickPsdPos = new Vector2(-99999, -99999);
                InvalidateLayerPreview();
                InitFoldoutStates(_psd.Root);

                _rightPanelUndoStack.Clear();
                _rightPanelRedoStack.Clear();

                _psdLayerNameSnapshot = TakeLayerNameSnapshot();

                LoadMergeExportConfig();

                // Initialize bottom slice batch toggle from config or _defaultSliceImage
                // (if config has sliceImage, majority of slice-eligible layers wins).
                _sliceToggleState = ComputeInitialSliceToggleState();

                // Initialize bottom TMP batch toggle from config or _defaultUseTMP
                // (if config has useTextMeshPro, majority of type layers wins).
                _tmpToggleState = ComputeInitialTmpToggleState();
                SyncUiComponentTypesWithFontSystem(_tmpToggleState == 0);

                RefreshPsdComposite(_liveComposite);
                AddRecentFile(path);
                Debug.Log($"PSD Editor: loaded {path} ({_psd.Width}x{_psd.Height}, {_psd.LayerCount} layers)");
                LogManagedSnapshot(
                    $"LoadPsdFromPath end (after RefreshPsdComposite) size={_psd.Width}x{_psd.Height} layers={_psd.LayerCount}");
                Repaint();
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Error", $"Failed to load PSD: {ex.Message}", "OK");
            }
        }

        private void InitFoldoutStates(Layer layer)
        {
            if (layer.IsGroup)
                _foldoutStates[layer] = true;
            foreach (var child in layer.Children)
                InitFoldoutStates(child);
        }

        /// <summary>Snapshot all layer names keyed by LayerId before export config overrides are applied.</summary>
        private Dictionary<int, string> TakeLayerNameSnapshot()
        {
            var snapshot = new Dictionary<int, string>();
            var list = new List<Layer>();
            CollectLayers(_psd.Root, list);
            foreach (var layer in list)
            {
                if (layer.LayerId.HasValue)
                    snapshot[layer.LayerId.Value] = layer.Name;
            }
            return snapshot;
        }

        /// <summary>Restore all layer names to the values read from the source PSD when the file was opened.</summary>
        private void RestoreAllLayerNamesFromPsd()
        {
            if (_psd == null || _psdLayerNameSnapshot == null || _psdLayerNameSnapshot.Count == 0)
                return;

            var list = new List<Layer>();
            CollectLayers(_psd.Root, list);

            bool hasNameChange = false;
            foreach (var layer in list)
            {
                if (!layer.LayerId.HasValue)
                    continue;
                if (!_psdLayerNameSnapshot.TryGetValue(layer.LayerId.Value, out string originalName))
                    continue;
                if (layer.Name == originalName)
                    continue;

                hasNameChange = true;
                break;
            }

            if (!hasNameChange)
                return;

            RecordRightPanelUndoBeforeChange();

            if (_inlineRenamingLayer != null)
                CancelInlineRename();

            foreach (var layer in list)
            {
                if (!layer.LayerId.HasValue)
                    continue;
                if (_psdLayerNameSnapshot.TryGetValue(layer.LayerId.Value, out string originalName))
                    layer.Name = originalName;
            }

            if (_selectedLayer != null)
                _editingName = _selectedLayer.Name;

            ScheduleSaveMergeExportConfig();
            Repaint();
        }

        /// <summary>Build or refresh the full PSD composite for the canvas preview.</summary>
        /// <param name="liveComposite">
        /// true  = composite from layer pixels; reflects current Visible flags;<br/>
        /// false = use PSD baked composite (fast; ignores visibility edits).
        /// </param>
        private void RefreshPsdComposite(bool liveComposite = true)
        {
            DestroyTexture(ref _psdCompositeTex);
            if (_psd == null) return;
            try
            {
                _psdCompositeTex = liveComposite
                    ? _psd.CompositeFromLayersOnly()
                    : _psd.Composite();
                if (_psdCompositeTex != null)
                    _psdCompositeTex.hideFlags = HideFlags.HideAndDontSave;
                // Pixels are baked into Texture2D; drop decompression caches immediately.
                _psd.ClearDecompressedCaches();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Failed to build PSD composite: {ex.Message}");
            }
        }

        private void BumpLayerPreviewEpoch()
        {
            _layerPreviewEpoch++;
        }

        private void InvalidateLayerPreview()
        {
            DestroyTexture(ref _layerPreviewTex);
            _layerPreviewBuiltForLayer = null;
            _layerPreviewBuiltForEpoch = -1;
        }

        /// <summary>Build or refresh the right-panel preview texture for the current selection.</summary>
        private void SyncLayerPreview()
        {
            if (_selectedLayer == null)
            {
                InvalidateLayerPreview();
                return;
            }

            bool needRebuild = _layerPreviewTex == null
                || _layerPreviewBuiltForLayer != _selectedLayer
                || _layerPreviewBuiltForEpoch != _layerPreviewEpoch;
            if (!needRebuild)
                return;

            DestroyTexture(ref _layerPreviewTex);
            _layerPreviewBuiltForLayer = null;
            _layerPreviewBuiltForEpoch = -1;
            try
            {
                _layerPreviewTex = _psd.CreateLayerPreviewTexture(_selectedLayer);
                if (_layerPreviewTex != null)
                    _layerPreviewTex.hideFlags = HideFlags.HideAndDontSave;
                _layerPreviewBuiltForEpoch = _layerPreviewEpoch;
                _layerPreviewBuiltForLayer = _selectedLayer;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Layer preview failed: {ex.Message}");
            }
            finally
            {
                // Preview built; release decompression cache (each layer selection decompresses; leaks stack up otherwise).
                _psd?.ClearDecompressedCaches();
            }
        }

        private static string GetExportConfigPath(string psdPath)
        {
            if (string.IsNullOrEmpty(psdPath)) return null;
            string name = Path.GetFileNameWithoutExtension(psdPath);
            string configDir = Path.Combine(Application.dataPath, "PsdToUnityUI", "PSDConfig");
            if (!Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);
            return Path.Combine(configDir, name + "_export_config.json");
        }

        private static bool ConfigFileContainsField(string configPath, string fieldName)
        {
            return !string.IsNullOrEmpty(configPath)
                && File.Exists(configPath)
                && File.ReadAllText(configPath).IndexOf(fieldName, System.StringComparison.Ordinal) >= 0;
        }

        /// <summary>Load shared-directory list from config; empty if missing.</summary>
        private void LoadCommonDirectories()
        {
            _commonDirList.Clear();
            string path = CommonDirectoriesConfigPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                string json = File.ReadAllText(path);
                var data = JsonUtility.FromJson<CommonDirectoriesData>(json);
                if (data?.paths != null)
                {
                    foreach (string p in data.paths)
                        if (!string.IsNullOrWhiteSpace(p))
                            _commonDirList.Add(p.Trim());
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Failed to load common directories config: {ex.Message}");
            }
        }

        /// <summary>
        /// Strip to the part after Assets/ for display (e.g. CommonUI/Picture).
        /// Empty or non-Assets paths are returned unchanged.
        /// </summary>
        private static string GetRelativeToAssets(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string normalized = path.Replace('\\', '/');
            // Match "Assets/" prefix (case-insensitive)
            const string prefix = "Assets/";
            if (normalized.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                return normalized.Substring(prefix.Length);
            return normalized;
        }

        /// <summary>GUIContent with tooltip for control labels.</summary>
        private static GUIContent TT(string label, string tooltip) => new GUIContent(label, tooltip);

        /// <summary>Save directory list to shared-directories config; creates Editor folder if needed.</summary>
        private void SaveCommonDirectories(List<string> paths)
        {
            string path = CommonDirectoriesConfigPath;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var data = new CommonDirectoriesData { paths = paths != null ? paths.ToArray() : new string[0] };
                File.WriteAllText(path, JsonUtility.ToJson(data, true));
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Failed to save common directories config: {ex.Message}");
            }
        }

        /// <summary>Call before export so external Prefab / reuse position / reuse size toggles are flushed to config.</summary>
        public void FlushExportConfig()
        {
            if (_psd != null && !string.IsNullOrEmpty(_psdPath))
                SaveMergeExportConfig();
        }

        /// <summary>Defer saving config until after layout to avoid EndLayoutGroup errors from Toggle callbacks.</summary>
        private void ScheduleSaveMergeExportConfig()
        {
            EditorApplication.delayCall += () =>
            {
                if (_psd != null && !string.IsNullOrEmpty(_psdPath))
                    SaveMergeExportConfig();
            };
        }

        private static void CollectLayers(Layer root, List<Layer> outList)
        {
            outList.Add(root);
            foreach (var child in root.Children)
                CollectLayers(child, outList);
        }

        private Layer FindLayerById(int layerId)
        {
            if (_psd == null) return null;

            var list = new List<Layer>();
            CollectLayers(_psd.Root, list);
            foreach (var layer in list)
            {
                if (layer.LayerId.HasValue && layer.LayerId.Value == layerId)
                    return layer;
            }
            return null;
        }

        private void HandleRightPanelUndoRedoHotkeys()
        {
            Event ev = Event.current;
            if (ev.type != EventType.KeyDown) return;
            // While editing a text field, leave Ctrl+Z to the text control
            if (EditorGUIUtility.editingTextField) return;
            bool mod = ev.control || ev.command;
            if (!mod) return;
            if (ev.keyCode == KeyCode.Z && !ev.shift)
            {
                if (TryUndoRightPanelChange()) ev.Use();
                return;
            }
            // Ctrl+Shift+Z / Cmd+Shift+Z, and Windows Ctrl+Y redo
            if ((ev.keyCode == KeyCode.Z && ev.shift) || (ev.keyCode == KeyCode.Y && !ev.shift))
            {
                if (TryRedoRightPanelChange()) ev.Use();
            }
        }

        private RightPanelUndoStep CaptureRightPanelUndoStep()
        {
            return new RightPanelUndoStep
            {
                exportData = BuildMergeExportConfigData(),
                commonDirList = _commonDirList != null ? _commonDirList.ToArray() : new string[0],
                hasSelectedLayer = _selectedLayer != null && _selectedLayer.LayerId.HasValue,
                selectedLayerId = _selectedLayer != null && _selectedLayer.LayerId.HasValue ? _selectedLayer.LayerId.Value : 0
            };
        }

        private void RecordRightPanelUndoBeforeChange()
        {
            if (_rightPanelUndoApplying || _psd == null) return;
            var step = CaptureRightPanelUndoStep();
            if (step.exportData == null || step.exportData.layers == null || step.exportData.layers.Length == 0) return;
            _rightPanelUndoStack.Add(step);
            while (_rightPanelUndoStack.Count > RightPanelUndoMaxSteps)
                _rightPanelUndoStack.RemoveAt(0);
            _rightPanelRedoStack.Clear();
        }

        private bool TryUndoRightPanelChange()
        {
            if (_psd == null || _rightPanelUndoStack.Count == 0) return false;
            var current = CaptureRightPanelUndoStep();
            if (current.exportData == null || current.exportData.layers == null || current.exportData.layers.Length == 0)
                return false;
            var prev = _rightPanelUndoStack[_rightPanelUndoStack.Count - 1];
            _rightPanelUndoStack.RemoveAt(_rightPanelUndoStack.Count - 1);
            _rightPanelRedoStack.Add(current);
            ApplyRightPanelUndoStep(prev);
            return true;
        }

        private bool TryRedoRightPanelChange()
        {
            if (_psd == null || _rightPanelRedoStack.Count == 0) return false;
            var current = CaptureRightPanelUndoStep();
            if (current.exportData == null || current.exportData.layers == null || current.exportData.layers.Length == 0)
                return false;
            var next = _rightPanelRedoStack[_rightPanelRedoStack.Count - 1];
            _rightPanelRedoStack.RemoveAt(_rightPanelRedoStack.Count - 1);
            _rightPanelUndoStack.Add(current);
            ApplyRightPanelUndoStep(next);
            return true;
        }

        private void ApplyRightPanelUndoStep(RightPanelUndoStep step)
        {
            if (step?.exportData == null || _psd == null) return;
            _rightPanelUndoApplying = true;
            try
            {
                ApplyMergeExportConfigData(step.exportData);
                _commonDirList.Clear();
                if (step.commonDirList != null)
                {
                    foreach (string p in step.commonDirList)
                    {
                        if (!string.IsNullOrWhiteSpace(p))
                            _commonDirList.Add(p.Trim());
                    }
                }
                SaveCommonDirectories(_commonDirList);

                _inlineRenamingLayer = null;
                _inlineRenameBuffer = null;

                Layer restoredSelection = step.hasSelectedLayer ? FindLayerById(step.selectedLayerId) : null;
                if (restoredSelection != null)
                {
                    ExpandParentsOf(restoredSelection);
                    SelectLayer(restoredSelection);
                    ScrollTreeToLayer(restoredSelection);
                }
                else if (!step.hasSelectedLayer)
                {
                    _selectedLayer = null;
                    _editingName = null;
                    _editingVisible = false;
                    _propertyScrollPos = Vector2.zero;
                }

                if (_selectedLayer != null)
                {
                    _editingName = _selectedLayer.Name;
                    _editingVisible = _selectedLayer.Visible;
                }

                BumpLayerPreviewEpoch();
                InvalidateLayerPreview();
                RefreshPsdComposite(_liveComposite);
                SaveMergeExportConfig();
            }
            finally
            {
                _rightPanelUndoApplying = false;
            }
            Repaint();
        }

        private void ClearExportOptionDictionaries()
        {
            _mergeExportByLayer.Clear();
            _exportPrefabByLayer.Clear();
            _useExternalPrefabByLayer.Clear();
            _externalPrefabPathByLayer.Clear();
            _externalPrefabReusePositionByLayer.Clear();
            _externalPrefabReuseSizeByLayer.Clear();
            _participateLocalDedupByLayer.Clear();
            _participateCommonDedupByLayer.Clear();
            _sliceImageByLayer.Clear();
            _primaryDedupNodeByLayer.Clear();
            _useCustomNineSliceParamsByLayer.Clear();
            _nineSliceParamsByLayer.Clear();
            _useCustomImageByLayer.Clear();
            _customImagePathByLayer.Clear();
            _uiComponentTypeByLayer.Clear();
            _scrollBarDirectionByLayer.Clear();
            _scrollBarHandleChildNameByLayer.Clear();
            _sliderDirectionByLayer.Clear();
            _sliderFillRectChildNameByLayer.Clear();
            _sliderHandleRectChildNameByLayer.Clear();
            _scrollRectScrollBgChildNameByLayer.Clear();
            _scrollRectContentChildNameByLayer.Clear();
            _scrollRectHorizontalByLayer.Clear();
            _scrollRectVerticalByLayer.Clear();
            _scrollRectHorizontalScrollbarChildNameByLayer.Clear();
            _scrollRectVerticalScrollbarChildNameByLayer.Clear();
            _toggleGraphicChildNameByLayer.Clear();
            _inputFieldTextChildNameByLayer.Clear();
            _inputFieldPlaceholderChildNameByLayer.Clear();
            _inputFieldTextViewportChildNameByLayer.Clear();
            _dropdownTemplateChildNameByLayer.Clear();
            _dropdownCaptionTextChildNameByLayer.Clear();
            _dropdownItemTextChildNameByLayer.Clear();
            _useTextMeshProByLayer.Clear();
            _exportedByLayer.Clear();
        }

        /// <summary>
        /// Restore export config and layer names/visibility from a full snapshot (undo/redo); clears then repopulates dicts.
        /// </summary>
        private void ApplyMergeExportConfigData(MergeExportConfigData data)
        {
            if (_psd == null || data?.layers == null || data.layers.Length == 0) return;

            var list = new List<Layer>();
            CollectLayers(_psd.Root, list);

            var entryById = new Dictionary<int, LayerConfigEntry>(data.layers.Length);
            foreach (var entry in data.layers)
                entryById[entry.id] = entry;

            ClearExportOptionDictionaries();

            foreach (var layer in list)
            {
                if (!layer.LayerId.HasValue) continue;
                if (!entryById.TryGetValue(layer.LayerId.Value, out LayerConfigEntry e)) continue;

                if (!string.IsNullOrEmpty(e.name)) layer.Name = e.name;
                layer.Visible = e.visible;

                _exportedByLayer[layer] = e.exported;

                if (e.merge) _mergeExportByLayer[layer] = true;
                else _mergeExportByLayer.Remove(layer);

                if (e.exportPrefab) _exportPrefabByLayer[layer] = true;
                else _exportPrefabByLayer.Remove(layer);

                if (e.useExternalPrefab)
                {
                    _useExternalPrefabByLayer[layer] = true;
                    if (!string.IsNullOrEmpty(e.externalPrefabPath))
                        _externalPrefabPathByLayer[layer] = e.externalPrefabPath;
                    else
                        _externalPrefabPathByLayer.Remove(layer);
                    _externalPrefabReusePositionByLayer[layer] = e.reusePosition;
                    _externalPrefabReuseSizeByLayer[layer] = e.reuseSize;
                }
                else
                {
                    _useExternalPrefabByLayer.Remove(layer);
                    _externalPrefabPathByLayer.Remove(layer);
                    _externalPrefabReusePositionByLayer.Remove(layer);
                    _externalPrefabReuseSizeByLayer.Remove(layer);
                }

                _participateLocalDedupByLayer[layer] = e.participateLocalDedup;
                _participateCommonDedupByLayer[layer] = e.participateCommonDedup;

                _sliceImageByLayer[layer] = e.sliceImage;

                if (e.useExternalPrefab || !e.primaryDedupNode)
                    _primaryDedupNodeByLayer.Remove(layer);
                else
                    _primaryDedupNodeByLayer[layer] = true;

                if (e.useCustomNineSliceParams && e.sliceImage)
                {
                    _useCustomNineSliceParamsByLayer[layer] = true;
                    _nineSliceParamsByLayer[layer] = new NineSliceLayerParams
                    {
                        borderInset      = e.nineSliceBorderInset,
                        pixelThreshold  = e.nineSlicePixelThreshold,
                        minCenterCols   = e.nineSliceMinCenterCols,
                        minCenterRows   = e.nineSliceMinCenterRows,
                        minSameZone      = e.nineSliceMinSameZone
                    };
                }
                else
                {
                    _useCustomNineSliceParamsByLayer.Remove(layer);
                    _nineSliceParamsByLayer.Remove(layer);
                }

                if (e.useCustomImage)
                {
                    _useCustomImageByLayer[layer] = true;
                    if (!string.IsNullOrEmpty(e.customImagePath))
                        _customImagePathByLayer[layer] = e.customImagePath;
                    else
                        _customImagePathByLayer.Remove(layer);
                }
                else
                {
                    _useCustomImageByLayer.Remove(layer);
                    _customImagePathByLayer.Remove(layer);
                }

                string uiType = string.IsNullOrEmpty(e.uiComponentType) ? "None" : e.uiComponentType;
                if (uiType != "None" && System.Array.IndexOf(UiComponentHandlerRegistry.AllComponentTypes, uiType) >= 0)
                    _uiComponentTypeByLayer[layer] = uiType;
                else
                {
                    uiType = "None";
                    _uiComponentTypeByLayer.Remove(layer);
                }

                string sbd = string.IsNullOrEmpty(e.scrollBarDirection) ? "left_to_right" : e.scrollBarDirection;
                if (System.Array.IndexOf(UiComponentHandlerRegistry.ScrollBarDirectionOptions, sbd) >= 0)
                    _scrollBarDirectionByLayer[layer] = sbd;
                else
                    _scrollBarDirectionByLayer[layer] = "left_to_right";

                if (!string.IsNullOrEmpty(e.scrollBarHandleChildName))
                    _scrollBarHandleChildNameByLayer[layer] = e.scrollBarHandleChildName;
                else
                    _scrollBarHandleChildNameByLayer.Remove(layer);

                if (!string.IsNullOrEmpty(e.toggleGraphicChildName))
                    _toggleGraphicChildNameByLayer[layer] = e.toggleGraphicChildName;
                else
                    _toggleGraphicChildNameByLayer.Remove(layer);

                if (uiType == "InputField(Legacy)" || uiType == "InputField(TMP)")
                {
                    if (!string.IsNullOrEmpty(e.inputFieldTextChildName))
                        _inputFieldTextChildNameByLayer[layer] = e.inputFieldTextChildName.Trim();
                    else
                        _inputFieldTextChildNameByLayer.Remove(layer);
                    if (!string.IsNullOrEmpty(e.inputFieldPlaceholderChildName))
                        _inputFieldPlaceholderChildNameByLayer[layer] = e.inputFieldPlaceholderChildName.Trim();
                    else
                        _inputFieldPlaceholderChildNameByLayer.Remove(layer);
                    if (uiType == "InputField(TMP)" && !string.IsNullOrEmpty(e.inputFieldTextViewportChildName))
                        _inputFieldTextViewportChildNameByLayer[layer] = e.inputFieldTextViewportChildName.Trim();
                    else
                        _inputFieldTextViewportChildNameByLayer.Remove(layer);
                }
                else
                {
                    _inputFieldTextChildNameByLayer.Remove(layer);
                    _inputFieldPlaceholderChildNameByLayer.Remove(layer);
                    _inputFieldTextViewportChildNameByLayer.Remove(layer);
                }

                if (uiType == "Dropdown(Legacy)" || uiType == "Dropdown(TMP)")
                {
                    if (!string.IsNullOrEmpty(e.dropdownTemplateChildName))
                        _dropdownTemplateChildNameByLayer[layer] = e.dropdownTemplateChildName.Trim();
                    else
                        _dropdownTemplateChildNameByLayer.Remove(layer);
                    if (!string.IsNullOrEmpty(e.dropdownCaptionTextChildName))
                        _dropdownCaptionTextChildNameByLayer[layer] = e.dropdownCaptionTextChildName.Trim();
                    else
                        _dropdownCaptionTextChildNameByLayer.Remove(layer);
                    if (!string.IsNullOrEmpty(e.dropdownItemTextChildName))
                        _dropdownItemTextChildNameByLayer[layer] = e.dropdownItemTextChildName.Trim();
                    else
                        _dropdownItemTextChildNameByLayer.Remove(layer);
                }
                else
                {
                    _dropdownTemplateChildNameByLayer.Remove(layer);
                    _dropdownCaptionTextChildNameByLayer.Remove(layer);
                    _dropdownItemTextChildNameByLayer.Remove(layer);
                }

                string sld = string.IsNullOrEmpty(e.sliderDirection) ? "left_to_right" : e.sliderDirection;
                if (System.Array.IndexOf(UiComponentHandlerRegistry.ScrollBarDirectionOptions, sld) >= 0)
                    _sliderDirectionByLayer[layer] = sld;
                else
                    _sliderDirectionByLayer[layer] = "left_to_right";

                if (!string.IsNullOrEmpty(e.sliderFillRectChildName))
                    _sliderFillRectChildNameByLayer[layer] = e.sliderFillRectChildName;
                else
                    _sliderFillRectChildNameByLayer.Remove(layer);

                if (!string.IsNullOrEmpty(e.sliderHandleRectChildName))
                    _sliderHandleRectChildNameByLayer[layer] = e.sliderHandleRectChildName;
                else
                    _sliderHandleRectChildNameByLayer.Remove(layer);

                if (layer.Kind == LayerKind.Type)
                    _useTextMeshProByLayer[layer] = e.useTextMeshPro;
                else
                    _useTextMeshProByLayer.Remove(layer);

                if (uiType == "ScrollRect")
                {
                    if (!string.IsNullOrEmpty(e.scrollRectViewportChildName))
                        _scrollRectScrollBgChildNameByLayer[layer] = e.scrollRectViewportChildName.Trim();
                    else
                        _scrollRectScrollBgChildNameByLayer.Remove(layer);

                    if (!string.IsNullOrEmpty(e.scrollRectContentChildName))
                        _scrollRectContentChildNameByLayer[layer] = e.scrollRectContentChildName.Trim();
                    else
                        _scrollRectContentChildNameByLayer.Remove(layer);

                    _scrollRectHorizontalByLayer[layer] = e.scrollRectHorizontal;
                    _scrollRectVerticalByLayer[layer] = e.scrollRectVertical;

                    if (!string.IsNullOrEmpty(e.scrollRectHorizontalScrollbarChildName))
                        _scrollRectHorizontalScrollbarChildNameByLayer[layer] = e.scrollRectHorizontalScrollbarChildName.Trim();
                    else
                        _scrollRectHorizontalScrollbarChildNameByLayer.Remove(layer);

                    if (!string.IsNullOrEmpty(e.scrollRectVerticalScrollbarChildName))
                        _scrollRectVerticalScrollbarChildNameByLayer[layer] = e.scrollRectVerticalScrollbarChildName.Trim();
                    else
                        _scrollRectVerticalScrollbarChildNameByLayer.Remove(layer);
                }
                else
                {
                    _scrollRectScrollBgChildNameByLayer.Remove(layer);
                    _scrollRectContentChildNameByLayer.Remove(layer);
                    _scrollRectHorizontalByLayer.Remove(layer);
                    _scrollRectVerticalByLayer.Remove(layer);
                    _scrollRectHorizontalScrollbarChildNameByLayer.Remove(layer);
                    _scrollRectVerticalScrollbarChildNameByLayer.Remove(layer);
                }
            }
        }

        /// <summary>Apply TMP vs Legacy to all type layers from the bottom batch toggle.</summary>
        private void ApplyTextMeshProToAllTextLayers(bool useTMP)
        {
            if (_psd == null) return;
            RecordRightPanelUndoBeforeChange();
            var list = new List<Layer>();
            CollectLayers(_psd.Root, list);
            foreach (var layer in list)
            {
                if (layer.Kind == LayerKind.Type)
                    _useTextMeshProByLayer[layer] = useTMP;
            }
            SyncUiComponentTypesWithFontSystem(useTMP);
            ScheduleSaveMergeExportConfig();
        }

        /// <summary>Apply Slice / nine-slice vs raw export to all slice-eligible layers from the bottom batch toggle.</summary>
        private void ApplySliceImageToAllEligibleLayers(bool useSlice)
        {
            if (_psd == null) return;
            RecordRightPanelUndoBeforeChange();
            var list = new List<Layer>();
            CollectLayers(_psd.Root, list);
            foreach (var layer in list)
            {
                if (!ShouldShowSliceCommonDedupOptions(layer))
                    continue;

                _sliceImageByLayer[layer] = useSlice;
                if (!useSlice)
                {
                    _useCustomNineSliceParamsByLayer.Remove(layer);
                    _nineSliceParamsByLayer.Remove(layer);
                }
            }
            ScheduleSaveMergeExportConfig();
        }

        private void SyncUiComponentTypesWithFontSystem(bool useTMP)
        {
            if (_psd == null) return;
            var list = new List<Layer>();
            CollectLayers(_psd.Root, list);
            bool changed = false;
            foreach (var layer in list)
            {
                if (!_uiComponentTypeByLayer.TryGetValue(layer, out string currentType)) continue;

                string newType = currentType;
                if (useTMP)
                {
                    if (currentType == "InputField(Legacy)") newType = "InputField(TMP)";
                    else if (currentType == "Dropdown(Legacy)") newType = "Dropdown(TMP)";
                }
                else
                {
                    if (currentType == "InputField(TMP)") newType = "InputField(Legacy)";
                    else if (currentType == "Dropdown(TMP)") newType = "Dropdown(Legacy)";
                }

                if (newType != currentType)
                {
                    _uiComponentTypeByLayer[layer] = newType;
                    changed = true;
                }
            }
            if (changed)
            {
                // Invalidate selected layer property panel cache as type changed
                if (_selectedLayer != null)
                    _propertyScrollPosByLayer.Remove(_selectedLayer);
                InvalidateLayerPreview();
            }
        }

        /// <summary>
        /// Infer initial state for the bottom TMP batch button after loading config.<br/>
        /// If the file has useTextMeshPro, majority of type layers wins (tie → TMP).<br/>
        /// Otherwise use <c>_defaultUseTMP</c>.<br/>
        /// Returns 0 = TMP mode (button switches to Legacy), 1 = Legacy (button switches to TMP).
        /// </summary>
        private int ComputeInitialTmpToggleState()
        {
            if (_psd == null) return _defaultUseTMP ? 0 : 1;

            string configPath = GetExportConfigPath(_psdPath);
            bool configHasTmpField = ConfigFileContainsField(configPath, "useTextMeshPro");

            if (!configHasTmpField)
                return _defaultUseTMP ? 0 : 1;

            // Config has field: count TMP vs Legacy among type layers
            int tmpCount = 0, legacyCount = 0;
            foreach (var kv in _useTextMeshProByLayer)
            {
                if (kv.Value) tmpCount++; else legacyCount++;
            }
            // TMP majority or tie → state 0; else state 1
            return tmpCount >= legacyCount ? 0 : 1;
        }

        /// <summary>
        /// Infer initial state for the bottom slice batch button after loading config.<br/>
        /// If the file has sliceImage, majority of slice-eligible layers wins (tie → Slice).<br/>
        /// Otherwise use <c>_defaultSliceImage</c>.<br/>
        /// Returns 0 = Slice mode (button switches to raw export), 1 = Raw mode (button switches to Slice / nine-slice).
        /// </summary>
        private int ComputeInitialSliceToggleState()
        {
            if (_psd == null) return _defaultSliceImage ? 0 : 1;

            string configPath = GetExportConfigPath(_psdPath);
            bool configHasSliceField = ConfigFileContainsField(configPath, "sliceImage");

            if (!configHasSliceField)
                return _defaultSliceImage ? 0 : 1;

            var list = new List<Layer>();
            CollectLayers(_psd.Root, list);

            int sliceCount = 0;
            int rawCount = 0;
            foreach (var layer in list)
            {
                if (!ShouldShowSliceCommonDedupOptions(layer))
                    continue;

                if (GetEffectiveSliceImage(layer)) sliceCount++; else rawCount++;
            }

            if (sliceCount == 0 && rawCount == 0)
                return _defaultSliceImage ? 0 : 1;

            return sliceCount >= rawCount ? 0 : 1;
        }

        private void LoadMergeExportConfig()
        {
            string path = GetExportConfigPath(_psdPath);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            var list = new List<Layer>();
            CollectLayers(_psd.Root, list);

            try
            {
                string json = File.ReadAllText(path);
                string normalizedJson = json.Replace("\"scrollRectScrollBgChildName\"", "\"scrollRectViewportChildName\"");
                var data = JsonUtility.FromJson<MergeExportConfigData>(normalizedJson);
                if (data?.layers == null || data.layers.Length == 0) return;

                bool hasDedupOptions = json.IndexOf("participateLocalDedup", System.StringComparison.Ordinal) >= 0;
                bool hasScrollRectOptions = json.IndexOf("scrollRectHorizontal", System.StringComparison.Ordinal) >= 0;
                bool hasSliceImage = json.IndexOf("sliceImage", System.StringComparison.Ordinal) >= 0;
                bool hasUseTextMeshPro = json.IndexOf("useTextMeshPro", System.StringComparison.Ordinal) >= 0;
                bool hasPrimaryDedupNode = json.IndexOf("primaryDedupNode", System.StringComparison.Ordinal) >= 0;

                // id → entry lookup
                var entryById = new Dictionary<int, LayerConfigEntry>(data.layers.Length);
                foreach (var entry in data.layers)
                    entryById[entry.id] = entry;

                foreach (var layer in list)
                {
                    if (!layer.LayerId.HasValue) continue;
                    if (!entryById.TryGetValue(layer.LayerId.Value, out var e)) continue;
                    // Entry exists in config and layer still in PSD → apply all fields.
                    // Entry for deleted layer → this loop skips; next save prunes.
                    // New PSD layer with no entry → keep PSD defaults; next save adds entry.

                    if (!string.IsNullOrEmpty(e.name))   layer.Name    = e.name;
                    layer.Visible = e.visible;

                    // exported: default true when old JSON omitted it (JsonUtility defaults bool to false; detect via substring)
                    bool hasExportedField = json.IndexOf("\"exported\"", System.StringComparison.Ordinal) >= 0;
                    _exportedByLayer[layer] = hasExportedField ? e.exported : true;

                    if (e.merge)        _mergeExportByLayer[layer]     = true;
                    if (e.exportPrefab) _exportPrefabByLayer[layer]    = true;

                    if (e.useExternalPrefab)
                    {
                        _useExternalPrefabByLayer[layer] = true;
                        if (!string.IsNullOrEmpty(e.externalPrefabPath))
                            _externalPrefabPathByLayer[layer] = e.externalPrefabPath;
                        _externalPrefabReusePositionByLayer[layer] = e.reusePosition;
                        _externalPrefabReuseSizeByLayer[layer]     = e.reuseSize;
                    }
                    bool localDedup = hasDedupOptions ? e.participateLocalDedup : true;
                    bool commonDedup = hasDedupOptions ? e.participateCommonDedup : true;
                    _participateLocalDedupByLayer[layer] = localDedup;
                    _participateCommonDedupByLayer[layer] = commonDedup;
                    bool sliceImg = hasSliceImage ? e.sliceImage : _defaultSliceImage;
                    _sliceImageByLayer[layer] = sliceImg;
                    bool primaryDn = hasPrimaryDedupNode && e.primaryDedupNode && !e.useExternalPrefab;
                    if (primaryDn)
                        _primaryDedupNodeByLayer[layer] = true;
                    else
                        _primaryDedupNodeByLayer.Remove(layer);
                    bool hasCustomNs = json.IndexOf("useCustomNineSliceParams", System.StringComparison.Ordinal) >= 0;
                    if (hasCustomNs && e.useCustomNineSliceParams && sliceImg)
                    {
                        _useCustomNineSliceParamsByLayer[layer] = true;
                        _nineSliceParamsByLayer[layer] = new NineSliceLayerParams
                        {
                            borderInset = e.nineSliceBorderInset,
                            pixelThreshold = e.nineSlicePixelThreshold,
                            minCenterCols = e.nineSliceMinCenterCols,
                            minCenterRows = e.nineSliceMinCenterRows,
                            minSameZone = e.nineSliceMinSameZone
                        };
                    }
                    else
                    {
                        _useCustomNineSliceParamsByLayer.Remove(layer);
                        _nineSliceParamsByLayer.Remove(layer);
                    }
                    bool useCustImg = json.IndexOf("useCustomImage", System.StringComparison.Ordinal) >= 0 ? e.useCustomImage : false;
                    _useCustomImageByLayer[layer] = useCustImg;
                    if (useCustImg && !string.IsNullOrEmpty(e.customImagePath))
                        _customImagePathByLayer[layer] = e.customImagePath;

                    string uiType = string.IsNullOrEmpty(e.uiComponentType) ? "None" : e.uiComponentType;
                    if (uiType != "None" && System.Array.IndexOf(UiComponentHandlerRegistry.AllComponentTypes, uiType) >= 0)
                        _uiComponentTypeByLayer[layer] = uiType;
                    else
                        _uiComponentTypeByLayer.Remove(layer);

                    string sbd = string.IsNullOrEmpty(e.scrollBarDirection) ? "left_to_right" : e.scrollBarDirection;
                    if (System.Array.IndexOf(UiComponentHandlerRegistry.ScrollBarDirectionOptions, sbd) >= 0)
                        _scrollBarDirectionByLayer[layer] = sbd;
                    else
                        _scrollBarDirectionByLayer[layer] = "left_to_right";

                    if (!string.IsNullOrEmpty(e.scrollBarHandleChildName))
                        _scrollBarHandleChildNameByLayer[layer] = e.scrollBarHandleChildName;
                    else
                        _scrollBarHandleChildNameByLayer.Remove(layer);

                    if (!string.IsNullOrEmpty(e.toggleGraphicChildName))
                        _toggleGraphicChildNameByLayer[layer] = e.toggleGraphicChildName;
                    else
                        _toggleGraphicChildNameByLayer.Remove(layer);

                    if (uiType == "InputField(Legacy)" || uiType == "InputField(TMP)")
                    {
                        if (!string.IsNullOrEmpty(e.inputFieldTextChildName))
                            _inputFieldTextChildNameByLayer[layer] = e.inputFieldTextChildName.Trim();
                        else
                            _inputFieldTextChildNameByLayer.Remove(layer);
                        if (!string.IsNullOrEmpty(e.inputFieldPlaceholderChildName))
                            _inputFieldPlaceholderChildNameByLayer[layer] = e.inputFieldPlaceholderChildName.Trim();
                        else
                            _inputFieldPlaceholderChildNameByLayer.Remove(layer);
                        if (uiType == "InputField(TMP)" && !string.IsNullOrEmpty(e.inputFieldTextViewportChildName))
                            _inputFieldTextViewportChildNameByLayer[layer] = e.inputFieldTextViewportChildName.Trim();
                        else
                            _inputFieldTextViewportChildNameByLayer.Remove(layer);
                    }

                    if (uiType == "Dropdown(Legacy)" || uiType == "Dropdown(TMP)")
                    {
                        if (!string.IsNullOrEmpty(e.dropdownTemplateChildName))
                            _dropdownTemplateChildNameByLayer[layer] = e.dropdownTemplateChildName.Trim();
                        else
                            _dropdownTemplateChildNameByLayer.Remove(layer);
                        if (!string.IsNullOrEmpty(e.dropdownCaptionTextChildName))
                            _dropdownCaptionTextChildNameByLayer[layer] = e.dropdownCaptionTextChildName.Trim();
                        else
                            _dropdownCaptionTextChildNameByLayer.Remove(layer);
                        if (!string.IsNullOrEmpty(e.dropdownItemTextChildName))
                            _dropdownItemTextChildNameByLayer[layer] = e.dropdownItemTextChildName.Trim();
                        else
                            _dropdownItemTextChildNameByLayer.Remove(layer);
                    }

                    string sld = string.IsNullOrEmpty(e.sliderDirection) ? "left_to_right" : e.sliderDirection;
                    if (System.Array.IndexOf(UiComponentHandlerRegistry.ScrollBarDirectionOptions, sld) >= 0)
                        _sliderDirectionByLayer[layer] = sld;
                    else
                        _sliderDirectionByLayer[layer] = "left_to_right";

                    if (!string.IsNullOrEmpty(e.sliderFillRectChildName))
                        _sliderFillRectChildNameByLayer[layer] = e.sliderFillRectChildName;
                    else
                        _sliderFillRectChildNameByLayer.Remove(layer);

                    if (!string.IsNullOrEmpty(e.sliderHandleRectChildName))
                        _sliderHandleRectChildNameByLayer[layer] = e.sliderHandleRectChildName;
                    else
                        _sliderHandleRectChildNameByLayer.Remove(layer);

                    if (layer.Kind == LayerKind.Type)
                    {
                        bool useTmp = hasUseTextMeshPro ? e.useTextMeshPro : _defaultUseTMP;
                        _useTextMeshProByLayer[layer] = useTmp;
                    }

                    if (uiType == "ScrollRect")
                    {
                        if (!string.IsNullOrEmpty(e.scrollRectViewportChildName))
                            _scrollRectScrollBgChildNameByLayer[layer] = e.scrollRectViewportChildName.Trim();
                        else
                            _scrollRectScrollBgChildNameByLayer.Remove(layer);

                        if (!string.IsNullOrEmpty(e.scrollRectContentChildName))
                            _scrollRectContentChildNameByLayer[layer] = e.scrollRectContentChildName.Trim();
                        else
                            _scrollRectContentChildNameByLayer.Remove(layer);

                        _scrollRectHorizontalByLayer[layer] = hasScrollRectOptions ? e.scrollRectHorizontal : true;
                        _scrollRectVerticalByLayer[layer] = hasScrollRectOptions ? e.scrollRectVertical : true;

                        if (!string.IsNullOrEmpty(e.scrollRectHorizontalScrollbarChildName))
                            _scrollRectHorizontalScrollbarChildNameByLayer[layer] = e.scrollRectHorizontalScrollbarChildName.Trim();
                        else
                            _scrollRectHorizontalScrollbarChildNameByLayer.Remove(layer);

                        if (!string.IsNullOrEmpty(e.scrollRectVerticalScrollbarChildName))
                            _scrollRectVerticalScrollbarChildNameByLayer[layer] = e.scrollRectVerticalScrollbarChildName.Trim();
                        else
                            _scrollRectVerticalScrollbarChildNameByLayer.Remove(layer);
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Failed to load export config: {ex.Message}");
            }

            // Persist immediately: add new PSD layers, strip stale entries for removed layers
            ScheduleSaveMergeExportConfig();
        }

        /// <summary>Build in-memory data matching <c>_export_config.json</c> without writing disk.</summary>
        private MergeExportConfigData BuildMergeExportConfigData()
        {
            if (_psd == null) return null;
            try
            {
                var list = new List<Layer>();
                CollectLayers(_psd.Root, list);

                var entries = new List<LayerConfigEntry>();
                foreach (var layer in list)
                {
                    if (!layer.LayerId.HasValue) continue;

                    bool useExt = _useExternalPrefabByLayer.TryGetValue(layer, out bool ue) && ue;
                    string extPath = (useExt && _externalPrefabPathByLayer.TryGetValue(layer, out string ep))
                        ? ep : "";
                    bool participateLocal = !_participateLocalDedupByLayer.TryGetValue(layer, out bool pl) || pl;
                    bool participateCommon = !_participateCommonDedupByLayer.TryGetValue(layer, out bool pc) || pc;
                    bool sliceImage = GetEffectiveSliceImage(layer);
                    bool primaryDedupNode = _primaryDedupNodeByLayer.TryGetValue(layer, out bool pdn) && pdn;
                    bool useCustomImage = _useCustomImageByLayer.TryGetValue(layer, out bool uc) && uc;
                    string customImagePath = useCustomImage && _customImagePathByLayer.TryGetValue(layer, out string cip) ? cip : "";
                    string uiComponentType = _uiComponentTypeByLayer.TryGetValue(layer, out string uct) && !string.IsNullOrEmpty(uct)
                        ? uct : "None";
                    if (System.Array.IndexOf(UiComponentHandlerRegistry.AllComponentTypes, uiComponentType) < 0)
                        uiComponentType = "None";

                    string scrollBarDir = _scrollBarDirectionByLayer.TryGetValue(layer, out string sbd) && !string.IsNullOrEmpty(sbd)
                        ? sbd : "left_to_right";
                    if (System.Array.IndexOf(UiComponentHandlerRegistry.ScrollBarDirectionOptions, scrollBarDir) < 0)
                        scrollBarDir = "left_to_right";
                    string scrollBarHandleName = _scrollBarHandleChildNameByLayer.TryGetValue(layer, out string shn) ? shn ?? "" : "";
                    string sliderDir = _sliderDirectionByLayer.TryGetValue(layer, out string sldr) && !string.IsNullOrEmpty(sldr)
                        ? sldr : "left_to_right";
                    if (System.Array.IndexOf(UiComponentHandlerRegistry.ScrollBarDirectionOptions, sliderDir) < 0)
                        sliderDir = "left_to_right";
                    string sliderFillName = _sliderFillRectChildNameByLayer.TryGetValue(layer, out string sfill) ? sfill ?? "" : "";
                    string sliderHandleName = _sliderHandleRectChildNameByLayer.TryGetValue(layer, out string shsl) ? shsl ?? "" : "";
                    string scrollRectBg = _scrollRectScrollBgChildNameByLayer.TryGetValue(layer, out string srbg) ? srbg ?? "" : "";
                    string scrollRectContent = _scrollRectContentChildNameByLayer.TryGetValue(layer, out string srct) ? srct ?? "" : "";
                    bool scrollRectH = _scrollRectHorizontalByLayer.TryGetValue(layer, out bool srh) ? srh : true;
                    bool scrollRectV = _scrollRectVerticalByLayer.TryGetValue(layer, out bool srv) ? srv : true;
                    string scrollRectHsb = _scrollRectHorizontalScrollbarChildNameByLayer.TryGetValue(layer, out string srhsb) ? srhsb ?? "" : "";
                    string scrollRectVsb = _scrollRectVerticalScrollbarChildNameByLayer.TryGetValue(layer, out string srvsb) ? srvsb ?? "" : "";
                    string toggleGraphicName = _toggleGraphicChildNameByLayer.TryGetValue(layer, out string tgn) ? tgn ?? "" : "";
                    string inputFieldTextName = _inputFieldTextChildNameByLayer.TryGetValue(layer, out string ift) ? ift ?? "" : "";
                    string inputFieldPhName = _inputFieldPlaceholderChildNameByLayer.TryGetValue(layer, out string ifp) ? ifp ?? "" : "";
                    string inputFieldVpName = _inputFieldTextViewportChildNameByLayer.TryGetValue(layer, out string ifv) ? ifv ?? "" : "";
                    string dropdownTemplateName = _dropdownTemplateChildNameByLayer.TryGetValue(layer, out string dtm) ? dtm ?? "" : "";
                    string dropdownCaptionName = _dropdownCaptionTextChildNameByLayer.TryGetValue(layer, out string dcap) ? dcap ?? "" : "";
                    string dropdownItemName = _dropdownItemTextChildNameByLayer.TryGetValue(layer, out string ditm) ? ditm ?? "" : "";
                    bool useTextMeshPro = GetEffectiveUseTextMeshPro(layer);

                    bool useCustNsWrite = !useExt && sliceImage && _useCustomNineSliceParamsByLayer.TryGetValue(layer, out bool ucn) && ucn;
                    NineSliceLayerParams nsWrite = GetDefaultNineSliceLayerParamsForSave();
                    if (useCustNsWrite && _nineSliceParamsByLayer.TryGetValue(layer, out var nsv))
                        nsWrite = nsv;

                    bool mergeVal = _mergeExportByLayer.TryGetValue(layer, out bool m) && m;
                    bool exportPrefabVal = _exportPrefabByLayer.TryGetValue(layer, out bool epFlagB) && epFlagB;
                    if (useExt)
                    {
                        mergeVal = false;
                        exportPrefabVal = false;
                        participateLocal = true;
                        participateCommon = true;
                        sliceImage = true;
                        primaryDedupNode = false;
                        useCustomImage = false;
                        customImagePath = "";
                        uiComponentType = "None";
                        scrollBarDir = "left_to_right";
                        scrollBarHandleName = "";
                        sliderDir = "left_to_right";
                        sliderFillName = "";
                        sliderHandleName = "";
                        scrollRectBg = "";
                        scrollRectH = true;
                        scrollRectV = true;
                        scrollRectHsb = "";
                        scrollRectVsb = "";
                        toggleGraphicName = "";
                        inputFieldTextName = "";
                        inputFieldPhName = "";
                        inputFieldVpName = "";
                        dropdownTemplateName = "";
                        dropdownCaptionName = "";
                        dropdownItemName = "";
                        useCustNsWrite = false;
                        nsWrite = GetDefaultNineSliceLayerParamsForSave();
                    }

                    entries.Add(new LayerConfigEntry
                    {
                        id               = layer.LayerId.Value,
                        name             = layer.Name,
                        exported         = !_exportedByLayer.TryGetValue(layer, out bool expd) || expd,
                        visible          = layer.Visible,
                        merge            = mergeVal,
                        exportPrefab     = exportPrefabVal,
                        useExternalPrefab = useExt,
                        externalPrefabPath = extPath,
                        reusePosition    = !_externalPrefabReusePositionByLayer.TryGetValue(layer, out bool rp) || rp,
                        reuseSize        = _externalPrefabReuseSizeByLayer.TryGetValue(layer, out bool rs) && rs,
                        participateLocalDedup  = participateLocal,
                        participateCommonDedup = participateCommon,
                        sliceImage       = sliceImage,
                        primaryDedupNode = primaryDedupNode,
                        useCustomNineSliceParams = useCustNsWrite,
                        nineSliceBorderInset = nsWrite.borderInset,
                        nineSlicePixelThreshold = nsWrite.pixelThreshold,
                        nineSliceMinCenterCols = nsWrite.minCenterCols,
                        nineSliceMinCenterRows = nsWrite.minCenterRows,
                        nineSliceMinSameZone = nsWrite.minSameZone,
                        useCustomImage   = useCustomImage,
                        customImagePath  = customImagePath ?? "",
                        uiComponentType  = uiComponentType,
                        scrollBarDirection = scrollBarDir,
                        scrollBarHandleChildName = scrollBarHandleName,
                        sliderDirection = sliderDir,
                        sliderFillRectChildName = sliderFillName,
                        sliderHandleRectChildName = sliderHandleName,
                        scrollRectViewportChildName = scrollRectBg,
                        scrollRectContentChildName = scrollRectContent,
                        scrollRectHorizontal = scrollRectH,
                        scrollRectVertical = scrollRectV,
                        scrollRectHorizontalScrollbarChildName = scrollRectHsb,
                        scrollRectVerticalScrollbarChildName = scrollRectVsb,
                        toggleGraphicChildName = toggleGraphicName,
                        inputFieldTextChildName = inputFieldTextName,
                        inputFieldPlaceholderChildName = inputFieldPhName,
                        inputFieldTextViewportChildName = inputFieldVpName,
                        dropdownTemplateChildName = dropdownTemplateName,
                        dropdownCaptionTextChildName = dropdownCaptionName,
                        dropdownItemTextChildName = dropdownItemName,
                        useTextMeshPro   = useTextMeshPro
                    });
                }

                return new MergeExportConfigData { layers = entries.ToArray() };
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Failed to build export config data: {ex.Message}");
                return null;
            }
        }

        private void SaveMergeExportConfig()
        {
            string path = GetExportConfigPath(_psdPath);
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                MergeExportConfigData data = BuildMergeExportConfigData();
                if (data == null) return;
                string json = JsonUtility.ToJson(data, true);
                File.WriteAllText(path, json);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Failed to save export config: {ex.Message}");
            }
        }

        // ─────────────────────── Left Panel: Layer Tree ───────────────────────

        private void DrawLeftPanel(Rect rect)
        {
            GUILayout.BeginArea(rect);

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("<< Back", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                CloseCurrentPsd();
            }
            GUILayout.Space(6);
            GUILayout.Label("Layers", EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            _treeScrollViewportHeight = Mathf.Max(1f, rect.height - EditorStyles.toolbar.fixedHeight);
            _treeScrollPos = EditorGUILayout.BeginScrollView(_treeScrollPos);

            foreach (Layer child in VisibleChildrenInDrawOrder(_psd.Root))
                DrawLayerNode(child, 0);

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawLayerNode(Layer layer, int depth)
        {
            Rect rowRect = EditorGUILayout.BeginHorizontal(GUILayout.Height(TREE_ROW_HEIGHT));

            bool isSelected = _selectedLayer == layer;
            if (isSelected && _selectionHighlightTex != null)
            {
                GUI.DrawTexture(rowRect, _selectionHighlightTex);
            }

            GUILayout.Space(depth * INDENT_WIDTH + 4);

            if (layer.IsGroup)
            {
                if (!_foldoutStates.ContainsKey(layer))
                    _foldoutStates[layer] = true;

                bool expanded = _foldoutStates[layer];
                Rect foldRect = GUILayoutUtility.GetRect(TREE_FOLDOUT_WIDTH, TREE_ROW_HEIGHT, GUILayout.Width(TREE_FOLDOUT_WIDTH));
                bool newExpanded = EditorGUI.Foldout(foldRect, expanded, GUIContent.none, true);
                if (newExpanded != expanded)
                    _foldoutStates[layer] = newExpanded;
            }
            else
            {
                GUILayout.Space(TREE_FOLDOUT_WIDTH);
            }

            // Visibility icon
            string visIcon = layer.Visible ? "d_ToggleUVOverlay" : "d_ToggleUVOverlay";
            Color prevColor = GUI.color;
            GUI.color = layer.Visible ? Color.white : new Color(1, 1, 1, 0.3f);
            if (GUILayout.Button(layer.Visible ? "\u25C9" : "\u25CB", EditorStyles.miniLabel, GUILayout.Width(16)))
            {
                layer.Visible = !layer.Visible;
                _compositeDirty = true;
                if (_selectedLayer == layer)
                    _editingVisible = layer.Visible;
                ScheduleSaveMergeExportConfig();
            }
            GUI.color = prevColor;

            // Layer name: click to select; F2 for inline rename on this row
            string kindPrefix = layer.IsGroup ? "\u25A0 " : "  ";
            GUIStyle nameStyle = isSelected ? EditorStyles.whiteLabel : EditorStyles.label;
            if (_inlineRenamingLayer == layer)
            {
                if (_inlineRenameFocusFramesLeft > 0)
                {
                    EditorGUI.FocusTextInControl(InlineRenameControlName(layer));
                    _inlineRenameFocusFramesLeft--;
                }

                GUILayout.Label(kindPrefix, nameStyle, GUILayout.Width(layer.IsGroup ? 22f : 16f));
                GUI.SetNextControlName(InlineRenameControlName(layer));
                _inlineRenameBuffer = EditorGUILayout.TextField(_inlineRenameBuffer ?? "", EditorStyles.textField, GUILayout.MinWidth(24f), GUILayout.ExpandWidth(true));
                if (_selectedLayer == layer)
                    _editingName = _inlineRenameBuffer;
            }
            else
            {
                GUIContent nameContent = new GUIContent(kindPrefix + layer.Name);
                Rect nameRect = GUILayoutUtility.GetRect(nameContent, nameStyle, GUILayout.ExpandWidth(true));
                Event ev = Event.current;
                if (ev.type == EventType.Repaint)
                    GUI.Label(nameRect, nameContent, nameStyle);

                if (ev.type == EventType.MouseDown && ev.button == 0 && nameRect.Contains(ev.mousePosition))
                {
                    SelectLayer(layer);
                    _canvasShowSelection = true;
                    _nameLongPressLayer = layer;
                    _nameLongPressStartTime = EditorApplication.timeSinceStartup;
                    _nameLongPressStartGui = ev.mousePosition;
                    _nameLongPressHoldMet = false;
                    _nameLongPressLeftButtonHeld = true;
                    ev.Use();
                }
                else if (ev.type == EventType.MouseDrag && ev.button == 0 && _nameLongPressLayer == layer)
                {
                    if ((ev.mousePosition - _nameLongPressStartGui).sqrMagnitude
                        > NAME_LONG_PRESS_MAX_GUI_DRAG_PX * NAME_LONG_PRESS_MAX_GUI_DRAG_PX)
                    {
                        _nameLongPressLayer = null;
                        _nameLongPressHoldMet = false;
                    }
                }
            }

            EditorGUILayout.EndHorizontal();

            // Draw children if expanded group
            if (layer.IsGroup && _foldoutStates.TryGetValue(layer, out bool fold) && fold)
            {
                foreach (Layer child in VisibleChildrenInDrawOrder(layer))
                    DrawLayerNode(child, depth + 1);
            }
        }

        /// <summary>Child order matching left tree draw (clipped layers skipped).</summary>
        private IEnumerable<Layer> VisibleChildrenInDrawOrder(Layer parent)
        {
            if (parent == null)
                yield break;
            var children = parent.Children;
            if (_usePsdNodeOrder)
            {
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    Layer c = children[i];
                    if (!c.IsClipped)
                        yield return c;
                }
            }
            else
            {
                for (int i = 0; i < children.Count; i++)
                {
                    Layer c = children[i];
                    if (!c.IsClipped)
                        yield return c;
                }
            }
        }

        private void TryHandleLayerRenameHotkey()
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown || e.keyCode != KeyCode.F2)
                return;
            if (_selectedLayer == null)
                return;
            BeginOrRefocusInlineRename(_selectedLayer);
            e.Use();
            Repaint();
        }

        /// <summary>When Enter is pressed in a right-panel text field, clear keyboard focus so
        /// arrow-key navigation works immediately without requiring a mouse click.</summary>
        private void TryHandleRightPanelTextFieldConfirm()
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown) return;
            if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
            if (!EditorGUIUtility.editingTextField) return;
            // Let HandleInlineRenameKeyboard own Enter for the left-tree inline rename field.
            if (_inlineRenamingLayer != null &&
                GUI.GetNameOfFocusedControl() == InlineRenameControlName(_inlineRenamingLayer))
                return;
            GUIUtility.keyboardControl = 0;
            e.Use();
            Repaint();
        }

        private void TryHandleLayerArrowNavigationHotkey()
        {
            if (_psd == null || _selectedLayer == null)
                return;

            Event e = Event.current;
            if (e.type != EventType.KeyDown)
                return;
            if (e.keyCode != KeyCode.UpArrow && e.keyCode != KeyCode.DownArrow)
                return;
            if (EditorGUIUtility.editingTextField)
            {
                // The left-tree inline-rename field owns arrow keys; bail out.
                if (_inlineRenamingLayer != null &&
                    GUI.GetNameOfFocusedControl() == InlineRenameControlName(_inlineRenamingLayer))
                    return;
                // A right-panel text field is focused: commit the edit and fall through to navigate.
                GUIUtility.keyboardControl = 0;
            }

            List<Layer> visibleLayers = new List<Layer>();
            CollectVisibleTreeLayers(_psd.Root, visibleLayers);
            if (visibleLayers.Count == 0)
                return;

            int index = visibleLayers.IndexOf(_selectedLayer);
            if (index < 0)
                return;

            int targetIndex = e.keyCode == KeyCode.UpArrow ? index - 1 : index + 1;
            if (targetIndex < 0 || targetIndex >= visibleLayers.Count)
                return;

            Layer targetLayer = visibleLayers[targetIndex];
            SelectLayer(targetLayer);
            ScrollTreeToLayer(targetLayer);
            _canvasShowSelection = true;
            e.Use();
            Repaint();
        }

        private void CollectVisibleTreeLayers(Layer root, List<Layer> outList)
        {
            if (root == null || outList == null)
                return;

            foreach (Layer child in VisibleChildrenInDrawOrder(root))
            {
                outList.Add(child);
                if (child.IsGroup && _foldoutStates.TryGetValue(child, out bool expanded) && expanded)
                    CollectVisibleTreeLayers(child, outList);
            }
        }

        /// <summary>F2 and long-press on name: start or refocus left-tree inline rename.</summary>
        private void BeginOrRefocusInlineRename(Layer layer)
        {
            if (layer == null) return;
            if (_inlineRenamingLayer == layer)
            {
                _inlineRenameFocusFramesLeft = 3;
                return;
            }

            if (_inlineRenamingLayer != null)
                CommitInlineRename();
            SelectLayer(layer);
            _canvasShowSelection = true;
            _inlineRenamingLayer = layer;
            _inlineRenameBuffer = layer.Name ?? "";
            _inlineRenameFocusFramesLeft = 3;
            _nameLongPressLayer = null;
            _nameLongPressHoldMet = false;
        }

        private void OnEditorUpdateNameLongPress()
        {
            if (_psd == null || _nameLongPressLayer == null)
                return;
            if (!_nameLongPressLeftButtonHeld)
                return;

            if (EditorApplication.timeSinceStartup - _nameLongPressStartTime < NAME_LONG_PRESS_HOLD_SECONDS)
                return;

            if (!_nameLongPressHoldMet)
                Repaint();
            _nameLongPressHoldMet = true;
        }

        private string InlineRenameControlName(Layer layer)
        {
            if (layer == null) return "PSDEditor_InlineRename_null";
            return "PSDEditor_InlineRename_" + PropertyScrollGuiHint(layer);
        }

        private void CommitInlineRename()
        {
            if (_inlineRenamingLayer == null) return;
            string trimmed = _inlineRenameBuffer ?? "";
            if (trimmed != _inlineRenamingLayer.Name)
            {
                _inlineRenamingLayer.Name = trimmed;
                ScheduleSaveMergeExportConfig();
                if (_selectedLayer == _inlineRenamingLayer)
                    _editingName = trimmed;
            }
            _inlineRenamingLayer = null;
            _inlineRenameBuffer = null;
            if (_selectedLayer != null)
                _layerBecameSelectedAt = EditorApplication.timeSinceStartup;
        }

        private void CancelInlineRename()
        {
            _inlineRenamingLayer = null;
            _inlineRenameBuffer = null;
            if (_selectedLayer != null)
            {
                _editingName = _selectedLayer.Name;
                _layerBecameSelectedAt = EditorApplication.timeSinceStartup;
            }
        }

        private void HandleInlineRenameKeyboard()
        {
            if (_inlineRenamingLayer == null) return;
            Event e = Event.current;
            if (e.type != EventType.KeyDown) return;
            if (e.keyCode == KeyCode.Escape)
            {
                CancelInlineRename();
                e.Use();
                Repaint();
                return;
            }

            if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter)
                return;
            if (GUI.GetNameOfFocusedControl() != InlineRenameControlName(_inlineRenamingLayer))
                return;
            CommitInlineRename();
            e.Use();
            Repaint();
        }

        private void SelectLayer(Layer layer)
        {
            GUIUtility.keyboardControl = 0;

            if (_inlineRenamingLayer != null && _inlineRenamingLayer != layer)
                CommitInlineRename();

            bool selectionChanged = _selectedLayer != layer;
            if (_selectedLayer != layer)
            {
                BumpLayerPreviewEpoch();
                // Persist scroll after EndScrollView from events; don’t overwrite stored per-layer pos with layout-tainted value here.
                if (!_propertyScrollPosByLayer.TryGetValue(layer, out _propertyScrollPos))
                    _propertyScrollPos = Vector2.zero;
            }
            _selectedLayer = layer;
            if (selectionChanged)
                _layerBecameSelectedAt = EditorApplication.timeSinceStartup;
            if (_inlineRenamingLayer == layer)
                _editingName = _inlineRenameBuffer ?? layer.Name;
            else
                _editingName = layer.Name;
            _editingVisible = layer.Visible;
            Repaint();
        }

        private int PropertyScrollGuiHint(Layer layer)
        {
            if (layer == null) return 0;
            if (!_propertyScrollGuiHintByLayer.TryGetValue(layer, out int hint))
            {
                hint = _nextPropertyScrollGuiHint++;
                _propertyScrollGuiHintByLayer[layer] = hint;
            }
            return hint;
        }

        private static bool ShouldPersistPropertyScrollToDict()
        {
            EventType t = Event.current.type;
            return t == EventType.Repaint || t == EventType.ScrollWheel || t == EventType.MouseDrag || t == EventType.MouseUp;
        }

        // ─────────────────────── Center Panel: Canvas Preview ───────────────────────

        private void DrawCenterPanel(Rect rect)
        {
            if (_compositeDirty)
            {
                _compositeDirty = false;
                RefreshPsdComposite(_liveComposite);
                BumpLayerPreviewEpoch();
            }

            GUILayout.BeginArea(rect);
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label($"Canvas preview  {_psd.Width} x {_psd.Height}", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label(_liveComposite ? "Live composite: On" : "Live composite: Off", EditorStyles.toolbarButton);
            EditorGUILayout.EndHorizontal();

            float toolbarH = EditorStyles.toolbar.fixedHeight;
            Rect canvasArea = new Rect(10, toolbarH + 10, rect.width - 20, rect.height - toolbarH - 20);

            if (canvasArea.width <= 0 || canvasArea.height <= 0)
            {
                GUILayout.EndArea();
                return;
            }

            float scaleX = canvasArea.width / _psd.Width;
            float scaleY = canvasArea.height / _psd.Height;
            float scale = Mathf.Min(scaleX, scaleY);

            float drawW = _psd.Width * scale;
            float drawH = _psd.Height * scale;
            float offsetX = canvasArea.x + (canvasArea.width - drawW) * 0.5f;
            float offsetY = canvasArea.y + (canvasArea.height - drawH) * 0.5f;

            Rect canvasRect = new Rect(offsetX, offsetY, drawW, drawH);

            // Canvas rect in window coords for top-level hit testing in OnGUI
            _canvasRectWindow = new Rect(rect.x + offsetX, rect.y + offsetY, drawW, drawH);

            // Draw canvas background
            if (_canvasBgTex != null)
                GUI.DrawTexture(canvasRect, _canvasBgTex);

            // Draw full PSD composite as effect preview, then wireframes on top
            if (_psdCompositeTex != null)
                GUI.DrawTexture(canvasRect, _psdCompositeTex);

            // Draw all visible layers as thin outlines
            DrawLayerOutlines(_psd.Root, canvasRect, scale, offsetX, offsetY);

            // Draw selected layer rectangle (only when canvas selection is active)
            if (_selectedLayer != null && _canvasShowSelection)
            {
                int layerLeft, layerTop, layerW, layerH;
                GetLayerBounds(_selectedLayer, out layerLeft, out layerTop, out layerW, out layerH);

                if (layerW > 0 && layerH > 0)
                {
                    Rect layerRect = new Rect(
                        offsetX + layerLeft * scale,
                        offsetY + layerTop * scale,
                        layerW * scale,
                        layerH * scale
                    );

                    if (_layerRectTex != null)
                        GUI.DrawTexture(layerRect, _layerRectTex);

                    // Blue outline
                    DrawRectOutline(layerRect, new Color(0.2f, 0.5f, 1f, 1f), 2f);

                    // Label
                    string info = $"{_selectedLayer.Name} ({layerW}x{layerH})";
                    GUI.Label(new Rect(layerRect.x, layerRect.yMax + 2, 300, 20), info, EditorStyles.miniLabel);
                }
            }

            // Handle canvas click-to-select
            Event evt = Event.current;
            if (evt.type == EventType.MouseDown && evt.button == 0 && canvasRect.Contains(evt.mousePosition))
            {
                float psdX = (evt.mousePosition.x - offsetX) / scale;
                float psdY = (evt.mousePosition.y - offsetY) / scale;

                if (psdX >= 0 && psdX < _psd.Width && psdY >= 0 && psdY < _psd.Height)
                {
                    float dx = psdX - _lastClickPsdPos.x;
                    float dy = psdY - _lastClickPsdPos.y;
                    bool sameSpot = (dx * dx + dy * dy) < CLICK_SAME_SPOT_THRESHOLD * CLICK_SAME_SPOT_THRESHOLD;

                    if (sameSpot && _clickCandidates.Count > 0)
                    {
                        _clickCandidateIndex = (_clickCandidateIndex + 1) % _clickCandidates.Count;
                    }
                    else
                    {
                        _lastClickPsdPos = new Vector2(psdX, psdY);
                        _clickCandidates = FindLayersAtPsdPosition(psdX, psdY);
                        _clickCandidateIndex = 0;
                    }

                    if (_clickCandidates.Count > 0)
                    {
                        _canvasShowSelection = true;
                        Layer picked = _clickCandidates[_clickCandidateIndex];
                        SelectLayer(picked);
                        Layer treeNav = LayerForTreeNavigation(picked);
                        if (treeNav != null)
                        {
                            ExpandParentsOf(treeNav);
                            ScrollTreeToLayer(treeNav);
                        }
                    }
                    else
                    {
                        _canvasShowSelection = false;
                    }
                }
                else
                {
                    _canvasShowSelection = false;
                }

                evt.Use();
            }

            GUILayout.EndArea();
        }

        private void DrawLayerOutlines(Layer layer, Rect canvasRect, float scale, float ox, float oy)
        {
            foreach (var child in layer.Children)
            {
                if (!child.Visible) continue;
                if (child.IsClipped) continue;

                if (!child.IsGroup)
                {
                    if (child.Width > 0 && child.Height > 0)
                    {
                        Rect r = new Rect(
                            ox + child.Left * scale,
                            oy + child.Top * scale,
                            child.Width * scale,
                            child.Height * scale
                        );
                        DrawRectOutline(r, new Color(0.5f, 0.5f, 0.5f, 0.2f), 1f);
                    }
                }
                else
                {
                    DrawLayerOutlines(child, canvasRect, scale, ox, oy);
                }
            }
        }

        private void GetLayerBounds(Layer layer, out int left, out int top, out int w, out int h)
        {
            if (layer.IsGroup)
            {
                var bbox = ((Group)layer).BBox;
                left = bbox.Left;
                top = bbox.Top;
                w = bbox.Right - bbox.Left;
                h = bbox.Bottom - bbox.Top;
            }
            else
            {
                left = layer.Left;
                top = layer.Top;
                w = layer.Width;
                h = layer.Height;
            }
        }

        // ─────────────────────── Canvas Click-to-Select ───────────────────────

        private struct LayerHitInfo
        {
            public Layer layer;
            public int depth;
            public float distSq;
        }

        /// <summary>Collect visible nodes (groups and leaves) containing the click; record depth and distance to center.</summary>
        private void CollectHitLayers(Layer parent, int depth, float psdX, float psdY, List<LayerHitInfo> results)
        {
            foreach (var child in parent.Children)
            {
                if (!child.Visible) continue;

                int childDepth = depth + 1;
                GetLayerBounds(child, out int left, out int top, out int w, out int h);
                if (w > 0 && h > 0 && psdX >= left && psdX < left + w && psdY >= top && psdY < top + h)
                {
                    float cx = left + w * 0.5f;
                    float cy = top + h * 0.5f;
                    float dx = psdX - cx;
                    float dy = psdY - cy;
                    results.Add(new LayerHitInfo { layer = child, depth = childDepth, distSq = dx * dx + dy * dy });
                }

                if (child.IsGroup)
                    CollectHitLayers(child, childDepth, psdX, psdY, results);
            }
        }

        /// <summary>Find visible nodes at a PSD point, sorted by depth desc then distance asc.</summary>
        private List<Layer> FindLayersAtPsdPosition(float psdX, float psdY)
        {
            var hits = new List<LayerHitInfo>();
            CollectHitLayers(_psd.Root, 0, psdX, psdY, hits);

            hits.Sort((a, b) =>
            {
                int cmp = b.depth.CompareTo(a.depth);
                if (cmp != 0) return cmp;
                return a.distSq.CompareTo(b.distSq);
            });

            var result = new List<Layer>(hits.Count);
            foreach (var hit in hits)
                result.Add(hit.layer);
            return result;
        }

        /// <summary>Expand all ancestor groups so the target row is visible in the left tree.</summary>
        private void ExpandParentsOf(Layer target)
        {
            ExpandParentsRecursive(_psd.Root, target);
        }

        private bool ExpandParentsRecursive(Layer current, Layer target)
        {
            foreach (var child in current.Children)
            {
                if (child == target)
                {
                    if (current.IsGroup && current != _psd.Root)
                        _foldoutStates[current] = true;
                    return true;
                }
                if (child.IsGroup && ExpandParentsRecursive(child, target))
                {
                    _foldoutStates[current] = true;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Match EditorGUILayout vertical stack: each row <see cref="TREE_ROW_HEIGHT"/> + <see cref="EditorGUIUtility.standardVerticalSpacing"/>.
        /// Using only N×row height underestimates content height and maxScroll vs Unity’s ScrollView.
        /// </summary>
        private static float TreeScrollContentHeight(int totalRows)
        {
            if (totalRows <= 0)
                return 0f;
            float gap = EditorGUIUtility.standardVerticalSpacing;
            return totalRows * TREE_ROW_HEIGHT + Mathf.Max(0, totalRows - 1) * gap;
        }

        /// <summary>Content-space Y of row rowIndex’s top edge (0-based).</summary>
        private static float TreeRowContentTopY(int rowIndex)
        {
            if (rowIndex <= 0)
                return 0f;
            float gap = EditorGUIUtility.standardVerticalSpacing;
            return rowIndex * (TREE_ROW_HEIGHT + gap);
        }

        /// <summary>Scroll left tree so the target row is in view. Call after ExpandParentsOf.</summary>
        private void ScrollTreeToLayer(Layer target)
        {
            if (_psd == null)
                return;
            int rowIndex = CountRowsToLayer(target);
            if (rowIndex < 0) return;

            float targetY = TreeRowContentTopY(rowIndex);
            float visibleH = _treeScrollViewportHeight > 1f
                ? _treeScrollViewportHeight
                : Mathf.Max(1f, position.height - BOTTOM_PANEL_HEIGHT - EditorStyles.toolbar.fixedHeight);

            int totalRows = CountAllVisibleTreeRows(_psd.Root);
            float contentH = TreeScrollContentHeight(totalRows);
            float maxScroll = Mathf.Max(0f, contentH - visibleH);

            // Edge-align and clamp; centering + ScrollView rounding can desync _treeScrollPos from the real viewport
            if (targetY < _treeScrollPos.y)
                _treeScrollPos.y = targetY;
            if (targetY + TREE_ROW_HEIGHT > _treeScrollPos.y + visibleH)
                _treeScrollPos.y = targetY + TREE_ROW_HEIGHT - visibleH;

            _treeScrollPos.y = Mathf.Clamp(_treeScrollPos.y, 0f, maxScroll);
        }

        /// <summary>Count visible rows before the target in left-tree display order.</summary>
        private int CountRowsToLayer(Layer target)
        {
            int count = 0;
            return CountRowsRecursive(_psd.Root, target, ref count) ? count : -1;
        }

        private bool CountRowsRecursive(Layer parent, Layer target, ref int count)
        {
            foreach (var child in VisibleChildrenInDrawOrder(parent))
            {
                if (child == target)
                    return true;
                count++;
                if (child.IsGroup && _foldoutStates.TryGetValue(child, out bool expanded) && expanded)
                {
                    if (CountRowsRecursive(child, target, ref count))
                        return true;
                }
            }
            return false;
        }

        private int CountAllVisibleTreeRows(Layer parent)
        {
            if (parent == null)
                return 0;
            int total = 0;
            foreach (var child in VisibleChildrenInDrawOrder(parent))
            {
                total++;
                if (child.IsGroup && _foldoutStates.TryGetValue(child, out bool expanded) && expanded)
                    total += CountAllVisibleTreeRows(child);
            }
            return total;
        }

        /// <summary>Clipped layers are hidden in the tree; navigate using their clipping base layer.</summary>
        private static Layer LayerForTreeNavigation(Layer layer)
        {
            if (layer == null)
                return null;
            return layer.IsClipped ? GetClippingBaseLayer(layer) : layer;
        }

        /// <summary>Matches PsdImage.BlitGroup: walk parent children upward to nearest non-clipped layer above the clip.</summary>
        private static Layer GetClippingBaseLayer(Layer clipped)
        {
            if (clipped == null || !clipped.IsClipped)
                return clipped;
            Layer parent = clipped.Parent;
            if (parent == null)
                return clipped;
            var siblings = parent.Children;
            int idx = -1;
            for (int i = 0; i < siblings.Count; i++)
            {
                if (siblings[i] == clipped)
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0)
                return clipped;
            for (int j = idx - 1; j >= 0; j--)
            {
                if (!siblings[j].IsClipped)
                    return siblings[j];
            }
            return clipped;
        }

        private void DrawRectOutline(Rect rect, Color color, float thickness)
        {
            Texture2D tex = MakeSolidTexture(color);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), tex);                         // top
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), tex);          // bottom
            GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), tex);                        // left
            GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), tex);         // right
            DestroyImmediate(tex);
        }

        // ─────────────────────── Right Panel: Properties ───────────────────────

        private void DrawRightPanel(Rect rect)
        {
            GUILayout.BeginArea(rect);

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Layer properties", EditorStyles.boldLabel);
            // GUILayout.FlexibleSpace();
            // GUILayout.Label("Ctrl+Z Undo · Ctrl+Shift+Z / Ctrl+Y Redo", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            if (_selectedLayer == null)
            {
                GUILayout.Space(20);
                GUILayout.Label("Select a layer in the left tree", EditorStyles.centeredGreyMiniLabel);
                GUILayout.EndArea();
                return;
            }

            float toolbarH = EditorStyles.toolbar.fixedHeight;
            float scrollViewH = Mathf.Max(32f, rect.height - toolbarH);
            // Consume a control id bound to this layer so the ScrollView state is isolated per layer
            GUIUtility.GetControlID(PropertyScrollGuiHint(_selectedLayer), FocusType.Passive);
            _propertyScrollPos = EditorGUILayout.BeginScrollView(
                _propertyScrollPos,
                false,
                false,
                GUI.skin.horizontalScrollbar,
                GUI.skin.verticalScrollbar,
                GUI.skin.scrollView,
                GUILayout.Height(scrollViewH));

            SyncLayerPreview();

            const float layerPreviewMaxH = 132f;
            EditorGUILayout.LabelField("Layer preview", EditorStyles.boldLabel);
            DrawSeparator();
            Rect previewBox = EditorGUILayout.GetControlRect(false, layerPreviewMaxH, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(previewBox, new Color(0.72f, 0.72f, 0.72f, 1f));
            if (_layerPreviewTex != null && _layerPreviewTex.width > 0 && _layerPreviewTex.height > 0)
            {
                float tw = _layerPreviewTex.width;
                float th = _layerPreviewTex.height;
                float pad = 6f;
                float innerW = Mathf.Max(1f, previewBox.width - pad * 2f);
                float innerH = Mathf.Max(1f, previewBox.height - pad * 2f);
                float scale = Mathf.Min(innerW / tw, innerH / th, 1f);
                float dw = tw * scale;
                float dh = th * scale;
                var inner = new Rect(
                    previewBox.x + (previewBox.width - dw) * 0.5f,
                    previewBox.y + (previewBox.height - dh) * 0.5f,
                    dw,
                    dh);
                GUI.DrawTexture(inner, _layerPreviewTex, ScaleMode.ScaleToFit, true);
            }
            else
            {
                var labelRect = new Rect(previewBox.x + 4f, previewBox.y + (previewBox.height - EditorGUIUtility.singleLineHeight) * 0.5f,
                    previewBox.width - 8f, EditorGUIUtility.singleLineHeight);
                GUI.Label(labelRect, "No pixel preview (empty group or no raster data)", EditorStyles.centeredGreyMiniLabel);
            }

            GUILayout.Space(10);
            EditorGUILayout.LabelField("Basic", EditorStyles.boldLabel);
            DrawSeparator();

            // Editable name (inline rename: F2 in tree); Delayed so one undo step per edit
            EditorGUILayout.LabelField("Name");
            EditorGUI.BeginChangeCheck();
            string newName = EditorGUILayout.DelayedTextField(_editingName);
            if (EditorGUI.EndChangeCheck() && newName != _editingName)
            {
                RecordRightPanelUndoBeforeChange();
                _editingName = newName;
                _selectedLayer.Name = newName;
                if (_inlineRenamingLayer == _selectedLayer)
                    _inlineRenameBuffer = newName;
                ScheduleSaveMergeExportConfig();
            }

            GUILayout.Space(4);

            // Export: off skips slice and Prefab node
            bool isExported = !_exportedByLayer.TryGetValue(_selectedLayer, out bool expVal) || expVal;
            bool newExported = EditorGUILayout.Toggle("Export", isExported);
            if (newExported != isExported)
            {
                RecordRightPanelUndoBeforeChange();
                _exportedByLayer[_selectedLayer] = newExported;
                ScheduleSaveMergeExportConfig();
            }

            // Visibility (meaningful when exported)
            bool newVisible = EditorGUILayout.Toggle("Visible", _editingVisible);
            if (newVisible != _editingVisible)
            {
                RecordRightPanelUndoBeforeChange();
                _editingVisible = newVisible;
                _selectedLayer.Visible = newVisible;
                _compositeDirty = true;
                InvalidateLayerPreview();
                Repaint();
                ScheduleSaveMergeExportConfig();
            }

            GUILayout.Space(4);

            // External prefab: no slice/export; place chosen prefab at this node on generation
            bool useExternal = _useExternalPrefabByLayer.TryGetValue(_selectedLayer, out bool ue) && ue;
            bool newUseExternal = EditorGUILayout.Toggle(TT("Use external Prefab", "When true, this node is not sliced/exported; export places the selected prefab at this transform."), useExternal);
            if (newUseExternal != useExternal)
            {
                RecordRightPanelUndoBeforeChange();
                _useExternalPrefabByLayer[_selectedLayer] = newUseExternal;
                if (!newUseExternal)
                {
                    _externalPrefabPathByLayer.Remove(_selectedLayer);
                    _externalPrefabReusePositionByLayer.Remove(_selectedLayer);
                    _externalPrefabReuseSizeByLayer.Remove(_selectedLayer);
                }
                else
                {
                    _externalPrefabReusePositionByLayer[_selectedLayer] = true;
                    _externalPrefabReuseSizeByLayer[_selectedLayer] = false;
                }
                ScheduleSaveMergeExportConfig();
            }
            if (useExternal)
            {
                _externalPrefabPathByLayer.TryGetValue(_selectedLayer, out string currentPath);
                UnityEngine.Object currentPrefab = null;
                if (!string.IsNullOrEmpty(currentPath))
                    currentPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(currentPath);
                UnityEngine.Object newPrefab = EditorGUILayout.ObjectField("Prefab reference", currentPrefab, typeof(GameObject), false);
                if (newPrefab != currentPrefab)
                {
                    RecordRightPanelUndoBeforeChange();
                    string path = newPrefab != null ? AssetDatabase.GetAssetPath(newPrefab) : null;
                    if (!string.IsNullOrEmpty(path))
                    {
                        _externalPrefabPathByLayer[_selectedLayer] = path;
                        ScheduleSaveMergeExportConfig();
                    }
                    else if (newPrefab == null)
                    {
                        _externalPrefabPathByLayer.Remove(_selectedLayer);
                        ScheduleSaveMergeExportConfig();
                    }
                }
                bool reusePos = _externalPrefabReusePositionByLayer.TryGetValue(_selectedLayer, out bool rp) && rp;
                bool newReusePos = EditorGUILayout.Toggle("Reuse this PSD node position", reusePos);
                if (newReusePos != reusePos)
                {
                    RecordRightPanelUndoBeforeChange();
                    _externalPrefabReusePositionByLayer[_selectedLayer] = newReusePos;
                    ScheduleSaveMergeExportConfig();
                }
                bool reuseSz = _externalPrefabReuseSizeByLayer.TryGetValue(_selectedLayer, out bool rs) && rs;
                bool newReuseSz = EditorGUILayout.Toggle("Reuse this node size", reuseSz);
                if (newReuseSz != reuseSz)
                {
                    RecordRightPanelUndoBeforeChange();
                    _externalPrefabReuseSizeByLayer[_selectedLayer] = newReuseSz;
                    ScheduleSaveMergeExportConfig();
                }
            }
            // HelpBox moved to tooltip

            bool hideSubordinateExportOptions = _useExternalPrefabByLayer.TryGetValue(_selectedLayer, out bool hideByExt) && hideByExt;
            if (hideSubordinateExportOptions)
                goto SkipSubordinateLayerExportOptions;

            GUILayout.Space(4);

            // Merge export: node + children → one image
            bool mergeExport = _mergeExportByLayer.TryGetValue(_selectedLayer, out bool v) && v;
            bool newMergeExport = EditorGUILayout.Toggle(TT("Merge for export", "When true, this node and its children export as a single image; children are not exported separately."), mergeExport);
            if (newMergeExport != mergeExport)
            {
                RecordRightPanelUndoBeforeChange();
                _mergeExportByLayer[_selectedLayer] = newMergeExport;
                ScheduleSaveMergeExportConfig();
            }
            // HelpBox moved to tooltip

            // Single export (no dedup): visible for image layers or merged-group layers
            bool isSingleExportEligible = _selectedLayer != null && _selectedLayer.LayerId.HasValue &&
                ((!_selectedLayer.IsGroup && _selectedLayer.Kind != LayerKind.Type) ||
                 (_selectedLayer.IsGroup && mergeExport));
            if (isSingleExportEligible)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUIUtility.labelWidth);
                if (GUILayout.Button(new GUIContent("Single export (no dedup)", "Export only this image directly to the output folder. Skips all dedup (local and common-dir). Nine-slice and Save-to-common-directory still follow the layer's settings. The output folder is never cleared."), GUILayout.Height(26)))
                {
                    if (string.IsNullOrEmpty(_psdPath))
                    {
                        EditorUtility.DisplayDialog("Cannot export", "Open a PSD file first.", "OK");
                    }
                    else
                    {
                        string pathToExport = _psdPath;
                        int layerId = _selectedLayer.LayerId.Value;
                        var win = this;
                        EditorApplication.delayCall += () =>
                        {
                            win.FlushExportConfig();
                            string imageRoot = SanitizeExportAssetsRoot(win._exportImageAssetsFolderRelative);
                            win._exportImageAssetsFolderRelative = imageRoot;
                            EditorPrefs.SetString(PrefExportImageFolder, imageRoot);
                            PSDAutoPrefab.ExportSingleLayerNoDedupFromPSD(pathToExport, imageRoot, layerId, win._exportAutoImageNaming);
                            if (win._autoNavigateAfterExport)
                            {
                                string folderPath = PSDAutoPrefab.LastExportedImageAssetsFolder;
                                if (!string.IsNullOrEmpty(folderPath))
                                {
                                    var folderAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(folderPath);
                                    if (folderAsset != null)
                                    {
                                        Selection.activeObject = folderAsset;
                                        EditorUtility.FocusProjectWindow();
                                        EditorGUIUtility.PingObject(folderAsset);
                                    }
                                }
                            }
                        };
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            GUILayout.Space(4);

            // Export Prefab: also write a standalone prefab for this node
            bool exportPrefab = _exportPrefabByLayer.TryGetValue(_selectedLayer, out bool ep) && ep;
            bool newExportPrefab = EditorGUILayout.Toggle(TT("Export Prefab", "When true, PSD export also generates an extra standalone Prefab for this node."), exportPrefab);
            if (newExportPrefab != exportPrefab)
            {
                RecordRightPanelUndoBeforeChange();
                _exportPrefabByLayer[_selectedLayer] = newExportPrefab;
                ScheduleSaveMergeExportConfig();
            }
            // HelpBox moved to tooltip

            if (exportPrefab && _selectedLayer != null && _selectedLayer.LayerId.HasValue)
            {
                GUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUIUtility.labelWidth);
                if (GUILayout.Button(new GUIContent("Export subtree now (slices + Prefab)", "Slice and nine-slice only this node and descendants; build this node’s prefab (other nodes with Export Prefab in the subtree export too). Does not build the full-PSD root prefab."), GUILayout.Height(26)))
                {
                    if (string.IsNullOrEmpty(_psdPath))
                    {
                        EditorUtility.DisplayDialog("Cannot export", "Open a PSD file first.", "OK");
                    }
                    else
                    {
                        string pathToExport = _psdPath;
                        int layerId = _selectedLayer.LayerId.Value;
                        var win = this;
                        EditorApplication.delayCall += () =>
                        {
                            win.FlushExportConfig();
                            string imageRoot = SanitizeExportAssetsRoot(win._exportImageAssetsFolderRelative);
                            string prefabRoot = SanitizeExportAssetsRoot(win._exportPrefabAssetsFolderRelative);
                            win._exportImageAssetsFolderRelative = imageRoot;
                            win._exportPrefabAssetsFolderRelative = prefabRoot;
                            EditorPrefs.SetString(PrefExportImageFolder, imageRoot);
                            EditorPrefs.SetString(PrefExportPrefabFolder, prefabRoot);
                            PSDAutoPrefab.GenerateSubtreeExportFromPSD(pathToExport, imageRoot, prefabRoot, layerId, win._exportAutoImageNaming);
                        };
                    }
                }
                EditorGUILayout.EndHorizontal();
                // HelpBox moved to button tooltip
            }

            GUILayout.Space(4);

            // Custom image: use assigned Sprite; skip slice, nine-slice, dedup, prefab swap
            bool useCustomImage = _useCustomImageByLayer.TryGetValue(_selectedLayer, out bool uc) && uc;
            bool newUseCustomImage = EditorGUILayout.Toggle(TT("Use custom image", "When enabled, uses your Sprite and skips slice, nine-slice, dedup, and prefab replacement."), useCustomImage);
            if (newUseCustomImage != useCustomImage)
            {
                RecordRightPanelUndoBeforeChange();
                _useCustomImageByLayer[_selectedLayer] = newUseCustomImage;
                if (!newUseCustomImage)
                {
                    _customImagePathByLayer.Remove(_selectedLayer);
                }
                ScheduleSaveMergeExportConfig();
            }
            if (useCustomImage)
            {
                _customImagePathByLayer.TryGetValue(_selectedLayer, out string currentImgPath);
                UnityEngine.Object currentSprite = null;
                if (!string.IsNullOrEmpty(currentImgPath))
                    currentSprite = AssetDatabase.LoadAssetAtPath<Sprite>(currentImgPath);
                UnityEngine.Object newSprite = EditorGUILayout.ObjectField("Custom Sprite", currentSprite, typeof(Sprite), false);
                if (newSprite != currentSprite)
                {
                    RecordRightPanelUndoBeforeChange();
                    string path = newSprite != null ? AssetDatabase.GetAssetPath(newSprite) : null;
                    if (!string.IsNullOrEmpty(path))
                    {
                        _customImagePathByLayer[_selectedLayer] = path;
                        ScheduleSaveMergeExportConfig();
                    }
                    else if (newSprite == null)
                    {
                        _customImagePathByLayer.Remove(_selectedLayer);
                        ScheduleSaveMergeExportConfig();
                    }
                }
            }
            // HelpBox moved to tooltip

            GUILayout.Space(4);

            // UI component type on export (UiComponentHandlerRegistry)
            var uiEntry = PsdUiComponentEditorHelper.BuildUiOnlyEntryFromLayer(
                _selectedLayer, _uiComponentTypeByLayer, _scrollBarDirectionByLayer,
                _scrollBarHandleChildNameByLayer, _sliderDirectionByLayer,
                _sliderFillRectChildNameByLayer, _sliderHandleRectChildNameByLayer,
                _scrollRectScrollBgChildNameByLayer, _scrollRectContentChildNameByLayer,
                _scrollRectHorizontalByLayer, _scrollRectVerticalByLayer,
                _scrollRectHorizontalScrollbarChildNameByLayer, _scrollRectVerticalScrollbarChildNameByLayer,
                _toggleGraphicChildNameByLayer,
                _inputFieldTextChildNameByLayer, _inputFieldPlaceholderChildNameByLayer, _inputFieldTextViewportChildNameByLayer,
                _dropdownTemplateChildNameByLayer, _dropdownCaptionTextChildNameByLayer, _dropdownItemTextChildNameByLayer);

            int uiTypeIndex = System.Array.IndexOf(UiComponentHandlerRegistry.AllComponentTypes, uiEntry.uiComponentType);
            if (uiTypeIndex < 0) uiTypeIndex = 0;
            int newUiTypeIndex = EditorGUILayout.Popup(TT("Type", "UI component to attach on export: Button; ScrollBar; Slider; ScrollRect; Toggle; InputField / Dropdown (Legacy·TMP); None unchanged."), uiTypeIndex, UiComponentHandlerRegistry.AllComponentTypes);
            if (newUiTypeIndex != uiTypeIndex)
            {
                RecordRightPanelUndoBeforeChange();
                uiEntry.uiComponentType = UiComponentHandlerRegistry.AllComponentTypes[newUiTypeIndex];
                if (uiEntry.uiComponentType == "ScrollBar" && string.IsNullOrEmpty(uiEntry.scrollBarDirection))
                    uiEntry.scrollBarDirection = "left_to_right";
                if (uiEntry.uiComponentType == "Slider" && string.IsNullOrEmpty(uiEntry.sliderDirection))
                    uiEntry.sliderDirection = "left_to_right";
                PsdUiComponentEditorHelper.SyncUiOnlyEntryToLayerDicts(
                    _selectedLayer, uiEntry, _uiComponentTypeByLayer, _scrollBarDirectionByLayer,
                    _scrollBarHandleChildNameByLayer, _sliderDirectionByLayer,
                    _sliderFillRectChildNameByLayer, _sliderHandleRectChildNameByLayer,
                    _scrollRectScrollBgChildNameByLayer, _scrollRectContentChildNameByLayer,
                    _scrollRectHorizontalByLayer, _scrollRectVerticalByLayer,
                    _scrollRectHorizontalScrollbarChildNameByLayer, _scrollRectVerticalScrollbarChildNameByLayer,
                    _toggleGraphicChildNameByLayer,
                    _inputFieldTextChildNameByLayer, _inputFieldPlaceholderChildNameByLayer, _inputFieldTextViewportChildNameByLayer,
                    _dropdownTemplateChildNameByLayer, _dropdownCaptionTextChildNameByLayer, _dropdownItemTextChildNameByLayer);
                ScheduleSaveMergeExportConfig();
                uiEntry = PsdUiComponentEditorHelper.BuildUiOnlyEntryFromLayer(
                    _selectedLayer, _uiComponentTypeByLayer, _scrollBarDirectionByLayer,
                    _scrollBarHandleChildNameByLayer, _sliderDirectionByLayer,
                    _sliderFillRectChildNameByLayer, _sliderHandleRectChildNameByLayer,
                    _scrollRectScrollBgChildNameByLayer, _scrollRectContentChildNameByLayer,
                    _scrollRectHorizontalByLayer, _scrollRectVerticalByLayer,
                    _scrollRectHorizontalScrollbarChildNameByLayer, _scrollRectVerticalScrollbarChildNameByLayer,
                    _toggleGraphicChildNameByLayer,
                    _inputFieldTextChildNameByLayer, _inputFieldPlaceholderChildNameByLayer, _inputFieldTextViewportChildNameByLayer,
                    _dropdownTemplateChildNameByLayer, _dropdownCaptionTextChildNameByLayer, _dropdownItemTextChildNameByLayer);
            }

            // HelpBox moved to tooltip

            var handler = UiComponentHandlerRegistry.Get(uiEntry.uiComponentType);
            handler?.DrawEditorUI(_selectedLayer, uiEntry, () =>
            {
                RecordRightPanelUndoBeforeChange();
                PsdUiComponentEditorHelper.SyncUiOnlyEntryToLayerDicts(
                    _selectedLayer, uiEntry, _uiComponentTypeByLayer, _scrollBarDirectionByLayer,
                    _scrollBarHandleChildNameByLayer, _sliderDirectionByLayer,
                    _sliderFillRectChildNameByLayer, _sliderHandleRectChildNameByLayer,
                    _scrollRectScrollBgChildNameByLayer, _scrollRectContentChildNameByLayer,
                    _scrollRectHorizontalByLayer, _scrollRectVerticalByLayer,
                    _scrollRectHorizontalScrollbarChildNameByLayer, _scrollRectVerticalScrollbarChildNameByLayer,
                    _toggleGraphicChildNameByLayer,
                    _inputFieldTextChildNameByLayer, _inputFieldPlaceholderChildNameByLayer, _inputFieldTextViewportChildNameByLayer,
                    _dropdownTemplateChildNameByLayer, _dropdownCaptionTextChildNameByLayer, _dropdownItemTextChildNameByLayer);
                ScheduleSaveMergeExportConfig();
            });

            if (_selectedLayer.Kind == LayerKind.Type)
            {
                GUILayout.Space(4);
                bool useTmp = GetEffectiveUseTextMeshPro(_selectedLayer);
                bool newUseTmp = EditorGUILayout.Toggle(TT("Use TextMeshPro", "On: type layer exports as TextMeshProUGUI. Off: Unity Legacy Text."), useTmp);
                if (newUseTmp != useTmp)
                {
                    RecordRightPanelUndoBeforeChange();
                    _useTextMeshProByLayer[_selectedLayer] = newUseTmp;
                    ScheduleSaveMergeExportConfig();
                }
                // HelpBox moved to tooltip
            }

            bool showSliceCommonDedup = ShouldShowSliceCommonDedupOptions(_selectedLayer);

            if (showSliceCommonDedup)
            {
            GUILayout.Space(4);

            bool primaryDedup = _primaryDedupNodeByLayer.TryGetValue(_selectedLayer, out bool pdm) && pdm;
            bool newPrimaryDedup = EditorGUILayout.Toggle(TT("Primary dedup node", "Within one local dedup group: one primary pins the virtual rep to that node (pixels + params); multiple primaries pick the largest resolution. None reverts to legacy virtual rep (largest image + merge params)."), primaryDedup);
            if (newPrimaryDedup != primaryDedup)
            {
                RecordRightPanelUndoBeforeChange();
                if (newPrimaryDedup)
                    _primaryDedupNodeByLayer[_selectedLayer] = true;
                else
                    _primaryDedupNodeByLayer.Remove(_selectedLayer);
                ScheduleSaveMergeExportConfig();
            }
            // HelpBox moved to tooltip

            // Slice: nine-slice on export when true; raw image when false
            bool sliceImage = GetEffectiveSliceImage(_selectedLayer);
            bool newSliceImage = EditorGUILayout.Toggle(TT("Slice / nine-slice", "On: export runs nine-slice. Off: export raw image with no nine-slice."), sliceImage);
            if (newSliceImage != sliceImage)
            {
                RecordRightPanelUndoBeforeChange();
                _sliceImageByLayer[_selectedLayer] = newSliceImage;
                if (!newSliceImage)
                {
                    _useCustomNineSliceParamsByLayer.Remove(_selectedLayer);
                    _nineSliceParamsByLayer.Remove(_selectedLayer);
                }
                ScheduleSaveMergeExportConfig();
            }
            // HelpBox moved to tooltip

            bool sliceOn = GetEffectiveSliceImage(_selectedLayer);
            if (sliceOn)
            {
                GUILayout.Space(4);
                bool useCustNs = _useCustomNineSliceParamsByLayer.TryGetValue(_selectedLayer, out bool ucn) && ucn;
                bool newUseCustNs = EditorGUILayout.Toggle(TT("Custom nine-slice params", "When on, this layer ignores global settings from Tools/PSD/Nine-slice settings… and uses the fields below."), useCustNs);
                if (newUseCustNs != useCustNs)
                {
                    RecordRightPanelUndoBeforeChange();
                    if (newUseCustNs)
                    {
                        _useCustomNineSliceParamsByLayer[_selectedLayer] = true;
                        if (!_nineSliceParamsByLayer.ContainsKey(_selectedLayer))
                            _nineSliceParamsByLayer[_selectedLayer] = ReadGlobalNineSliceDefaultsFromEditorPrefs();
                    }
                    else
                    {
                        _useCustomNineSliceParamsByLayer.Remove(_selectedLayer);
                        _nineSliceParamsByLayer.Remove(_selectedLayer);
                    }
                    ScheduleSaveMergeExportConfig();
                }
                if (_useCustomNineSliceParamsByLayer.TryGetValue(_selectedLayer, out bool _) && _nineSliceParamsByLayer.ContainsKey(_selectedLayer))
                {
                    // HelpBox moved to tooltip
                    var p = _nineSliceParamsByLayer.TryGetValue(_selectedLayer, out var pv)
                        ? pv
                        : ReadGlobalNineSliceDefaultsFromEditorPrefs();
                    EditorGUI.BeginChangeCheck();
                    p.borderInset = Mathf.Max(0, EditorGUILayout.IntField("Border inset (BORDER_INSET)", p.borderInset));
                    p.pixelThreshold = Mathf.Clamp(EditorGUILayout.IntField("Adjacent pixel diff threshold (0–255)", p.pixelThreshold), 0, 255);
                    p.minSameZone = Mathf.Max(1, EditorGUILayout.IntField("Min same-zone run (MIN_SAME_ZONE)", p.minSameZone));
                    p.minCenterCols = Mathf.Max(1, EditorGUILayout.IntField("Max center column shrink", p.minCenterCols));
                    p.minCenterRows = Mathf.Max(1, EditorGUILayout.IntField("Max center row shrink", p.minCenterRows));
                    if (EditorGUI.EndChangeCheck())
                    {
                        RecordRightPanelUndoBeforeChange();
                        _nineSliceParamsByLayer[_selectedLayer] = p;
                        ScheduleSaveMergeExportConfig();
                    }
                }
            }

            GUILayout.Space(4);

            bool participateLocal = !_participateLocalDedupByLayer.TryGetValue(_selectedLayer, out bool pl) || pl;
            bool newParticipateLocal = EditorGUILayout.Toggle(TT("Same-export dedup", "On: this export dedupes against other images in the run. Off: always export a unique file."), participateLocal);
            if (newParticipateLocal != participateLocal)
            {
                RecordRightPanelUndoBeforeChange();
                _participateLocalDedupByLayer[_selectedLayer] = newParticipateLocal;
                ScheduleSaveMergeExportConfig();
            }
            // HelpBox moved to tooltip

            bool participateCommon = !_participateCommonDedupByLayer.TryGetValue(_selectedLayer, out bool pcd) || pcd;
            bool newParticipateCommon = EditorGUILayout.Toggle(TT("Common-directory dedup", "Compare against the common cache images."), participateCommon);
            if (newParticipateCommon != participateCommon)
            {
                RecordRightPanelUndoBeforeChange();
                _participateCommonDedupByLayer[_selectedLayer] = newParticipateCommon;
                ScheduleSaveMergeExportConfig();
            }
            // HelpBox moved to tooltip

            } // showSliceCommonDedup

            SkipSubordinateLayerExportOptions:

            // ── Details block (commented out) ──
            // GUILayout.Space(12);
            // EditorGUILayout.LabelField("Details", EditorStyles.boldLabel);
            // DrawSeparator();
            //
            // EditorGUI.BeginDisabledGroup(true);
            // EditorGUILayout.TextField("Kind", _selectedLayer.Kind.ToString());
            //
            // int left, top, w, h;
            // GetLayerBounds(_selectedLayer, out left, out top, out w, out h);
            // EditorGUILayout.IntField("X", left);
            // EditorGUILayout.IntField("Y", top);
            // EditorGUILayout.IntField("Width", w);
            // EditorGUILayout.IntField("Height", h);
            // EditorGUILayout.Slider("Opacity", _selectedLayer.OpacityFloat, 0f, 1f);
            // EditorGUILayout.TextField("Blend mode", _selectedLayer.BlendMode.ToString());
            //
            // if (_selectedLayer.IsClipped)
            //     EditorGUILayout.LabelField("Clipping mask", "Yes");
            // if (_selectedLayer.HasMask)
            //     EditorGUILayout.LabelField("Layer mask", "Yes");
            //
            // EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndScrollView();
            if (_selectedLayer != null && ShouldPersistPropertyScrollToDict())
                _propertyScrollPosByLayer[_selectedLayer] = _propertyScrollPos;
            GUILayout.EndArea();
        }

        // ─────────────────────── Bottom Panel: Config ───────────────────────

        private void DrawBottomPanel(Rect rect)
        {
            GUILayout.BeginArea(rect);

            DrawSeparatorHorizontal(rect.width);
            GUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(10);

            GUILayout.Label($"PSD: {System.IO.Path.GetFileName(_psdPath)}", EditorStyles.miniLabel, GUILayout.Width(210));

            GUILayout.Space(16);
            // ── TMP batch toggle ──
            // _tmpToggleState: 0 = TMP mode (button says set all Labels to Legacy), 1 = Legacy (button says set all to TMP)
            // Uninitialized: infer from _defaultUseTMP (LoadPsdFromPath sets this; fallback here)
            if (_tmpToggleState < 0)
                _tmpToggleState = _defaultUseTMP ? 0 : 1;
            string tmpBtnLabel = _tmpToggleState == 0 ? "Set all labels to Legacy" : "Set all labels to TMP";
            GUI.backgroundColor = _tmpToggleState == 0 ? new Color(0.9f, 0.75f, 0.4f) : new Color(0.4f, 0.75f, 0.9f);
            if (GUILayout.Button(tmpBtnLabel, GUILayout.Width(160), GUILayout.Height(20)))
            {
                // state 0 (TMP): click sets all type layers to Legacy → state 1; vice versa
                bool setTMP = _tmpToggleState != 0; // state==1 → TMP; state==0 → Legacy
                _tmpToggleState = _tmpToggleState == 0 ? 1 : 0;
                ApplyTextMeshProToAllTextLayers(setTMP);
            }
            GUI.backgroundColor = Color.white;

            GUILayout.Space(8);
            // ── Slice batch toggle ──
            // _sliceToggleState: 0 = Slice mode (button says set all images to raw export), 1 = Raw mode (button says set all images to Slice / nine-slice)
            // Uninitialized: infer from _defaultSliceImage (LoadPsdFromPath sets this; fallback here)
            if (_sliceToggleState < 0)
                _sliceToggleState = _defaultSliceImage ? 0 : 1;
            string sliceBtnLabel = _sliceToggleState == 0 ? "Set all images to raw export" : "Set all images to Slice / nine-slice";
            GUI.backgroundColor = _sliceToggleState == 0 ? new Color(0.9f, 0.75f, 0.4f) : new Color(0.4f, 0.75f, 0.9f);
            if (GUILayout.Button(sliceBtnLabel, GUILayout.Width(185), GUILayout.Height(20)))
            {
                // state 0 (Slice): click sets all slice-eligible layers to raw export → state 1; vice versa
                bool setSlice = _sliceToggleState != 0; // state==1 → Slice; state==0 → raw export
                _sliceToggleState = _sliceToggleState == 0 ? 1 : 0;
                ApplySliceImageToAllEligibleLayers(setSlice);
            }
            GUI.backgroundColor = Color.white;

            GUILayout.Space(8);
            bool newAutoImgName = EditorGUILayout.ToggleLeft("Auto image naming", _exportAutoImageNaming, GUILayout.Width(130));
            if (newAutoImgName != _exportAutoImageNaming)
            {
                _exportAutoImageNaming = newAutoImgName;
                EditorPrefs.SetBool(PrefExportAutoImageNaming, _exportAutoImageNaming);
            }

            GUILayout.Space(8);
            bool newCompareNameDiff = EditorGUILayout.ToggleLeft("Compare name differences", _exportCompareNameDiff, GUILayout.Width(170));
            if (newCompareNameDiff != _exportCompareNameDiff)
            {
                _exportCompareNameDiff = newCompareNameDiff;
                EditorPrefs.SetBool(PrefExportCompareNameDiff, _exportCompareNameDiff);
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Reload PSD", GUILayout.Width(90), GUILayout.Height(32)))
            {
                ReloadCurrentPsd();
            }

            GUILayout.Space(8);

            EditorGUI.BeginDisabledGroup(_psdLayerNameSnapshot == null || _psdLayerNameSnapshot.Count == 0);
            if (GUILayout.Button("Restore PSD Names", GUILayout.Width(140), GUILayout.Height(32)))
            {
                RestoreAllLayerNamesFromPsd();
            }
            EditorGUI.EndDisabledGroup();

            GUILayout.Space(8);

            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.4f);
            if (GUILayout.Button("Export (slices + Prefab)", GUILayout.Width(200), GUILayout.Height(32)))
            {
                if (!string.IsNullOrEmpty(_psdPath))
                {
                    string pathToExport = _psdPath;
                    var win = this;
                    EditorApplication.delayCall += () =>
                    {
                        win.FlushExportConfig();
                        string imageRoot = SanitizeExportAssetsRoot(win._exportImageAssetsFolderRelative);
                        string prefabRoot = SanitizeExportAssetsRoot(win._exportPrefabAssetsFolderRelative);
                        win._exportImageAssetsFolderRelative = imageRoot;
                        win._exportPrefabAssetsFolderRelative = prefabRoot;
                        EditorPrefs.SetString(PrefExportImageFolder, imageRoot);
                        EditorPrefs.SetString(PrefExportPrefabFolder, prefabRoot);
                        // Delete the image export folder before export so stale slices do not carry over
                        if (win._clearExportFolderBeforeExport)
                        {
                            string psdBaseName = Path.GetFileNameWithoutExtension(pathToExport);
                            string exportFolderAssetPath = imageRoot + "/" + psdBaseName;
                            // AssetDatabase.DeleteAsset removes the folder and its .meta from the project
                            if (!AssetDatabase.DeleteAsset(exportFolderAssetPath))
                            {
                                // Not tracked by AssetDatabase yet; delete directly from disk
                                string diskRootPath = imageRoot.Equals("Assets", System.StringComparison.OrdinalIgnoreCase)
                                    ? Application.dataPath
                                    : Path.Combine(Application.dataPath, imageRoot.Substring("Assets/".Length).Replace('/', Path.DirectorySeparatorChar));
                                string diskExportFolder = Path.Combine(diskRootPath, psdBaseName);
                                if (Directory.Exists(diskExportFolder))
                                    Directory.Delete(diskExportFolder, true);
                            }
                        }
                        PSDAutoPrefab.CompareNameDiff = win._exportCompareNameDiff;
                        PSDAutoPrefab.GenerateFromPSD(pathToExport, imageRoot, prefabRoot, win._exportAutoImageNaming);
                        if (win._autoNavigateAfterExport)
                        {
                            string navigatePath = PSDAutoPrefab.LastGeneratedPrefabAssetPath;
                            if (!string.IsNullOrEmpty(navigatePath))
                            {
                                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(navigatePath);
                                if (asset != null)
                                {
                                    Selection.activeObject = asset;
                                    EditorUtility.FocusProjectWindow();
                                    EditorGUIUtility.PingObject(asset);
                                }
                            }
                        }
                    };
                }
            }
            GUI.backgroundColor = Color.white;

            GUILayout.Space(6);

            GUI.backgroundColor = new Color(0.3f, 0.65f, 1.0f);
            if (GUILayout.Button("Export textures", GUILayout.Width(120), GUILayout.Height(32)))
            {
                if (!string.IsNullOrEmpty(_psdPath))
                {
                    string pathToExport = _psdPath;
                    var win = this;
                    EditorApplication.delayCall += () =>
                    {
                        win.FlushExportConfig();
                        string imageRoot = SanitizeExportAssetsRoot(win._exportImageAssetsFolderRelative);
                        win._exportImageAssetsFolderRelative = imageRoot;
                        EditorPrefs.SetString(PrefExportImageFolder, imageRoot);
                        // Delete the image export folder before export so stale slices do not carry over
                        if (win._clearExportFolderBeforeExport)
                        {
                            string psdBaseName = Path.GetFileNameWithoutExtension(pathToExport);
                            string exportFolderAssetPath = imageRoot + "/" + psdBaseName;
                            // AssetDatabase.DeleteAsset removes the folder and its .meta from the project
                            if (!AssetDatabase.DeleteAsset(exportFolderAssetPath))
                            {
                                // Not tracked by AssetDatabase yet; delete directly from disk
                                string diskRootPath = imageRoot.Equals("Assets", System.StringComparison.OrdinalIgnoreCase)
                                    ? Application.dataPath
                                    : Path.Combine(Application.dataPath, imageRoot.Substring("Assets/".Length).Replace('/', Path.DirectorySeparatorChar));
                                string diskExportFolder = Path.Combine(diskRootPath, psdBaseName);
                                if (Directory.Exists(diskExportFolder))
                                    Directory.Delete(diskExportFolder, true);
                            }
                        }
                        PSDAutoPrefab.ExportTexturesFromPSD(pathToExport, imageRoot, win._exportAutoImageNaming);
                        if (win._autoNavigateAfterExport)
                        {
                            string folderPath = PSDAutoPrefab.LastExportedImageAssetsFolder;
                            if (!string.IsNullOrEmpty(folderPath))
                            {
                                var folderAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(folderPath);
                                if (folderAsset != null)
                                {
                                    Selection.activeObject = folderAsset;
                                    EditorUtility.FocusProjectWindow();
                                    EditorGUIUtility.PingObject(folderAsset);
                                }
                            }
                        }
                    };
                }
            }
            GUI.backgroundColor = Color.white;

            GUILayout.Space(10);
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(4);
            DrawExportAssetsFolderRow("Image export folder", ref _exportImageAssetsFolderRelative, PrefExportImageFolder, 120);
            GUILayout.Space(2);
            DrawExportAssetsFolderRow("Prefab export folder", ref _exportPrefabAssetsFolderRelative, PrefExportPrefabFolder, 120);

            GUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(10);
            bool newAutoNav = EditorGUILayout.ToggleLeft("Navigate Project to output after export", _autoNavigateAfterExport);
            if (newAutoNav != _autoNavigateAfterExport)
            {
                _autoNavigateAfterExport = newAutoNav;
                EditorPrefs.SetBool(PrefAutoNavigateAfterExport, _autoNavigateAfterExport);
            }

            GUILayout.Space(4);
            bool newDetectCommonDir2 = EditorGUILayout.ToggleLeft("Detect larger image in common dirs", _detectCommonDirLargerImage);
            if (newDetectCommonDir2 != _detectCommonDirLargerImage)
            {
                _detectCommonDirLargerImage = newDetectCommonDir2;
                EditorPrefs.SetBool(PrefDetectCommonDirLargerImage, _detectCommonDirLargerImage);
            }

            GUILayout.Space(4);
            bool newClearExportFolder = EditorGUILayout.ToggleLeft("Clear image folder before export", _clearExportFolderBeforeExport);
            if (newClearExportFolder != _clearExportFolderBeforeExport)
            {
                _clearExportFolderBeforeExport = newClearExportFolder;
                EditorPrefs.SetBool(PrefClearExportFolderBeforeExport, _clearExportFolderBeforeExport);
            }

            EditorGUILayout.EndHorizontal();

            GUILayout.EndArea();
        }

        /// <param name="labelWidth">Label width in pixels.</param>
        private void DrawExportAssetsFolderRow(string label, ref string folderRelative, string prefKey, float labelWidth)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(10);
            EditorGUILayout.LabelField(label, GUILayout.Width(labelWidth));
            EditorGUI.BeginChangeCheck();
            string folderField = EditorGUILayout.TextField(folderRelative);
            if (EditorGUI.EndChangeCheck() && folderField != folderRelative)
            {
                folderRelative = string.IsNullOrWhiteSpace(folderField) ? "Assets" : folderField.Trim();
                EditorPrefs.SetString(prefKey, folderRelative);
            }
            if (GUILayout.Button("Browse", GUILayout.Width(54)))
            {
                string picked = EditorUtility.OpenFolderPanel($"Choose {label} (inside project Assets)", Application.dataPath, "");
                if (!string.IsNullOrEmpty(picked))
                {
                    string dataNorm = Application.dataPath.Replace('\\', '/');
                    string pickNorm = picked.Replace('\\', '/');
                    if (pickNorm.StartsWith(dataNorm, System.StringComparison.OrdinalIgnoreCase))
                    {
                        string tail = pickNorm.Substring(dataNorm.Length).TrimStart('/');
                        folderRelative = string.IsNullOrEmpty(tail) ? "Assets" : "Assets/" + tail;
                        EditorPrefs.SetString(prefKey, folderRelative);
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Invalid path", "Select Assets or a subfolder of the current Unity project.", "OK");
                    }
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private static string SanitizeExportAssetsRoot(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "Assets";
            input = input.Replace('\\', '/').Trim().TrimEnd('/');
            if (!input.StartsWith("Assets", System.StringComparison.OrdinalIgnoreCase))
                input = "Assets/" + input.TrimStart('/');
            return input;
        }

        private bool GetEffectiveSliceImage(Layer layer)
        {
            if (layer == null)
                return _defaultSliceImage;
            return _sliceImageByLayer.TryGetValue(layer, out bool sliceImage) ? sliceImage : _defaultSliceImage;
        }

        private bool GetEffectiveUseTextMeshPro(Layer layer)
        {
            if (layer == null)
                return _defaultUseTMP;
            if (layer.Kind != LayerKind.Type)
                return true;
            return _useTextMeshProByLayer.TryGetValue(layer, out bool useTmp) ? useTmp : _defaultUseTMP;
        }

        /// <summary>Whether the layer should expose Slice / nine-slice and dedup options.</summary>
        private bool ShouldShowSliceCommonDedupOptions(Layer layer)
        {
            if (layer == null)
                return false;
            if (_useExternalPrefabByLayer.TryGetValue(layer, out bool useExternalPrefab) && useExternalPrefab)
                return false;

            bool isMergeExportGroup = layer.IsGroup
                && _mergeExportByLayer.TryGetValue(layer, out bool mergeExport) && mergeExport;
            return layer.Kind != LayerKind.Type && (!layer.IsGroup || isMergeExportGroup);
        }

        // ─────────────────────── Drawing Helpers ───────────────────────

        private void DrawPanelBorders(Rect left, Rect center, Rect right, Rect bottom)
        {
            Color borderColor = new Color(0.15f, 0.15f, 0.15f, 0.6f);
            Texture2D borderTex = MakeSolidTexture(borderColor);

            GUI.DrawTexture(new Rect(left.xMax, 0, 1, left.height), borderTex);
            GUI.DrawTexture(new Rect(right.x, 0, 1, right.height), borderTex);

            DestroyImmediate(borderTex);
        }

        private void DrawSeparator()
        {
            GUILayout.Space(2);
            Rect r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(0.3f, 0.3f, 0.3f, 0.5f));
            GUILayout.Space(2);
        }

        private void DrawSeparatorHorizontal(float width)
        {
            Rect r = new Rect(0, 0, width, 1);
            EditorGUI.DrawRect(r, new Color(0.15f, 0.15f, 0.15f, 0.6f));
        }

        private static Texture2D MakeSolidTexture(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        private static void DestroyTexture(ref Texture2D tex)
        {
            if (tex != null)
            {
                DestroyImmediate(tex);
                tex = null;
            }
        }

        /// <summary>Matches <see cref="PSDNineSlicePrefs"/> defaults as placeholder when JSON lacks custom values.</summary>
        private static NineSliceLayerParams GetDefaultNineSliceLayerParamsForSave()
        {
            return new NineSliceLayerParams
            {
                borderInset = PSDNineSlicePrefs.DefaultBorderInset,
                pixelThreshold = PSDNineSlicePrefs.DefaultPixelThreshold,
                minCenterCols = PSDNineSlicePrefs.DefaultMinCenterCols,
                minCenterRows = PSDNineSlicePrefs.DefaultMinCenterRows,
                minSameZone = PSDNineSlicePrefs.DefaultMinSameZone
            };
        }

        /// <summary>Read global nine-slice params from EditorPrefs for first-time custom layer values.</summary>
        private static NineSliceLayerParams ReadGlobalNineSliceDefaultsFromEditorPrefs()
        {
            return new NineSliceLayerParams
            {
                borderInset = EditorPrefs.GetInt(PSDNineSlicePrefs.KeyBorderInset, PSDNineSlicePrefs.DefaultBorderInset),
                pixelThreshold = EditorPrefs.GetInt(PSDNineSlicePrefs.KeyPixelThreshold, PSDNineSlicePrefs.DefaultPixelThreshold),
                minCenterCols = EditorPrefs.GetInt(PSDNineSlicePrefs.KeyMinCenterCols, PSDNineSlicePrefs.DefaultMinCenterCols),
                minCenterRows = EditorPrefs.GetInt(PSDNineSlicePrefs.KeyMinCenterRows, PSDNineSlicePrefs.DefaultMinCenterRows),
                minSameZone = EditorPrefs.GetInt(PSDNineSlicePrefs.KeyMinSameZone, PSDNineSlicePrefs.DefaultMinSameZone)
            };
        }
    }

    /// <summary>Per-layer nine-slice detection params in the PSD editor (same meaning as Tools/PSD/Nine-slice settings…).</summary>
    [System.Serializable]
    public struct NineSliceLayerParams
    {
        public int borderInset;
        public int pixelThreshold;
        public int minCenterCols;
        public int minCenterRows;
        public int minSameZone;
    }

    /// <summary>Full config entry for one layer in _export_config.json.</summary>
    [System.Serializable]
    public class LayerConfigEntry
    {
        /// <summary>Unique layer id (from PSD LayerRecord).</summary>
        public int id;
        /// <summary>Layer name from editor; applied first on re-import.</summary>
        public string name;
        /// <summary>Export this node: false skips slice and Prefab node (default true).</summary>
        public bool exported = true;
        /// <summary>Visibility in exported Prefab (GameObject.activeSelf); only when exported is true.</summary>
        public bool visible;
        /// <summary>Merge for export: true merges this node and children into one image.</summary>
        public bool merge;
        /// <summary>Also export a standalone Prefab for this node.</summary>
        public bool exportPrefab;
        /// <summary>Use external Prefab (no slice; generation swaps in chosen prefab).</summary>
        public bool useExternalPrefab;
        /// <summary>Referenced prefab asset path (Assets/xxx.prefab).</summary>
        public string externalPrefabPath;
        /// <summary>When referencing Prefab, reuse this node’s position (default true).</summary>
        public bool reusePosition;
        /// <summary>When referencing Prefab, reuse this node’s size (default false).</summary>
        public bool reuseSize;
        /// <summary>Same-export dedup vs other outputs in this run (default true).</summary>
        public bool participateLocalDedup;
        public bool participateCommonDedup;
        /// <summary>Slice / nine-slice: true runs nine-slice on export, false exports raw image (default true).</summary>
        public bool sliceImage;
        /// <summary>Primary dedup node: virtual rep uses primaries (multiple → pick largest resolution); pixels and params from that node.</summary>
        public bool primaryDedupNode;
        /// <summary>Use nine-slice params from this entry instead of global EditorPrefs when slicing.</summary>
        public bool useCustomNineSliceParams;
        /// <summary>Custom nine-slice: border inset.</summary>
        public int nineSliceBorderInset;
        /// <summary>Custom nine-slice: adjacent pixel diff threshold (0–255).</summary>
        public int nineSlicePixelThreshold;
        /// <summary>Custom nine-slice: max center column shrink.</summary>
        public int nineSliceMinCenterCols;
        /// <summary>Custom nine-slice: max center row shrink.</summary>
        public int nineSliceMinCenterRows;
        /// <summary>Custom nine-slice: minimum same-zone run length.</summary>
        public int nineSliceMinSameZone;
        /// <summary>Use custom image: skips slice, nine-slice, dedup, and prefab replacement when true.</summary>
        public bool useCustomImage;
        /// <summary>Custom image asset path (e.g. Assets/xxx.png); only when useCustomImage is true.</summary>
        public string customImagePath;
        /// <summary>UI component on export: None (default), Button, ScrollBar, etc.</summary>
        public string uiComponentType = "None";
        /// <summary>Scrollbar.Direction string: left_to_right, right_to_left, bottom_to_top, top_to_bottom.</summary>
        public string scrollBarDirection = "left_to_right";
        /// <summary>Child name for Scrollbar.handleRect (matches exported child GameObject name).</summary>
        public string scrollBarHandleChildName = "";
        /// <summary>Slider.Direction string: left_to_right, right_to_left, bottom_to_top, top_to_bottom.</summary>
        public string sliderDirection = "left_to_right";
        /// <summary>Child name for Slider.fillRect.</summary>
        public string sliderFillRectChildName = "";
        /// <summary>Child name for Slider.handleRect.</summary>
        public string sliderHandleRectChildName = "";
        /// <summary>ScrollRect existing Viewport child name.</summary>
        public string scrollRectViewportChildName = "";
        /// <summary>ScrollRect Content child; found by name and assigned to ScrollRect.content.</summary>
        public string scrollRectContentChildName = "";
        /// <summary>ScrollRect allows horizontal scrolling.</summary>
        public bool scrollRectHorizontal = true;
        /// <summary>ScrollRect allows vertical scrolling.</summary>
        public bool scrollRectVertical = true;
        /// <summary>ScrollRect horizontal Scrollbar child name (object must have Scrollbar).</summary>
        public string scrollRectHorizontalScrollbarChildName = "";
        /// <summary>ScrollRect vertical Scrollbar child name (object must have Scrollbar).</summary>
        public string scrollRectVerticalScrollbarChildName = "";
        /// <summary>Child name for Toggle.graphic (matches exported child GameObject name).</summary>
        public string toggleGraphicChildName = "";
        /// <summary>InputField Text / TMP textComponent child name.</summary>
        public string inputFieldTextChildName = "";
        /// <summary>InputField Placeholder child name.</summary>
        public string inputFieldPlaceholderChildName = "";
        /// <summary>InputField(TMP) textViewport child name (RectTransform).</summary>
        public string inputFieldTextViewportChildName = "";
        /// <summary>Dropdown Template child name (RectTransform root).</summary>
        public string dropdownTemplateChildName = "";
        /// <summary>Dropdown Caption Text child (Legacy: UI Text; TMP: TextMeshPro).</summary>
        public string dropdownCaptionTextChildName = "";
        /// <summary>Dropdown Item Text child (Legacy: UI Text; TMP: TextMeshPro).</summary>
        public string dropdownItemTextChildName = "";
        /// <summary>Type layers export with TextMeshPro; ignored for non-type layers but still serialized.</summary>
        public bool useTextMeshPro;
    }

    [System.Serializable]
    public class MergeExportConfigData
    {
        public LayerConfigEntry[] layers;
    }

    /// <summary>Common directories config JSON root; file at Assets/PsdToUnityUI/EditorConfig/PSD_CommonDirectories.json.</summary>
    [System.Serializable]
    public class CommonDirectoriesData
    {
        public string[] paths;
    }
} // namespace PsdTools
