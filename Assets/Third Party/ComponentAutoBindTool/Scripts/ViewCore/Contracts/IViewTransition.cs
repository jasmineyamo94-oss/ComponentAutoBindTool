using System;

// ReSharper disable once CheckNamespace
namespace ComponentAutoBindTool.ViewCore
{
    /// <summary>
    /// 窗口表现层入场/退场过渡动画契约。
    /// 业务层可通过在窗口 Prefab 上挂载实现此接口的 MonoBehaviour（如 Animator 或 DOTween 驱动器）接入动效。
    /// </summary>
    public interface IViewTransition
    {
        /// <summary>
        /// 播放入场过渡动画，完成后必须调用 onCompleted。
        /// </summary>
        void PlayEnter(ViewBase host, Action onCompleted);

        /// <summary>
        /// 播放退场过渡动画，完成后必须调用 onCompleted。
        /// </summary>
        void PlayExit(ViewBase host, Action onCompleted);

        /// <summary>
        /// 当窗口在过渡期间发生状态抢占（如退场中途被立即重开或强制销毁）时，立即中断当前动画。
        /// </summary>
        void Cancel(ViewBase host);
    }
}
