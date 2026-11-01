using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using VContainer;

namespace ComponentAutoBindTool.ViewCore
{
    /// <summary>
    /// 窗口 Presenter 泛型基类（支持强类型开窗参数 TArgs）
    /// </summary>
    public abstract class WindowPresenterBase<TUIView, TArgs> : IWindowPresenter, IWindowArgsContract
        where TUIView : class
        where TArgs : class, IViewArgs
    {
        protected TUIView View { get; }
        public Type ArgsType => typeof(TArgs);
        protected IWindowContext Context { get; private set; }
        private IObjectResolver _windowScope;

        private bool _isOpenSessionActive;
        private bool _isResumed;
        private bool _isPausing;
        private bool _isClosing;
        private bool _isDestroying;
        private bool _isDestroyed;
        private bool _closeRequested;
        private bool _destroyRequested;

        // 1. 实例存活期清理袋（OnCreate -> OnDestroy，在 NonDestroy 缓存期间保留）
        private readonly List<IDisposable> _instanceDisposables = new();
        private CancellationTokenSource _instanceCts = new();

        // 2. 打开活动期清理袋（OnOpen -> OnClose，每次窗口关闭进缓存或重复刷新时强制自动清空）
        private readonly List<IDisposable> _openSessionDisposables = new();
        private CancellationTokenSource _openSessionCts;

        // 3. 归属于当前窗口的逻辑子控件树
        private readonly List<IWidgetPresenter> _childWidgets = new();

        protected CancellationToken DestroyToken => _instanceCts?.Token ?? CancellationToken.None;
        protected CancellationToken OpenSessionToken => _openSessionCts?.Token ?? CancellationToken.None;

        protected WindowPresenterBase(TUIView view)
        {
            View = view;
        }

        protected void AddToInstanceLifetime(IDisposable disposable)
        {
            if (disposable != null) _instanceDisposables.Add(disposable);
        }

        protected void AddToOpenSession(IDisposable disposable)
        {
            if (disposable != null) _openSessionDisposables.Add(disposable);
        }

        /// <summary>
        /// 绑定窗口内的子控件 Widget（共享当前窗口子容器 Scope，自动纳入窗口生命周期级联树）
        /// </summary>
        protected TWidgetPresenter BindWidget<TWidgetPresenter>(WidgetViewBase widgetHost)
            where TWidgetPresenter : class, IWidgetPresenter
        {
            if (_isClosing || _isDestroying || _isDestroyed)
            {
                throw new InvalidOperationException("正在关闭或已销毁的窗口不能再绑定子控件。");
            }
            if (widgetHost == null) throw new ArgumentNullException(nameof(widgetHost));

            // 1. 显式触发子控件的静态绑定
            ((IAutoBindHost)widgetHost).EnsureAutoBind(widgetHost.gameObject);
            object widgetUIView = widgetHost.GetGeneratedUIView();
            if (widgetUIView == null)
            {
                throw new InvalidOperationException($"[{widgetHost.name}] Widget 绑定失败，不能创建 Presenter。");
            }

            // 2. 从当前窗口子容器解析 WidgetPresenter（自动注入全局/窗口服务）
            var widgetPresenter = _windowScope.Resolve<TWidgetPresenter>();
            widgetPresenter.InternalAttach(widgetUIView, Context, _windowScope);

            // 3. 加入逻辑树并同步当前窗口所处生命周期阶段
            _childWidgets.Add(widgetPresenter);
            widgetPresenter.InternalOnCreate();

            if (_isOpenSessionActive)
            {
                widgetPresenter.InternalOnOpen();
                if (_isResumed)
                {
                    widgetPresenter.InternalOnResume();
                }
            }

            return widgetPresenter;
        }

        /// <summary>
        /// 创建随父实例释放的动态 Widget 复用池。
        /// </summary>
        protected WidgetPresenterPool<THost, TPresenter> CreateWidgetPool<THost, TPresenter>(
            THost template, UnityEngine.Transform parent)
            where THost : WidgetViewBase
            where TPresenter : class, IWidgetPresenter, IWidgetVisibility
        {
            var pool = new WidgetPresenterPool<THost, TPresenter>(template, parent,
                host => BindWidget<TPresenter>(host), presenter => UnbindWidget(presenter));
            AddToInstanceLifetime(pool);
            return pool;
        }

        /// <summary>动态解绑并销毁指定的子控件 Widget。</summary>
        protected void UnbindWidget(IWidgetPresenter widgetPresenter)
        {
            if (widgetPresenter != null && _childWidgets.Remove(widgetPresenter))
            {
                widgetPresenter.InternalOnDestroy();
            }
        }

        void IWindowPresenter.InternalAttach(IWindowContext context, IObjectResolver windowScope)
        {
            Context = context;
            _windowScope = windowScope;
        }

        void IWindowPresenter.InternalOnCreate() => OnCreate();

        void IWindowPresenter.InternalOnOpen(IViewArgs args)
        {
            if (_isClosing || _isDestroying || _isDestroyed) return;
            // 若在已打开态重复调用 ShowView 刷新数据，先重置上一轮 OpenSession 订阅
            ClearOpenSession();
            if (_isDestroyed) return;
            _isOpenSessionActive = true;
            _openSessionCts = new CancellationTokenSource();

            OnOpen(args as TArgs);

            for (int i = 0; i < _childWidgets.Count && _isOpenSessionActive;)
            {
                IWidgetPresenter child = _childWidgets[i];
                child.InternalOnOpen();
                if (i < _childWidgets.Count && ReferenceEquals(child, _childWidgets[i])) i++;
            }
        }

        void IWindowPresenter.InternalOnResume()
        {
            if (!_isOpenSessionActive || _isClosing || _isDestroying || _isDestroyed || _isResumed) return;
            _isResumed = true;
            OnResume();

            for (int i = 0; i < _childWidgets.Count && _isResumed;)
            {
                IWidgetPresenter child = _childWidgets[i];
                child.InternalOnResume();
                if (i < _childWidgets.Count && ReferenceEquals(child, _childWidgets[i])) i++;
            }
        }

        void IWindowPresenter.InternalOnPause()
        {
            List<Exception> exceptions = null;
            PauseRunningSubtree(ref exceptions);
            CompleteRequestedTransitions(ref exceptions);
            ThrowExceptions(exceptions);
        }

        void IWindowPresenter.InternalOnClose()
        {
            List<Exception> exceptions = null;
            CloseOpenSession(ref exceptions);
            CompleteRequestedTransitions(ref exceptions);
            ThrowExceptions(exceptions);
        }

        private void CloseOpenSession(ref List<Exception> exceptions)
        {
            if (!_isOpenSessionActive || _isClosing || _isDestroyed) return;
            if (_isPausing)
            {
                _closeRequested = true;
                return;
            }

            _isClosing = true;
            _closeRequested = false;
            // 先让会话失效，回调中的 Close 不会重复进入当前关闭阶段。
            _isOpenSessionActive = false;
            try
            {
                PauseRunningSubtree(ref exceptions);
                for (int i = _childWidgets.Count - 1; i >= 0;)
                {
                    IWidgetPresenter child = _childWidgets[i];
                    try
                    {
                        child.InternalOnClose();
                    }
                    catch (Exception exception)
                    {
                        AddException(ref exceptions, exception);
                    }
                    if (i < _childWidgets.Count && ReferenceEquals(child, _childWidgets[i]))
                    {
                        i--;
                    }
                    else
                    {
                        int movedIndex = _childWidgets.IndexOf(child);
                        i = movedIndex >= 0 ? movedIndex - 1 : Math.Min(i - 1, _childWidgets.Count - 1);
                    }
                }
                try
                {
                    OnClose();
                }
                catch (Exception exception)
                {
                    AddException(ref exceptions, exception);
                }
            }
            finally
            {
                ClearOpenSession(ref exceptions);
                _isClosing = false;
            }
        }

        void IWindowPresenter.InternalOnDestroy()
        {
            List<Exception> exceptions = null;
            Destroy(ref exceptions);
            CompleteRequestedTransitions(ref exceptions);
            ThrowExceptions(exceptions);
        }

        private void Destroy(ref List<Exception> exceptions)
        {
            if (_isDestroyed || _isDestroying) return;
            if (_isClosing || _isPausing)
            {
                // 等当前子树 Pause/Close 完成后再销毁，避免阶段交错。
                _destroyRequested = true;
                return;
            }

            _isDestroying = true;
            _destroyRequested = false;
            try
            {
                CloseOpenSession(ref exceptions);
                while (_childWidgets.Count > 0)
                {
                    int index = _childWidgets.Count - 1;
                    IWidgetPresenter child = _childWidgets[index];
                    _childWidgets.RemoveAt(index);
                    try
                    {
                        child.InternalOnDestroy();
                    }
                    catch (Exception exception)
                    {
                        AddException(ref exceptions, exception);
                    }
                }
                try
                {
                    OnDestroy();
                }
                catch (Exception exception)
                {
                    AddException(ref exceptions, exception);
                }
            }
            finally
            {
                _isOpenSessionActive = false;
                _isResumed = false;
                ClearOpenSession(ref exceptions);
                CancelAndDispose(ref _instanceCts, ref exceptions);
                ReleaseDisposables(_instanceDisposables, ref exceptions);
                _closeRequested = false;
                _destroyRequested = false;
                _isDestroyed = true;
                _isDestroying = false;
            }
        }

        private void PauseRunningSubtree(ref List<Exception> exceptions)
        {
            if (!_isResumed) return;

            _isResumed = false;
            _isPausing = true;
            try
            {
                for (int i = 0; i < _childWidgets.Count;)
                {
                    IWidgetPresenter child = _childWidgets[i];
                    try
                    {
                        child.InternalOnPause();
                    }
                    catch (Exception exception)
                    {
                        AddException(ref exceptions, exception);
                    }
                    // 子控件可在回调中解绑自身，索引不能跳过移入当前位置的兄弟。
                    if (i < _childWidgets.Count && ReferenceEquals(child, _childWidgets[i])) i++;
                }
                try
                {
                    OnPause();
                }
                catch (Exception exception)
                {
                    AddException(ref exceptions, exception);
                }
            }
            finally
            {
                _isPausing = false;
            }
        }

        private void CompleteRequestedTransitions(ref List<Exception> exceptions)
        {
            if (_isPausing || _isClosing || _isDestroying || _isDestroyed) return;
            if (_destroyRequested)
            {
                Destroy(ref exceptions);
            }
            else if (_closeRequested)
            {
                CloseOpenSession(ref exceptions);
                if (_destroyRequested) Destroy(ref exceptions);
            }
        }

        void IWindowPresenter.InternalTick(float deltaTime)
        {
            if (!_isOpenSessionActive || !_isResumed) return;

            OnWindowTick(deltaTime);
            for (int i = 0; i < _childWidgets.Count; i++)
            {
                _childWidgets[i].InternalTick(deltaTime);
            }
        }

        private void ClearOpenSession()
        {
            List<Exception> exceptions = null;
            ClearOpenSession(ref exceptions);
            ThrowExceptions(exceptions);
        }

        private void ClearOpenSession(ref List<Exception> exceptions)
        {
            CancelAndDispose(ref _openSessionCts, ref exceptions);
            ReleaseDisposables(_openSessionDisposables, ref exceptions);
        }

        private static void CancelAndDispose(ref CancellationTokenSource source, ref List<Exception> exceptions)
        {
            CancellationTokenSource ownedSource = source;
            // 取消回调可能重入，先转移所有权再调用用户代码。
            source = null;
            if (ownedSource == null) return;
            try
            {
                ownedSource.Cancel();
            }
            catch (Exception exception)
            {
                AddException(ref exceptions, exception);
            }
            finally
            {
                ownedSource.Dispose();
            }
        }

        private static void ReleaseDisposables(List<IDisposable> disposables, ref List<Exception> exceptions)
        {
            while (disposables.Count > 0)
            {
                int index = disposables.Count - 1;
                IDisposable disposable = disposables[index];
                disposables.RemoveAt(index);
                try
                {
                    disposable?.Dispose();
                }
                catch (Exception exception)
                {
                    AddException(ref exceptions, exception);
                }
            }
        }

        private static void AddException(ref List<Exception> exceptions, Exception exception)
        {
            exceptions ??= new List<Exception>();
            exceptions.Add(exception);
        }

        private static void ThrowExceptions(List<Exception> exceptions)
        {
            if (exceptions == null) return;
            if (exceptions.Count == 1)
            {
                ExceptionDispatchInfo.Capture(exceptions[0]).Throw();
            }
            throw new AggregateException("窗口生命周期回调或资源释放失败。", exceptions);
        }

        protected virtual void OnCreate() { }
        protected abstract void OnOpen(TArgs args);
        protected virtual void OnResume() { }
        protected virtual void OnPause() { }
        protected virtual void OnClose() { }
        protected virtual void OnDestroy() { }
        protected virtual void OnWindowTick(float deltaTime) { }
    }

    /// <summary>
    /// 无需特定强类型参数（或使用通用 IViewArgs / LegacyViewArgs）的便捷窗口 Presenter 基类
    /// </summary>
    public abstract class WindowPresenterBase<TUIView> : WindowPresenterBase<TUIView, IViewArgs>
        where TUIView : class
    {
        protected WindowPresenterBase(TUIView view) : base(view) { }
    }
}
