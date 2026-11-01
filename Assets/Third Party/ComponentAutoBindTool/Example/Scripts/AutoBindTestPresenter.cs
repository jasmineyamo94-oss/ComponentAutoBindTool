using ComponentAutoBindTool.ViewCore;
using UnityEngine;

namespace ComponentAutoBindTool.Example
{
    public sealed class AutoBindTestPresenter : WindowPresenterBase<AutoBindTest.UIView, LabOpenArgs>
    {
        private readonly LabTelemetry _trace;
        private readonly LabScopeProbe _probe;
        private LabWindowRecord _record;
        private float _refresh;
        public LabWidgetGroupPresenter Group { get; private set; }
        public AutoBindTestPresenter(AutoBindTest.UIView view, LabTelemetry trace, LabScopeProbe probe) : base(view)
        { _trace = trace; _probe = probe; }
        protected override void OnCreate()
        {
            _record = _trace.Track(Context, _probe.Id, View, this);
            View.btnClose.onClick.AddListener(Close);
            AddToInstanceLifetime(new LabCleanup(() => _trace.ReleaseInstance(Context.InstanceId)));
            var root = (RectTransform)View.btnClose.transform.parent;
            root.anchoredPosition = new Vector2(((int)Context.Layer - 1) * 22 + Context.InstanceId % 3 * 12, -(int)Context.Layer * 12);
            if (Context.ViewName == "Widgets")
            {
                Group = BindWidget<LabWidgetGroupPresenter>(View.widgetGroupDetails);
                Group.RequestDetach = DetachGroup;
                View.tMTxtStatus.rectTransform.anchorMin = new Vector2(0.04f, 0.68f);
                View.tMTxtStatus.rectTransform.anchorMax = new Vector2(0.96f, 0.80f);
            }
            _trace.Log($"#{Context.InstanceId} OnCreate / {Context.ViewName}");
        }
        protected override void OnOpen(LabOpenArgs args)
        {
            _record.Opens++;
            _record.Parameter = (args?.Label ?? "无参数") + " " + (args?.Revision ?? 0);
            AddToOpenSession(new LabCleanup(() => _trace.ReleaseSession(Context.InstanceId)));
            _trace.Log($"#{Context.InstanceId} OnOpen / {_record.Parameter}");
            Refresh();
        }
        protected override void OnResume() { _trace.Log($"#{Context.InstanceId} OnResume"); Refresh(); }
        protected override void OnPause() { _trace.Log($"#{Context.InstanceId} OnPause"); Refresh(); }
        protected override void OnClose() { _record.ClosedAt = Time.unscaledTime; _trace.Log($"#{Context.InstanceId} OnClose"); }
        protected override void OnDestroy()
        {
            View.btnClose.onClick.RemoveListener(Close);
            _trace.Log($"#{Context.InstanceId} OnDestroy");
        }
        protected override void OnWindowTick(float deltaTime)
        {
            _record.Ticks++;
            _refresh += Time.unscaledDeltaTime;
            if (_refresh < 0.1f) return;
            _refresh = 0;
            Refresh();
        }
        private void Refresh()
        {
            View.tMTxtTitle.text = $"{Context.ViewName}  #{Context.InstanceId}";
            View.tMTxtStatus.color = Context.State == WindowState.Paused ? new Color32(255, 198, 107, 255) : Color.white;
            View.tMTxtStatus.text = Context.ViewName == "Widgets"
                ? $"{Context.State} · Scope {_probe.Id} · {_record.Parameter}"
                : $"状态  {Context.State}     层级  {Context.Layer}\nScope #{_probe.Id}     UIView {_record.UIViewId}\n打开 {_record.Opens} 次 · Tick {_record.Ticks}\n强类型参数：{_record.Parameter}\n青色标记：子 Canvas 相对排序 +2";
        }
        public void DetachGroup()
        {
            if (Group == null) return;
            UnbindWidget(Group);
            Group = null;
            View.widgetGroupDetails.SetWidgetVisible(false);
            _trace.Log($"#{Context.InstanceId} Widget 已解绑并清理");
        }
        private void Close() => Context.CloseSelf();
    }
}
