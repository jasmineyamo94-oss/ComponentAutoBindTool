using System.Linq;
using NUnit.Framework;
using PsdUiWorkbench.Core;

namespace PsdUiWorkbench.Tests
{
    public sealed class PsdVisibilityTests
    {
        private sealed class Repository : IPsdVisibilityRepository
        {
            public PsdVisibilityState State = new PsdVisibilityState();
            public int Saves;
            public PsdVisibilityState Load(string guid) => State;
            public void Save(string guid, PsdVisibilityState state) { State = state; Saves++; }
        }

        private static PsdNode Node(int id, int? parent = null, bool group = false, bool flatten = false, bool imported = true, int order = 0, string name = "同名")
            => new PsdNode(id, parent, name, group, imported, true, flatten, group && !flatten ? "" : "sprite" + id,
                group && !flatten ? default : new PixelRect(10, 20, 30, 40), order);

        private static PsdDocumentSnapshot Document(params PsdNode[] nodes) => new PsdDocumentSnapshot("asset", 100, 200, nodes);

        [Test]
        public void ParentGatesPreserveChildChoicesAndAllowEditingWhileHidden()
        {
            var repository = new Repository();
            var session = new PsdSession(repository);
            session.Refresh(Document(Node(1, group: true), Node(2, 1, order: 1), Node(3, 1, order: 2)));
            Assert.That(repository.Saves, Is.Zero);
            session.Select(2);
            session.SetNodeVisible(2, false);
            Assert.That(session.IsNodeEnabled(1), Is.True);
            Assert.That(session.HitTest(15, 25), Is.EqualTo(3));
            session.SetNodeVisible(1, false);
            Assert.That(session.IsNodeEnabled(3), Is.True);
            Assert.That(session.IsHiddenByAncestor(3), Is.True);
            session.SetNodeVisible(2, true);
            session.SetNodeVisible(3, false);
            Assert.That(session.VisibleDrawNodes, Is.Empty);
            session.SetNodeVisible(1, true);
            Assert.That(session.VisibleDrawNodes.Select(n => n.Id), Is.EqualTo(new[] { 2 }));
            Assert.That(session.SelectedId, Is.EqualTo(2));
            session.SetDocumentVisible(false);
            Assert.That(session.GetVisibleBounds(2).IsValid, Is.False);
            Assert.That(session.HitTest(15, 25), Is.Null);
            session.SetNodeVisible(3, true);
            session.SetDocumentVisible(true);
            Assert.That(session.GetVisibleBounds(2).IsValid, Is.True);
            Assert.That(session.VisibleDrawNodes.Count, Is.EqualTo(2));
            Assert.That(session.Document.Find(3).IsImported, Is.True);
            Assert.That(session.Document.Find(3).IsVisible, Is.True);
        }

        [Test]
        public void FlattenedChildrenAndUnimportedLayersCannotBeToggled()
        {
            var session = new PsdSession();
            session.Refresh(Document(Node(1, group: true, flatten: true), Node(2, 1), Node(3, imported: false)));
            Assert.That(session.CanToggle(1), Is.True);
            Assert.That(session.CanToggle(2), Is.False);
            Assert.That(session.CanToggle(3), Is.False);
            Assert.That(session.GetMergedAncestor(2), Is.EqualTo(1));
            Assert.That(session.GetVisibleBounds(2).IsValid, Is.False);
            session.SetNodeVisible(2, false);
            Assert.That(session.IsNodeEnabled(2), Is.True);
            Assert.That(session.VisibleDrawNodes.Select(n => n.Id), Is.EqualTo(new[] { 1 }));
            session.SetNodeVisible(1, false);
            Assert.That(session.VisibleDrawNodes, Is.Empty);
        }

        [Test]
        public void IdBasedStateSurvivesRenameReorderDeletionAndReappearanceUntilReset()
        {
            var repository = new Repository();
            var session = new PsdSession(repository);
            session.Refresh(Document(Node(1), Node(2, order: 1)));
            session.Select(2);
            session.SetNodeVisible(2, false);
            session.Refresh(Document(Node(2, order: 0, name: "改名"), Node(1, order: 1), Node(3, order: 2)));
            Assert.That(session.SelectedId, Is.EqualTo(2));
            Assert.That(session.IsEffectivelyVisible(2), Is.False);
            Assert.That(session.IsEffectivelyVisible(3), Is.True);
            session.Refresh(Document(Node(1)));
            Assert.That(session.SelectedId, Is.Null);
            Assert.That(repository.State.HiddenLayerIds, Does.Contain(2));
            session.SetDocumentVisible(false);
            var reopened = new PsdSession(repository);
            reopened.Refresh(Document(Node(2)));
            Assert.That(reopened.DocumentVisible, Is.False);
            Assert.That(reopened.IsNodeEnabled(2), Is.False);
            reopened.Refresh(Document(Node(1)));
            reopened.ResetVisibility();
            Assert.That(repository.State.HiddenLayerIds, Is.Empty);
            reopened.Refresh(Document(Node(2)));
            Assert.That(reopened.IsEffectivelyVisible(2), Is.True);
        }

        [Test]
        public void VisibleGroupBoundsExcludeHiddenDescendants()
        {
            var session = new PsdSession();
            var far = new PsdNode(3, 1, "远处", false, true, true, false, "sprite3", new PixelRect(80, 90, 10, 10), 2);
            session.Refresh(Document(Node(1, group: true), Node(2, 1, order: 1), far));
            Assert.That(session.GetVisibleBounds(1).Width, Is.EqualTo(80));
            session.SetNodeVisible(3, false);
            Assert.That(session.GetVisibleBounds(1).Width, Is.EqualTo(30));
            session.SetNodeVisible(2, false);
            Assert.That(session.GetVisibleBounds(1).IsValid, Is.False);
        }
    }
}
