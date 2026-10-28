using System;
using System.Collections.Generic;
using UnityEngine;
using VContainer;

namespace ComponentAutoBindTool.ViewCore
{
    public sealed class WindowEntry : IWindowContext, IDisposable
    {
        public int InstanceId { get; }
        public string ViewName { get; }
        public ViewBase ViewHost { get; }
        public GameObject GameObject => ViewHost != null ? ViewHost.gameObject : null;
        public UILayer Layer => ViewHost.Layer;
        public WindowState State { get; private set; } = WindowState.Constructing;

        public IWindowPresenter Presenter { get; private set; }

        private readonly IViewManager m_ViewManager;
        private readonly Canvas m_RootCanvas;
        private readonly List<(Canvas canvas, int relativeOffset)> m_SubCanvases = new();
        private IScopedObjectResolver m_ChildScope;
        private bool m_IsDisposed;

        public WindowEntry(int instanceId, string viewName, ViewBase viewHost, IViewManager viewManager)
        {
            InstanceId = instanceId;
            ViewName = viewName;
            ViewHost = viewHost;
            m_ViewManager = viewManager;

            m_RootCanvas = viewHost.GetComponent<Canvas>();
            if (m_RootCanvas != null)
            {
                m_RootCanvas.overrideSorting = true;
                int rootBaseOrder = m_RootCanvas.sortingOrder;

                // 收集所有子 Canvas 相对于根 Canvas 的初始排序偏移（解决 A-P08）
                var allCanvases = viewHost.GetComponentsInChildren<Canvas>(true);
                for (int i = 0; i < allCanvases.Length; i++)
                {
                    var c = allCanvases[i];
                    if (c != m_RootCanvas)
                    {
                        m_SubCanvases.Add((c, c.sortingOrder - rootBaseOrder));
                    }
                }
            }
        }

        /// <summary>
        /// 显式执行静态绑定并从父容器创建窗口子 Scope（解决 A-P03）
        /// </summary>
        public void BuildScopeAndCreate(IObjectResolver parentResolver)
        {
            // 1. 显式调用静态组件绑定，生成 UIView
            ((IAutoBindHost)ViewHost).EnsureAutoBind(ViewHost.gameObject);

            // 2. 通过父容器纯代码创建窗口子容器作用域
            m_ChildScope = parentResolver.CreateScope(builder =>
            {
                builder.RegisterInstance<IWindowContext>(this);
                builder.RegisterInstance<IViewManager>(m_ViewManager);
                ViewHost.ConfigureWindow(builder);
            });

            // 3. 解析 Presenter 并完成关联与首次创建初始化
            Presenter = m_ChildScope.Resolve<IWindowPresenter>();
            Presenter.InternalAttach(this, m_ChildScope);
            Presenter.InternalOnCreate();
        }

        /// <summary>
        /// 设置根 Canvas 排序并按初始相对差值同步平移所有子 Canvas（解决 A-P08）
        /// </summary>
        public void SetSortingOrder(int rootSortingOrder)
        {
            if (m_RootCanvas != null)
            {
                m_RootCanvas.sortingOrder = rootSortingOrder;
            }

            for (int i = 0; i < m_SubCanvases.Count; i++)
            {
                var (subCanvas, offset) = m_SubCanvases[i];
                if (subCanvas != null)
                {
                    subCanvas.sortingOrder = rootSortingOrder + offset;
                }
            }
        }

        public void DeliverOpenArgs(IViewArgs args)
        {
            Presenter?.InternalOnOpen(args ?? EmptyViewArgs.Instance);
        }

        public void SetActiveAndResume()
        {
            if (State == WindowState.Destroyed) return;

            ViewHost.SetViewVisible(true);
            if (State != WindowState.OpenAndActive)
            {
                State = WindowState.OpenAndActive;
                Presenter?.InternalOnResume();
            }
        }

        public void Pause()
        {
            if (State == WindowState.OpenAndActive)
            {
                State = WindowState.Paused;
                Presenter?.InternalOnPause();
                ViewHost.SetViewVisible(false);
            }
        }

        public void EnterClosedState(WindowState closedTargetState)
        {
            if (State == WindowState.Destroyed) return;

            if (State == WindowState.OpenAndActive)
            {
                Presenter?.InternalOnPause();
            }

            State = closedTargetState;
            Presenter?.InternalOnClose();
            if (ViewHost != null)
            {
                ViewHost.SetViewVisible(false);
            }
        }

        public void Tick(float deltaTime)
        {
            if (State == WindowState.OpenAndActive)
            {
                Presenter?.InternalTick(deltaTime);
            }
        }

        public void CloseSelf()
        {
            m_ViewManager.CloseView(InstanceId);
        }

        public void Dispose()
        {
            // DestroyImmediate 已先切换状态；状态不能代表 Presenter 与 Scope 已释放。
            if (m_IsDisposed) return;
            m_IsDisposed = true;

            if (State == WindowState.OpenAndActive || State == WindowState.Paused)
            {
                EnterClosedState(WindowState.Destroyed);
            }
            State = WindowState.Destroyed;

            try
            {
                Presenter?.InternalOnDestroy();
            }
            finally
            {
                Presenter = null;
                m_ChildScope?.Dispose();
                m_ChildScope = null;
            }
        }
    }
}
