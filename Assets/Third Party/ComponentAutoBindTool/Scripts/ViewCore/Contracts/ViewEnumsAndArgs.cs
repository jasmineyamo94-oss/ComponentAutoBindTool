using System;
using UnityEngine;

// ReSharper disable once CheckNamespace
namespace ComponentAutoBindTool.ViewCore
{
    /// <summary>
    /// 窗口层级（各自拥有独立的顺序列表与排序区间）
    /// </summary>
    public enum UILayer
    {
        HUD = 0,
        Normal = 1,
        Top = 2,
        System = 3
    }

    /// <summary>
    /// 窗口实例复用策略（解决 A-P02）
    /// </summary>
    public enum WindowInstanceMode
    {
        /// <summary>单实例：全局唯一，重复打开时从原位置抽离置顶并刷新参数（如背包、设置）</summary>
        Singleton = 0,
        /// <summary>多实例：每次打开分配新 InstanceId 并创建新实例（如独立详情弹窗）</summary>
        Multiple = 1
    }

    /// <summary>
    /// 同层遮挡模式
    /// </summary>
    public enum ViewCoverMode
    {
        /// <summary>全屏/遮挡模式：暂停并隐藏同层位于其下方的窗口</summary>
        PausePrevious = 0,
        /// <summary>叠加模式：不隐藏同层位于其下方的窗口</summary>
        Overlay = 1
    }

    /// <summary>
    /// 窗口关闭策略
    /// </summary>
    public enum ViewCloseStrategy
    {
        /// <summary>关闭后不销毁，保留子容器与 UIView 进入缓存池（自动清空 OpenSession 订阅）</summary>
        NonDestroy = 0,
        /// <summary>关闭后立即销毁</summary>
        DestroyImmediate = 1,
        /// <summary>关闭后延迟指定秒数销毁（期间若重开可拦截复活）</summary>
        DestroyDelayed = 2
    }

    /// <summary>
    /// 窗口运行期精确状态（解决 A-P01 / A-P05）
    /// </summary>
    public enum WindowState
    {
        Constructing = 0,
        OpenAndActive = 1,
        Paused = 2,
        Cached = 3,
        PendingDestroy = 4,
        Destroyed = 5
    }

    /// <summary>
    /// 强类型开窗参数标记接口（解决 A-P09）
    /// </summary>
    public interface IViewArgs { }

    /// <summary>
    /// 空参数单例
    /// </summary>
    public sealed class EmptyViewArgs : IViewArgs
    {
        public static readonly EmptyViewArgs Instance = new EmptyViewArgs();
    }

    /// <summary>
    /// 兼容旧版 params object[] 的参数包装器
    /// </summary>
    public sealed class LegacyViewArgs : IViewArgs
    {
        public object[] DataBundle { get; }

        public LegacyViewArgs(params object[] dataBundle)
        {
            DataBundle = dataBundle ?? Array.Empty<object>();
        }

        public T Get<T>(int index, T defaultValue = default)
        {
            if (DataBundle != null && index >= 0 && index < DataBundle.Length && DataBundle[index] is T typedValue)
            {
                return typedValue;
            }
            return defaultValue;
        }
    }

    /// <summary>
    /// 框架独立日志出口（解决 A-P11，移除对项目 CCDebug 的硬编码依赖）
    /// </summary>
    public static class ViewLogger
    {
        public static Action<string> OnInfo = Debug.Log;
        public static Action<string> OnWarning = Debug.LogWarning;
        public static Action<string> OnError = Debug.LogError;

        public static void Info(string msg) => OnInfo?.Invoke($"[UIFramework] {msg}");
        public static void Warning(string msg) => OnWarning?.Invoke($"[UIFramework] {msg}");
        public static void Error(string msg) => OnError?.Invoke($"[UIFramework] {msg}");
    }
}