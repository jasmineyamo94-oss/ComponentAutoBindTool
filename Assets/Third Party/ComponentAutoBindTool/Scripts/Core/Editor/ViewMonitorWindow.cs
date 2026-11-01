using System;
using System.Collections.Generic;
using System.IO;
using ComponentAutoBindTool.ViewCore;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ComponentAutoBindTool.Editor
{
    public sealed class ViewMonitorWindow : EditorWindow
    {
        private static readonly UILayer[] s_DisplayLayers =
        {
            UILayer.System, UILayer.Top, UILayer.Normal, UILayer.HUD
        };

        private readonly List<IViewManagerDiagnostics> _managers = new();
        private readonly List<string> _managerChoices = new() { "无活跃管理器" };
        private readonly List<WindowDiagnosticSnapshot> _windowBuffer = new(32);
        private readonly List<QueuedRequestSnapshot> _queueBuffer = new(16);
        private readonly List<Label> _queueRows = new();
        private readonly WindowSection[] _layerSections = new WindowSection[4];
        private IViewManagerDiagnostics _selectedManager;
        private PopupField<string> _managerSelector;
        private Label _summary;
        private VisualElement _actions;
        private WindowSection _inactiveSection;
        private Box _queueSection;
        private Label _queueHeader;
        private Label _queueEmpty;
        private double _nextRefreshTime;
        private bool _isChangingPlayMode;

        [MenuItem("自动绑定UI组件/框架实验台/运行期窗口监控台")]
        public static void Open()
        {
            var window = GetWindow<ViewMonitorWindow>("窗口运行监控台");
            window.minSize = new Vector2(520, 420);
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            ClearReferences();
        }

        public void CreateGUI()
        {
            ClearReferences();
            rootVisualElement.Clear();
            rootVisualElement.styleSheets.Clear();
            _queueRows.Clear();
            string scriptPath = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this));
            string stylePath = Path.Combine(Path.GetDirectoryName(scriptPath) ?? string.Empty,
                "ViewMonitorWindow.uss").Replace('\\', '/');
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(stylePath);
            if (styleSheet != null)
            {
                rootVisualElement.styleSheets.Add(styleSheet);
            }

            rootVisualElement.AddToClassList("view-monitor");
            rootVisualElement.Add(CreateLabel("VIEWCORE / 运行期窗口监控台", "monitor-title"));
            _managerSelector = new PopupField<string>("管理器", _managerChoices, 0)
            {
                name = "managerSelector"
            };
            _managerSelector.RegisterValueChangedCallback(evt =>
            {
                int index = _managerChoices.IndexOf(evt.newValue);
                _selectedManager = index >= 0 && index < _managers.Count ? _managers[index] : null;
                RefreshView();
            });
            rootVisualElement.Add(_managerSelector);
            _summary = CreateLabel(string.Empty, "monitor-summary");
            _summary.name = "summary";
            rootVisualElement.Add(_summary);
            _actions = new VisualElement { name = "actions" };
            _actions.AddToClassList("monitor-actions");
            _actions.Add(CreateButton("导航返回", "navigateBack", () => Execute(manager => manager.TryNavigateBack())));
            _actions.Add(CreateButton("关闭全部活动窗口", "closeAll", () => Execute(manager => manager.CloseAllViews())));
            _actions.Add(CreateButton("关闭全部并销毁缓存", "destroyAll", () => Execute(manager => manager.CloseAllViews(true))));
            _actions.Add(CreateButton("清空等待队列", "clearQueue", () => Execute(manager => manager.ClearWaitingQueue())));
            rootVisualElement.Add(_actions);

            var scroll = new ScrollView(ScrollViewMode.Vertical) { name = "content" };
            scroll.AddToClassList("monitor-scroll");
            rootVisualElement.Add(scroll);
            for (int i = 0; i < s_DisplayLayers.Length; i++)
            {
                _layerSections[i] = new WindowSection(this, "layer" + s_DisplayLayers[i],
                    "层级 " + s_DisplayLayers[i]);
                scroll.Add(_layerSections[i].Root);
            }

            _queueSection = new Box { name = "queue" };
            _queueSection.AddToClassList("monitor-section");
            _queueHeader = CreateLabel("等待队列", "monitor-header");
            _queueEmpty = new Label("当前没有排队请求。");
            _queueSection.Add(_queueHeader);
            _queueSection.Add(_queueEmpty);
            scroll.Add(_queueSection);
            _inactiveSection = new WindowSection(this, "inactive", "缓存与待销毁池");
            scroll.Add(_inactiveSection.Root);
            RefreshView();
        }

        private void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup < _nextRefreshTime)
            {
                return;
            }

            _nextRefreshTime = EditorApplication.timeSinceStartup + 0.25d;
            RefreshView();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            _isChangingPlayMode = state == PlayModeStateChange.ExitingEditMode ||
                                   state == PlayModeStateChange.ExitingPlayMode;
            ClearReferences();
            RefreshView();
        }

        internal void RefreshView()
        {
            if (_summary == null)
            {
                return;
            }

            if (!EditorApplication.isPlaying || _isChangingPlayMode)
            {
                ClearReferences();
                _summary.text = "请进入 Play Mode，以监控窗口、缓存与等待队列。";
                return;
            }

            RefreshManagers();
            var manager = GetActiveManager();
            _actions.SetEnabled(manager != null);
            if (manager == null)
            {
                ClearRows();
                _summary.text = "当前没有活跃的 ViewManager 实例。";
                return;
            }

            _summary.text = $"活跃管理器：{_managerChoices.IndexOf(_managerSelector.value) + 1}/{_managers.Count}  |  " +
                             $"总登记实例：{manager.TotalInstanceCount}  |  等待队列：{manager.WaitingQueueCount}";
            for (int i = 0; i < s_DisplayLayers.Length; i++)
            {
                _windowBuffer.Clear();
                manager.GetLayerSnapshots(s_DisplayLayers[i], _windowBuffer);
                _layerSections[i].Bind(manager, _windowBuffer, true);
            }

            _windowBuffer.Clear();
            manager.GetInactiveSnapshots(_windowBuffer);
            _inactiveSection.Bind(manager, _windowBuffer, false);
            _windowBuffer.Clear();
            _queueBuffer.Clear();
            manager.GetWaitingQueueSnapshots(_queueBuffer);
            _queueHeader.text = $"等待队列 — {_queueBuffer.Count} 项";
            _queueEmpty.EnableInClassList("monitor-hidden", _queueBuffer.Count != 0);
            for (int i = 0; i < _queueBuffer.Count; i++)
            {
                if (i == _queueRows.Count)
                {
                    var row = CreateLabel(string.Empty, "monitor-queue-row");
                    _queueRows.Add(row);
                    _queueSection.Add(row);
                }

                var request = _queueBuffer[i];
                _queueRows[i].text = $"[{request.QueueIndex}] {request.ViewName}  |  优先级：{request.Priority}  |  参数：{request.ArgsTypeName}";
                _queueRows[i].RemoveFromClassList("monitor-hidden");
            }

            for (int i = _queueBuffer.Count; i < _queueRows.Count; i++)
            {
                _queueRows[i].text = string.Empty;
                _queueRows[i].AddToClassList("monitor-hidden");
            }
            _queueBuffer.Clear();
        }

        private void RefreshManagers()
        {
            var managers = ViewManagerDebugRegistry.ActiveManagers;
            bool changed = managers.Count != _managers.Count;
            for (int i = 0; !changed && i < managers.Count; i++)
            {
                changed = !ReferenceEquals(managers[i], _managers[i]);
            }

            if (!changed)
            {
                return;
            }

            _managers.Clear();
            _managerChoices.Clear();
            int selectedIndex = -1;
            for (int i = 0; i < managers.Count; i++)
            {
                _managers.Add(managers[i]);
                _managerChoices.Add($"{managers[i].GetType().Name} #{i + 1}");
                if (ReferenceEquals(_selectedManager, managers[i]))
                {
                    selectedIndex = i;
                }
            }

            if (_managers.Count == 0)
            {
                _selectedManager = null;
                _managerChoices.Add("无活跃管理器");
            }
            else
            {
                selectedIndex = Math.Max(0, selectedIndex);
                _selectedManager = _managers[selectedIndex];
            }

            _managerSelector.choices = _managerChoices;
            _managerSelector.SetValueWithoutNotify(_managerChoices[Math.Max(0, selectedIndex)]);
            _managerSelector.SetEnabled(_managers.Count > 1);
        }

        private IViewManagerDiagnostics GetActiveManager()
        {
            if (!EditorApplication.isPlaying || _isChangingPlayMode || _selectedManager == null || _selectedManager.IsDisposed)
            {
                return null;
            }

            var managers = ViewManagerDebugRegistry.ActiveManagers;
            for (int i = 0; i < managers.Count; i++)
            {
                if (ReferenceEquals(managers[i], _selectedManager))
                {
                    return _selectedManager;
                }
            }

            return null;
        }

        private void Execute(Action<IViewManagerDiagnostics> action)
        {
            try
            {
                var manager = GetActiveManager();
                if (manager != null)
                {
                    action(manager);
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                RefreshView();
            }
        }

        private void ExecuteRow(IViewManagerDiagnostics manager, int instanceId, bool close)
        {
            Execute(activeManager =>
            {
                if (!ReferenceEquals(activeManager, manager))
                {
                    return;
                }

                for (int i = 0; i <= s_DisplayLayers.Length; i++)
                {
                    _windowBuffer.Clear();
                    if (i == s_DisplayLayers.Length)
                    {
                        activeManager.GetInactiveSnapshots(_windowBuffer);
                    }
                    else
                    {
                        activeManager.GetLayerSnapshots(s_DisplayLayers[i], _windowBuffer);
                    }

                    for (int j = 0; j < _windowBuffer.Count; j++)
                    {
                        var snapshot = _windowBuffer[j];
                        if (snapshot.InstanceId != instanceId)
                        {
                            continue;
                        }

                        _windowBuffer.Clear();
                        if (close)
                        {
                            if (CanClose(snapshot.State))
                            {
                                activeManager.CloseView(instanceId);
                            }
                        }
                        else if (snapshot.HostObject != null)
                        {
                            Selection.activeObject = snapshot.HostObject;
                            EditorGUIUtility.PingObject(snapshot.HostObject);
                        }
                        return;
                    }
                }
                _windowBuffer.Clear();
            });
        }

        private void ClearReferences()
        {
            _selectedManager = null;
            _managers.Clear();
            _managerChoices.Clear();
            _managerChoices.Add("无活跃管理器");
            if (_managerSelector != null)
            {
                _managerSelector.choices = _managerChoices;
                _managerSelector.SetValueWithoutNotify(_managerChoices[0]);
                _managerSelector.SetEnabled(false);
            }

            _actions?.SetEnabled(false);
            ClearRows();
        }

        private void ClearRows()
        {
            _windowBuffer.Clear();
            _queueBuffer.Clear();
            for (int i = 0; i < _layerSections.Length; i++)
            {
                _layerSections[i]?.Clear();
            }
            _inactiveSection?.Clear();
            for (int i = 0; i < _queueRows.Count; i++)
            {
                _queueRows[i].text = string.Empty;
                _queueRows[i].AddToClassList("monitor-hidden");
            }
            if (_queueHeader != null)
            {
                _queueHeader.text = "等待队列 — 0 项";
                _queueEmpty.RemoveFromClassList("monitor-hidden");
            }
        }

        private static bool CanClose(WindowState state)
        {
            return state == WindowState.OpenAndActive || state == WindowState.Paused;
        }

        private static Label CreateLabel(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }

        private static Button CreateButton(string text, string name, Action action)
        {
            return new Button(action) { text = text, name = name };
        }

        private sealed class WindowSection
        {
            public readonly Box Root;
            private readonly ViewMonitorWindow _window;
            private readonly string _title;
            private readonly Label _header;
            private readonly Label _empty;
            private readonly List<WindowRow> _rows = new();

            public WindowSection(ViewMonitorWindow window, string name, string title)
            {
                _window = window;
                _title = title;
                Root = new Box { name = name };
                Root.AddToClassList("monitor-section");
                _header = CreateLabel(title, "monitor-header");
                _empty = new Label("当前没有窗口。");
                Root.Add(_header);
                Root.Add(_empty);
            }

            public void Bind(IViewManagerDiagnostics manager, List<WindowDiagnosticSnapshot> snapshots, bool reverse)
            {
                _header.text = $"{_title} — {snapshots.Count} 项";
                _empty.EnableInClassList("monitor-hidden", snapshots.Count != 0);
                for (int i = 0; i < snapshots.Count; i++)
                {
                    if (i == _rows.Count)
                    {
                        var row = new WindowRow(_window);
                        _rows.Add(row);
                        Root.Add(row.Root);
                    }
                    _rows[i].Bind(manager, snapshots[reverse ? snapshots.Count - i - 1 : i], reverse && i == 0);
                }
                for (int i = snapshots.Count; i < _rows.Count; i++)
                {
                    _rows[i].Clear();
                }
            }

            public void Clear()
            {
                _header.text = _title + " — 0 项";
                _empty.RemoveFromClassList("monitor-hidden");
                for (int i = 0; i < _rows.Count; i++)
                {
                    _rows[i].Clear();
                }
            }
        }

        private sealed class WindowRow
        {
            public readonly VisualElement Root = new();
            private readonly Label _info;
            private readonly Button _ping;
            private readonly Button _close;
            private IViewManagerDiagnostics _manager;
            private int _instanceId;

            public WindowRow(ViewMonitorWindow window)
            {
                Root.AddToClassList("monitor-row");
                _info = CreateLabel(string.Empty, "monitor-row-info");
                _ping = CreateButton("定位", "ping", () => window.ExecuteRow(_manager, _instanceId, false));
                _close = CreateButton("关闭", "close", () => window.ExecuteRow(_manager, _instanceId, true));
                Root.Add(_info);
                Root.Add(_ping);
                Root.Add(_close);
            }

            public void Bind(IViewManagerDiagnostics manager, WindowDiagnosticSnapshot snapshot, bool top)
            {
                _manager = manager;
                _instanceId = snapshot.InstanceId;
                string timer = snapshot.State == WindowState.PendingDestroy
                    ? $" · 销毁倒计时 {snapshot.RemainingDestroyTime:F1}s" : string.Empty;
                string transition = snapshot.IsTransitioning ? " · 过渡中" : string.Empty;
                _info.text = $"#{snapshot.InstanceId} {(top ? "[栈顶] " : string.Empty)}{snapshot.ViewName} [{snapshot.State}]{transition}\n" +
                              $"Order:{snapshot.SortingOrder} · 子Canvas:{snapshot.SubCanvasCount} · " +
                              $"{snapshot.InstanceMode}/{snapshot.CoverMode}/{snapshot.CloseStrategy} · " +
                              $"允许返回:{snapshot.AllowNavigateBack}{timer}";
                _ping.SetEnabled(snapshot.HostObject != null);
                _close.SetEnabled(CanClose(snapshot.State));
                Root.RemoveFromClassList("monitor-hidden");
            }

            public void Clear()
            {
                _manager = null;
                _instanceId = 0;
                _info.text = string.Empty;
                _ping.SetEnabled(false);
                _close.SetEnabled(false);
                Root.AddToClassList("monitor-hidden");
            }
        }
    }
}
