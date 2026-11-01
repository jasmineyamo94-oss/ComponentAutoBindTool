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
        private string _folder;
        private Scene _scene;
        private Scene _previousScene;
        private AutoBindGlobalSetting _setting;
        private AutoBindKeyMapSetting _keyMap;
        private Core.ComponentAutoBindTool _tool;
        private GameObject _child;
        private bool _editing;

        [SetUp]
        public void SetUp()
        {
            _previousScene = SceneManager.GetActiveScene();
            _scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(_scene);
            _folder = "Assets/AATest/Temporary/" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(_folder);
            _setting = ScriptableObject.CreateInstance<AutoBindGlobalSetting>();
            var settingData = new SerializedObject(_setting);
            settingData.FindProperty("_useGlobalDefaultSavePath").boolValue = false;
            settingData.FindProperty("_codePath").stringValue = _folder;
            settingData.ApplyModifiedPropertiesWithoutUndo();
            _keyMap = ScriptableObject.CreateInstance<AutoBindKeyMapSetting>();
            var root = new GameObject("Root");
            _tool = root.AddComponent<Core.ComponentAutoBindTool>();
            _tool.m_targetScript = root.AddComponent<AutoBindPlainHost>();
            _child = new GameObject("Btn_First", typeof(RectTransform), typeof(Button));
            _child.transform.SetParent(root.transform);
            Assert.That(EditorSceneManager.SaveScene(_scene, _folder + "/Test.unity"), Is.True);
            BeginAssetEditing();
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (_scene.IsValid() && _scene.isLoaded) EditorSceneManager.CloseScene(_scene, true);
                if (_previousScene.IsValid() && _previousScene.isLoaded) SceneManager.SetActiveScene(_previousScene);
            }
            finally
            {
                try
                {
                    Object.DestroyImmediate(_setting);
                    Object.DestroyImmediate(_keyMap);
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
            if (string.IsNullOrEmpty(_folder)) return;
            if (Directory.Exists(_folder))
            {
                foreach (string file in Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
            }
            if (File.Exists(_folder + ".meta")) File.SetAttributes(_folder + ".meta", FileAttributes.Normal);

            // 先通知资产数据库，避免已导入的脚本只从磁盘消失而仍留在编译列表中。
            if (!AssetDatabase.DeleteAsset(_folder))
            {
                // 尚未导入的目录没有资产记录，清理其物理文件后由 TearDown 统一同步。
                if (Directory.Exists(_folder)) FileUtil.DeleteFileOrDirectory(_folder);
                if (File.Exists(_folder + ".meta")) FileUtil.DeleteFileOrDirectory(_folder + ".meta");
            }
            if (Directory.Exists(_folder) || File.Exists(_folder + ".meta"))
                throw new IOException("未能清理流水线测试临时资产：" + _folder);
        }

        private void BeginAssetEditing()
        {
            AssetDatabase.StartAssetEditing();
            _editing = true;
        }

        private void EndAssetEditing()
        {
            if (!_editing) return;
            _editing = false;
            AssetDatabase.StopAssetEditing();
        }

        private AutoBindPipelineReport Generate() => AutoBindBuildPipeline.GenerateCode(_tool, _setting, _keyMap);
        private void AssertSuccess(AutoBindPipelineReport report) => Assert.That(report.Succeeded, Is.True, report.ValidationResult.BuildReport());

        [Test]
        public void GenerateRescansAndSecondRunDoesNotRewrite()
        {
            var first = Generate();
            AssertSuccess(first);
            string initial = File.ReadAllText(first.GeneratedFilePath);
            Assert.That(initial, Does.Contain("private UIView _view;"));
            Assert.That(initial, Does.Not.Contain("private UIView view;"));
            DateTime timestamp = File.GetLastWriteTimeUtc(first.GeneratedFilePath);
            var second = Generate();
            AssertSuccess(second);
            Assert.That(second.CodeFileUpdated, Is.False);
            Assert.That(second.BindingsUpdated, Is.False);
            Assert.That(File.GetLastWriteTimeUtc(first.GeneratedFilePath), Is.EqualTo(timestamp));
            Assert.That(initial, Does.Not.Contain("\r"));
            Assert.That(File.ReadAllBytes(first.GeneratedFilePath).Take(3).ToArray(), Is.Not.EqualTo(new byte[] { 239, 187, 191 }));
            _child.name = "Btn_Renamed";
            var renamed = Generate();
            AssertSuccess(renamed);
            Assert.That(_tool.BindDatas[0].Name, Is.EqualTo("Btn_Renamed"));
            Assert.That(File.ReadAllText(renamed.GeneratedFilePath), Does.Contain("btnRenamed"));
            Assert.That(_scene.isDirty, Is.True);
        }

        [TestCase("Unknown_Field")]
        [TestCase("Txt_Missing")]
        [TestCase("Btn_Bad-Name")]
        public void InvalidScanPreservesObjectAndFiles(string nodeName)
        {
            var first = Generate();
            AssertSuccess(first);
            string snapshot = EditorJsonUtility.ToJson(_tool);
            byte[] scene = File.ReadAllBytes(_scene.path);
            byte[] code = File.ReadAllBytes(first.GeneratedFilePath);
            _child.name = nodeName;
            var failed = Generate();
            Assert.That(failed.Succeeded, Is.False);
            Assert.That(failed.ValidationResult.ErrorCount, Is.GreaterThan(0));
            Assert.That(EditorJsonUtility.ToJson(_tool), Is.EqualTo(snapshot));
            Assert.That(File.ReadAllBytes(_scene.path), Is.EqualTo(scene));
            Assert.That(File.ReadAllBytes(first.GeneratedFilePath), Is.EqualTo(code));
            var rescan = AutoBindBuildPipeline.RescanAndValidate(_tool, _setting, _keyMap);
            Assert.That(rescan.Succeeded, Is.False);
            Assert.That(EditorJsonUtility.ToJson(_tool), Is.EqualTo(snapshot));
        }

        [Test]
        public void DuplicateFieldsAndKeywordsAreRejected()
        {
            var duplicate = new GameObject("Btn_First", typeof(RectTransform), typeof(Button));
            duplicate.transform.SetParent(_tool.transform);
            Assert.That(Generate().ValidationResult.BuildReport(), Does.Contain("重复"));
            Assert.That(AutoBindFieldNameUtility.IsValidIdentifier("class"), Is.False);
            Assert.That(AutoBindFieldNameUtility.BuildFieldName("TMTxt_Title"), Is.EqualTo("tMTxtTitle"));
        }

        [Test]
        public void CommitFailureRestoresBindingsAndUnsavedSceneEdits()
        {
            var first = Generate();
            AssertSuccess(first);
            byte[] scene = File.ReadAllBytes(_scene.path);
            byte[] code = File.ReadAllBytes(first.GeneratedFilePath);
            string snapshot = EditorJsonUtility.ToJson(_tool);
            _child.name = "Btn_Renamed";
            var unrelated = new GameObject("先前未保存的编辑");
            EditorSceneManager.MarkSceneDirty(_scene);
            File.Delete(first.GeneratedFilePath);
            Directory.CreateDirectory(first.GeneratedFilePath);
            AutoBindPipelineReport failed;
            try { failed = Generate(); }
            finally
            {
                Directory.Delete(first.GeneratedFilePath);
                File.WriteAllBytes(first.GeneratedFilePath, code);
            }
            Assert.That(failed.Succeeded, Is.False);
            Assert.That(EditorJsonUtility.ToJson(_tool), Is.EqualTo(snapshot));
            Assert.That(File.ReadAllBytes(_scene.path), Is.EqualTo(scene));
            Assert.That(unrelated != null, Is.True);
            Assert.That(_child.name, Is.EqualTo("Btn_Renamed"));
            Assert.That(_scene.isDirty, Is.True);
        }

        [Test]
        public void ReadOnlySceneDoesNotPreventGenerationOrChangeDisk()
        {
            byte[] scene = File.ReadAllBytes(_scene.path);
            File.SetAttributes(_scene.path, FileAttributes.ReadOnly);
            try { AssertSuccess(Generate()); }
            finally { File.SetAttributes(_scene.path, FileAttributes.Normal); }
            Assert.That(File.ReadAllBytes(_scene.path), Is.EqualTo(scene));
            Assert.That(_scene.isDirty, Is.True);
        }

        [Test]
        public void GenerationSupportsUndoWithoutRemovingCodeFile()
        {
            string snapshot = EditorJsonUtility.ToJson(_tool);
            var report = Generate();
            AssertSuccess(report);
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            Assert.That(EditorJsonUtility.ToJson(_tool), Is.EqualTo(snapshot));
            Assert.That(File.Exists(report.GeneratedFilePath), Is.True);
        }

        [Test]
        public void MissingFieldNeverFallsBackToSameTypeAtOldIndex()
        {
            AssertSuccess(Generate());
            LogAssert.Expect(UnityEngine.LogType.Error, new Regex("字段不存在"));
            Assert.That(_tool.GetBindComponent<Button>(0, "missing"), Is.Null);
            Assert.That(_tool.GetBindComponent<Button>(0), Is.SameAs(_child.GetComponent<Button>()));
        }

        [Test]
        public void RescanSupportsUndo()
        {
            string snapshot = EditorJsonUtility.ToJson(_tool);
            AssertSuccess(AutoBindBuildPipeline.RescanAndValidate(_tool, _setting, _keyMap));
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            Assert.That(EditorJsonUtility.ToJson(_tool), Is.EqualTo(snapshot));
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
        public void UnsavedSceneGeneratesButDifferentAssemblyIsRejected()
        {
            Scene unsaved = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.MoveGameObjectToScene(_tool.gameObject, unsaved);
            AssertSuccess(Generate());
            Assert.That(unsaved.isDirty, Is.True);
            SceneManager.MoveGameObjectToScene(_tool.gameObject, _scene);
            EditorSceneManager.CloseScene(unsaved, true);
            var data = new SerializedObject(_setting);
            data.FindProperty("_codePath").stringValue = "Assets/AATest/Editor/Output";
            data.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(Generate().ValidationResult.BuildReport(), Does.Contain("同一程序集"));
        }

        [Test]
        public void OutputOwnedByAnotherHostIsNotOverwritten()
        {
            string output = _folder + "/AutoBindPlainHost.BindComponents.cs";
            File.WriteAllText(output, "// 手写文件");
            var result = Generate();
            Assert.That(result.Succeeded, Is.False);
            Assert.That(File.ReadAllText(output), Is.EqualTo("// 手写文件"));
        }

        [Test]
        public void NestedRootIsBoundButItsChildrenAreNotScanned()
        {
            _child.name = "Tf_Nested";
            _child.AddComponent<Core.ComponentAutoBindTool>();
            var badDescendant = new GameObject("Unknown_Ignored");
            badDescendant.transform.SetParent(_child.transform);
            AssertSuccess(Generate());
            Assert.That(_tool.BindDatas.Count, Is.EqualTo(1));
        }

        [Test]
        public void SavedSceneReloadKeepsRuntimeBindings()
        {
            var report = Generate();
            AssertSuccess(report);
            string scenePath = _scene.path;
            Assert.That(EditorSceneManager.SaveScene(_scene), Is.True);
            EditorSceneManager.CloseScene(_scene, true);
            _scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            var tool = _scene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<Core.ComponentAutoBindTool>()).Single();
            Assert.That(tool.GetBindComponent<Button>(-1, "btnFirst"), Is.Not.Null);
        }

        [Test]
        public void PrefabGenerationLeavesOtherDirtyComponentsUnsaved()
        {
            AssertSuccess(Generate());
            EndAssetEditing();
            string prefabPath = _folder + "/Host.prefab";
            PrefabUtility.SaveAsPrefabAsset(_tool.gameObject, prefabPath, out bool saved);
            Assert.That(saved, Is.True);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            _tool = prefab.GetComponent<Core.ComponentAutoBindTool>();
            prefab.transform.localPosition = new Vector3(17, 23, 0);
            EditorUtility.SetDirty(prefab.transform);
            byte[] before = File.ReadAllBytes(prefabPath);
            AssertSuccess(Generate());
            Assert.That(EditorUtility.IsDirty(prefab.transform), Is.True);
            Assert.That(File.ReadAllBytes(prefabPath), Is.EqualTo(before));
            var loaded = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Assert.That(loaded.transform.localPosition, Is.Not.EqualTo(new Vector3(17, 23, 0)));
                Assert.That(loaded.GetComponent<Core.ComponentAutoBindTool>().GetBindComponent<Button>(-1, "btnFirst"), Is.Not.Null);
            }
            finally { PrefabUtility.UnloadPrefabContents(loaded); }
        }

        [Test]
        public void UngeneratedWidgetCanBeAttachedAndReportsMissingGeneration()
        {
            var host = _child.AddComponent<AutoBindUngeneratedWidget>();
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
                serialized.FindProperty("m_BindComs"), serialized.FindProperty("_bindFieldNames"));
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
            Object.DestroyImmediate(_tool.m_targetScript);
            _tool.m_targetScript = (MonoBehaviour)_tool.gameObject.AddComponent(hostType);
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
            Object.DestroyImmediate(_tool.m_targetScript);
            _tool.m_targetScript = (MonoBehaviour)_tool.gameObject.AddComponent(hostType);
            var result = Generate();
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.ValidationResult.BuildReport(), Does.Contain("冲突"));
        }

        [TestCase(typeof(AutoBindSealedHost))]
        [TestCase(typeof(AutoBindHiddenHost))]
        public void NearestSealedOrHiddenBaseMethodPreventsGeneration(Type hostType)
        {
            Object.DestroyImmediate(_tool.m_targetScript);
            _tool.m_targetScript = (MonoBehaviour)_tool.gameObject.AddComponent(hostType);
            string snapshot = EditorJsonUtility.ToJson(_tool);
            var result = Generate();
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.ValidationResult.BuildReport(), Does.Contain("最近的基类"));
            Assert.That(EditorJsonUtility.ToJson(_tool), Is.EqualTo(snapshot));
            Assert.That(Directory.GetFiles(_folder, "*.cs"), Is.Empty);
        }

        [Test]
        public void PrefabStageGenerationLeavesBindingsDirtyWithoutSaving()
        {
            string prefabPath = _folder + "/Stage.prefab";
            PrefabUtility.SaveAsPrefabAsset(_tool.gameObject, prefabPath, out bool saved);
            Assert.That(saved, Is.True);
            EndAssetEditing();
            var stage = PrefabStageUtility.OpenPrefab(prefabPath);
            BeginAssetEditing();
            try
            {
                _tool = stage.prefabContentsRoot.GetComponent<Core.ComponentAutoBindTool>();
                stage.prefabContentsRoot.transform.GetChild(0).name = "Btn_Stage";
                AssertSuccess(Generate());
                Assert.That(stage.scene.isDirty, Is.True);
                Assert.That(_tool.GetBindComponent<Button>(-1, "btnStage"), Is.Not.Null);
            }
            finally
            {
                stage.ClearDirtiness();
                StageUtility.GoToMainStage();
            }
            var loaded = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Assert.That(loaded.transform.GetChild(0).name, Is.EqualTo("Btn_First"));
            }
            finally { PrefabUtility.UnloadPrefabContents(loaded); }
        }

        [Test]
        public void PrefabInstanceGenerationLeavesOverridesDirtyUntilExplicitSave()
        {
            string prefabPath = _folder + "/Instance.prefab";
            PrefabUtility.SaveAsPrefabAsset(_tool.gameObject, prefabPath, out bool saved);
            Assert.That(saved, Is.True);
            EndAssetEditing();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, _scene);
            _tool = instance.GetComponent<Core.ComponentAutoBindTool>();
            instance.transform.GetChild(0).name = "Btn_Instance";
            PrefabUtility.RecordPrefabInstancePropertyModifications(instance.transform.GetChild(0).gameObject);
            byte[] originalPrefab = File.ReadAllBytes(prefabPath);
            BeginAssetEditing();
            AssertSuccess(Generate());
            Assert.That(File.ReadAllBytes(prefabPath), Is.EqualTo(originalPrefab));
            string scenePath = _scene.path;
            Assert.That(EditorSceneManager.SaveScene(_scene), Is.True);
            EditorSceneManager.CloseScene(_scene, true);
            _scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            var reloaded = _scene.GetRootGameObjects().Single(PrefabUtility.IsPartOfPrefabInstance);
            Assert.That(reloaded.GetComponent<Core.ComponentAutoBindTool>().GetBindComponent<Button>(-1, "btnInstance"), Is.Not.Null);
        }
    }
}
