using System.Text.RegularExpressions;
using ComponentAutoBindTool.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Tests
{
    public class AutoBindInspectorMetadataTests
    {
        private GameObject _root;
        private Core.ComponentAutoBindTool _target;
        private AutoBindGlobalSetting _setting;
        private SerializedObject _serialized;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("InspectorMetadataTest");
            _target = _root.AddComponent<Core.ComponentAutoBindTool>();
            _target.m_targetScript = _root.AddComponent<AutoBindPlainHost>();
            _target.BindDatas.Add(new Core.ComponentAutoBindTool.BindData("Tf_Root", _root.transform));
            _setting = ScriptableObject.CreateInstance<AutoBindGlobalSetting>();
            _serialized = new SerializedObject(_target);
            Property("_namespace").stringValue = "Previous.Namespace";
            Property("_className").stringValue = "PreviousHost";
            Property("_codePath").stringValue = "Assets/Previous";
            _serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearUndo(_target);
            _serialized.Dispose();
            Object.DestroyImmediate(_setting);
            Object.DestroyImmediate(_root);
        }

        [Test]
        public void ClearingTargetClearsMetadataWithoutConfigurationOrBindingChanges()
        {
            Property("m_targetScript").objectReferenceValue = null;

            bool succeeded = Sync(null);

            Assert.That(succeeded, Is.True);
            Assert.That(Property("_namespace").stringValue, Is.Empty);
            Assert.That(Property("_className").stringValue, Is.Empty);
            Assert.That(Property("_codePath").stringValue, Is.Empty);
            Assert.That(_target.ClassName, Is.EqualTo("PreviousHost"), "同步只准备序列化候选，不提前提交。");
            Assert.That(_target.BindDatas.Count, Is.EqualTo(1));
            Assert.That(_target.BindDatas[0].BindCom, Is.SameAs(_root.transform));
        }

        [Test]
        public void MissingConfigurationDoesNotPartiallyChangePendingMetadata()
        {
            LogAssert.Expect(UnityEngine.LogType.Error, new Regex("配置|路径"));

            bool succeeded = Sync(null);

            Assert.That(succeeded, Is.False);
            Assert.That(Property("_namespace").stringValue, Is.EqualTo("Previous.Namespace"));
            Assert.That(Property("_className").stringValue, Is.EqualTo("PreviousHost"));
            Assert.That(Property("_codePath").stringValue, Is.EqualTo("Assets/Previous"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExplicitSelectionAndMetadataUndoAndRedoTogether(bool clearTarget)
        {
            SetConfiguration("Assets/AATest/MetadataOutput");
            MonoBehaviour previous = _target.m_targetScript;
            MonoBehaviour selected = clearTarget ? null : _root.AddComponent<AutoBindVirtualHost>();
            var inspector = CreateInspector();
            Undo.IncrementCurrentGroup();
            try
            {
                inspector.serializedObject.Update();
                inspector.serializedObject.FindProperty("m_targetScript").objectReferenceValue = selected;

                Assert.That(inspector.ApplyTargetScriptChange(clearTarget ? null : _setting, out string error), Is.True, error);
                Undo.FlushUndoRecordObjects();
                Assert.That(_target.m_targetScript, Is.EqualTo(selected));
                Assert.That(_target.ClassName, Is.EqualTo(clearTarget ? "" : "AutoBindVirtualHost"));
                Assert.That(_target.CodePath, Is.EqualTo(clearTarget ? "" : "Assets/AATest/MetadataOutput"));
                AssertBindingsUnchanged();

                Undo.PerformUndo();
                Assert.That(_target.m_targetScript, Is.EqualTo(previous));
                Assert.That(_target.Namespace, Is.EqualTo("Previous.Namespace"));
                Assert.That(_target.ClassName, Is.EqualTo("PreviousHost"));
                Assert.That(_target.CodePath, Is.EqualTo("Assets/Previous"));
                AssertBindingsUnchanged();

                Undo.PerformRedo();
                Assert.That(_target.m_targetScript, Is.EqualTo(selected));
                Assert.That(_target.Namespace, Is.EqualTo(clearTarget ? "" : "ComponentAutoBindTool.Tests"));
                Assert.That(_target.ClassName, Is.EqualTo(clearTarget ? "" : "AutoBindVirtualHost"));
                Assert.That(_target.CodePath, Is.EqualTo(clearTarget ? "" : "Assets/AATest/MetadataOutput"));
                AssertBindingsUnchanged();
            }
            finally
            {
                Object.DestroyImmediate(inspector);
                Undo.IncrementCurrentGroup();
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Assets/../../Outside")]
        [TestCase("C:relative")]
        public void InvalidConfigurationDiscardsTheSelectionWithoutPartialChanges(string codePath)
        {
            SetConfiguration(codePath ?? "Assets/AATest/MetadataOutput");
            var replacement = _root.AddComponent<AutoBindVirtualHost>();
            string before = EditorJsonUtility.ToJson(_target);
            var inspector = CreateInspector();
            try
            {
                inspector.serializedObject.Update();
                inspector.serializedObject.FindProperty("m_targetScript").objectReferenceValue = replacement;

                bool succeeded = inspector.ApplyTargetScriptChange(codePath == null ? null : _setting, out string error);

                Assert.That(succeeded, Is.False);
                Assert.That(error, Is.Not.Empty);
                Assert.That(EditorJsonUtility.ToJson(_target), Is.EqualTo(before));
                Assert.That(inspector.serializedObject.FindProperty("m_targetScript").objectReferenceValue,
                    Is.EqualTo(_target.m_targetScript));
                AssertBindingsUnchanged();
            }
            finally { Object.DestroyImmediate(inspector); }
        }

        [Test]
        public void InspectorSummaryUsesCurrentConfigurationWithoutDirtyingTheTarget()
        {
            string before = EditorJsonUtility.ToJson(_target);
            int dirtyCount = EditorUtility.GetDirtyCount(_target);
            var inspector = CreateInspector();
            try
            {
                SetConfiguration("Assets/AATest/FirstOutput");
                Assert.That(AutoBindTargetScriptSyncUtility.TryResolveMetadata(_target.m_targetScript, _setting,
                    out var first, out string firstError), Is.True, firstError);
                Assert.That(first.Namespace, Is.EqualTo("ComponentAutoBindTool.Tests"));
                Assert.That(first.ClassName, Is.EqualTo("AutoBindPlainHost"));
                Assert.That(first.CodePath, Is.EqualTo("Assets/AATest/FirstOutput"));

                SetConfiguration("Assets/AATest/SecondOutput");
                for (int i = 0; i < 3; i++)
                {
                    Assert.That(AutoBindTargetScriptSyncUtility.TryResolveMetadata(_target.m_targetScript, _setting,
                        out var current, out string currentError), Is.True, currentError);
                    Assert.That(current.CodePath, Is.EqualTo("Assets/AATest/SecondOutput"));
                }

                Assert.That(EditorJsonUtility.ToJson(_target), Is.EqualTo(before));
                Assert.That(EditorUtility.GetDirtyCount(_target), Is.EqualTo(dirtyCount));
                Assert.That(inspector.serializedObject.hasModifiedProperties, Is.False);
                AssertBindingsUnchanged();
            }
            finally { Object.DestroyImmediate(inspector); }
        }

        [Test]
        public void FollowingScriptDirectoryMatchesThePipelineCandidate()
        {
            SetConfiguration("Assets/Ignored", true);

            Assert.That(AutoBindTargetScriptSyncUtility.TryResolveMetadata(_target.m_targetScript, _setting,
                out var metadata, out string error), Is.True, error);

            Assert.That(metadata.CodePath, Is.EqualTo("Assets/AATest/Fixtures"));
            Assert.That(new AutoBindBuildInput(_target, _setting, _target.BindDatas).CodePath, Is.EqualTo(metadata.CodePath));
            Assert.That(_target.CodePath, Is.EqualTo("Assets/Previous"));
        }

        private void SetConfiguration(string path, bool followScript = false)
        {
            using (var settings = new SerializedObject(_setting))
            {
                settings.FindProperty("_codePath").stringValue = path;
                settings.FindProperty("_useGlobalDefaultSavePath").boolValue = followScript;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private ComponentAutoBindToolInspector CreateInspector() =>
            (ComponentAutoBindToolInspector)UnityEditor.Editor.CreateEditor(_target);

        private void AssertBindingsUnchanged()
        {
            Assert.That(_target.BindDatas.Count, Is.EqualTo(1));
            Assert.That(_target.BindDatas[0].Name, Is.EqualTo("Tf_Root"));
            Assert.That(_target.BindDatas[0].BindCom, Is.SameAs(_root.transform));
            Assert.That(_target.m_BindComs, Is.Empty, "元数据编辑不得自动重建运行时绑定缓存。");
        }

        private SerializedProperty Property(string name) => _serialized.FindProperty(name);

        private bool Sync(AutoBindGlobalSetting setting) => AutoBindTargetScriptSyncUtility.SyncFromTargetScript(
            Property("m_targetScript"), Property("_namespace"), Property("_className"), Property("_codePath"), setting);
    }
}
