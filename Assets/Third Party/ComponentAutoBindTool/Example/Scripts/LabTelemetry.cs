using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using ComponentAutoBindTool.ViewCore;
using UnityEngine;

namespace ComponentAutoBindTool.Example
{
    public sealed class LabOpenArgs : IViewArgs
    {
        public readonly string Label;
        public readonly int Revision;
        public LabOpenArgs(string label, int revision) { Label = label; Revision = revision; }
    }

    public sealed class LabWindowRecord
    {
        public IWindowContext Context;
        public UILayer Layer;
        public int ScopeId, UIViewId, Opens, Ticks;
        public float ClosedAt;
        public string Parameter;
        public object Presenter;
    }

    // 仅示例观察服务；事件来自正式框架回调，不维护第二套窗口状态机。
    public sealed class LabTelemetry
    {
        private readonly Queue<string> m_Events = new Queue<string>(200);
        public readonly Dictionary<int, LabWindowRecord> Windows = new Dictionary<int, LabWindowRecord>();
        public int ScopesCreated { get; private set; }
        public int ScopesDisposed { get; private set; }
        public int SessionsReleased { get; private set; }
        public int InstancesReleased { get; private set; }
        public int EventCount => m_Events.Count;
        public int BeginScope() { int id = ++ScopesCreated; Log("Scope #" + id + " 创建"); return id; }
        public void EndScope(int id) { ScopesDisposed++; Log("Scope #" + id + " 已释放"); }
        public void ReleaseSession(int id) { SessionsReleased++; Log("#" + id + " OpenSession 已清理"); }
        public void ReleaseInstance(int id) { InstancesReleased++; Log("#" + id + " 实例订阅已清理"); }

        public LabWindowRecord Track(IWindowContext context, int scope, object view, object presenter)
        {
            var record = new LabWindowRecord { Context = context, Layer = context.Layer, ScopeId = scope,
                UIViewId = RuntimeHelpers.GetHashCode(view), Presenter = presenter };
            Windows[context.InstanceId] = record;
            return record;
        }

        public void Log(string message)
        {
            if (m_Events.Count == 200) m_Events.Dequeue();
            m_Events.Enqueue(Time.unscaledTime.ToString("F2") + "  " + message);
        }

        public void Clear()
        {
            Windows.Clear();
            m_Events.Clear();
            Log("新实验 Scope 已就绪");
        }

        public string Describe()
        {
            var text = new StringBuilder(2048);
            text.AppendLine($"活动 Scope {ScopesCreated - ScopesDisposed}   会话清理 {SessionsReleased}   实例清理 {InstancesReleased}");
            foreach (var item in Windows.Values.OrderBy(x => x.Context.InstanceId))
            {
                text.AppendLine($"#{item.Context.InstanceId}  {item.Context.ViewName}  [{item.Layer}]  {item.Context.State}");
                text.AppendLine($"    Scope {item.ScopeId} · UIView {item.UIViewId} · Open {item.Opens} · Tick {item.Ticks}");
                if (item.Context.State == WindowState.PendingDestroy)
                    text.AppendLine($"    预计剩余 {Mathf.Max(0, 3 - (Time.unscaledTime - item.ClosedAt)):F1}s");
            }
            text.AppendLine("\n事件时间线 / 最近 200 条");
            foreach (string line in m_Events.Reverse()) text.AppendLine(line);
            return text.ToString();
        }
    }

    public sealed class LabScopeProbe : IDisposable
    {
        private readonly LabTelemetry m_Trace;
        public int Id { get; }
        public LabScopeProbe(LabTelemetry trace) { m_Trace = trace; Id = trace.BeginScope(); }
        public void Dispose() => m_Trace.EndScope(Id);
    }

    public sealed class LabCleanup : IDisposable
    {
        private Action m_Action;
        public LabCleanup(Action action) { m_Action = action; }
        public void Dispose() { var action = m_Action; m_Action = null; action?.Invoke(); }
    }

    public sealed class LabResources : IViewPrefabHelper, IViewRootHelper
    {
        private readonly GameObject[] m_Prefabs;
        private readonly Transform[] m_Roots;
        private readonly LabTelemetry m_Trace;
        public LabResources(GameObject[] prefabs, Transform[] roots, LabTelemetry trace)
        { m_Prefabs = prefabs; m_Roots = roots; m_Trace = trace; }
        public GameObject GetViewPrefab(string viewName) => Array.Find(m_Prefabs, item => item.name == viewName);
        public Transform GetViewRoot(string viewName, UILayer layer = UILayer.Normal) => m_Roots[(int)layer];
        public void ReleaseViewInstance(string viewName, GameObject instance)
        {
            m_Trace.Log(viewName + " Prefab 实例已释放");
            UnityEngine.Object.Destroy(instance);
        }
    }
}
