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
        private GameObject m_Root;
        private Core.ComponentAutoBindTool m_Target;
        private AutoBindGlobalSetting m_Setting;
        private SerializedObject m_Serialized;

        [SetUp]
        public void SetUp()
        {
            m_Root = new GameObject("InspectorMetadataTest");
            m_Target = m_Root.AddComponent<Core.ComponentAutoBindTool>();
            m_Target.m_targetScript = m_Root.AddComponent<AutoBindPlainHost>();
            m_Target.BindDatas.Add(new Core.ComponentAutoBindTool.BindData("Tf_Root", m_Root.transform));
            m_Setting = ScriptableObject.CreateInstance<AutoBindGlobalSetting>();
            m_Serialized = new SerializedObject(m_Target);
            Property("m_Namespace").stringValue = "Previous.Namespace";
            Property("m_ClassName").stringValue = "PreviousHost";
            Property("m_CodePath").stringValue = "Assets/Previous";
            m_Serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearUndo(m_Target);
            m_Serialized.Dispose();
            Object.DestroyImmediate(m_Setting);
            Object.DestroyImmediate(m_Root);
        }

        [Test]
        public void ClearingTargetClearsMetadataWithoutConfigurationOrBindingChanges()
        {
            Property("m_targetScript").objectReferenceValue = null;

            bool succeeded = Sync(null);

            Assert.That(succeeded, Is.True);
            Assert.That(Property("m_Namespace").stringValue, Is.Empty);
            Assert.That(Property("m_ClassName").stringValue, Is.Empty);
            Assert.That(Property("m_CodePath").stringValue, Is.Empty);
            Assert.That(m_Target.ClassName, Is.EqualTo("PreviousHost"), "同步只准备序列化候选，不提前提交。");
            Assert.That(m_Target.BindDatas.Count, Is.EqualTo(1));
            Assert.That(m_Target.BindDatas[0].BindCom, Is.SameAs(m_Root.transform));
        }

        [Test]
        public void MissingConfigurationDoesNotPartiallyChangePendingMetadata()
        {
            LogAssert.Expect(UnityEngine.LogType.Error, new Regex("配置|路径"));

            bool succeeded = Sync(null);

            Assert.That(succeeded, Is.False);
            Assert.That(Property("m_Namespace").stringValue, Is.EqualTo("Previous.Namespace"));
            Assert.That(Property("m_ClassName").stringValue, Is.EqualTo("PreviousHost"));
            Assert.That(Property("m_CodePath").stringValue, Is.EqualTo("Assets/Previous"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExplicitSelectionAndMetadataUndoAndRedoTogether(bool clearTarget)
        {
            SetConfiguration("Assets/AATest/MetadataOutput");
            MonoBehaviour previous = m_Target.m_targetScript;
            MonoBehaviour selected = clearTarget ? null : m_Root.AddComponent<AutoBindVirtualHost>();
            var inspector = CreateInspector();
            Undo.IncrementCurrentGroup();
            try
            {
                inspector.serializedObject.Update();
                inspector.serializedObject.FindProperty("m_targetScript").objectReferenceValue = selected;

                Assert.That(inspector.ApplyTargetScriptChange(clearTarget ? null : m_Setting, out string error), Is.True, error);
                Undo.FlushUndoRecordObjects();
                Assert.That(m_Target.m_targetScript, Is.EqualTo(selected));
                Assert.That(m_Target.ClassName, Is.EqualTo(clearTarget ? "" : "AutoBindVirtualHost"));
                Assert.That(m_Target.CodePath, Is.EqualTo(clearTarget ? "" : "Assets/AATest/MetadataOutput"));
                AssertBindingsUnchanged();

                Undo.PerformUndo();
                Assert.That(m_Target.m_targetScript, Is.EqualTo(previous));
                Assert.That(m_Target.Namespace, Is.EqualTo("Previous.Namespace"));
                Assert.That(m_Target.ClassName, Is.EqualTo("PreviousHost"));
                Assert.That(m_Target.CodePath, Is.EqualTo("Assets/Previous"));
                AssertBindingsUnchanged();

                Undo.PerformRedo();
                Assert.That(m_Target.m_targetScript, Is.EqualTo(selected));
                Assert.That(m_Target.Namespace, Is.EqualTo(clearTarget ? "" : "ComponentAutoBindTool.Tests"));
                Assert.That(m_Target.ClassName, Is.EqualTo(clearTarget ? "" : "AutoBindVirtualHost"));
                Assert.That(m_Target.CodePath, Is.EqualTo(clearTarget ? "" : "Assets/AATest/MetadataOutput"));
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
            var replacement = m_Root.AddComponent<AutoBindVirtualHost>();
            string before = EditorJsonUtility.ToJson(m_Target);
            var inspector = CreateInspector();
            try
            {
                inspector.serializedObject.Update();
                inspector.serializedObject.FindProperty("m_targetScript").objectReferenceValue = replacement;

                bool succeeded = inspector.ApplyTargetScriptChange(codePath == null ? null : m_Setting, out string error);

                Assert.That(succeeded, Is.False);
                Assert.That(error, Is.Not.Empty);
                Assert.That(EditorJsonUtility.ToJson(m_Target), Is.EqualTo(before));
                Assert.That(inspector.serializedObject.FindProperty("m_targetScript").objectReferenceValue,
                    Is.EqualTo(m_Target.m_targetScript));
                AssertBindingsUnchanged();
            }
            finally { Object.DestroyImmediate(inspector); }
        }

        [Test]
        public void InspectorSummaryUsesCurrentConfigurationWithoutDirtyingTheTarget()
        {
            string before = EditorJsonUtility.ToJson(m_Target);
            int dirtyCount = EditorUtility.GetDirtyCount(m_Target);
            var inspector = CreateInspector();
            try
            {
                SetConfiguration("Assets/AATest/FirstOutput");
                Assert.That(AutoBindTargetScriptSyncUtility.TryResolveMetadata(m_Target.m_targetScript, m_Setting,
                    out var first, out string firstError), Is.True, firstError);
                Assert.That(first.Namespace, Is.EqualTo("ComponentAutoBindTool.Tests"));
                Assert.That(first.ClassName, Is.EqualTo("AutoBindPlainHost"));
                Assert.That(first.CodePath, Is.EqualTo("Assets/AATest/FirstOutput"));

                SetConfiguration("Assets/AATest/SecondOutput");
                for (int i = 0; i < 3; i++)
                {
                    Assert.That(AutoBindTargetScriptSyncUtility.TryResolveMetadata(m_Target.m_targetScript, m_Setting,
                        out var current, out string currentError), Is.True, currentError);
                    Assert.That(current.CodePath, Is.EqualTo("Assets/AATest/SecondOutput"));
                }

                Assert.That(EditorJsonUtility.ToJson(m_Target), Is.EqualTo(before));
                Assert.That(EditorUtility.GetDirtyCount(m_Target), Is.EqualTo(dirtyCount));
                Assert.That(inspector.serializedObject.hasModifiedProperties, Is.False);
                AssertBindingsUnchanged();
            }
            finally { Object.DestroyImmediate(inspector); }
        }

        [Test]
        public void FollowingScriptDirectoryMatchesThePipelineCandidate()
        {
            SetConfiguration("Assets/Ignored", true);

            Assert.That(AutoBindTargetScriptSyncUtility.TryResolveMetadata(m_Target.m_targetScript, m_Setting,
                out var metadata, out string error), Is.True, error);

            Assert.That(metadata.CodePath, Is.EqualTo("Assets/AATest/Fixtures"));
            Assert.That(new AutoBindBuildInput(m_Target, m_Setting, m_Target.BindDatas).CodePath, Is.EqualTo(metadata.CodePath));
            Assert.That(m_Target.CodePath, Is.EqualTo("Assets/Previous"));
        }

        private void SetConfiguration(string path, bool followScript = false)
        {
            using (var settings = new SerializedObject(m_Setting))
            {
                settings.FindProperty("m_CodePath").stringValue = path;
                settings.FindProperty("m_UseGlobalDefaultSavePath").boolValue = followScript;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private ComponentAutoBindToolInspector CreateInspector() =>
            (ComponentAutoBindToolInspector)UnityEditor.Editor.CreateEditor(m_Target);

        private void AssertBindingsUnchanged()
        {
            Assert.That(m_Target.BindDatas.Count, Is.EqualTo(1));
            Assert.That(m_Target.BindDatas[0].Name, Is.EqualTo("Tf_Root"));
            Assert.That(m_Target.BindDatas[0].BindCom, Is.SameAs(m_Root.transform));
            Assert.That(m_Target.m_BindComs, Is.Empty, "元数据编辑不得自动重建运行时绑定缓存。");
        }

        private SerializedProperty Property(string name) => m_Serialized.FindProperty(name);

        private bool Sync(AutoBindGlobalSetting setting) => AutoBindTargetScriptSyncUtility.SyncFromTargetScript(
            Property("m_targetScript"), Property("m_Namespace"), Property("m_ClassName"), Property("m_CodePath"), setting);
    }
}
