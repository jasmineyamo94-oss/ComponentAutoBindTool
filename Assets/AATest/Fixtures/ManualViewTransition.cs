using System;
using ComponentAutoBindTool.ViewCore;
using UnityEngine;

namespace ComponentAutoBindTool.Tests
{
    public sealed class ManualViewTransition : MonoBehaviour, IViewTransition
    {
        public Action PendingEnter;
        public Action PendingExit;
        public int EnterCount;
        public int CancelCount;
        public bool CompleteSynchronously;
        public bool ThrowOnExit;

        public void PlayEnter(ViewBase host, Action onCompleted)
        {
            EnterCount++;
            PendingEnter = onCompleted;
            if (CompleteSynchronously) onCompleted();
        }

        public void PlayExit(ViewBase host, Action onCompleted)
        {
            PendingExit = onCompleted;
            if (ThrowOnExit) throw new InvalidOperationException("测试退场异常");
            if (CompleteSynchronously) onCompleted();
        }

        public void Cancel(ViewBase host) => CancelCount++;
    }
}
