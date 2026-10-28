using Third_Party.ComponentAutoBindTool.Scripts.ViewCore;
using UnityEngine;
using UnityEngine.UI;
using VContainer;
using VContainer.Unity;
using IAutoBindHost = ComponentAutoBindTool.ViewCore.IAutoBindHost;

namespace ComponentAutoBindTool
{
    public enum ViewLayer
    {
        /// <summary>
        /// 1. HUD / Bottom: 场景直接引用的常驻菜单（如主城的 HUD、关卡中的血条）。
        /// </summary>
        HUD = 500,
        /// <summary>
        /// 2. Normal / Window: 基础弹窗（如背包、属性界面、商店）。
        /// 这类界面最适合“栈”管理，打开新的会自动暂停旧的。
        /// </summary>
        Normal = 700,
        /// <summary>
        /// 3. Top / Modal: 模态确认框（如购买确认、系统提示、奖励领取）。
        /// 置于所有 Normal 之上，具有独占性或覆盖性。
        /// </summary>
        Top = 1000,
        /// <summary>
        /// 4. System / Overlay: 系统级覆盖（如断线提示、Loading、全局通知）。
        /// 最高优先级，不参与普通的入栈/出栈流程。
        /// </summary>
        System = 2000
    }
    
    public enum DestroyType
    {
        /// <summary>
        /// 不销毁
        /// </summary>
        NonDestroy,
        /// <summary>
        /// 延迟销毁（带时间）
        /// </summary>
        DelayDestroy,
    }

    // public enum ViewEventTrigger
    // {
    //     
    // }
    
    [RequireComponent(typeof(Canvas))]
    [RequireComponent(typeof(GraphicRaycaster))]
    [RequireComponent(typeof(Core.ComponentAutoBindTool))]
    public class ViewLifetimeScope : LifetimeScope, IAutoBindHost
    {
        [SerializeField] public ViewLayer viewLayer;
        [SerializeField] public DestroyType destroyType;
        [SerializeField, Min(0f)] private float m_DestroyDelaySeconds;

        public float DestroyDelaySeconds => m_DestroyDelaySeconds;

        /// <summary>
        /// 用于存储和传递给视图的参数集合。
        /// </summary>
        private ViewBundle ViewBundle { get; } = new();

        /// <summary>
        /// 界面名称
        /// </summary>
        public string ViewName { get; private set; }

        protected override void Configure(IContainerBuilder builder)
        {
            // 兼容生成方法重新实现接口、隐藏基类虚方法的情况。
            ((IAutoBindHost)this).EnsureAutoBind(gameObject);
        }

        public virtual void EnsureAutoBind(GameObject go) { }
        
        protected RegistrationBuilder RegisterCommon<T>(IContainerBuilder builder, IUiViewComponent iView = null)
        {
            CCDebug.Log($"{ViewName} 配置相关注入", LogType.System);
            if (iView != null) builder.RegisterInstance(iView).AsSelf();
            builder.RegisterEntryPointExceptionHandler(Debug.LogError);
            return builder.RegisterEntryPoint<T>().WithParameter(ViewBundle);
        }
        
        public virtual void Init(params object[] args)
        {
            CCDebug.Log($"{ViewName} 设置数据", LogType.System);
            ViewBundle.SetViewBundle(args);
        }

        /// <summary>
        /// 设置画布层级
        /// </summary>
        /// <param name="order"></param>
        public void SetCanvasOrder(int order)
        {
            GetComponent<Canvas>().sortingOrder = order;
        }

        public void SetViewName(string viewName)
        {
            ViewName = viewName;
        }

        /// <summary>
        /// 展示界面
        /// </summary>
        public virtual void Show()
        {
            gameObject.SetActive(true);
        }

        /// <summary>
        /// 关闭界面
        /// </summary>
        public virtual void Hide()
        {
            gameObject.SetActive(false);
        }

        /// <summary>
        /// 暂停界面
        /// </summary>
        public virtual void Pause()
        {
            gameObject.SetActive(false);
        }

        /// <summary>
        /// 恢复界面
        /// </summary>
        public virtual void Resume()
        {
            gameObject.SetActive(true);
        }
    }
}
