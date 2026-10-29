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
    public abstract class WindowPresenterBase<TUIView, TArgs> : IWindowPresenter
        where TUIView : class
        where TArgs : class, IViewArgs
    {
        protected TUIView View { get; }
        protected IWindowContext Context { get; private set; }
        private IObjectResolver m_WindowScope;

        private bool m_IsOpenSessionActive;
        private bool m_IsResumed;
        private bool m_IsPausing;
        private bool m_IsClosing;
        private bool m_IsDestroying;
        private bool m_IsDestroyed;
        private bool m_CloseRequested;
        private bool m_DestroyRequested;

        // 1. 实例存活期清理袋（OnCreate -> OnDestroy，在 NonDestroy 缓存期间保留）
        private readonly List<IDisposable> m_InstanceDisposables = new();
        private CancellationTokenSource m_InstanceCts = new();

        // 2. 打开活动期清理袋（OnOpen -> OnClose，每次窗口关闭进缓存或重复刷新时强制自动清空）
        private readonly List<IDisposable> m_OpenSessionDisposables = new();
        private CancellationTokenSource m_OpenSessionCts;

        // 3. 归属于当前窗口的逻辑子控件树
        private readonly List<IWidgetPresenter> m_ChildWidgets = new();

        protected CancellationToken DestroyToken => m_InstanceCts?.Token ?? CancellationToken.None;
        protected CancellationToken OpenSessionToken => m_OpenSessionCts?.Token ?? CancellationToken.None;

        protected WindowPresenterBase(TUIView view)
        {
            View = view;
        }

        protected void AddToInstanceLifetime(IDisposable disposable)
        {
            if (disposable != null) m_InstanceDisposables.Add(disposable);
        }

        protected void AddToOpenSession(IDisposable disposable)
        {
            if (disposable != null) m_OpenSessionDisposables.Add(disposable);
        }

        /// <summary>
        /// 绑定窗口内的子控件 Widget（共享当前窗口子容器 Scope，自动纳入窗口生命周期级联树）
        /// </summary>
        protected TWidgetPresenter BindWidget<TWidgetPresenter>(WidgetViewBase widgetHost)
            where TWidgetPresenter : class, IWidgetPresenter
        {
            if (m_IsClosing || m_IsDestroying || m_IsDestroyed)
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
            var widgetPresenter = m_WindowScope.Resolve<TWidgetPresenter>();
            widgetPresenter.InternalAttach(widgetUIView, Context, m_WindowScope);

            // 3. 加入逻辑树并同步当前窗口所处生命周期阶段
            m_ChildWidgets.Add(widgetPresenter);
            widgetPresenter.InternalOnCreate();

            if (m_IsOpenSessionActive)
            {
                widgetPresenter.InternalOnOpen();
                if (m_IsResumed)
                {
                    widgetPresenter.InternalOnResume();
                }
            }

            return widgetPresenter;
        }

        /// <summary>
        /// 动态解绑并销毁指定的子控件 Widget
        /// </summary>
        protected void UnbindWidget(IWidgetPresenter widgetPresenter)
        {
            if (widgetPresenter != null && m_ChildWidgets.Remove(widgetPresenter))
            {
                widgetPresenter.InternalOnDestroy();
            }
        }

        void IWindowPresenter.InternalAttach(IWindowContext context, IObjectResolver windowScope)
        {
            Context = context;
            m_WindowScope = windowScope;
        }

        void IWindowPresenter.InternalOnCreate() => OnCreate();

        void IWindowPresenter.InternalOnOpen(IViewArgs args)
        {
            if (m_IsClosing || m_IsDestroying || m_IsDestroyed) return;
            // 若在已打开态重复调用 ShowView 刷新数据，先重置上一轮 OpenSession 订阅
            ClearOpenSession();
            if (m_IsDestroyed) return;
            m_IsOpenSessionActive = true;
            m_OpenSessionCts = new CancellationTokenSource();

            OnOpen(args as TArgs);

            for (int i = 0; i < m_ChildWidgets.Count && m_IsOpenSessionActive;)
            {
                IWidgetPresenter child = m_ChildWidgets[i];
                child.InternalOnOpen();
                if (i < m_ChildWidgets.Count && ReferenceEquals(child, m_ChildWidgets[i])) i++;
            }
        }

        void IWindowPresenter.InternalOnResume()
        {
            if (!m_IsOpenSessionActive || m_IsClosing || m_IsDestroying || m_IsDestroyed || m_IsResumed) return;
            m_IsResumed = true;
            OnResume();

            for (int i = 0; i < m_ChildWidgets.Count && m_IsResumed;)
            {
                IWidgetPresenter child = m_ChildWidgets[i];
                child.InternalOnResume();
                if (i < m_ChildWidgets.Count && ReferenceEquals(child, m_ChildWidgets[i])) i++;
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
            if (!m_IsOpenSessionActive || m_IsClosing || m_IsDestroyed) return;
            if (m_IsPausing)
            {
                m_CloseRequested = true;
                return;
            }

            m_IsClosing = true;
            m_CloseRequested = false;
            // 先让会话失效，回调中的 Close 不会重复进入当前关闭阶段。
            m_IsOpenSessionActive = false;
            try
            {
                PauseRunningSubtree(ref exceptions);
                for (int i = m_ChildWidgets.Count - 1; i >= 0;)
                {
                    IWidgetPresenter child = m_ChildWidgets[i];
                    try
                    {
                        child.InternalOnClose();
                    }
                    catch (Exception exception)
                    {
                        AddException(ref exceptions, exception);
                    }
                    if (i < m_ChildWidgets.Count && ReferenceEquals(child, m_ChildWidgets[i]))
                    {
                        i--;
                    }
                    else
                    {
                        int movedIndex = m_ChildWidgets.IndexOf(child);
                        i = movedIndex >= 0 ? movedIndex - 1 : Math.Min(i - 1, m_ChildWidgets.Count - 1);
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

        void IWindowPresenter.InternalOnDestroy()
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
                // 等当前子树 Pause/Close 完成后再销毁，避免阶段交错。
                m_DestroyRequested = true;
                return;
            }

            m_IsDestroying = true;
            m_DestroyRequested = false;
            try
            {
                CloseOpenSession(ref exceptions);
                while (m_ChildWidgets.Count > 0)
                {
                    int index = m_ChildWidgets.Count - 1;
                    IWidgetPresenter child = m_ChildWidgets[index];
                    m_ChildWidgets.RemoveAt(index);
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
                m_IsOpenSessionActive = false;
                m_IsResumed = false;
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
                for (int i = 0; i < m_ChildWidgets.Count;)
                {
                    IWidgetPresenter child = m_ChildWidgets[i];
                    try
                    {
                        child.InternalOnPause();
                    }
                    catch (Exception exception)
                    {
                        AddException(ref exceptions, exception);
                    }
                    // 子控件可在回调中解绑自身，索引不能跳过移入当前位置的兄弟。
                    if (i < m_ChildWidgets.Count && ReferenceEquals(child, m_ChildWidgets[i])) i++;
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

        void IWindowPresenter.InternalTick(float deltaTime)
        {
            if (!m_IsOpenSessionActive || !m_IsResumed) return;

            OnWindowTick(deltaTime);
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
