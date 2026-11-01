using System;
using System.Collections.Generic;
using ComponentAutoBindTool.ViewCore;
using UnityEngine;
using VContainer;

namespace ComponentAutoBindTool.Tests
{
    public class RouteArgs : IViewArgs { public int Value; }
    public sealed class OtherRouteArgs : IViewArgs { }
    public sealed class DerivedRouteArgs : RouteArgs { }
    public enum RoutePresenterKind { Typed, Other, MissingContract, Empty }

    public class RouteViewHost : ViewBase
    {
        public RoutePresenterKind PresenterKind;
        public override void EnsureAutoBind(GameObject go) { }
        public override void ConfigureWindow(IContainerBuilder builder)
        {
            builder.Register<RouteScopeProbe>(Lifetime.Scoped);
            switch (PresenterKind)
            {
                case RoutePresenterKind.Typed: RegisterWindow<RoutePresenter, object>(builder, new object()); break;
                case RoutePresenterKind.Other: RegisterWindow<OtherRoutePresenter, object>(builder, new object()); break;
                case RoutePresenterKind.Empty: RegisterWindow<EmptyRoutePresenter, object>(builder, new object()); break;
                default: builder.Register<UncontractedRoutePresenter>(Lifetime.Scoped).As<IWindowPresenter>(); break;
            }
        }
    }

    public sealed class RouteProbe
    {
        public int Creates, Opens, Closes, Destroys, ScopeDisposals, SessionDisposals, LastValue;
        public IViewArgs LastArgs;
        public readonly Dictionary<int, IWindowContext> Contexts = new();
    }

    public sealed class RouteScopeProbe : IDisposable
    {
        private readonly RouteProbe _probe;
        public RouteScopeProbe(RouteProbe probe) { _probe = probe; }
        public void Dispose() => _probe.ScopeDisposals++;
    }

    public sealed class RoutePresenter : WindowPresenterBase<object, RouteArgs>
    {
        private readonly RouteProbe _probe;
        public RoutePresenter(object view, RouteProbe probe, RouteScopeProbe lifetime) : base(view) { _probe = probe; }
        protected override void OnCreate() { _probe.Creates++; _probe.Contexts[Context.InstanceId] = Context; }
        protected override void OnOpen(RouteArgs args)
        {
            _probe.Opens++;
            _probe.LastArgs = args;
            _probe.LastValue = args.Value;
            AddToOpenSession(new SessionCleanup(_probe));
        }
        protected override void OnClose() => _probe.Closes++;
        protected override void OnDestroy() => _probe.Destroys++;
        private sealed class SessionCleanup : IDisposable
        {
            private readonly RouteProbe _probe;
            public SessionCleanup(RouteProbe probe) { _probe = probe; }
            public void Dispose() => _probe.SessionDisposals++;
        }
    }

    public sealed class OtherRoutePresenter : WindowPresenterBase<object, OtherRouteArgs>
    {
        public OtherRoutePresenter(object view, RouteScopeProbe lifetime) : base(view) { }
        protected override void OnOpen(OtherRouteArgs args) { }
    }

    public sealed class EmptyRoutePresenter : WindowPresenterBase<object, EmptyViewArgs>
    {
        private readonly RouteProbe _probe;
        public EmptyRoutePresenter(object view, RouteProbe probe) : base(view) { _probe = probe; }
        protected override void OnOpen(EmptyViewArgs args) { _probe.Opens++; _probe.LastArgs = args; }
    }

    public sealed class UncontractedRoutePresenter : IWindowPresenter
    {
        private readonly RouteProbe _probe;
        public UncontractedRoutePresenter(RouteProbe probe, RouteScopeProbe lifetime) { _probe = probe; }
        public void InternalAttach(IWindowContext context, IObjectResolver scope) { }
        public void InternalOnCreate() => _probe.Creates++;
        public void InternalOnOpen(IViewArgs args) => _probe.Opens++;
        public void InternalOnResume() { }
        public void InternalOnPause() { }
        public void InternalOnClose() { }
        public void InternalOnDestroy() => _probe.Destroys++;
        public void InternalTick(float deltaTime) { }
    }
}
