using System;
using System.Collections.Generic;
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
            // 若在已打开态重复调用 ShowView 刷新数据，先重置上一轮 OpenSession 订阅
            ClearOpenSession();
            m_IsOpenSessionActive = true;
            m_OpenSessionCts = new CancellationTokenSource();

            OnOpen(args as TArgs);

            for (int i = 0; i < m_ChildWidgets.Count; i++)
            {
                m_ChildWidgets[i].InternalOnOpen();
            }
        }

        void IWindowPresenter.InternalOnResume()
        {
            m_IsResumed = true;
            OnResume();

            for (int i = 0; i < m_ChildWidgets.Count; i++)
            {
                m_ChildWidgets[i].InternalOnResume();
            }
        }

        void IWindowPresenter.InternalOnPause()
        {
            m_IsResumed = false;
            for (int i = 0; i < m_ChildWidgets.Count; i++)
            {
                m_ChildWidgets[i].InternalOnPause();
            }
            OnPause();
        }

        void IWindowPresenter.InternalOnClose()
        {
            try
            {
                for (int i = m_ChildWidgets.Count - 1; i >= 0; i--)
                {
                    m_ChildWidgets[i].InternalOnClose();
                }
                OnClose();
            }
            finally
            {
                m_IsOpenSessionActive = false;
                m_IsResumed = false;
                ClearOpenSession();
            }
        }

        void IWindowPresenter.InternalOnDestroy()
        {
            try
            {
                for (int i = m_ChildWidgets.Count - 1; i >= 0; i--)
                {
                    m_ChildWidgets[i].InternalOnDestroy();
                }
                m_ChildWidgets.Clear();
                OnDestroy();
            }
            finally
            {
                ClearOpenSession();
                if (m_InstanceCts != null)
                {
                    m_InstanceCts.Cancel();
                    m_InstanceCts.Dispose();
                    m_InstanceCts = null;
                }
                for (int i = m_InstanceDisposables.Count - 1; i >= 0; i--)
                {
                    m_InstanceDisposables[i]?.Dispose();
                }
                m_InstanceDisposables.Clear();
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
            if (m_OpenSessionCts != null)
            {
                m_OpenSessionCts.Cancel();
                m_OpenSessionCts.Dispose();
                m_OpenSessionCts = null;
            }
            for (int i = m_OpenSessionDisposables.Count - 1; i >= 0; i--)
            {
                m_OpenSessionDisposables[i]?.Dispose();
            }
            m_OpenSessionDisposables.Clear();
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
