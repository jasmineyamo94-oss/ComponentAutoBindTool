using System;
using UnityEngine;
using UnityEngine.UI;
using VContainer;

// ReSharper disable once CheckNamespace
namespace ComponentAutoBindTool.ViewCore
{
    [RequireComponent(typeof(Canvas),
        typeof(GraphicRaycaster),
        typeof(Core.ComponentAutoBindTool))]
    public abstract class ViewBase : MonoBehaviour, IAutoBindHost
    {
        [Header("窗口调度与层级配置")]
        public UILayer Layer = UILayer.Normal;
        public WindowInstanceMode InstanceMode = WindowInstanceMode.Singleton;
        public ViewCoverMode CoverMode = ViewCoverMode.PausePrevious;
        public ViewCloseStrategy CloseStrategy = ViewCloseStrategy.NonDestroy;

        [Tooltip("当 CloseStrategy 为 DestroyDelayed 时的延迟销毁秒数（使用真实时间）")]
        public float DestroyDelaySeconds = 3f;

        [Tooltip("是否允许被返回键 (TryNavigateBack) 关闭")]
        public bool AllowNavigateBack = true;

        private GraphicRaycaster m_GraphicRaycaster;
        private IViewTransition m_Transition;
        private bool m_TransitionSearched;

        /// <summary>
        /// 当前宿主是否挂载了自定义入场/退场过渡动画组件
        /// </summary>
        public bool HasTransition
        {
            get
            {
                EnsureTransitionResolved();
                return m_Transition != null;
            }
        }

        // 由生成代码重写；默认实现允许宿主在首次生成前编译。
        public virtual void EnsureAutoBind(GameObject go)
        {
            ViewLogger.Error($"[{name}] 尚未生成 AutoBind 绑定代码。");
        }

        /// <summary>
        /// 由子类实现，向当前窗口的 VContainer 子容器注册 UIView 与 Presenter
        /// </summary>
        public abstract void ConfigureWindow(IContainerBuilder builder);

        /// <summary>
        /// 标准窗口注册辅助方法：注册生成的 UIView 强类型实例与窗口 Presenter
        /// </summary>
        protected void RegisterWindow<TPresenter, TUIView>(IContainerBuilder builder, TUIView uiView)
            where TPresenter : class, IWindowPresenter
            where TUIView : class
        {
            if (uiView == null)
            {
                throw new InvalidOperationException($"[{name}] RegisterWindow 失败：UIView 为 null，请先生成并检查 AutoBind 绑定。");
            }

            builder.RegisterInstance(uiView).AsSelf();
            builder.Register<TPresenter>(Lifetime.Scoped)
                   .As<IWindowPresenter>()
                   .AsSelf();
        }

        /// <summary>
        /// 控制窗口根 GraphicRaycaster 交互开关（用于过渡动画期间防连点）
        /// </summary>
        public virtual void SetInteractable(bool interactable)
        {
            if (m_GraphicRaycaster == null && this != null)
            {
                TryGetComponent(out m_GraphicRaycaster);
            }

            if (m_GraphicRaycaster != null && m_GraphicRaycaster.enabled != interactable)
            {
                m_GraphicRaycaster.enabled = interactable;
            }
        }

        /// <summary>
        /// 表现层显示钩子
        /// </summary>
        public virtual void SetViewVisible(bool visible)
        {
            if (this != null && gameObject != null && gameObject.activeSelf != visible)
            {
                gameObject.SetActive(visible);
            }
        }

        /// <summary>
        /// 播放入场过渡（未挂载 IViewTransition 时同步立即完成，零延迟）
        /// </summary>
        public virtual void PlayEnterTransition(Action onCompleted)
        {
            EnsureTransitionResolved();
            if (m_Transition == null)
            {
                onCompleted?.Invoke();
                return;
            }

            m_Transition.PlayEnter(this, onCompleted);
        }

        /// <summary>
        /// 播放退场过渡（未挂载 IViewTransition 时同步立即完成，零延迟）
        /// </summary>
        public virtual void PlayExitTransition(Action onCompleted)
        {
            EnsureTransitionResolved();
            if (m_Transition == null)
            {
                onCompleted?.Invoke();
                return;
            }

            m_Transition.PlayExit(this, onCompleted);
        }

        /// <summary>
        /// 中断正在进行的过渡动画
        /// </summary>
        public virtual void CancelTransition()
        {
            EnsureTransitionResolved();
            m_Transition?.Cancel(this);
        }

        private void EnsureTransitionResolved()
        {
            if (m_TransitionSearched || this == null) return;
            m_TransitionSearched = true;
            TryGetComponent(out m_Transition);
        }
    }
}
