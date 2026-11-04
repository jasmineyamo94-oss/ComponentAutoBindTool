using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace PsdTools
{
    /// <summary>Node structure for dataset A/B during export (both from real Prefabs, including component JSON).</summary>
    public struct ExportDiffNode
    {
        public int treeNodeId;       // Unique ID for building the tree (positive for PSD layers, negative for non-PSD nodes)
        public int psdLayerId;       // Actual PSD layer ID (-1 if non-PSD node)
        public bool isPsdNode;       // Whether there is a corresponding PSD layer
        public string name;
        public int parentTreeNodeId; // Points to parent treeNodeId for 100% tree structure restoration
        public int parentPsdLayerId; // Parent PSD layer ID, only used to compare "if parent node changed"
        public string parentName;
        public string componentsJson;
        public int siblingIndex;
        public bool isPrefab;        // Whether it is a prefab instance
    }

    /// <summary>
    /// Export difference comparison window: compares existing Prefab (A) with the temporary Prefab (B) to be generated from the current PSD.
    /// User clicks "Apply" after selection to perform incremental update, then deletes the temporary Prefab.
    /// Selection is not persisted.
    /// </summary>
    public sealed class PsdExportDiffWindow : EditorWindow
    {
        // ───────────────────────── Internal Tree Node ─────────────────────────

        private sealed class TreeNode
        {
            public int treeNodeId;
            public int psdLayerId;
            public bool isPsdNode;
            public TreeNode parent;
            public string name;
            public bool isOnlyInA;
            public bool isOnlyInB;
            public bool parentChanged;
            public bool componentChanged;
            public NodeStatus status = NodeStatus.Unchanged;
            public List<TreeNode> children = new List<TreeNode>();
            public bool expanded = true;
            public string aComponentsJson;
            public string bComponentsJson;
            public string aName;
            public string bName;
            public int siblingIndex;
            /// <summary>B-side corresponding siblingIndex; used for SetSiblingIndex when applying B structure.</summary>
            public int bSiblingIndex = -1;
            public bool isPrefab;        // Whether it is a prefab
        }

        private enum NodeStatus
        {
            Unchanged,
            OnlyInA,
            OnlyInB,
            ParentChanged,
            ComponentChanged,
        }

        // ───────────────────────── User Selection ─────────────────────────

        private sealed class NodeAction
        {
            /// <summary>OnlyInA nodes: Whether to keep (true=keep user-added node, false=delete). Default false (delete).</summary>
            public bool keepInA = false;
            /// <summary>OnlyInB root nodes: Whether to add this new subtree from B. Default true.</summary>
            public bool addInB = true;
            /// <summary>ParentChanged nodes: Whether to apply PSD structure.</summary>
            public bool applyStructure;
            /// <summary>ComponentChanged nodes: Use A's components (false) or B's components (true). Default is false to keep user modifications.</summary>
            public bool useB;
        }

        // ───────────────────────── Status ─────────────────────────

        private List<TreeNode> _rootsA = new List<TreeNode>();
        private List<TreeNode> _rootsB = new List<TreeNode>();
        private Vector2 _scrollA;
        private Vector2 _scrollB;
        private Vector2 _detailScroll;
        private string _psdName;

        private TreeNode _selectedNode;

        private Dictionary<TreeNode, NodeAction> _nodeActions = new Dictionary<TreeNode, NodeAction>();
        private Dictionary<int, TreeNode> _bNodesByPsdLayerId = new Dictionary<int, TreeNode>();

        // Callback: Executed after user clicks "Apply"
        private System.Action<ExportDiffDecisions> _onApply;
        // Callback: Executed after user clicks "Fresh Export"
        private System.Action _onFreshExport;
        // Callback: Executed after user closes window (Cancel)
        private System.Action _onCancel;

        private static readonly Color ColOnlyA = new Color(1f, 0.35f, 0.35f, 0.5f);
        private static readonly Color ColOnlyB = new Color(0.35f, 0.85f, 0.35f, 0.5f);
        private static readonly Color ColParentChanged = new Color(1f, 0.85f, 0.2f, 0.5f);
        private static readonly Color ColCompChanged = new Color(1f, 0.6f, 0.15f, 0.5f);

        private bool _applied;
        private bool _compareNameDiff = true;

        // ───────────────────────── Public Entry ─────────────────────────

        /// <summary>
        /// Open the export difference comparison window.
        /// </summary>
        /// <param name="dataA">Node data of existing Prefab</param>
        /// <param name="dataB">Node data of temporary Prefab (to be generated from current PSD)</param>
        /// <param name="psdName">PSD name</param>
        /// <param name="onApply">Callback after user confirmation, passes selection results</param>
        /// <param name="onFreshExport">User chose fresh overwrite export</param>
        /// <param name="onCancel">User canceled (closed window)</param>
        public static PsdExportDiffWindow Show(
            List<ExportDiffNode> dataA,
            List<ExportDiffNode> dataB,
            string psdName,
            bool compareNameDiff,
            System.Action<ExportDiffDecisions> onApply,
            System.Action onFreshExport,
            System.Action onCancel)
        {
            var win = GetWindow<PsdExportDiffWindow>(true, $"Export Diff — {psdName}", true);
            win.minSize = new Vector2(900, 520);
            win._psdName = psdName;
            win._compareNameDiff = compareNameDiff;
            win._onApply = onApply;
            win._onFreshExport = onFreshExport;
            win._onCancel = onCancel;
            win._applied = false;
            win.Build(dataA, dataB, compareNameDiff);
            win.Repaint();
            return win;
        }

        // ───────────────────────── Tree Construction ─────────────────────────

        private void Build(List<ExportDiffNode> dataA, List<ExportDiffNode> dataB, bool compareNameDiff)
        {
            _rootsA.Clear();
            _rootsB.Clear();
            _selectedNode = null;
            _nodeActions.Clear();
            _bNodesByPsdLayerId.Clear();

            // Only establish shortcut mapping for valid PSD layers for diff calculation (Structural nodes dont participate in Diff)
            var aPsdIds = new Dictionary<int, ExportDiffNode>();
            foreach (var a in dataA)
                if (a.isPsdNode)
                    aPsdIds[a.psdLayerId] = a;

            var bPsdIds = new Dictionary<int, ExportDiffNode>();
            foreach (var b in dataB)
                if (b.isPsdNode)
                    bPsdIds[b.psdLayerId] = b;

            // ── Dataset A Tree ──
            var aNodesMap = new Dictionary<int, TreeNode>(dataA.Count);
            foreach (var a in dataA)
            {
                bool onlyInA = false;
                bool pChanged = false;
                bool cChanged = false;

                // Only PSD nodes participate in Diff calculation
                if (a.isPsdNode)
                {
                    onlyInA = !bPsdIds.ContainsKey(a.psdLayerId);
                    if (!onlyInA && bPsdIds.TryGetValue(a.psdLayerId, out var bMatch))
                    {
                        // Determine parentChanged: use old parentLayerId for logic judgment
                        pChanged = bMatch.parentPsdLayerId != a.parentPsdLayerId;
                        bool nameChanged = compareNameDiff && (a.name != bMatch.name);
                        cChanged = ComponentsJsonChanged(a.componentsJson, bMatch.componentsJson) || nameChanged || a.isPrefab != bMatch.isPrefab;
                    }
                }

                var node = new TreeNode
                {
                    treeNodeId = a.treeNodeId,
                    psdLayerId = a.psdLayerId,
                    isPsdNode = a.isPsdNode,
                    name = a.name,
                    isOnlyInA = onlyInA,
                    parentChanged = pChanged,
                    componentChanged = cChanged,
                    aComponentsJson = a.componentsJson,
                    bComponentsJson = onlyInA || !a.isPsdNode ? null : bPsdIds[a.psdLayerId].componentsJson,
                    aName = a.name,
                    bName = onlyInA || !a.isPsdNode ? null : bPsdIds[a.psdLayerId].name,
                    siblingIndex = a.siblingIndex,
                    bSiblingIndex = onlyInA || !a.isPsdNode ? -1 : bPsdIds[a.psdLayerId].siblingIndex,
                    isPrefab = a.isPrefab,
                };

                if (a.isPsdNode)
                {
                    if (onlyInA) node.status = NodeStatus.OnlyInA;
                    else if (pChanged) node.status = NodeStatus.ParentChanged;
                    else if (cChanged) node.status = NodeStatus.ComponentChanged;

                    if (onlyInA || pChanged || cChanged)
                        _nodeActions[node] = new NodeAction();
                }

                aNodesMap[a.treeNodeId] = node;
            }
            _rootsA = BuildRoots(aNodesMap, dataA.ConvertAll(a => (a.treeNodeId, a.parentTreeNodeId)));
            _rootsA.Sort((x, y) => x.siblingIndex.CompareTo(y.siblingIndex));
            SortChildrenBySiblingIndex(_rootsA);

            // ── Dataset B Tree ──
            var bNodesMap = new Dictionary<int, TreeNode>(dataB.Count);
            foreach (var b in dataB)
            {
                bool onlyInB = false;
                if (b.isPsdNode)
                {
                    onlyInB = !aPsdIds.ContainsKey(b.psdLayerId);
                }

                var node = new TreeNode
                {
                    treeNodeId = b.treeNodeId,
                    psdLayerId = b.psdLayerId,
                    isPsdNode = b.isPsdNode,
                    name = b.name,
                    isOnlyInB = onlyInB,
                    bComponentsJson = b.componentsJson,
                    aComponentsJson = onlyInB || !b.isPsdNode ? null : aPsdIds[b.psdLayerId].componentsJson,
                    bName = b.name,
                    aName = onlyInB || !b.isPsdNode ? null : aPsdIds[b.psdLayerId].name,
                    siblingIndex = b.siblingIndex,
                    isPrefab = b.isPrefab,
                };

                if (b.isPsdNode)
                {
                    if (onlyInB)
                    {
                        node.status = NodeStatus.OnlyInB;
                        _nodeActions[node] = new NodeAction();
                    }
                    else
                    {
                        var aNode = aPsdIds[b.psdLayerId];
                        if (aNode.parentPsdLayerId != b.parentPsdLayerId)
                            node.status = NodeStatus.ParentChanged;
                        else
                        {
                            bool nameChanged = compareNameDiff && (aNode.name != b.name);
                            if (ComponentsJsonChanged(aNode.componentsJson, b.componentsJson) || nameChanged || aNode.isPrefab != b.isPrefab)
                                node.status = NodeStatus.ComponentChanged;
                        }
                    }

                    _bNodesByPsdLayerId[b.psdLayerId] = node;
                }

                bNodesMap[b.treeNodeId] = node;
            }
            _rootsB = BuildRoots(bNodesMap, dataB.ConvertAll(b => (b.treeNodeId, b.parentTreeNodeId)));

            AutoLocateFirstDifference();
        }

        private void AutoLocateFirstDifference()
        {
            TreeNode firstDiffNodeA = null;
            foreach (var root in _rootsA)
            {
                if (ExpandParentsToDifference(root, out var childDiff))
                {
                    if (firstDiffNodeA == null) firstDiffNodeA = childDiff;
                }
            }

            TreeNode firstDiffNodeB = null;
            foreach (var root in _rootsB)
            {
                if (ExpandParentsToDifference(root, out var childDiff))
                {
                    if (firstDiffNodeB == null) firstDiffNodeB = childDiff;
                }
            }

            _selectedNode = firstDiffNodeA ?? firstDiffNodeB;

            if (firstDiffNodeA != null)
            {
                int indexA = 0;
                if (GetVisibleNodeIndex(firstDiffNodeA, _rootsA, ref indexA))
                {
                    _scrollA.y = Mathf.Max(0, indexA * EditorGUIUtility.singleLineHeight - 100f);
                }
            }

            if (firstDiffNodeB != null)
            {
                int indexB = 0;
                if (GetVisibleNodeIndex(firstDiffNodeB, _rootsB, ref indexB))
                {
                    _scrollB.y = Mathf.Max(0, indexB * EditorGUIUtility.singleLineHeight - 100f);
                }
            }
        }

        private bool ExpandParentsToDifference(TreeNode node, out TreeNode firstDiff)
        {
            firstDiff = null;
            bool foundInSelfOrChildren = false;

            if (node.status != NodeStatus.Unchanged)
            {
                firstDiff = node;
                foundInSelfOrChildren = true;
            }

            foreach (var child in node.children)
            {
                if (ExpandParentsToDifference(child, out var childDiff))
                {
                    if (firstDiff == null)
                        firstDiff = childDiff;
                    foundInSelfOrChildren = true;
                }
            }

            if (foundInSelfOrChildren)
                node.expanded = true;
            else
                node.expanded = false;

            return foundInSelfOrChildren;
        }

        private bool GetVisibleNodeIndex(TreeNode target, List<TreeNode> currentNodes, ref int currentIndex)
        {
            foreach (var node in currentNodes)
            {
                if (node == target) return true;
                currentIndex++;

                if (node.expanded && node.children.Count > 0)
                {
                    if (GetVisibleNodeIndex(target, node.children, ref currentIndex))
                        return true;
                }
            }
            return false;
        }

        private static List<TreeNode> BuildRoots(
            Dictionary<int, TreeNode> nodesMap,
            List<(int treeNodeId, int parentTreeNodeId)> relations)
        {
            var roots = new List<TreeNode>();
            foreach (var (tid, pid) in relations)
            {
                if (!nodesMap.TryGetValue(tid, out var node)) continue;
                if (!nodesMap.TryGetValue(pid, out var parent))
                {
                    node.parent = null;
                    roots.Add(node);
                }
                else
                {
                    node.parent = parent;
                    parent.children.Add(node);
                }
            }
            return roots;
        }

        private static void SortChildrenBySiblingIndex(List<TreeNode> roots)
        {
            foreach (var r in roots)
                SortChildrenRecursive(r);
        }

        private static void SortChildrenRecursive(TreeNode node)
        {
            node.children.Sort((x, y) => x.siblingIndex.CompareTo(y.siblingIndex));
            foreach (var child in node.children)
                SortChildrenRecursive(child);
        }

        // ───────────────────────── GUI ─────────────────────────

        private void OnGUI()
        {
            DrawToolbar();

            float detailH = _selectedNode != null ? 250f : 0f;
            float bottomBarH = 60f; // Increased a bit for larger buttons
            float treeH = position.height - EditorStyles.toolbar.fixedHeight - detailH - bottomBarH - 12f;

            EditorGUILayout.BeginHorizontal();

            // --- Left side: Dataset A (Existing Prefab) ---
            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.5f - 2f));
            DrawPanelHeader("Existing Prefab (A)");
            _scrollA = EditorGUILayout.BeginScrollView(_scrollA, GUILayout.Height(treeH));
            foreach (var root in _rootsA)
                DrawNodeA(root, 0);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            GUILayout.Space(4);

            // --- Right side: Dataset B (PSD Export results) ---
            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.5f - 2f));
            DrawPanelHeader("PSD Export Results (B)");
            _scrollB = EditorGUILayout.BeginScrollView(_scrollB, GUILayout.Height(treeH));
            foreach (var root in _rootsB)
                DrawNodeB(root, 0, false);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            if (_selectedNode != null)
            {
                // Fixed detail panel height to prevent bottom bar being squeezed out
                EditorGUILayout.BeginVertical(GUILayout.Height(detailH));
                DrawDetailPanel(_selectedNode);
                EditorGUILayout.EndVertical();
            }
            else
            {
                // If no node selected, still take space or let TreeView fill (current logic treeH is fixed)
                GUILayout.Space(detailH);
            }

            DrawBottomBar();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label($"PSD: {_psdName}", EditorStyles.boldLabel);
            if (GUILayout.Button("Expand All", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                SetAllExpanded(_rootsA, true);
                SetAllExpanded(_rootsB, true);
            }
            if (GUILayout.Button("Collapse All", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                SetAllExpanded(_rootsA, false);
                SetAllExpanded(_rootsB, false);
            }
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawLegend(Color col, string label)
        {
            var saved = GUI.backgroundColor;
            GUI.backgroundColor = col + new Color(0, 0, 0, 0.5f);
            GUILayout.Box(label, EditorStyles.toolbarButton);
            GUI.backgroundColor = saved;
        }

        private static void DrawPanelHeader(string title)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        // ── Dataset A Node Drawing ──

        private void DrawNodeA(TreeNode node, int depth)
        {
            bool hasChildren = node.children.Count > 0;
            float rowH = EditorGUIUtility.singleLineHeight;

            EditorGUILayout.BeginHorizontal(GUILayout.Height(rowH));
            GUILayout.Space(depth * 14f);

            Color rowColor = GetStatusColor(node.status);
            if (rowColor.a > 0)
            {
                var r = GUILayoutUtility.GetLastRect();
                r.x = 0;
                r.width = position.width * 0.5f;
                r.height = rowH;
                EditorGUI.DrawRect(r, rowColor);
            }

            if (hasChildren)
            {
                Rect foldRect = GUILayoutUtility.GetRect(12f, rowH, GUILayout.Width(12));
                node.expanded = EditorGUI.Foldout(foldRect, node.expanded, "");
            }
            else
                GUILayout.Space(16);

            string label = node.isPsdNode ? $"[{node.psdLayerId}] {node.name}" : $"[ - ] {node.name}";
            bool wasSelected = (_selectedNode == node);
            GUIStyle btnStyle = new GUIStyle(wasSelected ? EditorStyles.boldLabel : EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft
            };
            if (!node.isPsdNode)
            {
                // Structural node, make the label less prominent
                var oldCol = GUI.contentColor;
                GUI.contentColor = new Color(0.6f, 0.6f, 0.6f);
                if (GUILayout.Button(label, btnStyle, GUILayout.Height(rowH)))
                    _selectedNode = wasSelected ? null : node;
                GUI.contentColor = oldCol;
            }
            else
            {
                if (GUILayout.Button(label, btnStyle, GUILayout.Height(rowH)))
                    _selectedNode = wasSelected ? null : node;
            }

            // Action Options
            if (_nodeActions.TryGetValue(node, out var action))
            {
                if (node.isOnlyInA)
                {
                    bool newKeep = GUILayout.Toggle(action.keepInA, "Keep",
                        EditorStyles.miniButton, GUILayout.Height(rowH), GUILayout.Width(46));
                    if (newKeep != action.keepInA) 
                    { 
                        action.keepInA = newKeep; 
                        SetKeepInAForChildren(node, newKeep);
                        Repaint(); 
                    }
                }
                else
                {
                    if (node.parentChanged)
                    {
                        bool structureBlocked = IsStructureApplyBlocked(node.psdLayerId);
                        if (structureBlocked && action.applyStructure)
                            action.applyStructure = false;

                        using (new EditorGUI.DisabledScope(structureBlocked))
                        {
                            bool newStr = GUILayout.Toggle(action.applyStructure, "Apply Right Hierarchy",
                                EditorStyles.miniButton, GUILayout.Height(rowH), GUILayout.Width(142));
                            if (newStr != action.applyStructure) { action.applyStructure = newStr; Repaint(); }
                        }
                    }
                    if (node.componentChanged)
                    {
                        bool newComp = GUILayout.Toggle(action.useB, "Apply Right Components",
                            EditorStyles.miniButton, GUILayout.Height(rowH), GUILayout.Width(152));
                        if (newComp != action.useB) { action.useB = newComp; Repaint(); }
                    }
                }
            }

            EditorGUILayout.EndHorizontal();

            if (hasChildren && node.expanded)
                foreach (var child in node.children)
                    DrawNodeA(child, depth + 1);
        }

        // ── Dataset B Node Drawing ──

        private void DrawNodeB(TreeNode node, int depth, bool hasOnlyInBAncestor)
        {
            bool hasChildren = node.children.Count > 0;
            float rowH = EditorGUIUtility.singleLineHeight;

            EditorGUILayout.BeginHorizontal(GUILayout.Height(rowH));
            GUILayout.Space(depth * 14f);

            Color rowColor = GetStatusColor(node.status);
            if (rowColor.a > 0)
            {
                var r = GUILayoutUtility.GetLastRect();
                r.x = 0;
                r.width = position.width * 0.5f;
                r.height = rowH;
                EditorGUI.DrawRect(r, rowColor);
            }

            if (hasChildren)
            {
                Rect foldRect = GUILayoutUtility.GetRect(12f, rowH, GUILayout.Width(12));
                node.expanded = EditorGUI.Foldout(foldRect, node.expanded, "");
            }
            else
                GUILayout.Space(16);

            string label = node.isPsdNode ? $"[{node.psdLayerId}] {node.name}" : $"[ - ] {node.name}";
            bool wasSelected = (_selectedNode == node);
            GUIStyle btnStyle = new GUIStyle(node.isOnlyInB ? EditorStyles.boldLabel : EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft
            };
            if (!node.isPsdNode)
            {
                var oldCol = GUI.contentColor;
                GUI.contentColor = new Color(0.6f, 0.6f, 0.6f);
                if (GUILayout.Button(label, btnStyle, GUILayout.Height(rowH)))
                    _selectedNode = wasSelected ? null : node;
                GUI.contentColor = oldCol;
            }
            else
            {
                if (GUILayout.Button(label, btnStyle, GUILayout.Height(rowH)))
                    _selectedNode = wasSelected ? null : node;
            }

            if (node.isOnlyInB && _nodeActions.TryGetValue(node, out var action))
            {
                var savedColor = GUI.backgroundColor;
                GUI.backgroundColor = action.addInB
                    ? new Color(0.35f, 0.85f, 0.35f, 0.9f)
                    : new Color(0.65f, 0.65f, 0.65f, 0.9f);

                if (!hasOnlyInBAncestor)
                {
                    bool newAdd = GUILayout.Toggle(action.addInB, "Add",
                        EditorStyles.miniButton, GUILayout.Height(rowH), GUILayout.Width(46));
                    if (newAdd != action.addInB)
                    {
                        action.addInB = newAdd;
                        SetAddInBForChildren(node, newAdd);
                        if (!newAdd)
                            ClearApplyStructureForDependentSubtree(node);
                        Repaint();
                    }
                }
                else
                {
                    GUILayout.Space(46f);
                }

                GUI.backgroundColor = savedColor;
            }

            EditorGUILayout.EndHorizontal();

            if (hasChildren && node.expanded)
                foreach (var child in node.children)
                    DrawNodeB(child, depth + 1, hasOnlyInBAncestor || node.isOnlyInB);
        }

        // ── Bottom Detail ──

        private void DrawDetailPanel(TreeNode node)
        {
            GUILayout.Space(2);
            var headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            EditorGUILayout.LabelField(node.isPsdNode ? $"Component Diff Detail — [{node.psdLayerId}] {node.name}" : $"Structure Detail — [Aux Node] {node.name}", headerStyle);
            
            _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll, GUILayout.Height(190));

            var mapA = ParseComponentsJsonMap(node.aComponentsJson);
            var mapB = ParseComponentsJsonMap(node.bComponentsJson);

            // Collect all involved component keys
            var allKeys = new HashSet<string>();
            if (mapA != null) foreach (var k in mapA.Keys) allKeys.Add(k);
            if (mapB != null) foreach (var k in mapB.Keys) allKeys.Add(k);

            bool anyDiffShown = false;

            // 1. Name difference display
            if (node.aName != node.bName)
            {
                anyDiffShown = true;
                DrawComponentDiff("Object Name", node.aName, node.bName);
                GUILayout.Space(10);
            }

            foreach (var compName in allKeys)
            {
                string jsonA = mapA != null && mapA.TryGetValue(compName, out var aVal) ? aVal : null;
                string jsonB = mapB != null && mapB.TryGetValue(compName, out var bVal) ? bVal : null;

                // Filter out unchanged components
                if (jsonA == jsonB) continue;

                anyDiffShown = true;
                DrawComponentDiff(compName, jsonA, jsonB);
                GUILayout.Space(10);
            }

            if (!anyDiffShown)
            {
                if (node.status == NodeStatus.Unchanged)
                    EditorGUILayout.HelpBox("No component or structure changes for this node.", MessageType.Info);
                else if (node.status == NodeStatus.ParentChanged)
                    EditorGUILayout.HelpBox("Only hierarchy structure (parent or order) changed; component data is identical.", MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawComponentDiff(string compName, string jsonA, string jsonB)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"[ {compName} ]", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();

            // Left side A
            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.5f - 20f));
            EditorGUILayout.LabelField("Existing Prefab (A)", EditorStyles.miniLabel);
            DrawJsonBox(compName, jsonA, jsonB, false);
            EditorGUILayout.EndVertical();

            // Right side B
            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.5f - 20f));
            EditorGUILayout.LabelField("PSD Export Result (B)", EditorStyles.miniLabel);
            DrawJsonBox(compName, jsonB, jsonA, true);
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private static GUIStyle _jsonBoxStyle;
        private void DrawJsonBox(string compName, string sourceJson, string targetJson, bool isB)
        {
            if (_jsonBoxStyle == null)
            {
                _jsonBoxStyle = new GUIStyle(EditorStyles.label)
                {
                    richText = true,
                    wordWrap = true,
                    normal = { textColor = new Color(0.7f, 0.7f, 0.7f) },
                    padding = new RectOffset(5, 5, 5, 5)
                };
            }

            // Show component name in the JSON box header
            var compHeaderStyle = new GUIStyle(EditorStyles.miniLabel) { richText = true };
            GUILayout.Label($"<b>{compName}</b>", compHeaderStyle);

            if (string.IsNullOrEmpty(sourceJson))
            {
                GUILayout.Label("<color=gray>(No such component)</color>", _jsonBoxStyle);
                return;
            }

            string formatted = FormatJsonWithDiff(sourceJson, targetJson);
            GUILayout.Label(formatted, _jsonBoxStyle);
        }

        private string FormatJsonWithDiff(string source, string target)
        {
            var sourceProps = ParseFlatJsonProperties(source);
            var targetProps = ParseFlatJsonProperties(target);

            var sb = new System.Text.StringBuilder();
            sb.Append("{\n");
            int count = sourceProps.Count;
            int i = 0;
            foreach (var kv in sourceProps)
            {
                sb.Append("  \"");
                sb.Append(kv.Key);
                sb.Append("\": ");

                bool isDiff = targetProps == null || !targetProps.TryGetValue(kv.Key, out var targetVal) || targetVal != kv.Value;
                
                if (isDiff) sb.Append("<color=#FF5555>");
                
                // Handle value display, add quotes if it's a string
                if (!string.IsNullOrEmpty(kv.Value) && kv.Value.StartsWith("{")) sb.Append(kv.Value); // Nested objects are not deeply processed
                else sb.Append(kv.Value);

                if (isDiff) sb.Append("</color>");

                if (++i < count) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("}");
            return sb.ToString();
        }

        private static Dictionary<string, string> ParseFlatJsonProperties(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            json = json.Trim();
            if (!json.StartsWith("{") || !json.EndsWith("}")) return new Dictionary<string, string> { { "data", json } };

            var result = new Dictionary<string, string>(System.StringComparer.Ordinal);
            int i = 1;
            int len = json.Length;

            while (i < len - 1)
            {
                while (i < len && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r' || json[i] == ',')) i++;
                if (i >= len - 1) break;
                if (json[i] != '"') break;

                int keyStart = i + 1; i++;
                while (i < len && (json[i] != '"' || json[i - 1] == '\\')) i++;
                if (i >= len) break;
                string key = json.Substring(keyStart, i - keyStart);
                i++;

                while (i < len && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r' || json[i] == ':')) i++;
                if (i >= len) break;

                int valueStart = i;
                if (json[i] == '"')
                {
                    i++;
                    while (i < len && (json[i] != '"' || json[i - 1] == '\\')) i++;
                    i++;
                }
                else if (json[i] == '{')
                {
                    int depth = 0; bool inStr = false;
                    while (i < len)
                    {
                        char c = json[i];
                        if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; }
                        else { if (c == '"') inStr = true; else if (c == '{') depth++; else if (c == '}') { if (--depth == 0) { i++; break; } } }
                        i++;
                    }
                }
                else
                {
                    while (i < len && json[i] != ',' && json[i] != '}' && json[i] != ' ' && json[i] != '\n') i++;
                }
                result[key] = json.Substring(valueStart, i - valueStart);
            }
            return result;
        }

        // ── Bottom Action Bar ──

        private void DrawBottomBar()
        {
            GUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            GUI.backgroundColor = new Color(1f, 0.7f, 0.2f);
            if (GUILayout.Button("Fresh Overwrite (Discard Existing Prefab)", GUILayout.Width(280), GUILayout.Height(28)))
            {
                if (EditorUtility.DisplayDialog("Confirm", "This will delete the existing Prefab and generate a fresh one. Continue?", "OK", "Cancel"))
                {
                    _applied = true;
                    _onFreshExport?.Invoke();
                    Close();
                }
            }
            GUI.backgroundColor = Color.white;

            GUILayout.Space(12);

            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.4f);
            if (GUILayout.Button("Apply Incremental Selection", GUILayout.Width(180), GUILayout.Height(28)))
            {
                _applied = true;
                var decisions = CollectDecisions();
                _onApply?.Invoke(decisions);
                Close();
            }
            GUI.backgroundColor = Color.white;

            GUILayout.Space(12);

            // if (GUILayout.Button("Cancel", GUILayout.Width(80), GUILayout.Height(28)))
            // {
            //     _applied = true;
            //     _onCancel?.Invoke();
            //     Close();
            // }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(4);
        }

        private void OnDestroy()
        {
            if (!_applied)
                _onCancel?.Invoke();
        }

        // ───────────────────────── Collect Decisions ─────────────────────────

        private ExportDiffDecisions CollectDecisions()
        {
            var result = new ExportDiffDecisions();
            result.compareNameDiff = _compareNameDiff;
            CollectDecisionsRecursive(_rootsA, result.nodesToDelete, result.nodesToApplyStructure,
                result.nodesToApplyStructureSiblingIndex, result.nodesToApplyBComponents, result.bComponentsJsonByLayerId, false, false);
            CollectOnlyBDecisions(_rootsB, result.nodesToAdd, result.nodesToAddSiblingIndex);

            // ── Debug Logs ──
            var onlyBNodes = new System.Collections.Generic.List<string>();
            CollectOnlyBDebug(_rootsB, onlyBNodes);
            Debug.Log($"[DiffWindow] CollectDecisions: nodesToAdd=[{string.Join(",", result.nodesToAdd)}] | onlyInB nodes(name/id/add)=[{string.Join("; ", onlyBNodes)}]");

            return result;
        }

        private void CollectOnlyBDebug(System.Collections.Generic.List<TreeNode> nodes, System.Collections.Generic.List<string> info)
        {
            foreach (var node in nodes)
            {
                if (node.isOnlyInB && node.isPsdNode)
                {
                    bool addInB = !_nodeActions.TryGetValue(node, out var action) || action.addInB;
                    info.Add($"{node.name}(id={node.psdLayerId},siblingIndex={node.siblingIndex},add={addInB})");
                }
                CollectOnlyBDebug(node.children, info);
            }
        }

        private void CollectDecisionsRecursive(List<TreeNode> nodes,
            List<int> toDelete, List<int> toReparent, Dictionary<int, int> reparentSiblingIndex,
            List<int> toApplyComp, Dictionary<int, string> bComponentsJsonByLayerId,
            bool hasDeleteAncestor, bool isAncestorDeleted)
        {
            foreach (var node in nodes)
            {
                bool childHasDeleteAncestor = hasDeleteAncestor;
                bool childAncestorDeleted = isAncestorDeleted;

                if (node.isPsdNode && _nodeActions.TryGetValue(node, out var action))
                {
                    if (node.isOnlyInA)
                    {
                        bool deleteNode = !action.keepInA || hasDeleteAncestor || isAncestorDeleted;
                        if (deleteNode)
                        {
                            bool hasSelectedStructuralSurvivor = HasSelectedStructuralSurvivor(node);
                            if (!hasDeleteAncestor)
                                toDelete.Add(node.psdLayerId);

                            childHasDeleteAncestor = true;
                            childAncestorDeleted = isAncestorDeleted || !hasSelectedStructuralSurvivor;
                        }
                    }
                    else
                    {
                        if (!isAncestorDeleted && node.parentChanged && action.applyStructure)
                        {
                            toReparent.Add(node.psdLayerId);
                            if (node.bSiblingIndex >= 0)
                                reparentSiblingIndex[node.psdLayerId] = node.bSiblingIndex;
                        }
                        if (!isAncestorDeleted && node.componentChanged && action.useB)
                        {
                            toApplyComp.Add(node.psdLayerId);
                            if (!string.IsNullOrEmpty(node.bComponentsJson))
                                bComponentsJsonByLayerId[node.psdLayerId] = node.bComponentsJson;
                        }
                    }
                }
                CollectDecisionsRecursive(node.children, toDelete, toReparent, reparentSiblingIndex,
                    toApplyComp, bComponentsJsonByLayerId, childHasDeleteAncestor, childAncestorDeleted);
            }
        }

        private bool HasSelectedStructuralSurvivor(TreeNode node)
        {
            foreach (var child in node.children)
            {
                if (!child.isPsdNode)
                {
                    if (HasSelectedStructuralSurvivor(child))
                        return true;
                    continue;
                }

                if (!child.isOnlyInA
                    && child.parentChanged
                    && _nodeActions.TryGetValue(child, out var action)
                    && action.applyStructure)
                    return true;

                if (HasSelectedStructuralSurvivor(child))
                    return true;
            }

            return false;
        }

        private void SetKeepInAForChildren(TreeNode node, bool keep)
        {
            foreach (var child in node.children)
            {
                if (child.isOnlyInA && _nodeActions.TryGetValue(child, out var action))
                {
                    action.keepInA = keep;
                }
                SetKeepInAForChildren(child, keep);
            }
        }

        private void SetAddInBForChildren(TreeNode node, bool add)
        {
            foreach (var child in node.children)
            {
                if (child.isOnlyInB && _nodeActions.TryGetValue(child, out var action))
                {
                    action.addInB = add;
                }
                SetAddInBForChildren(child, add);
            }
        }

        private void ClearApplyStructureForDependentSubtree(TreeNode node)
        {
            foreach (var child in node.children)
            {
                if (child.parentChanged && _nodeActions.TryGetValue(child, out var action))
                    action.applyStructure = false;
                ClearApplyStructureForDependentSubtree(child);
            }
        }

        private void CollectOnlyBDecisions(List<TreeNode> nodes, List<int> toAdd, Dictionary<int, int> siblingIndexByLayerId,
            bool hasSelectedOnlyInBAncestor = false)
        {
            foreach (var node in nodes)
            {
                bool selectedNewSubtree = false;
                if (node.isPsdNode && node.isOnlyInB && _nodeActions.TryGetValue(node, out var action) && action.addInB)
                {
                    selectedNewSubtree = true;
                    if (!hasSelectedOnlyInBAncestor)
                    {
                        toAdd.Add(node.psdLayerId);
                        siblingIndexByLayerId[node.psdLayerId] = node.siblingIndex;
                    }
                }
                CollectOnlyBDecisions(node.children, toAdd, siblingIndexByLayerId,
                    hasSelectedOnlyInBAncestor || selectedNewSubtree);
            }
        }

        private bool IsStructureApplyBlocked(int psdLayerId)
        {
            if (!_bNodesByPsdLayerId.TryGetValue(psdLayerId, out var bNode))
                return false;

            for (var current = bNode.parent; current != null; current = current.parent)
            {
                if (!current.isOnlyInB)
                    continue;
                if (_nodeActions.TryGetValue(current, out var action) && !action.addInB)
                    return true;
            }

            return false;
        }

        // ───────────────────────── Helpers ─────────────────────────

        private static Color GetStatusColor(NodeStatus status)
        {
            switch (status)
            {
                case NodeStatus.OnlyInA: return ColOnlyA;
                case NodeStatus.OnlyInB: return ColOnlyB;
                case NodeStatus.ParentChanged: return ColParentChanged;
                case NodeStatus.ComponentChanged: return ColCompChanged;
                default: return Color.clear;
            }
        }

        private static void SetAllExpanded(List<TreeNode> roots, bool expanded)
        {
            foreach (var r in roots)
                SetExpandedRecursive(r, expanded);
        }

        private static void SetExpandedRecursive(TreeNode node, bool expanded)
        {
            node.expanded = expanded;
            foreach (var c in node.children)
                SetExpandedRecursive(c, expanded);
        }

        // ───────────────────────── Component JSON Comparison ─────────────────────────

        /// <summary>
        /// Compares two componentsJson strings (new format: {"CompName":{...},...}).
        /// Step-by-component comparison: if any component property differs (or one side has extra/missing components), it's considered changed.
        /// Degrades compatibility with old bundle format ({"components":[...]}) and simple string equality.
        /// </summary>
        private static bool ComponentsJsonChanged(string jsonA, string jsonB)
        {
            if (jsonA == jsonB) return false;
            if (string.IsNullOrEmpty(jsonA) || string.IsNullOrEmpty(jsonB)) return true;

            // Parse both sides into key→value maps
            Dictionary<string, string> mapA = ParseComponentsJsonMap(jsonA);
            Dictionary<string, string> mapB = ParseComponentsJsonMap(jsonB);

            // If parsing failed for both, fall back to string comparison
            if (mapA == null && mapB == null) return jsonA != jsonB;
            if (mapA == null || mapB == null) return true;

            // Check same set of component keys
            if (mapA.Count != mapB.Count) return true;
            foreach (var kv in mapA)
            {
                if (!mapB.TryGetValue(kv.Key, out string bVal)) return true;
                if (kv.Value != bVal) return true;
            }
            return false;
        }

        /// <summary>
        /// Parse a componentsJson string (new flat format: {"CompName":{...},...}) into a dictionary.
        /// Returns null if the string is not a valid flat JSON object (e.g. it might be the old bundle format).
        /// </summary>
        private static Dictionary<string, string> ParseComponentsJsonMap(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            json = json.Trim();
            if (!json.StartsWith("{") || !json.EndsWith("}")) return null;

            var result = new Dictionary<string, string>(System.StringComparer.Ordinal);
            int i = 1; // skip opening '{'
            int len = json.Length;

            while (i < len - 1)
            {
                // Skip whitespace
                while (i < len && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r' || json[i] == ',')) i++;
                if (i >= len - 1) break;
                if (json[i] != '"') return null; // unexpected character

                // Read key string
                int keyStart = i + 1;
                i++; // skip opening quote
                while (i < len && (json[i] != '"' || json[i - 1] == '\\')) i++;
                if (i >= len) return null;
                string key = json.Substring(keyStart, i - keyStart);
                i++; // skip closing quote

                // Skip ':' and whitespace
                while (i < len && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r')) i++;
                if (i >= len || json[i] != ':') return null;
                i++;
                while (i < len && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r')) i++;

                // Read value (must be a JSON object '{')
                if (i >= len || json[i] != '{') return null;
                int valueStart = i;
                int depth = 0;
                bool inStr = false;
                while (i < len)
                {
                    char c = json[i];
                    if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; }
                    else { if (c == '"') inStr = true; else if (c == '{') depth++; else if (c == '}') { if (--depth == 0) { i++; break; } } }
                    i++;
                }
                result[key] = json.Substring(valueStart, i - valueStart);
            }
            return result.Count > 0 ? result : null;
        }
    }

    /// <summary>User decision results from export difference comparison window (not persisted).</summary>
    public class ExportDiffDecisions
    {
        public bool compareNameDiff;
        /// <summary>Delete roots for OnlyInA nodes chosen to remove; applied after structural rescue and additions.</summary>
        public List<int> nodesToDelete = new List<int>();
        /// <summary>ParentChanged and user chose to apply PSD structure -> list of layerIds to reparent.</summary>
        public List<int> nodesToApplyStructure = new List<int>();
        /// <summary>siblingIndex of ParentChanged nodes in Dataset B (indexed by layerId); used to insert nodes at specified positions when applying structure.</summary>
        public Dictionary<int, int> nodesToApplyStructureSiblingIndex = new Dictionary<int, int>();
        /// <summary>ComponentChanged and user chose to use B components -> list of layerIds to reset components.</summary>
        public List<int> nodesToApplyBComponents = new List<int>();
        /// <summary>componentsJson of ComponentChanged nodes on B side (indexed by layerId); used to restore RectTransform position directly without relying on PSD layer data recalculation.</summary>
        public Dictionary<int, string> bComponentsJsonByLayerId = new Dictionary<int, string>();
        /// <summary>OnlyInB and user chose to add -> list of layerIds to add.</summary>
        public List<int> nodesToAdd = new List<int>();
        /// <summary>siblingIndex of OnlyInB nodes in Dataset B (indexed by layerId); used to insert new nodes in B's order.</summary>
        public Dictionary<int, int> nodesToAddSiblingIndex = new Dictionary<int, int>();

        public bool HasAnyAction =>
            nodesToDelete.Count > 0 || nodesToApplyStructure.Count > 0 ||
            nodesToApplyBComponents.Count > 0 || nodesToAdd.Count > 0;
    }
}
