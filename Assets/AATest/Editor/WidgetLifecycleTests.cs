using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ComponentAutoBindTool.ViewCore;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace ComponentAutoBindTool.Tests
{
    public class WidgetLifecycleTests
    {
        private readonly List<GameObject> m_Hosts = new();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject host in m_Hosts)
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
            m_Hosts.Clear();
        }

        private sealed class CallbackDisposable : IDisposable
        {
            private readonly Action m_Callback;

            public CallbackDisposable(Action callback)
            {
                m_Callback = callback;
            }

            public void Dispose() => m_Callback();
        }

        public sealed class LifecycleView
        {
            public string Name;
            public List<string> Events;
        }

        public sealed class LifecycleProbe : WidgetPresenterBase<LifecycleView>
        {
            public CancellationToken InstanceToken => DestroyToken;
            public CancellationToken SessionToken => OpenSessionToken;
            public int Ticks { get; private set; }
            public Action<string> PhaseCallback { get; set; }

            public LifecycleProbe BindChild(WidgetViewBase host) => BindWidget<LifecycleProbe>(host);
            public void TrackSession(Action callback) => AddToOpenSession(new CallbackDisposable(callback));
            public void TrackInstance(Action callback) => AddToInstanceLifetime(new CallbackDisposable(callback));

            protected override void OnCreate()
            {
                AddToInstanceLifetime(new CallbackDisposable(() => Record("InstanceDisposed")));
            }

            protected override void OnOpen()
            {
                AddToOpenSession(new CallbackDisposable(() => Record("SessionDisposed")));
            }

            protected override void OnPause() => Record("Pause");
            protected override void OnClose() => Record("Close");
            protected override void OnDestroy() => Record("Destroy");
            protected override void OnWidgetTick(float deltaTime) => Ticks++;
            private void Record(string phase)
            {
                View.Events.Add(View.Name + "." + phase);
                PhaseCallback?.Invoke(phase);
            }
        }

        private sealed class WindowProbe : WindowPresenterBase<object>
        {
            public WindowProbe() : base(new object())
            {
            }

            public LifecycleProbe Bind(WidgetViewBase host) => BindWidget<LifecycleProbe>(host);
            public void Unbind(IWidgetPresenter widget) => UnbindWidget(widget);
            protected override void OnOpen(IViewArgs args)
            {
            }
        }

        private WidgetLifecycleHost CreateHost(string name, List<string> events)
        {
            var gameObject = new GameObject(name);
            m_Hosts.Add(gameObject);
            var host = gameObject.AddComponent<WidgetLifecycleHost>();
            host.UIView = new LifecycleView { Name = name, Events = events };
            return host;
        }

        private static IObjectResolver CreateScope()
        {
            var builder = new ContainerBuilder();
            builder.Register<LifecycleProbe>(Lifetime.Transient);
            return builder.Build();
        }

        private static WindowProbe OpenWindow(IObjectResolver scope)
        {
            var window = new WindowProbe();
            var lifecycle = (IWindowPresenter)window;
            lifecycle.InternalAttach(null, scope);
            lifecycle.InternalOnCreate();
            lifecycle.InternalOnOpen(EmptyViewArgs.Instance);
            lifecycle.InternalOnResume();
            return window;
        }

        [Test]
        public void ActiveUnbindPausesClosesAndDestroysWidgetBeforeReleasingResources()
        {
            using IObjectResolver scope = CreateScope();
            var events = new List<string>();
            WindowProbe window = OpenWindow(scope);
            LifecycleProbe widget = window.Bind(CreateHost("widget", events));
            CancellationToken sessionToken = widget.SessionToken;
            CancellationToken instanceToken = widget.InstanceToken;

            window.Unbind(widget);
            ((IWidgetPresenter)widget).InternalTick(1f);

            Assert.That(events, Is.EqualTo(new[]
            {
                "widget.Pause", "widget.Close", "widget.SessionDisposed",
                "widget.Destroy", "widget.InstanceDisposed"
            }));
            Assert.That(sessionToken.IsCancellationRequested, Is.True);
            Assert.That(instanceToken.IsCancellationRequested, Is.True);
            Assert.That(widget.Ticks, Is.Zero, "已解绑的 Widget 不再运行 Tick。");
            ((IWindowPresenter)window).InternalOnDestroy();
        }

        [Test]
        public void ActiveUnbindPausesWholeNestedTreeBeforeClosingAndDestroyingChildren()
        {
            using IObjectResolver scope = CreateScope();
            var events = new List<string>();
            WindowProbe window = OpenWindow(scope);
            LifecycleProbe root = window.Bind(CreateHost("root", events));
            root.BindChild(CreateHost("left", events));
            root.BindChild(CreateHost("right", events));

            window.Unbind(root);

            Assert.That(events, Is.EqualTo(new[]
            {
                "left.Pause", "right.Pause", "root.Pause",
                "right.Close", "right.SessionDisposed", "left.Close", "left.SessionDisposed",
                "root.Close", "root.SessionDisposed",
                "right.Destroy", "right.InstanceDisposed", "left.Destroy", "left.InstanceDisposed",
                "root.Destroy", "root.InstanceDisposed"
            }));
            ((IWindowPresenter)window).InternalOnDestroy();
        }

        [Test]
        public void RepeatedDestroyEndsOpenSessionAndDestroysEachWidgetOnlyOnce()
        {
            using IObjectResolver scope = CreateScope();
            var events = new List<string>();
            var widget = new LifecycleProbe();
            var lifecycle = (IWidgetPresenter)widget;
            lifecycle.InternalAttach(new LifecycleView { Name = "widget", Events = events }, null, scope);
            lifecycle.InternalOnCreate();
            lifecycle.InternalOnOpen();
            lifecycle.InternalOnResume();

            lifecycle.InternalOnDestroy();
            lifecycle.InternalOnDestroy();
            lifecycle.InternalOnClose();

            Assert.That(events, Is.EqualTo(new[]
            {
                "widget.Pause", "widget.Close", "widget.SessionDisposed",
                "widget.Destroy", "widget.InstanceDisposed"
            }));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ClosingHiddenOrPausedWidgetDoesNotRepeatPause(bool locallyHidden)
        {
            using IObjectResolver scope = CreateScope();
            var events = new List<string>();
            WindowProbe window = OpenWindow(scope);
            LifecycleProbe widget = window.Bind(CreateHost("widget", events));
            var lifecycle = (IWidgetPresenter)widget;
            if (locallyHidden) widget.SetVisible(false);
            else lifecycle.InternalOnPause();
            events.Clear();

            lifecycle.InternalOnClose();
            lifecycle.InternalOnClose();
            window.Unbind(widget);

            Assert.That(events, Is.EqualTo(new[]
            {
                "widget.Close", "widget.SessionDisposed", "widget.Destroy", "widget.InstanceDisposed"
            }));
            ((IWindowPresenter)window).InternalOnDestroy();
        }

        [Test]
        public void NeverOpenedWidgetDestroysInstanceWithoutInventingCloseOrPause()
        {
            var events = new List<string>();
            var widget = new LifecycleProbe();
            var lifecycle = (IWidgetPresenter)widget;
            lifecycle.InternalAttach(new LifecycleView { Name = "widget", Events = events }, null, null);
            lifecycle.InternalOnCreate();

            lifecycle.InternalOnClose();
            lifecycle.InternalOnDestroy();
            lifecycle.InternalOnDestroy();

            Assert.That(events, Is.EqualTo(new[] { "widget.Destroy", "widget.InstanceDisposed" }));
        }

        [Test]
        public void RepeatedOpenRefreshesSessionWithoutClosingOrPausingRunningWidget()
        {
            using IObjectResolver scope = CreateScope();
            var events = new List<string>();
            WindowProbe window = OpenWindow(scope);
            LifecycleProbe widget = window.Bind(CreateHost("widget", events));
            var lifecycle = (IWidgetPresenter)widget;
            CancellationToken previousSession = widget.SessionToken;

            lifecycle.InternalOnOpen();
            lifecycle.InternalTick(1f);

            Assert.That(events, Is.EqualTo(new[] { "widget.SessionDisposed" }));
            Assert.That(previousSession.IsCancellationRequested, Is.True);
            Assert.That(widget.SessionToken.IsCancellationRequested, Is.False);
            Assert.That(widget.Ticks, Is.EqualTo(1));
            window.Unbind(widget);
            ((IWindowPresenter)window).InternalOnDestroy();
        }

        [Test]
        public void NestedCallbackFailuresStillCloseDestroyAndReleaseEveryWidget()
        {
            using IObjectResolver scope = CreateScope();
            var events = new List<string>();
            WindowProbe window = OpenWindow(scope);
            LifecycleProbe root = window.Bind(CreateHost("root", events));
            LifecycleProbe left = root.BindChild(CreateHost("left", events));
            LifecycleProbe right = root.BindChild(CreateHost("right", events));
            left.PhaseCallback = phase =>
            {
                if (phase == "Pause") throw new InvalidOperationException("left pause");
            };
            right.PhaseCallback = phase =>
            {
                if (phase == "Close" || phase == "Destroy" || phase == "SessionDisposed" || phase == "InstanceDisposed")
                {
                    throw new InvalidOperationException("right " + phase);
                }
            };
            root.PhaseCallback = phase =>
            {
                if (phase == "Close") throw new InvalidOperationException("root close");
            };
            CancellationToken rootToken = root.InstanceToken;
            CancellationToken leftToken = left.InstanceToken;
            CancellationToken rightToken = right.InstanceToken;

            AggregateException failure = Assert.Throws<AggregateException>(() => window.Unbind(root));

            Assert.That(failure.Flatten().InnerExceptions.Select(exception => exception.Message), Is.EquivalentTo(new[]
            {
                "left pause", "right Close", "right SessionDisposed", "root close", "right Destroy", "right InstanceDisposed"
            }));
            Assert.That(events, Is.EqualTo(new[]
            {
                "left.Pause", "right.Pause", "root.Pause",
                "right.Close", "right.SessionDisposed", "left.Close", "left.SessionDisposed",
                "root.Close", "root.SessionDisposed",
                "right.Destroy", "right.InstanceDisposed", "left.Destroy", "left.InstanceDisposed",
                "root.Destroy", "root.InstanceDisposed"
            }));
            Assert.That(rootToken.IsCancellationRequested && leftToken.IsCancellationRequested && rightToken.IsCancellationRequested,
                Is.True, "任意回调异常都不能跳过其他节点的实例取消令牌。");
            Assert.DoesNotThrow(() => ((IWidgetPresenter)root).InternalOnDestroy());
            ((IWindowPresenter)window).InternalOnDestroy();
        }

        [Test]
        public void CancellationAndDisposeFailuresStillReleaseAllRemainingResources()
        {
            using IObjectResolver scope = CreateScope();
            var events = new List<string>();
            WindowProbe window = OpenWindow(scope);
            LifecycleProbe widget = window.Bind(CreateHost("widget", events));
            widget.SessionToken.Register(() => throw new InvalidOperationException("session cancel"));
            widget.InstanceToken.Register(() => throw new InvalidOperationException("instance cancel"));
            int successfulReleases = 0;
            widget.TrackSession(() => successfulReleases++);
            widget.TrackSession(() => throw new InvalidOperationException("session dispose"));
            widget.TrackInstance(() => successfulReleases++);
            widget.TrackInstance(() => throw new InvalidOperationException("instance dispose"));

            AggregateException failure = Assert.Throws<AggregateException>(() => window.Unbind(widget));

            Assert.That(failure.Flatten().InnerExceptions.Select(exception => exception.Message), Is.EquivalentTo(new[]
            {
                "session cancel", "session dispose", "instance cancel", "instance dispose"
            }));
            Assert.That(successfulReleases, Is.EqualTo(2));
            Assert.That(events, Is.EqualTo(new[]
            {
                "widget.Pause", "widget.Close", "widget.SessionDisposed", "widget.Destroy", "widget.InstanceDisposed"
            }));
            Assert.DoesNotThrow(() => ((IWidgetPresenter)widget).InternalOnDestroy());
            ((IWindowPresenter)window).InternalOnDestroy();
        }

        [Test]
        public void ReentrantCloseAndDestroyCompleteEachTransitionOnce()
        {
            using IObjectResolver scope = CreateScope();
            var events = new List<string>();
            WindowProbe window = OpenWindow(scope);
            LifecycleProbe root = window.Bind(CreateHost("root", events));
            LifecycleProbe child = root.BindChild(CreateHost("child", events));
            var lifecycle = (IWidgetPresenter)root;
            child.PhaseCallback = phase =>
            {
                if (phase == "Pause" || phase == "Close")
                {
                    lifecycle.InternalOnClose();
                    lifecycle.InternalOnDestroy();
                }
            };
            root.PhaseCallback = phase =>
            {
                if (phase == "Close" || phase == "Destroy" || phase == "SessionDisposed" || phase == "InstanceDisposed")
                {
                    lifecycle.InternalOnClose();
                    lifecycle.InternalOnDestroy();
                }
            };

            lifecycle.InternalOnClose();
            window.Unbind(root);

            Assert.That(events, Is.EqualTo(new[]
            {
                "child.Pause", "root.Pause", "child.Close", "child.SessionDisposed", "root.Close", "root.SessionDisposed",
                "child.Destroy", "child.InstanceDisposed", "root.Destroy", "root.InstanceDisposed"
            }));
            ((IWindowPresenter)window).InternalOnDestroy();
        }

        [Test]
        public void DestroyRequestedByHidePauseWaitsForPauseCallbackToComplete()
        {
            using IObjectResolver scope = CreateScope();
            var events = new List<string>();
            WindowProbe window = OpenWindow(scope);
            LifecycleProbe widget = window.Bind(CreateHost("widget", events));
            widget.PhaseCallback = phase =>
            {
                if (phase != "Pause") return;
                ((IWidgetPresenter)widget).InternalOnDestroy();
                events.Add("widget.PauseCompleted");
            };

            widget.SetVisible(false);
            window.Unbind(widget);

            Assert.That(events, Is.EqualTo(new[]
            {
                "widget.Pause", "widget.PauseCompleted", "widget.Close", "widget.SessionDisposed",
                "widget.Destroy", "widget.InstanceDisposed"
            }));
            ((IWindowPresenter)window).InternalOnDestroy();
        }

        private sealed class Probe : WidgetPresenterBase<object>
        {
            public int Resumes, Pauses, Ticks;
            protected override void OnResume() => Resumes++;
            protected override void OnPause() => Pauses++;
            protected override void OnWidgetTick(float dt) => Ticks++;
        }

        [Test]
        public void LocalHideShowResumesTickExactlyOnce()
        {
            var probe = new Probe();
            var lifecycle = (IWidgetPresenter)probe;
            lifecycle.InternalOnOpen();
            lifecycle.InternalOnResume();
            probe.SetVisible(false);
            lifecycle.InternalTick(1);
            probe.SetVisible(true);
            lifecycle.InternalTick(1);
            lifecycle.InternalOnResume();
            Assert.That(probe.Ticks, Is.EqualTo(1));
            Assert.That(probe.Resumes, Is.EqualTo(2));
            Assert.That(probe.Pauses, Is.EqualTo(1));
            lifecycle.InternalOnDestroy();
        }

        [Test]
        public void LocalVisibilityCannotResumePausedParent()
        {
            var probe = new Probe();
            var lifecycle = (IWidgetPresenter)probe;
            lifecycle.InternalOnOpen();
            lifecycle.InternalOnResume();
            probe.SetVisible(false);
            lifecycle.InternalOnPause();
            probe.SetVisible(true);
            lifecycle.InternalTick(1);
            Assert.That(probe.Ticks, Is.Zero);
            Assert.That(probe.Pauses, Is.EqualTo(1));
            lifecycle.InternalOnResume();
            lifecycle.InternalTick(1);
            Assert.That(probe.Resumes, Is.EqualTo(2));
            Assert.That(probe.Ticks, Is.EqualTo(1));
            lifecycle.InternalOnClose();
            lifecycle.InternalOnDestroy();
        }
    }
}
