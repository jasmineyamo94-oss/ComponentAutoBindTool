using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.Core;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdNodeStateTests
    {
        private sealed class Repository : IPsdVisibilityRepository, IPsdExportRepository
        {
            private readonly Dictionary<string, PsdVisibilityState> _visibility = new Dictionary<string, PsdVisibilityState>();
            private readonly Dictionary<string, PsdExportState> _export = new Dictionary<string, PsdExportState>();
            public PsdVisibilityState Load(string guid) => _visibility.TryGetValue(guid, out var state) ? state : new PsdVisibilityState();
            public void Save(string guid, PsdVisibilityState state) => _visibility[guid] = state;
            public PsdExportState LoadExport(string guid) => _export.TryGetValue(guid, out var state) ? state : new PsdExportState();
            public void SaveExport(string guid, PsdExportState state) => _export[guid] = state;
        }

        private static PsdNode Layer(int id, int? parent = null, bool group = false, bool flatten = false, bool imported = true, string name = "同名", int order = 0)
            => new PsdNode(id, parent, name, group, imported, true, flatten, group && !flatten ? "" : "sprite" + id,
                group && !flatten ? default : new PixelRect(10, 20, 30, 40), order);
        private static PsdDocumentSnapshot Document(params PsdNode[] nodes) => new PsdDocumentSnapshot("asset", 100, 200, nodes);

        [Test]
        public void GroupExportGatePreservesChildChoicesWithoutHidingPreview()
        {
            var session = new PsdSession();
            session.Refresh(Document(Layer(1, group: true), Layer(2, 1, order: 1), Layer(3, 1, order: 2)));
            session.SelectMany(new[] { 2, 3 }, 3);
            session.SetNodeExportable(2, false);
            var frozen = session.CaptureExport();
            session.SetNodeExportable(1, false);
            Assert.That(session.ExportableDrawNodeCount, Is.Zero);
            Assert.That(session.GetExportExcludedAncestor(3), Is.EqualTo(1));
            Assert.That(session.IsNodeExportEnabled(3), Is.True);
            Assert.That(session.VisibleDrawNodes.Count, Is.EqualTo(2), "不导出不能隐藏预览。");
            session.SetNodeExportable(2, true);
            session.SetNodeExportable(3, false);
            session.SetNodeExportable(1, true);
            Assert.That(session.IsEffectivelyExported(2), Is.True);
            Assert.That(session.IsEffectivelyExported(3), Is.False);
            Assert.That(session.ExportableDrawNodeCount, Is.EqualTo(1));
            Assert.That(session.SelectedIds, Is.EqualTo(new[] { 2, 3 }));
            Assert.That(session.SelectedId, Is.EqualTo(3));
            Assert.That(frozen.ExcludedLayerIds, Is.EqualTo(new[] { 2 }), "导出快照不能随编辑状态改变。");
        }

        [Test]
        public void ActivationAndResetDoNotChangeExportExclusions()
        {
            var session = new PsdSession();
            session.Refresh(Document(Layer(1), Layer(2, order: 1)));
            session.SetNodeExportable(2, false);
            session.SetNodesVisible(new[] { 1, 2 }, false);
            session.SetDocumentVisible(false);
            var plan = PsdPrefabPlanner.Build(session.Document, session.CaptureVisibility(), session.CaptureExport());
            Assert.That(plan.Nodes.Single().Id, Is.EqualTo(1));
            Assert.That(plan.Nodes.Single().ActiveSelf, Is.False, "不激活但允许导出的节点应保留。");
            Assert.That(plan.ContentActive, Is.False);
            session.ResetVisibility();
            Assert.That(session.IsNodeEnabled(1) && session.IsNodeEnabled(2), Is.True);
            Assert.That(session.IsNodeExportEnabled(2), Is.False, "重置显示不得恢复导出。");
            Assert.That(session.ExportableDrawNodeCount, Is.EqualTo(1));
        }

        [Test]
        public void BatchEditsIgnoreInvalidUnimportedAndMergedChildren()
        {
            var repository = new Repository();
            var session = new PsdSession(repository);
            session.Refresh(Document(Layer(1, group: true, flatten: true), Layer(2, 1, order: 1),
                Layer(3, imported: false, order: 2), Layer(4, order: 3)));
            session.SetNodesVisible(new[] { 2, 3, 999 }, false);
            session.SetNodesExportable(new[] { 2, 3, 999 }, false);
            Assert.That(session.CaptureVisibility().HiddenLayerIds, Is.Empty);
            Assert.That(session.CaptureExport().ExcludedLayerIds, Is.Empty);
            session.SetNodesVisible(new[] { 1, 4, 4, 2, 3, 999 }, false);
            session.SetNodesExportable(new[] { 1, 4, 4, 2, 3, 999 }, false);
            session.SetNodesExportable(new[] { 1, 4 }, false);
            Assert.That(repository.Load("asset").HiddenLayerIds, Is.EqualTo(new[] { 1, 4 }));
            Assert.That(repository.LoadExport("asset").ExcludedLayerIds, Is.EqualTo(new[] { 1, 4 }));
            Assert.That(session.IsNodeExportEnabled(2), Is.True);
            Assert.That(session.ExportableDrawNodeCount, Is.Zero);
        }

        [Test]
        public void SelectionAndExclusionsFollowStableIdsAcrossReimport()
        {
            var session = new PsdSession(new Repository());
            session.Refresh(Document(Layer(1), Layer(2, order: 1), Layer(3, order: 2)));
            session.SelectMany(new[] { 2, 3, 2, 999 }, 3);
            session.SetNodeExportable(2, false);
            session.Refresh(Document(Layer(3, name: "改名", order: 0), Layer(2, order: 1), Layer(1, order: 2)));
            Assert.That(session.SelectedIds, Is.EqualTo(new[] { 2, 3 }));
            Assert.That(session.SelectedId, Is.EqualTo(3));
            session.Refresh(Document(Layer(1), Layer(2, order: 1)));
            Assert.That(session.SelectedIds, Is.EqualTo(new[] { 2 }));
            Assert.That(session.SelectedId, Is.EqualTo(2));
            session.Refresh(Document(Layer(1)));
            Assert.That(session.SelectedIds, Is.Empty);
            Assert.That(session.SelectedId, Is.Null);
            session.Refresh(Document(Layer(2, name: "恢复")));
            Assert.That(session.IsEffectivelyExported(2), Is.False, "暂时消失的 ID 再出现时应恢复排除状态。");
        }

        [Test]
        public void DifferentDocumentsKeepIndependentEditsButDoNotShareSelection()
        {
            var session = new PsdSession(new Repository());
            var first = Document(Layer(1), Layer(2, order: 1));
            session.Refresh(first);
            session.SelectMany(new[] { 1, 2 });
            session.SetNodeExportable(2, false);
            session.SetNodeVisible(1, false);
            session.Refresh(new PsdDocumentSnapshot("other", 100, 200, first.Nodes));
            Assert.That(session.SelectedIds, Is.Empty);
            Assert.That(session.IsEffectivelyExported(2), Is.True);
            Assert.That(session.IsNodeEnabled(1), Is.True);
            session.Refresh(first);
            Assert.That(session.IsEffectivelyExported(2), Is.False);
            Assert.That(session.IsNodeEnabled(1), Is.False);
            Assert.That(session.SelectedIds, Is.Empty);
        }
    }
}
