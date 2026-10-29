using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
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
        public UILayer Layer { get; }
        public WindowState State { get; private set; } = WindowState.Constructing;
        public bool IsTransitioning { get; private set; }
        public int RootSortingOrder => m_RootCanvas != null ? m_RootCanvas.sortingOrder : 0;
        public int SubCanvasCount => m_SubCanvases.Count;

        public IWindowPresenter Presenter { get; private set; }

        private readonly IViewManager m_ViewManager;
        private readonly Canvas m_RootCanvas;
        private readonly List<(Canvas canvas, int relativeOffset)> m_SubCanvases = new();
        private IScopedObjectResolver m_ChildScope;
        private bool m_IsDisposed;
        private int m_TransitionVersion;
        internal bool IsClosing { get; private set; }
        internal bool HasOpenSession { get; private set; }

        public WindowEntry(int instanceId, string viewName, ViewBase viewHost, IViewManager viewManager)
        {
            InstanceId = instanceId;
            ViewName = viewName;
            ViewHost = viewHost;
            Layer = viewHost.Layer;
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
            if (m_IsDisposed || IsClosing) return;
            HasOpenSession = true;
            Presenter?.InternalOnOpen(args ?? EmptyViewArgs.Instance);
        }

        public void SetActiveAndResume()
        {
            // 重算其他窗口的排序不能重播本窗口正在进行的入场。
            if (m_IsDisposed || IsClosing || State == WindowState.Destroyed || State == WindowState.OpenAndActive) return;
            int version = ++m_TransitionVersion;
            IsTransitioning = false;
            State = WindowState.OpenAndActive;
            List<Exception> errors = null;
            try { ViewHost.CancelTransition(); }
            catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
            if (version == m_TransitionVersion && !m_IsDisposed)
            {
                ViewHost.SetViewVisible(true);
                ViewHost.SetInteractable(false);
                try { Presenter?.InternalOnResume(); }
                catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
                if (version == m_TransitionVersion && State == WindowState.OpenAndActive && !m_IsDisposed)
                {
                    if (ViewHost.HasTransition)
                    {
                        IsTransitioning = true;
                        try { ViewHost.PlayEnterTransition(() => CompleteEnter(version)); }
                        catch (Exception error)
                        {
                            ViewLifecycleErrors.Add(ref errors, error);
                            CompleteEnter(version);
                        }
                    }
                    else ViewHost.SetInteractable(true);
                }
            }
            ViewLifecycleErrors.Throw(errors);
        }

        private void CompleteEnter(int version)
        {
            if (version != m_TransitionVersion || !IsTransitioning || m_IsDisposed || State != WindowState.OpenAndActive) return;
            IsTransitioning = false;
            if (ViewHost != null) ViewHost.SetInteractable(true);
        }

        public void Pause()
        {
            if (State == WindowState.OpenAndActive)
            {
                int version = ++m_TransitionVersion;
                IsTransitioning = false;
                State = WindowState.Paused;
                List<Exception> errors = null;
                try { ViewHost.CancelTransition(); }
                catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
                try { Presenter?.InternalOnPause(); }
                catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
                if (version == m_TransitionVersion && !m_IsDisposed && ViewHost != null)
                {
                    ViewHost.SetInteractable(false);
                    ViewHost.SetViewVisible(false);
                }
                ViewLifecycleErrors.Throw(errors);
            }
        }

        public void EnterClosedState(WindowState closedTargetState) => EnterClosedState(closedTargetState, null);

        public void EnterClosedState(WindowState closedTargetState, Action onExitTransitionComplete)
        {
            if (m_IsDisposed || IsClosing || State == WindowState.Destroyed ||
                (!HasOpenSession && (State == WindowState.Cached || State == WindowState.PendingDestroy))) return;
            int version = ++m_TransitionVersion;
            bool wasActive = State == WindowState.OpenAndActive;
            State = closedTargetState;
            HasOpenSession = false;
            IsTransitioning = false;
            IsClosing = true;
            List<Exception> errors = null;
            try
            {
                try { if (ViewHost != null) ViewHost.CancelTransition(); }
                catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
                if (ViewHost != null) ViewHost.SetInteractable(false);
                if (wasActive)
                {
                    try { Presenter?.InternalOnPause(); }
                    catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
                }
                try { Presenter?.InternalOnClose(); }
                catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
            }
            finally { IsClosing = false; }
            if (version == m_TransitionVersion && !m_IsDisposed)
            {
                IsTransitioning = true;
                try
                {
                    if (ViewHost != null && ViewHost.HasTransition && ViewHost.gameObject.activeInHierarchy)
                        ViewHost.PlayExitTransition(() => CompleteExit(version, onExitTransitionComplete));
                    else CompleteExit(version, onExitTransitionComplete);
                }
                catch (Exception error)
                {
                    ViewLifecycleErrors.Add(ref errors, error);
                    try { CompleteExit(version, onExitTransitionComplete); }
                    catch (Exception cleanupError) { ViewLifecycleErrors.Add(ref errors, cleanupError); }
                }
            }
            ViewLifecycleErrors.Throw(errors);
        }

        private void CompleteExit(int version, Action onCompleted)
        {
            if (version != m_TransitionVersion || !IsTransitioning || m_IsDisposed) return;
            IsTransitioning = false;
            try { if (ViewHost != null) ViewHost.SetViewVisible(false); }
            finally { onCompleted?.Invoke(); }
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

        public Awaitable WaitClosedAsync(CancellationToken cancellationToken = default) =>
            m_ViewManager.WaitViewClosedAsync(InstanceId, cancellationToken);

        public void Dispose()
        {
            // DestroyImmediate 已先切换状态；状态不能代表 Presenter 与 Scope 已释放。
            if (m_IsDisposed) return;
            m_IsDisposed = true;
            ++m_TransitionVersion;
            IsTransitioning = false;
            bool wasActive = State == WindowState.OpenAndActive;
            bool wasOpen = HasOpenSession || wasActive || State == WindowState.Paused;
            HasOpenSession = false;
            State = WindowState.Destroyed;
            List<Exception> errors = null;
            try
            {
                try
                {
                    if (ViewHost != null)
                    {
                        ViewHost.CancelTransition();
                        ViewHost.SetInteractable(false);
                        ViewHost.SetViewVisible(false);
                    }
                }
                catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
                if (wasActive)
                {
                    try { Presenter?.InternalOnPause(); }
                    catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
                }
                if (wasOpen)
                {
                    try { Presenter?.InternalOnClose(); }
                    catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
                }
                try { Presenter?.InternalOnDestroy(); }
                catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
            }
            finally
            {
                Presenter = null;
                var scope = m_ChildScope;
                m_ChildScope = null;
                try { scope?.Dispose(); }
                catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
            }
            ViewLifecycleErrors.Throw(errors);
        }
    }

    internal static class ViewLifecycleErrors
    {
        internal static void Add(ref List<Exception> errors, Exception error)
        {
            errors ??= new List<Exception>();
            errors.Add(error);
        }

        internal static void Throw(List<Exception> errors)
        {
            if (errors == null) return;
            if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
            throw new AggregateException("窗口生命周期或资源释放失败。", errors);
        }
    }
}
