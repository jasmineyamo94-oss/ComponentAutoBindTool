using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace ComponentAutoBindTool.ViewCore
{
    public readonly struct WindowDiagnosticSnapshot
    {
        public readonly int InstanceId;
        public readonly string ViewName;
        public readonly UILayer Layer;
        public readonly WindowState State;
        public readonly WindowInstanceMode InstanceMode;
        public readonly ViewCoverMode CoverMode;
        public readonly ViewCloseStrategy CloseStrategy;
        public readonly int SortingOrder;
        public readonly int SubCanvasCount;
        public readonly float RemainingDestroyTime;
        public readonly bool IsTransitioning;
        public readonly bool AllowNavigateBack;
        public readonly GameObject HostObject;

        public WindowDiagnosticSnapshot(int instanceId, string viewName, UILayer layer, WindowState state,
            WindowInstanceMode instanceMode, ViewCoverMode coverMode, ViewCloseStrategy closeStrategy,
            int sortingOrder, int subCanvasCount, float remainingDestroyTime, bool isTransitioning,
            bool allowNavigateBack, GameObject hostObject)
        {
            InstanceId = instanceId;
            ViewName = viewName;
            Layer = layer;
            State = state;
            InstanceMode = instanceMode;
            CoverMode = coverMode;
            CloseStrategy = closeStrategy;
            SortingOrder = sortingOrder;
            SubCanvasCount = subCanvasCount;
            RemainingDestroyTime = remainingDestroyTime;
            IsTransitioning = isTransitioning;
            AllowNavigateBack = allowNavigateBack;
            HostObject = hostObject;
        }
    }

    public readonly struct QueuedRequestSnapshot
    {
        public readonly int QueueIndex;
        public readonly string ViewName;
        public readonly int Priority;
        public readonly string ArgsTypeName;

        public QueuedRequestSnapshot(int queueIndex, string viewName, int priority, string argsTypeName)
        {
            QueueIndex = queueIndex;
            ViewName = viewName;
            Priority = priority;
            ArgsTypeName = argsTypeName;
        }
    }

    public interface IViewManagerDiagnostics
    {
        bool IsDisposed { get; }
        int TotalInstanceCount { get; }
        int WaitingQueueCount { get; }

        // 实现方清空并复用调用方提供的缓冲区；层快照按栈底到栈顶排列。
        void GetLayerSnapshots(UILayer layer, List<WindowDiagnosticSnapshot> results);
        void GetInactiveSnapshots(List<WindowDiagnosticSnapshot> results);
        void GetWaitingQueueSnapshots(List<QueuedRequestSnapshot> results);
        bool CloseView(int instanceId);
        bool TryNavigateBack();
        void CloseAllViews(bool destroyCached = false);
        void ClearWaitingQueue();
    }

    public static class ViewManagerDebugRegistry
    {
        private static readonly List<IViewManagerDiagnostics> s_ActiveManagers = new();
        private static readonly ReadOnlyCollection<IViewManagerDiagnostics> s_ReadOnlyManagers =
            s_ActiveManagers.AsReadOnly();

        public static IReadOnlyList<IViewManagerDiagnostics> ActiveManagers
        {
            get
            {
                RemoveDisposedManagers();
                return s_ReadOnlyManagers;
            }
        }

        public static void Register(IViewManagerDiagnostics manager)
        {
            RemoveDisposedManagers();
            if (!IsAlive(manager))
            {
                return;
            }

            for (int i = 0; i < s_ActiveManagers.Count; i++)
            {
                if (ReferenceEquals(s_ActiveManagers[i], manager))
                {
                    return;
                }
            }

            s_ActiveManagers.Add(manager);
        }

        public static void Unregister(IViewManagerDiagnostics manager)
        {
            for (int i = s_ActiveManagers.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(s_ActiveManagers[i], manager))
                {
                    s_ActiveManagers.RemoveAt(i);
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            s_ActiveManagers.Clear();
        }

        private static void RemoveDisposedManagers()
        {
            for (int i = s_ActiveManagers.Count - 1; i >= 0; i--)
            {
                if (!IsAlive(s_ActiveManagers[i]))
                {
                    s_ActiveManagers.RemoveAt(i);
                }
            }
        }

        private static bool IsAlive(IViewManagerDiagnostics manager)
        {
            return manager != null && !(manager is Object unityObject && unityObject == null) &&
                   !manager.IsDisposed;
        }
    }
}
