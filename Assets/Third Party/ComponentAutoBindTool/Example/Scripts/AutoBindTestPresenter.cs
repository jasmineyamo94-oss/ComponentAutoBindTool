using ComponentAutoBindTool.ViewCore;
using UnityEngine;

namespace ComponentAutoBindTool.Example
{
    public sealed class AutoBindTestPresenter : WindowPresenterBase<AutoBindTest.UIView, LabOpenArgs>
    {
        private readonly LabTelemetry m_Trace;
        private readonly LabScopeProbe m_Probe;
        private LabWindowRecord m_Record;
        private float m_Refresh;
        public LabWidgetGroupPresenter Group { get; private set; }
        public AutoBindTestPresenter(AutoBindTest.UIView view, LabTelemetry trace, LabScopeProbe probe) : base(view)
        { m_Trace = trace; m_Probe = probe; }
        protected override void OnCreate()
        {
            m_Record = m_Trace.Track(Context, m_Probe.Id, View, this);
            View.btnClose.onClick.AddListener(Close);
            AddToInstanceLifetime(new LabCleanup(() => m_Trace.ReleaseInstance(Context.InstanceId)));
            var root = (RectTransform)View.btnClose.transform.parent;
            root.anchoredPosition = new Vector2(((int)Context.Layer - 1) * 22 + Context.InstanceId % 3 * 12, -(int)Context.Layer * 12);
            if (Context.ViewName == "Widgets")
            {
                Group = BindWidget<LabWidgetGroupPresenter>(View.widgetDetails);
                Group.RequestDetach = DetachGroup;
                View.tMTxtStatus.rectTransform.anchorMin = new Vector2(0.04f, 0.68f);
                View.tMTxtStatus.rectTransform.anchorMax = new Vector2(0.96f, 0.80f);
            }
            m_Trace.Log($"#{Context.InstanceId} OnCreate / {Context.ViewName}");
        }
        protected override void OnOpen(LabOpenArgs args)
        {
            m_Record.Opens++;
            m_Record.Parameter = (args?.Label ?? "无参数") + " " + (args?.Revision ?? 0);
            AddToOpenSession(new LabCleanup(() => m_Trace.ReleaseSession(Context.InstanceId)));
            m_Trace.Log($"#{Context.InstanceId} OnOpen / {m_Record.Parameter}");
            Refresh();
        }
        protected override void OnResume() { m_Trace.Log($"#{Context.InstanceId} OnResume"); Refresh(); }
        protected override void OnPause() { m_Trace.Log($"#{Context.InstanceId} OnPause"); Refresh(); }
        protected override void OnClose() { m_Record.ClosedAt = Time.unscaledTime; m_Trace.Log($"#{Context.InstanceId} OnClose"); }
        protected override void OnDestroy()
        {
            View.btnClose.onClick.RemoveListener(Close);
            m_Trace.Log($"#{Context.InstanceId} OnDestroy");
        }
        protected override void OnWindowTick(float deltaTime)
        {
            m_Record.Ticks++;
            m_Refresh += Time.unscaledDeltaTime;
            if (m_Refresh < 0.1f) return;
            m_Refresh = 0;
            Refresh();
        }
        private void Refresh()
        {
            View.tMTxtTitle.text = $"{Context.ViewName}  #{Context.InstanceId}";
            View.tMTxtStatus.color = Context.State == WindowState.Paused ? new Color32(255, 198, 107, 255) : Color.white;
            View.tMTxtStatus.text = Context.ViewName == "Widgets"
                ? $"{Context.State} · Scope {m_Probe.Id} · {m_Record.Parameter}"
                : $"状态  {Context.State}     层级  {Context.Layer}\nScope #{m_Probe.Id}     UIView {m_Record.UIViewId}\n打开 {m_Record.Opens} 次 · Tick {m_Record.Ticks}\n强类型参数：{m_Record.Parameter}\n青色标记：子 Canvas 相对排序 +2";
        }
        public void DetachGroup()
        {
            if (Group == null) return;
            UnbindWidget(Group);
            Group = null;
            View.widgetDetails.SetWidgetVisible(false);
            m_Trace.Log($"#{Context.InstanceId} Widget 已解绑并清理");
        }
        private void Close() => Context.CloseSelf();
    }
}
