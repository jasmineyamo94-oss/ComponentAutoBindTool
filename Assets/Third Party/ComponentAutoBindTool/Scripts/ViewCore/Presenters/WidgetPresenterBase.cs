using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using VContainer;

// ReSharper disable once CheckNamespace
namespace ComponentAutoBindTool.ViewCore
{
    public abstract class WidgetPresenterBase<TUIView> : IWidgetPresenter, IWidgetVisibility
        where TUIView : class
    {
        protected TUIView View { get; private set; }
        protected IWindowContext WindowContext { get; private set; }
        private IObjectResolver _windowScope;

        public bool IsVisible { get; private set; } = true;
        private bool _isOpenSessionActive;
        private bool _isResumed;
        private bool _isParentResumed;
        private bool _isPausing;
        private bool _isClosing;
        private bool _isDestroying;
        private bool _isDestroyed;
        private bool _closeRequested;
        private bool _destroyRequested;

        private readonly List<IDisposable> _instanceDisposables = new();
        private readonly List<IDisposable> _openSessionDisposables = new();
        private CancellationTokenSource _instanceCts = new();
        private CancellationTokenSource _openSessionCts;

        private readonly List<IWidgetPresenter> _childWidgets = new();

        protected CancellationToken DestroyToken => 
            _instanceCts?.Token ?? CancellationToken.None;
        protected CancellationToken OpenSessionToken => 
            _openSessionCts?.Token ?? CancellationToken.None;

        protected void AddToInstanceLifetime(IDisposable disposable)
        {
            if (disposable != null) _instanceDisposables.Add(disposable);
        }

        protected void AddToOpenSession(IDisposable disposable)
        {
            if (disposable != null) _openSessionDisposables.Add(disposable);
        }

        /// <summary>
        /// 在当前 Widget 下继续绑定嵌套子 Widget（复用所属窗口 Scope，零额外容器开销）
        /// </summary>
        protected TChildPresenter BindWidget<TChildPresenter>(WidgetViewBase childWidgetHost)
            where TChildPresenter : class, IWidgetPresenter
        {
            if (_isClosing || _isDestroying || _isDestroyed)
            {
                throw new InvalidOperationException("正在关闭或已销毁的 Widget 不能再绑定子控件。");
            }
            if (childWidgetHost == null) throw new ArgumentNullException(nameof(childWidgetHost));

            ((IAutoBindHost)childWidgetHost).EnsureAutoBind(childWidgetHost.gameObject);
            object childUIView = childWidgetHost.GetGeneratedUIView();
            if (childUIView == null)
            {
                throw new InvalidOperationException($"[{childWidgetHost.name}] Widget 绑定失败，不能创建 Presenter。");
            }

            var childPresenter = _windowScope.Resolve<TChildPresenter>();
            childPresenter.InternalAttach(childUIView, WindowContext, _windowScope);

            _childWidgets.Add(childPresenter);
            childPresenter.InternalOnCreate();

            if (_isOpenSessionActive)
            {
                childPresenter.InternalOnOpen();
                if (_isResumed)
                {
                    childPresenter.InternalOnResume();
                }
            }
            return childPresenter;
        }

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

        protected void UnbindWidget(IWidgetPresenter presenter)
        {
            if (presenter != null && _childWidgets.Remove(presenter)) presenter.InternalOnDestroy();
        }

        /// <summary>
        /// 控制子控件自身的局部显隐，并同步触发 OnResume / OnPause
        /// </summary>
        public void SetVisible(bool visible, WidgetViewBase hostComponent = null)
        {
            if (IsVisible == visible) return;
            IsVisible = visible;
            hostComponent?.SetWidgetVisible(visible);

            RefreshEffectiveState();
        }

        void IWidgetPresenter.InternalAttach(object uiView, IWindowContext windowContext, IObjectResolver windowScope)
        {
            View = (TUIView)uiView;
            WindowContext = windowContext;
            _windowScope = windowScope;
        }

        void IWidgetPresenter.InternalOnCreate() => OnCreate();

        void IWidgetPresenter.InternalOnOpen()
        {
            if (_isClosing || _isDestroying || _isDestroyed) return;
            ClearOpenSession();
            _isOpenSessionActive = true;
            _openSessionCts = new CancellationTokenSource();

            OnOpen();

            for (int i = 0; i < _childWidgets.Count;)
            {
                IWidgetPresenter child = _childWidgets[i];
                child.InternalOnOpen();
                if (i < _childWidgets.Count && ReferenceEquals(child, _childWidgets[i])) i++;
            }
        }

        void IWidgetPresenter.InternalOnResume()
        {
            if (_isClosing || _isDestroying || _isDestroyed) return;
            _isParentResumed = true;
            RefreshEffectiveState();
        }

        void IWidgetPresenter.InternalOnPause()
        {
            _isParentResumed = false;
            RefreshEffectiveState();
        }

        private void RefreshEffectiveState()
        {
            bool active = _isOpenSessionActive && _isParentResumed && IsVisible;
            if (_isResumed == active) return;
            if (active)
            {
                _isResumed = true;
                OnResume();
                for (int i = 0; i < _childWidgets.Count && _isResumed;)
                {
                    IWidgetPresenter child = _childWidgets[i];
                    child.InternalOnResume();
                    if (i < _childWidgets.Count && ReferenceEquals(child, _childWidgets[i])) i++;
                }
            }
            else
            {
                List<Exception> exceptions = null;
                PauseRunningSubtree(ref exceptions);
                CompleteRequestedTransitions(ref exceptions);
                ThrowExceptions(exceptions);
            }
        }

        void IWidgetPresenter.InternalOnClose()
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
            // 先让会话失效，回调中再次 Close 不会进入同一轮关闭。
            _isOpenSessionActive = false;
            _isParentResumed = false;
            try
            {
                PauseRunningSubtree(ref exceptions);
                for (int i = _childWidgets.Count - 1; i >= 0; i--)
                {
                    if (i >= _childWidgets.Count) continue;
                    try
                    {
                        _childWidgets[i].InternalOnClose();
                    }
                    catch (Exception exception)
                    {
                        AddException(ref exceptions, exception);
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

        void IWidgetPresenter.InternalOnDestroy()
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
                // 让当前子树的 Pause/Close 完成后再进入销毁阶段。
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
                _childWidgets.Clear();
                _isOpenSessionActive = false;
                _isResumed = false;
                _isParentResumed = false;
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

        void IWidgetPresenter.InternalTick(float deltaTime)
        {
            if (!IsVisible || !_isOpenSessionActive || !_isResumed) return;

            OnWidgetTick(deltaTime);
            for (int i = 0; i < _childWidgets.Count;)
            {
                IWidgetPresenter child = _childWidgets[i];
                child.InternalTick(deltaTime);
                if (i < _childWidgets.Count && ReferenceEquals(child, _childWidgets[i])) i++;
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
            // 取消回调可以重入生命周期入口，先移走资源所有权。
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
                // 调用用户清理动作前移除，避免重入时重复释放。
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
            throw new AggregateException("Widget 生命周期回调或资源释放失败。", exceptions);
        }

        protected virtual void OnCreate() { }
        protected virtual void OnOpen() { }
        protected virtual void OnResume() { }
        protected virtual void OnPause() { }
        protected virtual void OnClose() { }
        protected virtual void OnDestroy() { }
        protected virtual void OnWidgetTick(float deltaTime) { }
    }
}
