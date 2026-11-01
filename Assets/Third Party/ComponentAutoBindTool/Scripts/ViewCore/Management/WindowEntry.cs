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
        public int RootSortingOrder => _rootCanvas != null ? _rootCanvas.sortingOrder : 0;
        public int SubCanvasCount => _subCanvases.Count;

        public IWindowPresenter Presenter { get; private set; }

        private readonly IViewManager _viewManager;
        private readonly Canvas _rootCanvas;
        private readonly List<(Canvas canvas, int relativeOffset)> _subCanvases = new();
        private IScopedObjectResolver _childScope;
        private bool _isDisposed;
        private int _transitionVersion;
        internal bool IsClosing { get; private set; }
        internal bool HasOpenSession { get; private set; }

        public WindowEntry(int instanceId, string viewName, ViewBase viewHost, IViewManager viewManager)
        {
            InstanceId = instanceId;
            ViewName = viewName;
            ViewHost = viewHost;
            Layer = viewHost.Layer;
            _viewManager = viewManager;

            _rootCanvas = viewHost.GetComponent<Canvas>();
            if (_rootCanvas != null)
            {
                _rootCanvas.overrideSorting = true;
                int rootBaseOrder = _rootCanvas.sortingOrder;

                // 收集所有子 Canvas 相对于根 Canvas 的初始排序偏移（解决 A-P08）
                var allCanvases = viewHost.GetComponentsInChildren<Canvas>(true);
                for (int i = 0; i < allCanvases.Length; i++)
                {
                    var c = allCanvases[i];
                    if (c != _rootCanvas)
                    {
                        _subCanvases.Add((c, c.sortingOrder - rootBaseOrder));
                    }
                }
            }
        }

        /// <summary>
        /// 显式执行静态绑定并从父容器创建窗口子 Scope（解决 A-P03）
        /// </summary>
        public void BuildScopeAndCreate(IObjectResolver parentResolver) => BuildScopeAndCreate(parentResolver, null);

        internal bool BuildScopeAndCreate(IObjectResolver parentResolver, ViewRoute route)
        {
            // 1. 显式调用静态组件绑定，生成 UIView
            ((IAutoBindHost)ViewHost).EnsureAutoBind(ViewHost.gameObject);

            // 2. 通过父容器纯代码创建窗口子容器作用域
            _childScope = parentResolver.CreateScope(builder =>
            {
                builder.RegisterInstance<IWindowContext>(this);
                builder.RegisterInstance<IViewManager>(_viewManager);
                ViewHost.ConfigureWindow(builder);
            });

            // 3. 解析 Presenter 并完成关联与首次创建初始化
            var presenter = _childScope.Resolve<IWindowPresenter>();
            if (route != null && !route.ValidatePresenter(presenter)) return false;
            Presenter = presenter;
            Presenter.InternalAttach(this, _childScope);
            Presenter.InternalOnCreate();
            return true;
        }

        /// <summary>
        /// 设置根 Canvas 排序并按初始相对差值同步平移所有子 Canvas（解决 A-P08）
        /// </summary>
        public void SetSortingOrder(int rootSortingOrder)
        {
            if (_rootCanvas != null)
            {
                _rootCanvas.sortingOrder = rootSortingOrder;
            }

            for (int i = 0; i < _subCanvases.Count; i++)
            {
                var (subCanvas, offset) = _subCanvases[i];
                if (subCanvas != null)
                {
                    subCanvas.sortingOrder = rootSortingOrder + offset;
                }
            }
        }

        public void DeliverOpenArgs(IViewArgs args)
        {
            if (_isDisposed || IsClosing) return;
            HasOpenSession = true;
            Presenter?.InternalOnOpen(args ?? EmptyViewArgs.Instance);
        }

        public void SetActiveAndResume()
        {
            // 重算其他窗口的排序不能重播本窗口正在进行的入场。
            if (_isDisposed || IsClosing || State == WindowState.Destroyed || State == WindowState.OpenAndActive) return;
            int version = ++_transitionVersion;
            IsTransitioning = false;
            State = WindowState.OpenAndActive;
            List<Exception> errors = null;
            try { ViewHost.CancelTransition(); }
            catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
            if (version == _transitionVersion && !_isDisposed)
            {
                ViewHost.SetViewVisible(true);
                ViewHost.SetInteractable(false);
                try { Presenter?.InternalOnResume(); }
                catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
                if (version == _transitionVersion && State == WindowState.OpenAndActive && !_isDisposed)
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
            if (version != _transitionVersion || !IsTransitioning || _isDisposed || State != WindowState.OpenAndActive) return;
            IsTransitioning = false;
            if (ViewHost != null) ViewHost.SetInteractable(true);
        }

        public void Pause()
        {
            if (State == WindowState.OpenAndActive)
            {
                int version = ++_transitionVersion;
                IsTransitioning = false;
                State = WindowState.Paused;
                List<Exception> errors = null;
                try { ViewHost.CancelTransition(); }
                catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
                try { Presenter?.InternalOnPause(); }
                catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
                if (version == _transitionVersion && !_isDisposed && ViewHost != null)
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
            if (_isDisposed || IsClosing || State == WindowState.Destroyed ||
                (!HasOpenSession && (State == WindowState.Cached || State == WindowState.PendingDestroy))) return;
            int version = ++_transitionVersion;
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
            if (version == _transitionVersion && !_isDisposed)
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
            if (version != _transitionVersion || !IsTransitioning || _isDisposed) return;
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
            _viewManager.CloseView(InstanceId);
        }

        public Awaitable WaitClosedAsync(CancellationToken cancellationToken = default) =>
            _viewManager.WaitViewClosedAsync(InstanceId, cancellationToken);

        public void Dispose()
        {
            // DestroyImmediate 已先切换状态；状态不能代表 Presenter 与 Scope 已释放。
            if (_isDisposed) return;
            _isDisposed = true;
            ++_transitionVersion;
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
                var scope = _childScope;
                _childScope = null;
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
