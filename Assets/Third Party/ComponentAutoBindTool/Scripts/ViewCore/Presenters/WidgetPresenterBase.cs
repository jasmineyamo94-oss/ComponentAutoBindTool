using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using VContainer;

// ReSharper disable once CheckNamespace
namespace ComponentAutoBindTool.ViewCore
{
    public abstract class WidgetPresenterBase<TUIView> : IWidgetPresenter
        where TUIView : class
    {
        protected TUIView View { get; private set; }
        protected IWindowContext WindowContext { get; private set; }
        private IObjectResolver m_WindowScope;

        public bool IsVisible { get; private set; } = true;
        private bool m_IsOpenSessionActive;
        private bool m_IsResumed;
        private bool m_IsParentResumed;
        private bool m_IsPausing;
        private bool m_IsClosing;
        private bool m_IsDestroying;
        private bool m_IsDestroyed;
        private bool m_CloseRequested;
        private bool m_DestroyRequested;

        private readonly List<IDisposable> m_InstanceDisposables = new();
        private readonly List<IDisposable> m_OpenSessionDisposables = new();
        private CancellationTokenSource m_InstanceCts = new();
        private CancellationTokenSource m_OpenSessionCts;

        private readonly List<IWidgetPresenter> m_ChildWidgets = new();

        protected CancellationToken DestroyToken => 
            m_InstanceCts?.Token ?? CancellationToken.None;
        protected CancellationToken OpenSessionToken => 
            m_OpenSessionCts?.Token ?? CancellationToken.None;

        protected void AddToInstanceLifetime(IDisposable disposable)
        {
            if (disposable != null) m_InstanceDisposables.Add(disposable);
        }

        protected void AddToOpenSession(IDisposable disposable)
        {
            if (disposable != null) m_OpenSessionDisposables.Add(disposable);
        }

        /// <summary>
        /// 在当前 Widget 下继续绑定嵌套子 Widget（复用所属窗口 Scope，零额外容器开销）
        /// </summary>
        protected TChildPresenter BindWidget<TChildPresenter>(WidgetViewBase childWidgetHost)
            where TChildPresenter : class, IWidgetPresenter
        {
            if (m_IsClosing || m_IsDestroying || m_IsDestroyed)
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

            var childPresenter = m_WindowScope.Resolve<TChildPresenter>();
            childPresenter.InternalAttach(childUIView, WindowContext, m_WindowScope);

            m_ChildWidgets.Add(childPresenter);
            childPresenter.InternalOnCreate();

            if (m_IsOpenSessionActive)
            {
                childPresenter.InternalOnOpen();
                if (m_IsResumed)
                {
                    childPresenter.InternalOnResume();
                }
            }
            return childPresenter;
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
            m_WindowScope = windowScope;
        }

        void IWidgetPresenter.InternalOnCreate() => OnCreate();

        void IWidgetPresenter.InternalOnOpen()
        {
            if (m_IsClosing || m_IsDestroying || m_IsDestroyed) return;
            ClearOpenSession();
            m_IsOpenSessionActive = true;
            m_OpenSessionCts = new CancellationTokenSource();

            OnOpen();

            for (int i = 0; i < m_ChildWidgets.Count; i++)
            {
                m_ChildWidgets[i].InternalOnOpen();
            }
        }

        void IWidgetPresenter.InternalOnResume()
        {
            if (m_IsClosing || m_IsDestroying || m_IsDestroyed) return;
            m_IsParentResumed = true;
            RefreshEffectiveState();
        }

        void IWidgetPresenter.InternalOnPause()
        {
            m_IsParentResumed = false;
            RefreshEffectiveState();
        }

        private void RefreshEffectiveState()
        {
            bool active = m_IsOpenSessionActive && m_IsParentResumed && IsVisible;
            if (m_IsResumed == active) return;
            if (active)
            {
                m_IsResumed = true;
                OnResume();
                for (int i = 0; i < m_ChildWidgets.Count && m_IsResumed; i++)
                {
                    m_ChildWidgets[i].InternalOnResume();
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
            if (!m_IsOpenSessionActive || m_IsClosing || m_IsDestroyed) return;
            if (m_IsPausing)
            {
                m_CloseRequested = true;
                return;
            }

            m_IsClosing = true;
            m_CloseRequested = false;
            // 先让会话失效，回调中再次 Close 不会进入同一轮关闭。
            m_IsOpenSessionActive = false;
            m_IsParentResumed = false;
            try
            {
                PauseRunningSubtree(ref exceptions);
                for (int i = m_ChildWidgets.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        m_ChildWidgets[i].InternalOnClose();
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
                m_IsClosing = false;
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
            if (m_IsDestroyed || m_IsDestroying) return;
            if (m_IsClosing || m_IsPausing)
            {
                // 让当前子树的 Pause/Close 完成后再进入销毁阶段。
                m_DestroyRequested = true;
                return;
            }

            m_IsDestroying = true;
            m_DestroyRequested = false;
            try
            {
                CloseOpenSession(ref exceptions);
                for (int i = m_ChildWidgets.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        m_ChildWidgets[i].InternalOnDestroy();
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
                m_ChildWidgets.Clear();
                m_IsOpenSessionActive = false;
                m_IsResumed = false;
                m_IsParentResumed = false;
                ClearOpenSession(ref exceptions);
                CancelAndDispose(ref m_InstanceCts, ref exceptions);
                ReleaseDisposables(m_InstanceDisposables, ref exceptions);
                m_CloseRequested = false;
                m_DestroyRequested = false;
                m_IsDestroyed = true;
                m_IsDestroying = false;
            }
        }

        private void PauseRunningSubtree(ref List<Exception> exceptions)
        {
            if (!m_IsResumed) return;

            m_IsResumed = false;
            m_IsPausing = true;
            try
            {
                for (int i = 0; i < m_ChildWidgets.Count; i++)
                {
                    try
                    {
                        m_ChildWidgets[i].InternalOnPause();
                    }
                    catch (Exception exception)
                    {
                        AddException(ref exceptions, exception);
                    }
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
                m_IsPausing = false;
            }
        }

        private void CompleteRequestedTransitions(ref List<Exception> exceptions)
        {
            if (m_IsPausing || m_IsClosing || m_IsDestroying || m_IsDestroyed) return;
            if (m_DestroyRequested)
            {
                Destroy(ref exceptions);
            }
            else if (m_CloseRequested)
            {
                CloseOpenSession(ref exceptions);
                if (m_DestroyRequested) Destroy(ref exceptions);
            }
        }

        void IWidgetPresenter.InternalTick(float deltaTime)
        {
            if (!IsVisible || !m_IsOpenSessionActive || !m_IsResumed) return;

            OnWidgetTick(deltaTime);
            for (int i = 0; i < m_ChildWidgets.Count; i++)
            {
                m_ChildWidgets[i].InternalTick(deltaTime);
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
            CancelAndDispose(ref m_OpenSessionCts, ref exceptions);
            ReleaseDisposables(m_OpenSessionDisposables, ref exceptions);
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
