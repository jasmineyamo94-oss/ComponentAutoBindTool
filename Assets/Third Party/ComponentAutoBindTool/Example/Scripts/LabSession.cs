using System;
using System.Linq;
using ComponentAutoBindTool.ViewCore;
using VContainer;

namespace ComponentAutoBindTool.Example
{
    /// <summary>每次实验拥有独立容器；仅由外层 Scope.Update 驱动一次 Tick。</summary>
    public sealed class LabSession : IDisposable
    {
        private readonly IObjectResolver m_Root;
        private readonly LabTelemetry m_Trace;
        private IScopedObjectResolver m_Scope;
        private int m_Revision;
        public ViewManager Manager { get; private set; }
        public int Experiment { get; private set; }
        public int LastInstanceId { get; private set; }
        public static readonly string[] Titles = { "窗口层级与遮挡", "实例与强类型参数", "关闭与资源生命周期", "导航、替换与排队", "嵌套 Widget 与依赖注入" };
        public static readonly string[] Hints = {
            "目标：比较四层排序。Overlay 保持下层运行；PausePrevious 暂停同层前窗。",
            "目标：A → B → A 复用 Singleton；重复打开刷新参数；Multiple 各自独立。",
            "目标：比较缓存、立即销毁、延迟 3 秒；倒计时内重开取消销毁。",
            "目标：Normal 清空才出队；替换缺失资源时原窗口仍保留。",
            "目标：两个叶 Widget 独立计数、共享窗口 Scope；隐藏后恢复 Tick。"
        };
        public static readonly string[][] Captions = {
            new[] { "HUD 层", "Normal / A", "Overlay 叠加", "暂停前窗", "Top 层", "System 层" },
            new[] { "打开 A", "打开 B", "刷新 A 参数", "新增 Multiple", "关闭最后 ID", "关闭全部" },
            new[] { "打开缓存窗", "关闭缓存窗", "打开延迟窗", "关闭延迟窗", "立即销毁窗", "关闭最后 ID" },
            new[] { "打开 A", "排队 B", "排队 Multiple", "替换为 B", "替换缺失资源", "返回栈顶" },
            new[] { "打开 Widget", "左侧数据 +1", "隐藏 / 恢复", "解绑 Widget", "暂停窗口", "返回栈顶" }
        };

        public LabSession(IObjectResolver root, LabTelemetry trace) { m_Root = root; m_Trace = trace; }

        public void Reset(int experiment)
        {
            Dispose();
            Experiment = experiment;
            m_Revision = 0;
            LastInstanceId = 0;
            m_Trace.Clear();
            m_Scope = m_Root.CreateScope(builder => builder.Register<ViewManager>(Lifetime.Scoped).AsSelf().As<IViewManager>());
            Manager = m_Scope.Resolve<ViewManager>();
        }

        public int Open(string name, bool immediately = true)
        {
            int id = Manager.ShowView(name, new LabOpenArgs("参数修订", ++m_Revision), immediately);
            if (id > 0) LastInstanceId = id;
            m_Trace.Log($"ShowView({name}, immediately:{immediately}) → {id}");
            return id;
        }

        public void Act(int action)
        {
            m_Trace.Log("操作：" + Captions[Experiment][action]);
            switch (Experiment)
            {
                case 0:
                    Open(new[] { "HUD", "AutoBindTest", "Multi", "Cover", "Top", "System" }[action]);
                    break;
                case 1:
                    if (action < 4) Open(new[] { "AutoBindTest", "Cover", "AutoBindTest", "Multi" }[action]);
                    else if (action == 4) Manager.CloseView(LastInstanceId);
                    else Manager.CloseAllViews(true);
                    break;
                case 2:
                    if (action == 0) Open("AutoBindTest");
                    if (action == 1) Manager.CloseView("AutoBindTest");
                    if (action == 2) Open("Delayed");
                    if (action == 3) Manager.CloseView("Delayed");
                    if (action == 4) Open("Multi");
                    if (action == 5) Manager.CloseView(LastInstanceId);
                    break;
                case 3:
                    if (action == 0) Open("AutoBindTest");
                    if (action == 1) Open("Cover", false);
                    if (action == 2) Open("Multi", false);
                    if (action == 3) LastInstanceId = Manager.ReplaceTopView("Cover", new LabOpenArgs("替换", ++m_Revision));
                    if (action == 4) Manager.ReplaceTopView("MissingPrefab", new LabOpenArgs("预期失败", 0));
                    if (action == 5) Manager.TryNavigateBack();
                    break;
                case 4:
                    if (action == 0) Open("Widgets");
                    var presenter = m_Trace.Windows.Values.LastOrDefault(r => r.Context.ViewName == "Widgets" &&
                        r.Context.State != WindowState.Destroyed)?.Presenter as AutoBindTestPresenter;
                    if (action == 1) presenter?.Group?.Left.Increment();
                    if (action == 2) presenter?.Group?.ToggleLeft();
                    if (action == 3) presenter?.DetachGroup();
                    if (action == 4) Open("Cover");
                    if (action == 5) Manager.TryNavigateBack();
                    break;
            }
        }

        public void Tick() => Manager?.Tick();
        public void Dispose() { m_Scope?.Dispose(); m_Scope = null; Manager = null; }
    }
}
