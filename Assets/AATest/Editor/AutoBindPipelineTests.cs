using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ComponentAutoBindTool.Editor;
using NUnit.Framework;
using ComponentAutoBindTool.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Tests
{
    public class AutoBindPipelineTests
    {
        private string m_Folder;
        private Scene m_Scene;
        private Scene m_PreviousScene;
        private AutoBindGlobalSetting m_Setting;
        private AutoBindKeyMapSetting m_KeyMap;
        private Core.ComponentAutoBindTool m_Tool;
        private GameObject m_Child;
        private bool m_Editing;

        [SetUp]
        public void SetUp()
        {
            m_PreviousScene = SceneManager.GetActiveScene();
            m_Scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(m_Scene);
            m_Folder = "Assets/AATest/Temporary/" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(m_Folder);
            m_Setting = ScriptableObject.CreateInstance<AutoBindGlobalSetting>();
            var settingData = new SerializedObject(m_Setting);
            settingData.FindProperty("m_UseGlobalDefaultSavePath").boolValue = false;
            settingData.FindProperty("m_CodePath").stringValue = m_Folder;
            settingData.ApplyModifiedPropertiesWithoutUndo();
            m_KeyMap = ScriptableObject.CreateInstance<AutoBindKeyMapSetting>();
            var root = new GameObject("Root");
            m_Tool = root.AddComponent<Core.ComponentAutoBindTool>();
            m_Tool.m_targetScript = root.AddComponent<AutoBindPlainHost>();
            m_Child = new GameObject("Btn_First", typeof(RectTransform), typeof(Button));
            m_Child.transform.SetParent(root.transform);
            Assert.That(EditorSceneManager.SaveScene(m_Scene, m_Folder + "/Test.unity"), Is.True);
            BeginAssetEditing();
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (m_Scene.IsValid() && m_Scene.isLoaded) EditorSceneManager.CloseScene(m_Scene, true);
                if (m_PreviousScene.IsValid() && m_PreviousScene.isLoaded) SceneManager.SetActiveScene(m_PreviousScene);
            }
            finally
            {
                try
                {
                    Object.DestroyImmediate(m_Setting);
                    Object.DestroyImmediate(m_KeyMap);
                }
                finally
                {
                    try
                    {
                        DeleteTemporaryAssets();
                    }
                    finally
                    {
                        try
                        {
                            EndAssetEditing();
                        }
                        finally
                        {
                            // 测试可能导入过生成脚本；进入后续 PlayMode 前同步删除后的编译输入。
                            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                        }
                    }
                }
            }
        }

        private void DeleteTemporaryAssets()
        {
            if (string.IsNullOrEmpty(m_Folder)) return;
            if (Directory.Exists(m_Folder))
            {
                foreach (string file in Directory.EnumerateFiles(m_Folder, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
            }
            if (File.Exists(m_Folder + ".meta")) File.SetAttributes(m_Folder + ".meta", FileAttributes.Normal);

            // 先通知资产数据库，避免已导入的脚本只从磁盘消失而仍留在编译列表中。
            if (!AssetDatabase.DeleteAsset(m_Folder))
            {
                // 尚未导入的目录没有资产记录，清理其物理文件后由 TearDown 统一同步。
                if (Directory.Exists(m_Folder)) FileUtil.DeleteFileOrDirectory(m_Folder);
                if (File.Exists(m_Folder + ".meta")) FileUtil.DeleteFileOrDirectory(m_Folder + ".meta");
            }
            if (Directory.Exists(m_Folder) || File.Exists(m_Folder + ".meta"))
                throw new IOException("未能清理流水线测试临时资产：" + m_Folder);
        }

        private void BeginAssetEditing()
        {
            AssetDatabase.StartAssetEditing();
            m_Editing = true;
        }

        private void EndAssetEditing()
        {
            if (!m_Editing) return;
            m_Editing = false;
            AssetDatabase.StopAssetEditing();
        }

        private AutoBindPipelineReport Generate() => AutoBindBuildPipeline.GenerateCode(m_Tool, m_Setting, m_KeyMap);
        private void AssertSuccess(AutoBindPipelineReport report) => Assert.That(report.Succeeded, Is.True, report.ValidationResult.BuildReport());

        [Test]
        public void GenerateRescansAndSecondRunDoesNotRewrite()
        {
            var first = Generate();
            AssertSuccess(first);
            string initial = File.ReadAllText(first.GeneratedFilePath);
            DateTime timestamp = File.GetLastWriteTimeUtc(first.GeneratedFilePath);
            var second = Generate();
            AssertSuccess(second);
            Assert.That(second.CodeFileUpdated, Is.False);
            Assert.That(second.BindingsUpdated, Is.False);
            Assert.That(File.GetLastWriteTimeUtc(first.GeneratedFilePath), Is.EqualTo(timestamp));
            Assert.That(initial, Does.Not.Contain("\r"));
            Assert.That(File.ReadAllBytes(first.GeneratedFilePath).Take(3).ToArray(), Is.Not.EqualTo(new byte[] { 239, 187, 191 }));
            m_Child.name = "Btn_Renamed";
            var renamed = Generate();
            AssertSuccess(renamed);
            Assert.That(m_Tool.BindDatas[0].Name, Is.EqualTo("Btn_Renamed"));
            Assert.That(File.ReadAllText(renamed.GeneratedFilePath), Does.Contain("btnRenamed"));
            Assert.That(m_Scene.isDirty, Is.False);
        }

        [TestCase("Unknown_Field")]
        [TestCase("Txt_Missing")]
        [TestCase("Btn_Bad-Name")]
        public void InvalidScanPreservesObjectAndFiles(string nodeName)
        {
            var first = Generate();
            AssertSuccess(first);
            string snapshot = EditorJsonUtility.ToJson(m_Tool);
            byte[] scene = File.ReadAllBytes(m_Scene.path);
            byte[] code = File.ReadAllBytes(first.GeneratedFilePath);
            m_Child.name = nodeName;
            var failed = Generate();
            Assert.That(failed.Succeeded, Is.False);
            Assert.That(failed.ValidationResult.ErrorCount, Is.GreaterThan(0));
            Assert.That(EditorJsonUtility.ToJson(m_Tool), Is.EqualTo(snapshot));
            Assert.That(File.ReadAllBytes(m_Scene.path), Is.EqualTo(scene));
            Assert.That(File.ReadAllBytes(first.GeneratedFilePath), Is.EqualTo(code));
            var rescan = AutoBindBuildPipeline.RescanAndValidate(m_Tool, m_Setting, m_KeyMap);
            Assert.That(rescan.Succeeded, Is.False);
            Assert.That(EditorJsonUtility.ToJson(m_Tool), Is.EqualTo(snapshot));
        }

        [Test]
        public void DuplicateFieldsAndKeywordsAreRejected()
        {
            var duplicate = new GameObject("Btn_First", typeof(RectTransform), typeof(Button));
            duplicate.transform.SetParent(m_Tool.transform);
            Assert.That(Generate().ValidationResult.BuildReport(), Does.Contain("重复"));
            Assert.That(AutoBindFieldNameUtility.IsValidIdentifier("class"), Is.False);
            Assert.That(AutoBindFieldNameUtility.BuildFieldName("TMTxt_Title"), Is.EqualTo("tMTxtTitle"));
        }

        [Test]
        public void WriteFailureAfterSavingRestoresBindingsAndUnsavedSceneEdits()
        {
            var first = Generate();
            AssertSuccess(first);
            byte[] scene = File.ReadAllBytes(m_Scene.path);
            byte[] code = File.ReadAllBytes(first.GeneratedFilePath);
            string snapshot = EditorJsonUtility.ToJson(m_Tool);
            m_Child.name = "Btn_Renamed";
            var unrelated = new GameObject("先前未保存的编辑");
            EditorSceneManager.MarkSceneDirty(m_Scene);
            void FailCodeCommit(Scene saved)
            {
                if (saved == m_Scene) File.SetAttributes(first.GeneratedFilePath, FileAttributes.ReadOnly);
            }
            EditorSceneManager.sceneSaved += FailCodeCommit;
            AutoBindPipelineReport failed;
            try { failed = Generate(); }
            finally
            {
                EditorSceneManager.sceneSaved -= FailCodeCommit;
                File.SetAttributes(first.GeneratedFilePath, FileAttributes.Normal);
            }
            Assert.That(failed.Succeeded, Is.False);
            Assert.That(EditorJsonUtility.ToJson(m_Tool), Is.EqualTo(snapshot));
            Assert.That(File.ReadAllBytes(m_Scene.path), Is.EqualTo(scene));
            Assert.That(File.ReadAllBytes(first.GeneratedFilePath), Is.EqualTo(code));
            Assert.That(unrelated != null && unrelated.name == "先前未保存的编辑", Is.True);
            Assert.That(m_Child.name, Is.EqualTo("Btn_Renamed"));
            Assert.That(m_Scene.isDirty, Is.True);
        }

        [Test]
        public void SaveFailureIsReportedWithoutCommitting()
        {
            string snapshot = EditorJsonUtility.ToJson(m_Tool);
            File.SetAttributes(m_Scene.path, FileAttributes.ReadOnly);
            AutoBindPipelineReport failed;
            try { failed = Generate(); }
            finally { File.SetAttributes(m_Scene.path, FileAttributes.Normal); }
            Assert.That(failed.Succeeded, Is.False);
            Assert.That(EditorJsonUtility.ToJson(m_Tool), Is.EqualTo(snapshot));
            Assert.That(Directory.GetFiles(m_Folder, "*.cs"), Is.Empty);
        }

        [Test]
        public void MissingFieldNeverFallsBackToSameTypeAtOldIndex()
        {
            AssertSuccess(Generate());
            LogAssert.Expect(UnityEngine.LogType.Error, new Regex("字段不存在"));
            Assert.That(m_Tool.GetBindComponent<Button>(0, "missing"), Is.Null);
            Assert.That(m_Tool.GetBindComponent<Button>(0), Is.SameAs(m_Child.GetComponent<Button>()));
        }

        [Test]
        public void RescanSupportsUndo()
        {
            string snapshot = EditorJsonUtility.ToJson(m_Tool);
            AssertSuccess(AutoBindBuildPipeline.RescanAndValidate(m_Tool, m_Setting, m_KeyMap));
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            Assert.That(EditorJsonUtility.ToJson(m_Tool), Is.EqualTo(snapshot));
        }

        [Test]
        public void PathNormalizationHonorsAssetsBoundaryAndLegacyFormat()
        {
            Assert.That(AutoBindPathUtility.NormalizeCodePath("/Third Party/Test"),
                Is.EqualTo(Application.dataPath.Replace('\\', '/') + "/Third Party/Test"));
            Assert.That(AutoBindPathUtility.NormalizeCodePath("Assets"), Is.EqualTo(Application.dataPath.Replace('\\', '/')));
            Assert.That(AutoBindPathUtility.IsPathUnderAssets(Application.dataPath + "Backup"), Is.False);
            Assert.That(AutoBindPathUtility.IsPathUnderAssets("Assets/../Outside"), Is.False);
        }

        [Test]
        public void UnsavedSceneAndDifferentAssemblyAreRejected()
        {
            Scene unsaved = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.MoveGameObjectToScene(m_Tool.gameObject, unsaved);
            Assert.That(Generate().ValidationResult.BuildReport(), Does.Contain("先保存"));
            SceneManager.MoveGameObjectToScene(m_Tool.gameObject, m_Scene);
            EditorSceneManager.CloseScene(unsaved, true);
            var data = new SerializedObject(m_Setting);
            data.FindProperty("m_CodePath").stringValue = "Assets/AATest/Editor/Output";
            data.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(Generate().ValidationResult.BuildReport(), Does.Contain("同一程序集"));
        }

        [Test]
        public void OutputOwnedByAnotherHostIsNotOverwritten()
        {
            string output = m_Folder + "/AutoBindPlainHost.BindComponents.cs";
            File.WriteAllText(output, "// 手写文件");
            var result = Generate();
            Assert.That(result.Succeeded, Is.False);
            Assert.That(File.ReadAllText(output), Is.EqualTo("// 手写文件"));
        }

        [Test]
        public void NestedRootIsBoundButItsChildrenAreNotScanned()
        {
            m_Child.name = "Tf_Nested";
            m_Child.AddComponent<Core.ComponentAutoBindTool>();
            var badDescendant = new GameObject("Unknown_Ignored");
            badDescendant.transform.SetParent(m_Child.transform);
            AssertSuccess(Generate());
            Assert.That(m_Tool.BindDatas.Count, Is.EqualTo(1));
        }

        [Test]
        public void SavedSceneReloadKeepsRuntimeBindings()
        {
            var report = Generate();
            AssertSuccess(report);
            string scenePath = m_Scene.path;
            EditorSceneManager.CloseScene(m_Scene, true);
            m_Scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            var tool = m_Scene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<Core.ComponentAutoBindTool>()).Single();
            Assert.That(tool.GetBindComponent<Button>(-1, "btnFirst"), Is.Not.Null);
        }

        [Test]
        public void PrefabSaveIncludesOtherDirtyComponentsAndCanReload()
        {
            AssertSuccess(Generate());
            EndAssetEditing();
            string prefabPath = m_Folder + "/Host.prefab";
            PrefabUtility.SaveAsPrefabAsset(m_Tool.gameObject, prefabPath, out bool saved);
            Assert.That(saved, Is.True);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            m_Tool = prefab.GetComponent<Core.ComponentAutoBindTool>();
            prefab.transform.localPosition = new Vector3(17, 23, 0);
            EditorUtility.SetDirty(prefab.transform);
            AssertSuccess(Generate());
            var loaded = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Assert.That(loaded.transform.localPosition, Is.EqualTo(new Vector3(17, 23, 0)));
                Assert.That(loaded.GetComponent<Core.ComponentAutoBindTool>().GetBindComponent<Button>(-1, "btnFirst"), Is.Not.Null);
            }
            finally { PrefabUtility.UnloadPrefabContents(loaded); }
        }

        [Test]
        public void UngeneratedWidgetCanBeAttachedAndReportsMissingGeneration()
        {
            var host = m_Child.AddComponent<AutoBindUngeneratedWidget>();
            LogAssert.Expect(UnityEngine.LogType.Error, new Regex("尚未生成 Widget UIView"));
            Assert.That(host.GetGeneratedUIView(), Is.Null);
        }

        [Test]
        public void FailedWidgetBindingCanRetryAfterSerializedDataIsRepaired()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Example.Editor.AutoBindExampleSetup.PrefabPath);
            var instance = Object.Instantiate(prefab);
            var widget = instance.GetComponentInChildren<Example.AutoBindExampleWidget>(true);
            var tool = widget.GetComponent<Core.ComponentAutoBindTool>();
            tool.m_BindComs.Clear();
            LogAssert.Expect(UnityEngine.LogType.Error, new Regex("索引超出范围"));
            LogAssert.Expect(UnityEngine.LogType.Error, new Regex("索引超出范围"));
            Assert.That(widget.GetGeneratedUIView(), Is.Null);
            var serialized = new SerializedObject(tool);
            AutoBindSerializedBindingUtility.SyncRuntimeBindingCaches(serialized.FindProperty("BindDatas"),
                serialized.FindProperty("m_BindComs"), serialized.FindProperty("m_BindFieldNames"));
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(widget.GetGeneratedUIView(), Is.Not.Null);
            Object.DestroyImmediate(instance);
        }

        [TestCase(typeof(AutoBindExplicitHost))]
        [TestCase(typeof(AutoBindProtectedHost))]
        [TestCase(typeof(AutoBindVirtualHost))]
        [TestCase(typeof(AutoBindOverloadedHost))]
        public void AlternateHostsGenerateCorrectInterfaceAndModifiers(Type hostType)
        {
            Object.DestroyImmediate(m_Tool.m_targetScript);
            m_Tool.m_targetScript = (MonoBehaviour)m_Tool.gameObject.AddComponent(hostType);
            var report = Generate();
            AssertSuccess(report);
            string code = File.ReadAllText(report.GeneratedFilePath);
            Assert.That(code, Does.Contain(": global::ComponentAutoBindTool.ViewCore.IAutoBindHost"));
            if (hostType == typeof(AutoBindProtectedHost))
                Assert.That(code, Does.Contain("protected internal override void EnsureAutoBind"));
        }

        [TestCase(typeof(AutoBindConflictingHost))]
        [TestCase(typeof(AutoBindPartialConflictHost))]
        [TestCase(typeof(AutoBindMultiFieldConflictHost))]
        [TestCase(typeof(AutoBindMethodFieldConflictHost))]
        [TestCase(typeof(AutoBindNamespaceAliasConflictHost))]
        public void RealPartialAndExplicitMemberConflictsAreRejected(Type hostType)
        {
            Object.DestroyImmediate(m_Tool.m_targetScript);
            m_Tool.m_targetScript = (MonoBehaviour)m_Tool.gameObject.AddComponent(hostType);
            var result = Generate();
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.ValidationResult.BuildReport(), Does.Contain("冲突"));
        }

        [TestCase(typeof(AutoBindSealedHost))]
        [TestCase(typeof(AutoBindHiddenHost))]
        public void NearestSealedOrHiddenBaseMethodPreventsGeneration(Type hostType)
        {
            Object.DestroyImmediate(m_Tool.m_targetScript);
            m_Tool.m_targetScript = (MonoBehaviour)m_Tool.gameObject.AddComponent(hostType);
            string snapshot = EditorJsonUtility.ToJson(m_Tool);
            var result = Generate();
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.ValidationResult.BuildReport(), Does.Contain("最近的基类"));
            Assert.That(EditorJsonUtility.ToJson(m_Tool), Is.EqualTo(snapshot));
            Assert.That(Directory.GetFiles(m_Folder, "*.cs"), Is.Empty);
        }

        [Test]
        public void PrefabStageSavesBindingsToTheOpenedPrefab()
        {
            string prefabPath = m_Folder + "/Stage.prefab";
            PrefabUtility.SaveAsPrefabAsset(m_Tool.gameObject, prefabPath, out bool saved);
            Assert.That(saved, Is.True);
            EndAssetEditing();
            var stage = PrefabStageUtility.OpenPrefab(prefabPath);
            BeginAssetEditing();
            try
            {
                m_Tool = stage.prefabContentsRoot.GetComponent<Core.ComponentAutoBindTool>();
                stage.prefabContentsRoot.transform.GetChild(0).name = "Btn_Stage";
                AssertSuccess(Generate());
                Assert.That(stage.scene.isDirty, Is.False);
            }
            finally
            {
                stage.ClearDirtiness();
                StageUtility.GoToMainStage();
            }
            var loaded = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Assert.That(loaded.GetComponent<Core.ComponentAutoBindTool>().GetBindComponent<Button>(-1, "btnStage"), Is.Not.Null);
            }
            finally { PrefabUtility.UnloadPrefabContents(loaded); }
        }

        [Test]
        public void PrefabInstanceGenerationSavesSceneOverridesWithoutApplyingToAsset()
        {
            string prefabPath = m_Folder + "/Instance.prefab";
            PrefabUtility.SaveAsPrefabAsset(m_Tool.gameObject, prefabPath, out bool saved);
            Assert.That(saved, Is.True);
            EndAssetEditing();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, m_Scene);
            m_Tool = instance.GetComponent<Core.ComponentAutoBindTool>();
            instance.transform.GetChild(0).name = "Btn_Instance";
            PrefabUtility.RecordPrefabInstancePropertyModifications(instance.transform.GetChild(0).gameObject);
            byte[] originalPrefab = File.ReadAllBytes(prefabPath);
            BeginAssetEditing();
            AssertSuccess(Generate());
            Assert.That(File.ReadAllBytes(prefabPath), Is.EqualTo(originalPrefab));
            string scenePath = m_Scene.path;
            EditorSceneManager.CloseScene(m_Scene, true);
            m_Scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            var reloaded = m_Scene.GetRootGameObjects().Single(PrefabUtility.IsPartOfPrefabInstance);
            Assert.That(reloaded.GetComponent<Core.ComponentAutoBindTool>().GetBindComponent<Button>(-1, "btnInstance"), Is.Not.Null);
        }
    }
}
