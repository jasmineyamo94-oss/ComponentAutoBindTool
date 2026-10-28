using System;
using System.Collections;
using System.Collections.Generic;
using ComponentAutoBindTool;
using UnityEngine;
using VContainer;
using VContainer.Unity;
using Object = UnityEngine.Object;

namespace Third_Party.ComponentAutoBindTool.Scripts.ViewCore
{
    public enum ViewStackDisplayMode
    {
        PausePrevious,
        Overlay
    }

    public class OpenViewInfo
    {
        public string ViewName;
        public object[] Params;
    }

    public class ViewManager : IViewManager, ITickable
    {
        protected readonly LifetimeScope ParentScope;
        protected readonly IViewPrefabHelper ViewPrefabHelper;
        protected readonly IViewRootHelper ViewRootHelper;

        public event Action<ViewLifetimeScope> OnViewOpened;
        public event Action<ViewLifetimeScope> OnViewClosed;

        /// <summary>
        /// 当前存在的界面栈（主要管理 Normal 层的弹窗）
        /// </summary>
        protected readonly Stack<ViewLifetimeScope> CurOpenViews = new();

        /// <summary>
        /// 将要打开的界面（当 CurOpenViews 的个数 == 0 的时候会自动补充）
        /// </summary>
        protected readonly Queue<OpenViewInfo> WaitingOpenSingleViewInfos = new();
        private readonly Dictionary<string, ViewLifetimeScope> m_CachedViews = new();
        private ViewLifetimeScope m_LastShownView;

        public ViewStackDisplayMode StackDisplayMode { get; }

        [Inject]
        public ViewManager(LifetimeScope parentScope, IViewPrefabHelper viewPrefabHelper,
            IViewRootHelper viewRootHelper)
            : this(parentScope, viewPrefabHelper, viewRootHelper, ViewStackDisplayMode.PausePrevious)
        {
        }

        public ViewManager(LifetimeScope parentScope, IViewPrefabHelper viewPrefabHelper,
            IViewRootHelper viewRootHelper, ViewStackDisplayMode stackDisplayMode)
        {
            ParentScope = parentScope;
            ViewPrefabHelper = viewPrefabHelper;
            ViewRootHelper = viewRootHelper;
            StackDisplayMode = stackDisplayMode;
        }

        /// <summary>
        /// 检查当前是否有弹窗正在显示
        /// </summary>
        public bool HasActivePopups => CurOpenViews.Count > 0;

        /// <summary>
        /// 获取当前栈顶界面
        /// </summary>
        /// <returns></returns>
        public ViewLifetimeScope GetPeekView()
        {
            CurOpenViews.TryPeek(out var view);
            return view;
        }

        /// <summary>
        /// 展示界面
        /// </summary>
        /// <param name="viewName"></param>
        /// <param name="immediately">是否立即打开（立即打开则放入栈中，否则缓存数据等待栈中元素为0时再入栈）</param>
        /// <param name="args"></param>
        /// <returns></returns>
        public ViewLifetimeScope ShowView(string viewName, bool immediately = true, params object[] args)
        {
            if (immediately)
            {
                if (CurOpenViews.Count > 0)
                {
                    var oldPeekView = CurOpenViews.Peek();
                    // 栈顶重复的界面检查
                    if (oldPeekView.ViewName == viewName)
                    {
                        oldPeekView.Init(args);
                        oldPeekView.Show();
                        return oldPeekView;
                    }
                }

                return SetSpawnView(viewName, args);
            }

            var openViewInfo = UnityEngine.Pool.GenericPool<OpenViewInfo>.Get();
            openViewInfo.ViewName = viewName;
            openViewInfo.Params = args;
            WaitingOpenSingleViewInfos.Enqueue(openViewInfo);

            return null;
        }

        /// <summary>
        /// 在关闭栈顶界面的同时，强行插入新界面（栈内界面展示延后）
        /// </summary>
        /// <param name="viewName"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public ViewLifetimeScope ShowViewAtHidePeekView(string viewName, params object[] args)
        {
            if (CurOpenViews.Count > 0)
            {
                CloseView(CurOpenViews.Peek());
            }

            return SetSpawnView(viewName, args);
        }

        private ViewLifetimeScope SetSpawnView(string viewName, params object[] args)
        {
            var view = GetOrSpawnView(viewName);
            view.SetViewName(viewName);
            if (view is IViewManagerInjectable injectable)
            {
                injectable.SetViewManager(this);
            }
            view.Init(args);

            // 设置画布层级
            int order = (int)view.viewLayer;
            if (view.viewLayer == ViewLayer.Normal)
            {
                if (StackDisplayMode == ViewStackDisplayMode.PausePrevious && CurOpenViews.Count > 0)
                {
                    CurOpenViews.Peek().Pause();
                }

                // Normal 层弹窗根据栈深度递增排序
                order += (CurOpenViews.Count + 1) * 10;
                CurOpenViews.Push(view);
            }
            // Top 和 System 层可以根据需要决定是否入栈，目前默认只有 Normal 入栈
            // 如果 Top 也需要入栈，可以根据业务逻辑调整

            view.SetCanvasOrder(order);
            m_LastShownView = view;

            view.Show();
            OnViewOpened?.Invoke(view);
            return view;
        }

        private ViewLifetimeScope GetOrSpawnView(string viewName)
        {
            if (m_CachedViews.TryGetValue(viewName, out ViewLifetimeScope cachedView))
            {
                m_CachedViews.Remove(viewName);
                if (cachedView != null)
                {
                    return cachedView;
                }
            }

            return SpawnView(viewName);
        }

        private ViewLifetimeScope SpawnView(string viewName)
        {
            ViewLifetimeScope view;
            using (LifetimeScope.EnqueueParent(ParentScope))
            {
                var viewPrefab = ViewPrefabHelper.GetViewPrefab(viewName);
                view = Object.Instantiate(viewPrefab, ViewRootHelper.GetViewRoot()).GetComponent<ViewLifetimeScope>();
            }

            return view;
        }

        /// <summary>
        /// 关闭栈顶界面
        /// </summary>
        public void HideView()
        {
            if (CurOpenViews.Count == 0)
            {
                if (m_LastShownView != null)
                {
                    CloseView(m_LastShownView);
                    return;
                }

                Debug.LogError("No view to hide...");
                return;
            }

            CloseView(CurOpenViews.Peek());
        }

        public void HideAllViews()
        {
            while (CurOpenViews.Count > 0)
            {
                HideView();
            }
        }

        public void Tick()
        {
            if (WaitingOpenSingleViewInfos.Count > 0 && CurOpenViews.Count == 0)
            {
                var openViewInfo = WaitingOpenSingleViewInfos.Dequeue();
                try
                {
                    ShowView(openViewInfo.ViewName, true, openViewInfo.Params);
                }
                finally
                {
                    openViewInfo.ViewName = null;
                    openViewInfo.Params = null;
                    UnityEngine.Pool.GenericPool<OpenViewInfo>.Release(openViewInfo);
                }
            }
        }

        private void CloseView(ViewLifetimeScope view)
        {
            if (view == null)
            {
                return;
            }

            bool wasTop = CurOpenViews.Count > 0 && ReferenceEquals(CurOpenViews.Peek(), view);
            if (wasTop)
            {
                CurOpenViews.Pop();
            }

            view.Hide();
            if (view.destroyType == DestroyType.NonDestroy)
            {
                if (m_CachedViews.TryGetValue(view.ViewName, out ViewLifetimeScope previous)
                    && previous != null && !ReferenceEquals(previous, view))
                {
                    Object.Destroy(previous.gameObject);
                }
                m_CachedViews[view.ViewName] = view;
            }
            else if (view.DestroyDelaySeconds <= 0f)
            {
                Object.Destroy(view.gameObject);
            }
            else
            {
                ParentScope.StartCoroutine(DestroyAfterDelayRealtime(view.gameObject, view.DestroyDelaySeconds));
            }

            if (ReferenceEquals(m_LastShownView, view))
            {
                m_LastShownView = GetPeekView();
            }

            if (wasTop && CurOpenViews.Count > 0 && StackDisplayMode == ViewStackDisplayMode.PausePrevious)
            {
                CurOpenViews.Peek().Resume();
            }

            OnViewClosed?.Invoke(view);
        }

        private static IEnumerator DestroyAfterDelayRealtime(GameObject target, float delaySeconds)
        {
            float destroyAt = Time.realtimeSinceStartup + delaySeconds;
            while (target != null && Time.realtimeSinceStartup < destroyAt)
            {
                yield return null;
            }

            if (target != null)
            {
                Object.Destroy(target);
            }
        }
    }
}
