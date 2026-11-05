using System;
using ComponentAutoBindTool.ViewCore;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace ComponentAutoBindTool.Tests
{
    public class SceneFlowSortingTests
    {
        private sealed class ReleaseProbe : ComponentAutoBindTool.AssetCore.IAssetHandle
        {
            public ViewManager Manager;
            public bool ManagerWasDisposed;
            public bool IsReleased { get; private set; }
            public string Key => "释放顺序探针";

            public void Dispose()
            {
                ManagerWasDisposed = Manager.IsDisposed;
                IsReleased = true;
            }
        }

        [Test]
        public void LateResourceScopeResolutionStillDisposesWindowsFirst()
        {
            var root = new GameObject(
                "资源释放顺序",
                typeof(CanvasGroup),
                typeof(StageViewRootHelper)
            );
            var registryObject = new GameObject("全局舞台", typeof(GlobalUIRootRegistry));
            var registry = registryObject.GetComponent<GlobalUIRootRegistry>();
            registry.Level = root.GetComponent<StageViewRootHelper>();
            using var assets = new ComponentAutoBindTool.AssetCore.AssetService(
                new ComponentAutoBindTool.AssetCore.DirectCatalogBackend(
                    new System.Collections.Generic.Dictionary<string, UnityEngine.Object>()
                )
            );
            try
            {
                var builder = new ContainerBuilder();
                builder.RegisterInstance(assets);
                builder.RegisterInstance(registry);
                builder.RegisterSceneViewArchitecture(true);
                var scope = builder.Build();
                var manager = scope.Resolve<ViewManager>();
                var releaseScope =
                    scope.Resolve<ComponentAutoBindTool.AssetCore.IAssetReleaseScope>();
                var probe = releaseScope.Track(new ReleaseProbe { Manager = manager });
                scope.Dispose();
                Assert.That(probe.IsReleased, Is.True);
                Assert.That(probe.ManagerWasDisposed, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(registryObject);
            }
        }

        private sealed class Prefabs : IViewPrefabHelper, IViewRootHelper
        {
            public GameObject Root;
            public GameObject Host;

            public GameObject GetViewPrefab(string name) => Host;

            public Transform GetViewRoot(string name, UILayer layer = UILayer.Normal) =>
                Root.transform;

            public void ReleaseViewInstance(string name, GameObject instance) =>
                UnityEngine.Object.DestroyImmediate(instance);
        }

        [Test]
        public void ExistingContainerRegistrationUsesTheThreeArgumentConstructor()
        {
            var root = new GameObject("排序夹具");
            var host = new GameObject("窗口");
            host.transform.SetParent(root.transform);
            host.AddComponent<EnhancementViewHost>().CoverMode = ViewCoverMode.Overlay;
            var builder = new ContainerBuilder();
            var prefabs = new Prefabs { Root = root, Host = host };
            builder.RegisterInstance(prefabs).As<IViewPrefabHelper>().As<IViewRootHelper>();
            builder.RegisterInstance(new EnhancementProbe());
            builder.Register<ViewManager>(Lifetime.Scoped).AsSelf().As<IViewManager>();
            try
            {
                using var scope = builder.Build();
                var manager = scope.Resolve<ViewManager>();
                int id = manager.ShowView("窗口");
                Assert.That(id, Is.GreaterThan(0));
                Assert.That(
                    root.GetComponentsInChildren<Canvas>()[1].sortingOrder,
                    Is.EqualTo(2000)
                );
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void OffsetMovesRootAndNestedCanvasesTogether()
        {
            var root = new GameObject("偏移夹具");
            var host = new GameObject("窗口");
            host.transform.SetParent(root.transform);
            var view = host.AddComponent<EnhancementViewHost>();
            view.Layer = UILayer.System;
            var nested = new GameObject("子Canvas", typeof(Canvas));
            nested.transform.SetParent(host.transform);
            nested.GetComponent<Canvas>().overrideSorting = true;
            nested.GetComponent<Canvas>().sortingOrder = 7;
            var builder = new ContainerBuilder();
            builder.RegisterInstance(new EnhancementProbe());
            try
            {
                using var scope = builder.Build();
                var prefabs = new Prefabs { Root = root, Host = host };
                using var manager = new ViewManager(scope, prefabs, prefabs, 16000);
                manager.ShowView("窗口");
                var canvases = root.transform.GetChild(1).GetComponentsInChildren<Canvas>();
                Assert.That(canvases[0].sortingOrder, Is.EqualTo(22000));
                Assert.That(canvases[1].sortingOrder, Is.EqualTo(22007));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
