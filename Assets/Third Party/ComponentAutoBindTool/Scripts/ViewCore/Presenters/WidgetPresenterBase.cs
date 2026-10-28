using System;
using System.Collections.Generic;
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

        private readonly List<IDisposable> m_InstanceDisposables = new();
        private readonly List<IDisposable> m_OpenSessionDisposables = new();
        private CancellationTokenSource m_InstanceCts = new();
        private CancellationTokenSource m_OpenSessionCts;

        private readonly List<IWidgetPresenter> m_ChildWidgets = new();

        protected CancellationToken DestroyToken => m_InstanceCts?.Token ?? CancellationToken.None;
        protected CancellationToken OpenSessionToken => m_OpenSessionCts?.Token ?? CancellationToken.None;

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
            m_IsResumed = active;
            if (active)
            {
                OnResume();
                foreach (var child in m_ChildWidgets) child.InternalOnResume();
            }
            else
            {
                foreach (var child in m_ChildWidgets) child.InternalOnPause();
                OnPause();
            }
        }

        void IWidgetPresenter.InternalOnClose()
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
                m_IsParentResumed = false;
                ClearOpenSession();
            }
        }

        void IWidgetPresenter.InternalOnDestroy()
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
        protected virtual void OnOpen() { }
        protected virtual void OnResume() { }
        protected virtual void OnPause() { }
        protected virtual void OnClose() { }
        protected virtual void OnDestroy() { }
        protected virtual void OnWidgetTick(float deltaTime) { }
    }
}
