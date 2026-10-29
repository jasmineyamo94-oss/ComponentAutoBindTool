using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace ComponentAutoBindTool.ViewCore
{
    /// <summary>
    /// 主线程拥有的关闭等待表。每次调用返回独立 Awaitable，调用方只能消费一次。
    /// </summary>
    internal sealed class ViewCloseWaiters : IDisposable
    {
        internal sealed class Batch
        {
            internal readonly ViewCloseWaiters Owner;
            internal readonly int InstanceId;
            internal readonly List<Waiter> Waiters = new();
            internal bool Detached;
            internal bool Completed;

            internal Batch(ViewCloseWaiters owner, int instanceId)
            {
                Owner = owner;
                InstanceId = instanceId;
            }
        }

        // 节点不复用：旧批次和已派发的取消回调始终指向原租约，不能误完成复用后的源。
        internal sealed class Waiter
        {
            internal readonly Batch Batch;
            internal AwaitableCompletionSource Source;
            internal CancellationTokenRegistration Registration;
            internal bool Completed;

            internal Waiter(Batch batch, AwaitableCompletionSource source)
            {
                Batch = batch;
                Source = source;
            }
        }

        private readonly int m_MainThreadId = Thread.CurrentThread.ManagedThreadId;
        private readonly Dictionary<int, Batch> m_ActiveBatches = new();
        private readonly HashSet<Batch> m_DetachedBatches = new();
        private readonly Stack<AwaitableCompletionSource> m_SourcePool = new();
        private bool m_IsDisposed;

        internal async Awaitable Wait(int instanceId, CancellationToken cancellationToken = default)
        {
            EnsureMainThread();
            if (m_IsDisposed) return;
            cancellationToken.ThrowIfCancellationRequested();

            if (!m_ActiveBatches.TryGetValue(instanceId, out Batch batch))
            {
                batch = new Batch(this, instanceId);
                m_ActiveBatches.Add(instanceId, batch);
            }

            // 新建源已准备好；池中源只在上一次 await 消费后 Reset 一次。
            AwaitableCompletionSource source = m_SourcePool.Count > 0
                ? m_SourcePool.Pop()
                : new AwaitableCompletionSource();
            var waiter = new Waiter(batch, source);
            batch.Waiters.Add(waiter);

            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(static state =>
                {
                    var pending = (Waiter)state;
                    pending.Batch.Owner.RequestCancellation(pending);
                }, waiter);
            }

            try
            {
                await source.Awaitable;
            }
            finally
            {
                // 所有完成均在主线程；外层返回值可以晚于此处才被调用方消费。
                // 注销并等待正在执行的取消注册，随后才允许复用完成源。
                waiter.Registration.Dispose();
                waiter.Source = null;
                if (!m_IsDisposed)
                {
                    source.Reset();
                    m_SourcePool.Push(source);
                }
            }
        }

        internal Batch Detach(int instanceId)
        {
            EnsureMainThread();
            if (!m_ActiveBatches.Remove(instanceId, out Batch batch)) return null;
            batch.Detached = true;
            m_DetachedBatches.Add(batch);
            return batch;
        }

        internal void Complete(Batch batch)
        {
            EnsureMainThread();
            if (batch == null) return;
            if (!ReferenceEquals(batch.Owner, this) || !batch.Detached)
                throw new ArgumentException("只能完成由当前等待表分离的批次。", nameof(batch));
            if (batch.Completed) return;

            // 先提交批次终态，再调用可能同步重入管理器的 continuation。
            batch.Completed = true;
            m_DetachedBatches.Remove(batch);
            for (int index = 0; index < batch.Waiters.Count; index++)
            {
                CompleteWaiter(batch.Waiters[index], false);
            }
            batch.Waiters.Clear();
        }

        public void Dispose()
        {
            EnsureMainThread();
            if (m_IsDisposed) return;
            m_IsDisposed = true;

            // 首先分离全部批次，后续 continuation 不会修改正在遍历的集合。
            foreach (Batch batch in m_ActiveBatches.Values)
            {
                batch.Detached = true;
                m_DetachedBatches.Add(batch);
            }
            m_ActiveBatches.Clear();
            var pendingBatches = new List<Batch>(m_DetachedBatches);
            m_DetachedBatches.Clear();
            m_SourcePool.Clear();
            foreach (Batch batch in pendingBatches)
            {
                Complete(batch);
            }
        }

        private void RequestCancellation(Waiter waiter)
        {
            if (Thread.CurrentThread.ManagedThreadId == m_MainThreadId)
            {
                CompleteWaiter(waiter, true);
            }
            else
            {
                CancelOnMainThread(waiter);
            }
        }

        // CancellationToken 可以从后台线程触发；回调不在后台访问表、节点状态或对象池。
        private async void CancelOnMainThread(Waiter waiter)
        {
            await Awaitable.MainThreadAsync();
            CompleteWaiter(waiter, true);
        }

        private void CompleteWaiter(Waiter waiter, bool canceled)
        {
            EnsureMainThread();
            if (waiter.Completed) return;
            waiter.Completed = true;

            Batch batch = waiter.Batch;
            if (!batch.Detached)
            {
                batch.Waiters.Remove(waiter);
                if (batch.Waiters.Count == 0)
                {
                    m_ActiveBatches.Remove(batch.InstanceId);
                }
            }

            // Detach 后保留节点到批次处理结束；已取消节点会被 Complete 跳过。
            if (canceled) waiter.Source.TrySetCanceled();
            else waiter.Source.TrySetResult();
        }

        private void EnsureMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != m_MainThreadId)
                throw new InvalidOperationException("关闭等待表只能从创建它的 Unity 主线程访问。");
        }
    }
}
