using System.Threading;
using UnityEngine;

// ReSharper disable once CheckNamespace
namespace ComponentAutoBindTool.ViewCore
{
    /// <summary>
    /// 注入到窗口子容器中的窗口实例上下文
    /// </summary>
    public interface IWindowContext
    {
        int InstanceId { get; }
        string ViewName { get; }
        UILayer Layer { get; }
        WindowState State { get; }

        /// <summary>
        /// 当前是否正在播放入场或退场过渡动画
        /// </summary>
        bool IsTransitioning { get; }

        /// <summary>
        /// 关闭当前窗口自身（无论当前窗口处于哪一层、是否是栈顶）
        /// </summary>
        void CloseSelf();

        /// <summary>
        /// 异步等待当前窗口关闭（当窗口已关闭或管理器释放时立即完成）
        /// </summary>
        Awaitable WaitClosedAsync(CancellationToken cancellationToken = default);
    }
}
