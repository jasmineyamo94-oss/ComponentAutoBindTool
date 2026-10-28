using UnityEngine;

// ReSharper disable once CheckNamespace
namespace ComponentAutoBindTool.ViewCore
{
    /// <summary>
    /// 局部子控件 (UIWidget) MonoBehaviour 宿主基类
    /// </summary>
    [RequireComponent(typeof(Core.ComponentAutoBindTool))]
    public abstract class WidgetViewBase : MonoBehaviour, IAutoBindHost
    {
        // 默认实现允许首次生成前编译和挂载宿主。
        public virtual void EnsureAutoBind(GameObject go)
        {
            ViewLogger.Error($"[{name}] 尚未生成 Widget AutoBind 绑定代码。");
        }

        /// <summary>
        /// 返回生成代码中的 view 字段（待后续微调生成器后可自动生成此方法）
        /// </summary>
        public virtual object GetGeneratedUIView()
        {
            ViewLogger.Error($"[{name}] 尚未生成 Widget UIView，请执行生成代码。");
            return null;
        }

        public virtual void SetWidgetVisible(bool visible)
        {
            if (gameObject.activeSelf != visible)
            {
                gameObject.SetActive(visible);
            }
        }
    }
}
