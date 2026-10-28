using ComponentAutoBindTool.ViewCore;
using UnityEngine;
using VContainer;
using VContainer.Unity;
namespace ComponentAutoBindTool.Example
{
    public sealed class AutoBindExampleScope : LifetimeScope
    {
        public const string WindowName = "AutoBindTest";
        public LabShellView Shell;
        public GameObject[] LabPrefabs;
        public Transform[] LabRoots;
        public LabSession Session { get; private set; }
        public LabTelemetry Telemetry { get; private set; }
        protected override void Configure(IContainerBuilder builder)
        {
            Telemetry = new LabTelemetry();
            builder.RegisterInstance(Telemetry);
            builder.RegisterInstance(new LabResources(LabPrefabs, LabRoots, Telemetry)).As<IViewPrefabHelper>().As<IViewRootHelper>();
        }
        private void Start()
        {
            Session = new LabSession(Container, Telemetry);
            Session.Reset(0);
            Shell.Initialize(Session, Telemetry);
            OpenWindow();
        }
        private void Update() { Session?.Tick(); }
        public void OpenWindow() => Session?.Open(WindowName);
        protected override void OnDestroy() { Session?.Dispose(); base.OnDestroy(); }
    }
}
