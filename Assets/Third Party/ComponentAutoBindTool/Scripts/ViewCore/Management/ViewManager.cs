using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Pool;
using VContainer;
using VContainer.Unity;

// ReSharper disable once CheckNamespace
namespace ComponentAutoBindTool.ViewCore
{
    public sealed class ViewManager : IViewManager, IViewManagerDiagnostics, IViewRouteManager, IAsyncViewManager, ITickable, IDisposable
    {
        private const int SortingStepPerWindow = 50;
        private const int MaxWindowsPerLayer = 38; // 50 * 38 = 1900，确保不跨层越界（解决 A-P08）

        private readonly IObjectResolver _parentResolver;
        private readonly IViewPrefabHelper _prefabHelper;
        private readonly IViewRootHelper _rootHelper;
        private readonly int _sortingOrderOffset;

        private sealed class PendingLoad
        {
            public readonly SemaphoreSlim Gate = new(1, 1);
            public readonly CancellationTokenSource Cancellation = new();
            public int Requests;
        }
        private readonly Dictionary<string, PendingLoad> _inFlightLoads = new();

        private int _nextInstanceId = 1;
        private bool _isDisposed;
        private readonly ViewCloseWaiters _closeWaiters = new();
        private readonly Dictionary<UILayer, int> _presentationVersions = new();

        public bool BlockQueueWhenHigherLayersOpen { get; set; } = true;
        public bool IsDisposed => _isDisposed;
        public int TotalInstanceCount => _allEntries.Count;
        public int WaitingQueueCount => _waitingQueue.Count;

        // 1. 全局唯一实例登记表：InstanceId -> WindowEntry（覆盖打开、暂停、缓存、延迟销毁所有状态，解决 A-P01）
        private readonly Dictionary<int, WindowEntry> _allEntries = new();

        // 2. 单实例索引：ViewName -> InstanceId（解决 A-P02）
        private readonly Dictionary<string, int> _singletonIndex = new();

        // 3. 四层独立可重排顺序列表：保存当前处于打开/暂停栈中的 InstanceId（解决 A-P01 / A-P02）
        private readonly Dictionary<UILayer, List<int>> _layerOrderLists = new()
        {
            { UILayer.HUD, new List<int>() },
            { UILayer.Normal, new List<int>() },
            { UILayer.Top, new List<int>() },
            { UILayer.System, new List<int>() }
        };

        // 4. 延迟销毁真实时间倒计时表：InstanceId -> 剩余秒数（脱离 ParentScope 协程，支持重开拦截复活与统一 Dispose，解决 A-P06）
        private readonly Dictionary<int, float> _pendingDestroyTimers = new();
        private readonly List<int> _expiredDestroyIdsCache = new();
        private readonly List<int> _tickIdsCache = new();
        private readonly List<int> _timerIdsCache = new();

        // 5. 排队延后打开队列
        private sealed class QueuedOpenRequest
        {
            public string ViewName;
            public IViewArgs Args;
            public int Priority;
            public string ArgsTypeName;
            public ViewRoute Route;
            public void Reset() { ViewName = null; Args = null; Priority = 0; ArgsTypeName = null; Route = null; }
        }
        private readonly List<QueuedOpenRequest> _waitingQueue = new();

        public event Action<IWindowContext> OnViewOpened;
        public event Action<IWindowContext> OnViewClosed;

        [Preserve, Inject]
        public ViewManager(
            IObjectResolver parentResolver,
            IViewPrefabHelper prefabHelper,
            IViewRootHelper rootHelper) : this(parentResolver, prefabHelper, rootHelper, 0) { }

        public ViewManager(IObjectResolver parentResolver, IViewPrefabHelper prefabHelper, IViewRootHelper rootHelper, int sortingOrderOffset)
        {
            _sortingOrderOffset = sortingOrderOffset;
            _parentResolver = parentResolver;
            _prefabHelper = prefabHelper;
            _rootHelper = rootHelper;
            ViewManagerDebugRegistry.Register(this);
        }

        int IViewRouteManager.ShowView(ViewRoute route, IViewArgs args, bool immediately)
        {
            if (_isDisposed || !ValidateRouteInput(route, args)) return 0;
            if (!immediately)
            {
                EnqueueInternal(route.Key, args, 0, route);
                return 0;
            }
            return ShowViewImmediateInternal(route.Key, args, route);
        }

        void IViewRouteManager.EnqueueView(ViewRoute route, IViewArgs args, int priority)
        {
            if (_isDisposed || !ValidateRouteInput(route, args)) return;
            EnqueueInternal(route.Key, args, priority, route);
        }

        int IViewRouteManager.ReplaceTopView(ViewRoute route, IViewArgs args, UILayer layer)
        {
            if (_isDisposed || !ValidateRouteInput(route, args)) return 0;
            return ReplaceTopViewInternal(route.Key, args, layer, route);
        }

        bool IViewRouteManager.CloseView(ViewRoute route)
        {
            if (_isDisposed || !ValidateRoute(route)) return false;
            List<Exception> errors = null;
            bool closed = CancelPendingLoad(route.Key, ref errors);
            var entry = FindEntryToClose(route.Key);
            try
            {
                if (entry != null && route.ValidateHost(entry.ViewHost) && route.ValidatePresenter(entry.Presenter))
                    closed |= CloseView(entry.InstanceId);
            }
            catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
            ViewLifecycleErrors.Throw(errors);
            return closed;
        }

        private static bool ValidateRouteInput(ViewRoute route, IViewArgs args) =>
            ValidateRoute(route) && route.ValidateArgs(args);

        private static bool ValidateRoute(ViewRoute route)
        {
            if (route != null) return true;
            ViewLogger.Error("窗口路由不能为空。");
            return false;
        }

        public int ShowView(string viewName, bool immediately, params object[] args)
        {
            IViewArgs wrappedArgs = (args == null || args.Length == 0)
                ? EmptyViewArgs.Instance
                : new LegacyViewArgs(args);
            return ShowView(viewName, wrappedArgs, immediately);
        }

        public int ShowView(string viewName, IViewArgs args = null, bool immediately = true)
        {
            if (_isDisposed || string.IsNullOrEmpty(viewName)) return 0;

            if (!immediately)
            {
                EnqueueView(viewName, args);
                return 0;
            }

            return ShowViewImmediateInternal(viewName, args ?? EmptyViewArgs.Instance);
        }

        public Awaitable<int> ShowViewAsync(string viewName, IViewArgs args = null,
            CancellationToken cancellationToken = default) =>
            OpenAsync(viewName, args ?? EmptyViewArgs.Instance, null, null, cancellationToken);

        Awaitable<int> IAsyncViewManager.ShowViewAsync(ViewRoute route, IViewArgs args,
            CancellationToken cancellationToken) =>
            OpenAsync(route?.Key, args, route, null, cancellationToken);

        public Awaitable<int> ReplaceTopViewAsync(string viewName, IViewArgs args = null,
            UILayer layer = UILayer.Normal, CancellationToken cancellationToken = default) =>
            OpenAsync(viewName, args ?? EmptyViewArgs.Instance, null, layer, cancellationToken);

        Awaitable<int> IAsyncViewManager.ReplaceTopViewAsync(ViewRoute route, IViewArgs args,
            UILayer layer, CancellationToken cancellationToken) =>
            OpenAsync(route?.Key, args, route, layer, cancellationToken);

        private async Awaitable<int> OpenAsync(string viewName, IViewArgs args, ViewRoute route,
            UILayer? replaceLayer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_isDisposed || string.IsNullOrEmpty(viewName) ||
                (route != null && !ValidateRouteInput(route, args))) return 0;
            int oldTop = 0;
            if (replaceLayer.HasValue)
            {
                var order = _layerOrderLists[replaceLayer.Value];
                if (order.Count > 0) oldTop = order[^1];
            }
            WindowEntry entry;
            if (!TryGetReusableEntry(viewName, route, out entry))
                entry = await PrepareWindowEntryAsync(viewName, route, cancellationToken);
            if (entry == null || _isDisposed) return 0;
            if (oldTop != 0 && oldTop != entry.InstanceId) CloseView(oldTop);
            return ActivateAndScheduleEntry(entry, args) ? entry.InstanceId : 0;
        }

        private async Awaitable<WindowEntry> PrepareWindowEntryAsync(string viewName, ViewRoute route,
            CancellationToken callerToken)
        {
            if (_prefabHelper is not IAsyncViewPrefabHelper helper)
                return GetOrPrepareWindowEntry(viewName, route);
            if (!_inFlightLoads.TryGetValue(viewName, out var pending))
                _inFlightLoads.Add(viewName, pending = new PendingLoad());
            pending.Requests++;
            bool entered = false;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, pending.Cancellation.Token);
            try
            {
                await pending.Gate.WaitAsync(linked.Token);
                entered = true;
                linked.Token.ThrowIfCancellationRequested();
                if (TryGetReusableEntry(viewName, route, out var existing)) return existing;
                GameObject prefab = await helper.GetViewPrefabAsync(viewName, linked.Token);
                if (_isDisposed || linked.IsCancellationRequested)
                {
                    helper.ReleaseUninstantiatedPrefab(viewName);
                    linked.Token.ThrowIfCancellationRequested();
                    return null;
                }
                // 等待期间同步 ShowView 可能已经创建了 Singleton。
                if (TryGetReusableEntry(viewName, route, out existing))
                {
                    helper.ReleaseUninstantiatedPrefab(viewName);
                    return existing;
                }
                return PrepareWindowEntry(viewName, prefab, route);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {
                return null;
            }
            finally
            {
                if (entered) pending.Gate.Release();
                if (--pending.Requests == 0)
                {
                    if (_inFlightLoads.TryGetValue(viewName, out var current) && ReferenceEquals(current, pending))
                        _inFlightLoads.Remove(viewName);
                    pending.Cancellation.Dispose();
                    pending.Gate.Dispose();
                }
            }
        }

        private bool CancelPendingLoad(string viewName, ref List<Exception> errors)
        {
            if (string.IsNullOrEmpty(viewName) || !_inFlightLoads.Remove(viewName, out var pending)) return false;
            try { pending.Cancellation.Cancel(); }
            catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
            return true;
        }

        private void CancelAllPendingLoads(ref List<Exception> errors)
        {
            var loads = new List<PendingLoad>(_inFlightLoads.Values);
            _inFlightLoads.Clear();
            foreach (var pending in loads)
            {
                try { pending.Cancellation.Cancel(); }
                catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
            }
        }

        public void EnqueueView(string viewName, IViewArgs args = null, int priority = 0)
        {
            if (_isDisposed || string.IsNullOrEmpty(viewName)) return;
            EnqueueInternal(viewName, args ?? EmptyViewArgs.Instance, priority, null);
        }

        private void EnqueueInternal(string viewName, IViewArgs args, int priority, ViewRoute route)
        {
            var request = GenericPool<QueuedOpenRequest>.Get();
            request.ViewName = viewName;
            request.Args = args ?? EmptyViewArgs.Instance;
            request.ArgsTypeName = request.Args.GetType().Name;
            request.Priority = priority;
            request.Route = route;
            int index = 0;
            while (index < _waitingQueue.Count && _waitingQueue[index].Priority >= priority) index++;
            _waitingQueue.Insert(index, request);
        }

        public void ClearWaitingQueue()
        {
            foreach (var request in _waitingQueue)
            {
                request.Reset();
                GenericPool<QueuedOpenRequest>.Release(request);
            }
            _waitingQueue.Clear();
        }

        public async Awaitable WaitViewClosedAsync(int instanceId, CancellationToken cancellationToken = default)
        {
            if (_isDisposed || !_allEntries.TryGetValue(instanceId, out var entry) ||
                (!entry.HasOpenSession && entry.State != WindowState.OpenAndActive && entry.State != WindowState.Paused)) return;
            await _closeWaiters.Wait(instanceId, cancellationToken);
        }

        /// <summary>
        /// 事务安全地替换指定层栈顶窗口（解决 A-P07：先确认新窗口准备就绪，再关闭原栈顶）
        /// </summary>
        public int ReplaceTopView(string viewName, IViewArgs args = null, UILayer layer = UILayer.Normal)
        {
            if (_isDisposed || string.IsNullOrEmpty(viewName)) return 0;
            return ReplaceTopViewInternal(viewName, args ?? EmptyViewArgs.Instance, layer, null);
        }

        private int ReplaceTopViewInternal(string viewName, IViewArgs args, UILayer layer, ViewRoute route)
        {

            var layerList = _layerOrderLists[layer];
            int oldTopInstanceId = layerList.Count > 0 ? layerList[^1] : 0;

            // 先准备或获取目标窗口实例
            WindowEntry targetEntry = GetOrPrepareWindowEntry(viewName, route);
            if (targetEntry == null)
            {
                ViewLogger.Error($"ReplaceTopView 失败：新窗口 [{viewName}] 准备失败，保留原栈顶窗口不关闭。");
                return 0;
            }

            // 如果新窗口就是当前栈顶自身，直接刷新参数即可
            if (oldTopInstanceId != 0 && oldTopInstanceId != targetEntry.InstanceId)
            {
                CloseView(oldTopInstanceId);
            }

            return ActivateAndScheduleEntry(targetEntry, args ?? EmptyViewArgs.Instance) ? targetEntry.InstanceId : 0;
        }

        private int ShowViewImmediateInternal(string viewName, IViewArgs args, ViewRoute route = null)
        {
            WindowEntry entry = GetOrPrepareWindowEntry(viewName, route);
            if (entry == null) return 0;

            return ActivateAndScheduleEntry(entry, args) ? entry.InstanceId : 0;
        }

        /// <summary>
        /// 获取已有可复用 Singleton 实例（含打开中、暂停中、NonDestroy 缓存中、延迟销毁等待中），或创建新实例
        /// </summary>
        private WindowEntry GetOrPrepareWindowEntry(string viewName, ViewRoute route = null)
        {
            if (TryGetReusableEntry(viewName, route, out var existing)) return existing;
            GameObject prefab = _prefabHelper.GetViewPrefab(viewName);
            return PrepareWindowEntry(viewName, prefab, route);
        }

        private bool TryGetReusableEntry(string viewName, ViewRoute route, out WindowEntry entry)
        {
            entry = null;
            // 1. 检查是否命中已有 Singleton 实例（彻底解决 A-P02 的 A->B->A 重复创建问题）
            if (_singletonIndex.TryGetValue(viewName, out int existingId))
            {
                if (_allEntries.TryGetValue(existingId, out var existingEntry) &&
                    existingEntry.GameObject != null &&
                    existingEntry.State != WindowState.Destroyed)
                {
                    // 当前 Close 回调尚未返回时，不能在同一个 Presenter 上交叠开启新会话。
                    if (existingEntry.IsClosing) return true;
                    if (route != null && (!route.ValidateHost(existingEntry.ViewHost) ||
                                          !route.ValidatePresenter(existingEntry.Presenter))) return true;
                    // 若该实例正在延迟销毁倒计时中，直接拦截并取消销毁任务！
                    _pendingDestroyTimers.Remove(existingId);
                    entry = existingEntry;
                    return true;
                }

                // 若记录已失效，清理残余索引
                _singletonIndex.Remove(viewName);
                if (existingEntry != null) FinalizeDestroyEntry(existingEntry);
            }

            return false;
        }

        private WindowEntry PrepareWindowEntry(string viewName, GameObject prefab, ViewRoute route)
        {
            bool transferred = false;
            GameObject instanceGo = null;
            try
            {
                // 2. 创建全新实例（四阶段流水线的前三阶段：准备 Prefab -> 实例化宿主 -> 显式构建子容器与 OnCreate）
                if (prefab == null)
                {
                    string contract = route == null ? "" : $" 预期宿主 {route.ViewType.FullName}，实际 null。";
                    ViewLogger.Error($"无法打开窗口 [{viewName}]：IViewPrefabHelper 返回 Prefab 为 null。" + contract);
                    return null;
                }

                var prefabViewComponent = prefab.GetComponent<ViewBase>();
                if (route != null && !route.ValidateHost(prefabViewComponent)) return null;
                UILayer targetLayer = prefabViewComponent != null ? prefabViewComponent.Layer : UILayer.Normal;
                Transform root = _rootHelper.GetViewRoot(viewName, targetLayer);

                instanceGo = UnityEngine.Object.Instantiate(prefab, root);
                if (_prefabHelper is IViewPrefabLeaseHelper leases)
                    leases.RegisterViewInstance(viewName, instanceGo);
                transferred = true;
                if (!instanceGo.TryGetComponent<ViewBase>(out var viewHost))
                {
                    ViewLogger.Error($"窗口 Prefab [{viewName}] 根节点缺少继承自 ViewBase 的脚本！");
                    _prefabHelper.ReleaseViewInstance(viewName, instanceGo);
                    return null;
                }

                int newId = _nextInstanceId++;
                var newEntry = new WindowEntry(newId, viewName, viewHost, this);

                bool prepared;
                try
                {
                    prepared = newEntry.BuildScopeAndCreate(_parentResolver, route);
                }
                catch (Exception ex)
                {
                    ViewLogger.Error($"窗口 [{viewName}] 构建子容器或执行 OnCreate 异常: {ex}");
                    try { newEntry.Dispose(); }
                    finally { _prefabHelper.ReleaseViewInstance(viewName, instanceGo); }
                    return null;
                }

                if (!prepared || _isDisposed)
                {
                    try { newEntry.Dispose(); }
                    finally { _prefabHelper.ReleaseViewInstance(viewName, instanceGo); }
                    return null;
                }

                _allEntries[newId] = newEntry;
                if (viewHost.InstanceMode == WindowInstanceMode.Singleton)
                {
                    _singletonIndex[viewName] = newId;
                }

                return newEntry;
            }
            finally
            {
                if (!transferred)
                {
                    if (instanceGo != null) UnityEngine.Object.DestroyImmediate(instanceGo);
                    if (_prefabHelper is IAsyncViewPrefabHelper asyncHelper)
                        asyncHelper.ReleaseUninstantiatedPrefab(viewName);
                }
            }
        }

        /// <summary>
        /// 第四阶段：将准备好的窗口移至对应层列表顶部、重算遮挡排序、交付参数并触发恢复/打开事件
        /// </summary>
        private bool ActivateAndScheduleEntry(WindowEntry entry, IViewArgs args)
        {
            if (_isDisposed || !_allEntries.ContainsKey(entry.InstanceId) || entry.GameObject == null) return false;
            var layerList = _layerOrderLists[entry.Layer];

            // 若已在列表中（例如 A->B->A 或栈顶刷新），先移除原位置再追加到末尾置顶
            layerList.Remove(entry.InstanceId);
            layerList.Add(entry.InstanceId);

            // 每次开窗必交付新参数（触发 Presenter.OnOpen 与子控件级联 OnOpen，解决 A-P04）
            entry.DeliverOpenArgs(args);

            if (_isDisposed || !_allEntries.ContainsKey(entry.InstanceId) || !layerList.Contains(entry.InstanceId)) return false;

            // 重新计算该层的 SortingOrder 与遮挡暂停/恢复状态
            RefreshLayerPresentation(entry.Layer);

            if (_isDisposed || !_allEntries.ContainsKey(entry.InstanceId) || !layerList.Contains(entry.InstanceId)) return false;
            OnViewOpened?.Invoke(entry);
            return true;
        }

        public bool CloseView(int instanceId)
        {
            if (_isDisposed || !_allEntries.TryGetValue(instanceId, out var entry))
                return false;

            // 若已经处于关闭缓存、待销毁或已销毁状态，无需重复关闭
            if ((!entry.HasOpenSession && (entry.State == WindowState.Cached ||
                entry.State == WindowState.PendingDestroy)) ||
                entry.State == WindowState.Destroyed)
            {
                return false;
            }

            UILayer layer = entry.Layer;
            _layerOrderLists[layer].Remove(instanceId);

            // 完成源在用户回调前与当前会话分离；回调重开后的等待属于新会话。
            var closedWaiters = _closeWaiters.Detach(instanceId);
            List<Exception> errors = null;
            var strategy = entry.ViewHost.CloseStrategy;
            float delay = entry.ViewHost.DestroyDelaySeconds;
            try
            {
                switch (strategy)
                {
                    case ViewCloseStrategy.NonDestroy:
                        entry.EnterClosedState(WindowState.Cached);
                        break;
                    case ViewCloseStrategy.DestroyDelayed when delay > 0f:
                        _pendingDestroyTimers[instanceId] = delay;
                        entry.EnterClosedState(WindowState.PendingDestroy, () =>
                        {
                            if (_pendingDestroyTimers.TryGetValue(instanceId, out float remaining) && remaining <= 0f)
                                FinalizeDestroyEntry(entry);
                        });
                        break;
                    default:
                        if (_singletonIndex.TryGetValue(entry.ViewName, out int mappedId) && mappedId == instanceId)
                            _singletonIndex.Remove(entry.ViewName);
                        entry.EnterClosedState(WindowState.Destroyed, () => FinalizeDestroyEntry(entry));
                        break;
                }
            }
            catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
            try { RefreshLayerPresentation(layer); }
            catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
            try { OnViewClosed?.Invoke(entry); }
            catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
            finally { _closeWaiters.Complete(closedWaiters); }
            ViewLifecycleErrors.Throw(errors);
            return true;
        }

        public bool CloseView(string viewName)
        {
            List<Exception> errors = null;
            bool closed = CancelPendingLoad(viewName, ref errors);
            var entry = FindEntryToClose(viewName);
            try { if (entry != null) closed |= CloseView(entry.InstanceId); }
            catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
            ViewLifecycleErrors.Throw(errors);
            return closed;
        }

        private WindowEntry FindEntryToClose(string viewName)
        {
            if (string.IsNullOrEmpty(viewName)) return null;

            if (_singletonIndex.TryGetValue(viewName, out int singleId))
            {
                return _allEntries.GetValueOrDefault(singleId);
            }

            // 若是多实例窗口，从高到低查找最后打开的同名实例并关闭
            UILayer[] searchOrder = { UILayer.System, UILayer.Top, UILayer.Normal, UILayer.HUD };
            foreach (var layer in searchOrder)
            {
                var list = _layerOrderLists[layer];
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var entry = _allEntries[list[i]];
                    if (entry.ViewName == viewName)
                    {
                        return entry;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 导航返回：按 System -> Top -> Normal 从高到低寻找顶层可返回窗口（解决 A-P01）
        /// </summary>
        public bool TryNavigateBack()
        {
            UILayer[] backLayers = { UILayer.System, UILayer.Top, UILayer.Normal };
            for (int l = 0; l < backLayers.Length; l++)
            {
                var list = _layerOrderLists[backLayers[l]];
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var entry = _allEntries[list[i]];
                    if (entry.ViewHost.AllowNavigateBack)
                    {
                        return CloseView(entry.InstanceId);
                    }
                }
            }
            return false;
        }

        public void CloseAllViews(bool destroyCached = false)
        {
            if (_isDisposed) return;
            List<Exception> cancellationErrors = null;
            CancelAllPendingLoads(ref cancellationErrors);
            var ids = ListPool<int>.Get();
            List<Exception> errors = cancellationErrors;
            try
            {
                foreach (var layer in new[] { UILayer.System, UILayer.Top, UILayer.Normal, UILayer.HUD })
                {
                    var list = _layerOrderLists[layer];
                    for (int i = list.Count - 1; i >= 0; i--) ids.Add(list[i]);
                }
                foreach (int id in ids)
                {
                    try { CloseView(id); }
                    catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
                }
                if (destroyCached)
                {
                    ids.Clear();
                    ids.AddRange(_allEntries.Keys);
                    foreach (int id in ids)
                    {
                        if (!_allEntries.TryGetValue(id, out var entry) ||
                            entry.State == WindowState.OpenAndActive || entry.State == WindowState.Paused) continue;
                        try { FinalizeDestroyEntry(entry); }
                        catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
                    }
                }
            }
            finally { ListPool<int>.Release(ids); }
            ViewLifecycleErrors.Throw(errors);
        }

        public bool IsViewOpen(string viewName)
        {
            foreach (var kvp in _allEntries)
            {
                var entry = kvp.Value;
                if (entry.ViewName == viewName &&
                    (entry.HasOpenSession || entry.State == WindowState.OpenAndActive || entry.State == WindowState.Paused))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 重新计算指定层级的 Canvas 排序与全屏遮挡状态（借鉴对象 B 的同层遮挡扫描）
        /// </summary>
        private void RefreshLayerPresentation(UILayer layer)
        {
            _presentationVersions.TryGetValue(layer, out int version);
            _presentationVersions[layer] = ++version;
            var liveList = _layerOrderLists[layer];
            if (liveList.Count == 0) return;
            var list = ListPool<int>.Get();
            list.AddRange(liveList);
            List<Exception> errors = null;
            try
            {

                int baseOrder = GetLayerBaseOrder(layer) + _sortingOrderOffset;
                if (list.Count > MaxWindowsPerLayer)
                {
                    ViewLogger.Warning($"层级 [{layer}] 当前打开窗口数 ({list.Count}) 超过预算上限 ({MaxWindowsPerLayer})，请注意排序区间！");
                }

                // 1. 自底向上设置根 Canvas 及子 Canvas 的相对排序
                for (int i = 0; i < list.Count; i++)
                {
                    if (_allEntries.TryGetValue(list[i], out var entry))
                    {
                        int clampedIndex = Mathf.Min(i, MaxWindowsPerLayer);
                        entry.SetSortingOrder(baseOrder + clampedIndex * SortingStepPerWindow);
                    }
                }

                // 2. 自顶向下扫描同层遮挡：遇到第一个 PausePrevious 窗口后，其下方所有同层窗口标记为遮挡暂停
                int firstFullScreenIndex = -1;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (!_allEntries.TryGetValue(list[i], out var entry)) continue;
                    if (entry.ViewHost.CoverMode == ViewCoverMode.PausePrevious)
                    {
                        firstFullScreenIndex = i;
                        break;
                    }
                }

                // 先将被遮挡的下层窗口 Pause，再将可见的上层窗口 Resume
                for (int i = 0; i < firstFullScreenIndex; i++)
                {
                    if (_isDisposed || _presentationVersions[layer] != version) break;
                    if (!_allEntries.TryGetValue(list[i], out var entry)) continue;
                    try { entry.Pause(); }
                    catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
                }
                for (int i = Math.Max(0, firstFullScreenIndex); i < list.Count; i++)
                {
                    if (_isDisposed || _presentationVersions[layer] != version) break;
                    if (!_allEntries.TryGetValue(list[i], out var entry)) continue;
                    try { entry.SetActiveAndResume(); }
                    catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
                }
            }
            finally { ListPool<int>.Release(list); }
            ViewLifecycleErrors.Throw(errors);
        }

        private static int GetLayerBaseOrder(UILayer layer)
        {
            return layer switch
            {
                UILayer.HUD => 1000,
                UILayer.Normal => 2000,
                UILayer.Top => 4000,
                UILayer.System => 6000,
                _ => 2000
            };
        }

        public void Tick()
        {
            if (_isDisposed) return;

            // 1. 当 Normal 层为空时，处理排队请求
            if (_layerOrderLists[UILayer.Normal].Count == 0 && _waitingQueue.Count > 0 &&
                (!BlockQueueWhenHigherLayersOpen ||
                 (_layerOrderLists[UILayer.Top].Count == 0 && _layerOrderLists[UILayer.System].Count == 0)))
            {
                var req = _waitingQueue[0];
                _waitingQueue.RemoveAt(0);
                try
                {
                    ShowViewImmediateInternal(req.ViewName, req.Args, req.Route);
                }
                finally
                {
                    req.Reset();
                    GenericPool<QueuedOpenRequest>.Release(req);
                }
            }

            // 2. 驱动所有处于 OpenAndActive 窗口及其可见子 Widget 的受控帧更新（解决 A-P05）
            float dt = Time.deltaTime;
            _tickIdsCache.Clear();
            foreach (var id in _allEntries.Keys)
            {
                _tickIdsCache.Add(id);
            }
            for (int i = 0; i < _tickIdsCache.Count; i++)
            {
                if (_allEntries.TryGetValue(_tickIdsCache[i], out var entry) &&
                    entry.State == WindowState.OpenAndActive)
                {
                    entry.Tick(dt);
                }
            }

            // 3. 驱动真实时间延迟销毁倒计时（解决 A-P06）
            if (_pendingDestroyTimers.Count > 0)
            {
                float unscaledDt = Time.unscaledDeltaTime;
                _expiredDestroyIdsCache.Clear();

                _timerIdsCache.Clear();
                _timerIdsCache.AddRange(_pendingDestroyTimers.Keys);
                foreach (int timerId in _timerIdsCache)
                {
                    if (!_pendingDestroyTimers.TryGetValue(timerId, out float remaining)) continue;
                    remaining = Mathf.Max(0f, remaining - unscaledDt);
                    _pendingDestroyTimers[timerId] = remaining;
                    if (remaining <= 0f && _allEntries.TryGetValue(timerId, out var pending) && !pending.IsTransitioning)
                    {
                        _expiredDestroyIdsCache.Add(timerId);
                    }
                }

                for (int i = 0; i < _expiredDestroyIdsCache.Count; i++)
                {
                    int expiredId = _expiredDestroyIdsCache[i];
                    if (_pendingDestroyTimers.TryGetValue(expiredId, out float remaining) && remaining <= 0f &&
                        _allEntries.TryGetValue(expiredId, out var expiredEntry) &&
                        expiredEntry.State == WindowState.PendingDestroy && !expiredEntry.IsTransitioning)
                    {
                        FinalizeDestroyEntry(expiredEntry);
                    }
                }
            }
        }

        private void FinalizeDestroyEntry(WindowEntry entry)
        {
            int id = entry.InstanceId;
            string viewName = entry.ViewName;
            GameObject go = entry.GameObject;

            if (!_allEntries.Remove(id)) return;
            _pendingDestroyTimers.Remove(id);
            if (_singletonIndex.TryGetValue(viewName, out int mappedId) && mappedId == id)
            {
                _singletonIndex.Remove(viewName);
            }

            var waiters = _closeWaiters.Detach(id);
            List<Exception> errors = null;
            try { entry.Dispose(); }
            catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
            try
            {
                if (go != null) _prefabHelper.ReleaseViewInstance(viewName, go);
            }
            catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
            finally { _closeWaiters.Complete(waiters); }
            ViewLifecycleErrors.Throw(errors);
        }

        public void GetLayerSnapshots(UILayer layer, List<WindowDiagnosticSnapshot> results)
        {
            if (results == null) return;
            results.Clear();
            if (!_layerOrderLists.TryGetValue(layer, out var ids)) return;
            foreach (int id in ids)
                if (_allEntries.TryGetValue(id, out var entry)) results.Add(BuildSnapshot(entry));
        }

        public void GetInactiveSnapshots(List<WindowDiagnosticSnapshot> results)
        {
            if (results == null) return;
            results.Clear();
            foreach (var entry in _allEntries.Values)
                if (entry.State == WindowState.Cached || entry.State == WindowState.PendingDestroy ||
                    (entry.State == WindowState.Destroyed && entry.IsTransitioning)) results.Add(BuildSnapshot(entry));
        }

        public void GetWaitingQueueSnapshots(List<QueuedRequestSnapshot> results)
        {
            if (results == null) return;
            results.Clear();
            for (int i = 0; i < _waitingQueue.Count; i++)
            {
                var request = _waitingQueue[i];
                results.Add(new QueuedRequestSnapshot(i, request.ViewName, request.Priority, request.ArgsTypeName));
            }
        }

        private WindowDiagnosticSnapshot BuildSnapshot(WindowEntry entry)
        {
            _pendingDestroyTimers.TryGetValue(entry.InstanceId, out float remaining);
            var host = entry.ViewHost;
            return new WindowDiagnosticSnapshot(entry.InstanceId, entry.ViewName, entry.Layer, entry.State,
                host != null ? host.InstanceMode : WindowInstanceMode.Singleton,
                host != null ? host.CoverMode : ViewCoverMode.Overlay,
                host != null ? host.CloseStrategy : ViewCloseStrategy.NonDestroy,
                entry.RootSortingOrder, entry.SubCanvasCount, remaining, entry.IsTransitioning,
                host != null && host.AllowNavigateBack, entry.GameObject);
        }

        /// <summary>
        /// 管理器整体幂等释放入口（随父容器 Dispose 自动调用，彻底解决 A-P06）
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            List<Exception> cancellationErrors = null;
            CancelAllPendingLoads(ref cancellationErrors);
            ViewManagerDebugRegistry.Unregister(this);
            ClearWaitingQueue();

            _pendingDestroyTimers.Clear();
            foreach (var list in _layerOrderLists.Values)
            {
                list.Clear();
            }
            _singletonIndex.Clear();

            // 先切断登记表，销毁回调和等待 continuation 可以安全重入查询。
            var entries = ListPool<WindowEntry>.Get();
            entries.AddRange(_allEntries.Values);
            _allEntries.Clear();
            List<Exception> errors = cancellationErrors;
            foreach (var entry in entries)
            {
                GameObject go = entry.GameObject;
                string viewName = entry.ViewName;
                try { entry.Dispose(); }
                catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
                try
                {
                    if (go != null) _prefabHelper.ReleaseViewInstance(viewName, go);
                }
                catch (Exception error) { ViewLifecycleErrors.Add(ref errors, error); }
            }
            ListPool<WindowEntry>.Release(entries);
            _closeWaiters.Dispose();
            ViewLifecycleErrors.Throw(errors);
        }
    }
}
