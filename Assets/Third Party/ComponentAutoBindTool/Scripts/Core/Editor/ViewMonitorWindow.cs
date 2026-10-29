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

        private readonly List<IViewManagerDiagnostics> m_Managers = new();
        private readonly List<string> m_ManagerChoices = new() { "无活跃管理器" };
        private readonly List<WindowDiagnosticSnapshot> m_WindowBuffer = new(32);
        private readonly List<QueuedRequestSnapshot> m_QueueBuffer = new(16);
        private readonly List<Label> m_QueueRows = new();
        private readonly WindowSection[] m_LayerSections = new WindowSection[4];
        private IViewManagerDiagnostics m_SelectedManager;
        private PopupField<string> m_ManagerSelector;
        private Label m_Summary;
        private VisualElement m_Actions;
        private WindowSection m_InactiveSection;
        private Box m_QueueSection;
        private Label m_QueueHeader;
        private Label m_QueueEmpty;
        private double m_NextRefreshTime;
        private bool m_IsChangingPlayMode;

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
            m_QueueRows.Clear();
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
            m_ManagerSelector = new PopupField<string>("管理器", m_ManagerChoices, 0)
            {
                name = "managerSelector"
            };
            m_ManagerSelector.RegisterValueChangedCallback(evt =>
            {
                int index = m_ManagerChoices.IndexOf(evt.newValue);
                m_SelectedManager = index >= 0 && index < m_Managers.Count ? m_Managers[index] : null;
                RefreshView();
            });
            rootVisualElement.Add(m_ManagerSelector);
            m_Summary = CreateLabel(string.Empty, "monitor-summary");
            m_Summary.name = "summary";
            rootVisualElement.Add(m_Summary);
            m_Actions = new VisualElement { name = "actions" };
            m_Actions.AddToClassList("monitor-actions");
            m_Actions.Add(CreateButton("导航返回", "navigateBack", () => Execute(manager => manager.TryNavigateBack())));
            m_Actions.Add(CreateButton("关闭全部活动窗口", "closeAll", () => Execute(manager => manager.CloseAllViews())));
            m_Actions.Add(CreateButton("关闭全部并销毁缓存", "destroyAll", () => Execute(manager => manager.CloseAllViews(true))));
            m_Actions.Add(CreateButton("清空等待队列", "clearQueue", () => Execute(manager => manager.ClearWaitingQueue())));
            rootVisualElement.Add(m_Actions);

            var scroll = new ScrollView(ScrollViewMode.Vertical) { name = "content" };
            scroll.AddToClassList("monitor-scroll");
            rootVisualElement.Add(scroll);
            for (int i = 0; i < s_DisplayLayers.Length; i++)
            {
                m_LayerSections[i] = new WindowSection(this, "layer" + s_DisplayLayers[i],
                    "层级 " + s_DisplayLayers[i]);
                scroll.Add(m_LayerSections[i].Root);
            }

            m_QueueSection = new Box { name = "queue" };
            m_QueueSection.AddToClassList("monitor-section");
            m_QueueHeader = CreateLabel("等待队列", "monitor-header");
            m_QueueEmpty = new Label("当前没有排队请求。");
            m_QueueSection.Add(m_QueueHeader);
            m_QueueSection.Add(m_QueueEmpty);
            scroll.Add(m_QueueSection);
            m_InactiveSection = new WindowSection(this, "inactive", "缓存与待销毁池");
            scroll.Add(m_InactiveSection.Root);
            RefreshView();
        }

        private void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup < m_NextRefreshTime)
            {
                return;
            }

            m_NextRefreshTime = EditorApplication.timeSinceStartup + 0.25d;
            RefreshView();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            m_IsChangingPlayMode = state == PlayModeStateChange.ExitingEditMode ||
                                   state == PlayModeStateChange.ExitingPlayMode;
            ClearReferences();
            RefreshView();
        }

        internal void RefreshView()
        {
            if (m_Summary == null)
            {
                return;
            }

            if (!EditorApplication.isPlaying || m_IsChangingPlayMode)
            {
                ClearReferences();
                m_Summary.text = "请进入 Play Mode，以监控窗口、缓存与等待队列。";
                return;
            }

            RefreshManagers();
            var manager = GetActiveManager();
            m_Actions.SetEnabled(manager != null);
            if (manager == null)
            {
                ClearRows();
                m_Summary.text = "当前没有活跃的 ViewManager 实例。";
                return;
            }

            m_Summary.text = $"活跃管理器：{m_ManagerChoices.IndexOf(m_ManagerSelector.value) + 1}/{m_Managers.Count}  |  " +
                             $"总登记实例：{manager.TotalInstanceCount}  |  等待队列：{manager.WaitingQueueCount}";
            for (int i = 0; i < s_DisplayLayers.Length; i++)
            {
                m_WindowBuffer.Clear();
                manager.GetLayerSnapshots(s_DisplayLayers[i], m_WindowBuffer);
                m_LayerSections[i].Bind(manager, m_WindowBuffer, true);
            }

            m_WindowBuffer.Clear();
            manager.GetInactiveSnapshots(m_WindowBuffer);
            m_InactiveSection.Bind(manager, m_WindowBuffer, false);
            m_WindowBuffer.Clear();
            m_QueueBuffer.Clear();
            manager.GetWaitingQueueSnapshots(m_QueueBuffer);
            m_QueueHeader.text = $"等待队列 — {m_QueueBuffer.Count} 项";
            m_QueueEmpty.EnableInClassList("monitor-hidden", m_QueueBuffer.Count != 0);
            for (int i = 0; i < m_QueueBuffer.Count; i++)
            {
                if (i == m_QueueRows.Count)
                {
                    var row = CreateLabel(string.Empty, "monitor-queue-row");
                    m_QueueRows.Add(row);
                    m_QueueSection.Add(row);
                }

                var request = m_QueueBuffer[i];
                m_QueueRows[i].text = $"[{request.QueueIndex}] {request.ViewName}  |  优先级：{request.Priority}  |  参数：{request.ArgsTypeName}";
                m_QueueRows[i].RemoveFromClassList("monitor-hidden");
            }

            for (int i = m_QueueBuffer.Count; i < m_QueueRows.Count; i++)
            {
                m_QueueRows[i].text = string.Empty;
                m_QueueRows[i].AddToClassList("monitor-hidden");
            }
            m_QueueBuffer.Clear();
        }

        private void RefreshManagers()
        {
            var managers = ViewManagerDebugRegistry.ActiveManagers;
            bool changed = managers.Count != m_Managers.Count;
            for (int i = 0; !changed && i < managers.Count; i++)
            {
                changed = !ReferenceEquals(managers[i], m_Managers[i]);
            }

            if (!changed)
            {
                return;
            }

            m_Managers.Clear();
            m_ManagerChoices.Clear();
            int selectedIndex = -1;
            for (int i = 0; i < managers.Count; i++)
            {
                m_Managers.Add(managers[i]);
                m_ManagerChoices.Add($"{managers[i].GetType().Name} #{i + 1}");
                if (ReferenceEquals(m_SelectedManager, managers[i]))
                {
                    selectedIndex = i;
                }
            }

            if (m_Managers.Count == 0)
            {
                m_SelectedManager = null;
                m_ManagerChoices.Add("无活跃管理器");
            }
            else
            {
                selectedIndex = Math.Max(0, selectedIndex);
                m_SelectedManager = m_Managers[selectedIndex];
            }

            m_ManagerSelector.choices = m_ManagerChoices;
            m_ManagerSelector.SetValueWithoutNotify(m_ManagerChoices[Math.Max(0, selectedIndex)]);
            m_ManagerSelector.SetEnabled(m_Managers.Count > 1);
        }

        private IViewManagerDiagnostics GetActiveManager()
        {
            if (!EditorApplication.isPlaying || m_IsChangingPlayMode || m_SelectedManager == null || m_SelectedManager.IsDisposed)
            {
                return null;
            }

            var managers = ViewManagerDebugRegistry.ActiveManagers;
            for (int i = 0; i < managers.Count; i++)
            {
                if (ReferenceEquals(managers[i], m_SelectedManager))
                {
                    return m_SelectedManager;
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
                    m_WindowBuffer.Clear();
                    if (i == s_DisplayLayers.Length)
                    {
                        activeManager.GetInactiveSnapshots(m_WindowBuffer);
                    }
                    else
                    {
                        activeManager.GetLayerSnapshots(s_DisplayLayers[i], m_WindowBuffer);
                    }

                    for (int j = 0; j < m_WindowBuffer.Count; j++)
                    {
                        var snapshot = m_WindowBuffer[j];
                        if (snapshot.InstanceId != instanceId)
                        {
                            continue;
                        }

                        m_WindowBuffer.Clear();
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
                m_WindowBuffer.Clear();
            });
        }

        private void ClearReferences()
        {
            m_SelectedManager = null;
            m_Managers.Clear();
            m_ManagerChoices.Clear();
            m_ManagerChoices.Add("无活跃管理器");
            if (m_ManagerSelector != null)
            {
                m_ManagerSelector.choices = m_ManagerChoices;
                m_ManagerSelector.SetValueWithoutNotify(m_ManagerChoices[0]);
                m_ManagerSelector.SetEnabled(false);
            }

            m_Actions?.SetEnabled(false);
            ClearRows();
        }

        private void ClearRows()
        {
            m_WindowBuffer.Clear();
            m_QueueBuffer.Clear();
            for (int i = 0; i < m_LayerSections.Length; i++)
            {
                m_LayerSections[i]?.Clear();
            }
            m_InactiveSection?.Clear();
            for (int i = 0; i < m_QueueRows.Count; i++)
            {
                m_QueueRows[i].text = string.Empty;
                m_QueueRows[i].AddToClassList("monitor-hidden");
            }
            if (m_QueueHeader != null)
            {
                m_QueueHeader.text = "等待队列 — 0 项";
                m_QueueEmpty.RemoveFromClassList("monitor-hidden");
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
            private readonly ViewMonitorWindow m_Window;
            private readonly string m_Title;
            private readonly Label m_Header;
            private readonly Label m_Empty;
            private readonly List<WindowRow> m_Rows = new();

            public WindowSection(ViewMonitorWindow window, string name, string title)
            {
                m_Window = window;
                m_Title = title;
                Root = new Box { name = name };
                Root.AddToClassList("monitor-section");
                m_Header = CreateLabel(title, "monitor-header");
                m_Empty = new Label("当前没有窗口。");
                Root.Add(m_Header);
                Root.Add(m_Empty);
            }

            public void Bind(IViewManagerDiagnostics manager, List<WindowDiagnosticSnapshot> snapshots, bool reverse)
            {
                m_Header.text = $"{m_Title} — {snapshots.Count} 项";
                m_Empty.EnableInClassList("monitor-hidden", snapshots.Count != 0);
                for (int i = 0; i < snapshots.Count; i++)
                {
                    if (i == m_Rows.Count)
                    {
                        var row = new WindowRow(m_Window);
                        m_Rows.Add(row);
                        Root.Add(row.Root);
                    }
                    m_Rows[i].Bind(manager, snapshots[reverse ? snapshots.Count - i - 1 : i], reverse && i == 0);
                }
                for (int i = snapshots.Count; i < m_Rows.Count; i++)
                {
                    m_Rows[i].Clear();
                }
            }

            public void Clear()
            {
                m_Header.text = m_Title + " — 0 项";
                m_Empty.RemoveFromClassList("monitor-hidden");
                for (int i = 0; i < m_Rows.Count; i++)
                {
                    m_Rows[i].Clear();
                }
            }
        }

        private sealed class WindowRow
        {
            public readonly VisualElement Root = new();
            private readonly Label m_Info;
            private readonly Button m_Ping;
            private readonly Button m_Close;
            private IViewManagerDiagnostics m_Manager;
            private int m_InstanceId;

            public WindowRow(ViewMonitorWindow window)
            {
                Root.AddToClassList("monitor-row");
                m_Info = CreateLabel(string.Empty, "monitor-row-info");
                m_Ping = CreateButton("定位", "ping", () => window.ExecuteRow(m_Manager, m_InstanceId, false));
                m_Close = CreateButton("关闭", "close", () => window.ExecuteRow(m_Manager, m_InstanceId, true));
                Root.Add(m_Info);
                Root.Add(m_Ping);
                Root.Add(m_Close);
            }

            public void Bind(IViewManagerDiagnostics manager, WindowDiagnosticSnapshot snapshot, bool top)
            {
                m_Manager = manager;
                m_InstanceId = snapshot.InstanceId;
                string timer = snapshot.State == WindowState.PendingDestroy
                    ? $" · 销毁倒计时 {snapshot.RemainingDestroyTime:F1}s" : string.Empty;
                string transition = snapshot.IsTransitioning ? " · 过渡中" : string.Empty;
                m_Info.text = $"#{snapshot.InstanceId} {(top ? "[栈顶] " : string.Empty)}{snapshot.ViewName} [{snapshot.State}]{transition}\n" +
                              $"Order:{snapshot.SortingOrder} · 子Canvas:{snapshot.SubCanvasCount} · " +
                              $"{snapshot.InstanceMode}/{snapshot.CoverMode}/{snapshot.CloseStrategy} · " +
                              $"允许返回:{snapshot.AllowNavigateBack}{timer}";
                m_Ping.SetEnabled(snapshot.HostObject != null);
                m_Close.SetEnabled(CanClose(snapshot.State));
                Root.RemoveFromClassList("monitor-hidden");
            }

            public void Clear()
            {
                m_Manager = null;
                m_InstanceId = 0;
                m_Info.text = string.Empty;
                m_Ping.SetEnabled(false);
                m_Close.SetEnabled(false);
                Root.AddToClassList("monitor-hidden");
            }
        }
    }
}
