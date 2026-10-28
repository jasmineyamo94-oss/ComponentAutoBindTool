using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ComponentAutoBindTool.Editor
{
    // 将可捕获的提交失败恢复到操作前；不承担编辑器进程崩溃后的恢复。
    internal sealed class AutoBindGenerationTransaction : IDisposable
    {
        private readonly Core.ComponentAutoBindTool m_Target;
        private readonly string m_Source;
        private readonly string m_ObjectSnapshot;
        private readonly Scene m_Scene;
        private readonly PrefabStage m_Stage;
        private readonly bool m_IsAsset;
        private readonly bool m_WasDirty;
        private readonly List<UnityEngine.Object> m_DirtyObjects = new List<UnityEngine.Object>();
        private readonly FileSnapshot m_OwnerFile;
        private readonly FileSnapshot m_CodeFile;
        private readonly GameObject m_InstanceRoot;
        private readonly PropertyModification[] m_Overrides;
        private readonly List<string> m_NewDirectories = new List<string>();
        private string m_TemporaryFile;
        private bool m_RefreshSuspended;

        internal AutoBindGenerationTransaction(Core.ComponentAutoBindTool target, string outputPath, string source)
        {
            // 完成之前重扫/手工编辑的 Undo 记录，避免保存后才被延迟标脏。
            Undo.FlushUndoRecordObjects();
            m_Target = target;
            m_Source = source;
            m_ObjectSnapshot = EditorJsonUtility.ToJson(target);
            m_Stage = PrefabStageUtility.GetPrefabStage(target.gameObject);
            m_IsAsset = EditorUtility.IsPersistent(target);
            m_Scene = target.gameObject.scene;
            string ownerPath = m_Stage != null ? m_Stage.assetPath :
                m_IsAsset ? AssetDatabase.GetAssetPath(target) : m_Scene.path;
            if (string.IsNullOrEmpty(ownerPath))
                throw new InvalidOperationException("请先保存当前场景，再生成绑定代码。");
            if (!File.Exists(ownerPath))
                throw new InvalidOperationException("所属场景或 Prefab 文件不存在：" + ownerPath);
            if (m_IsAsset)
            {
                foreach (Transform child in target.transform.root.GetComponentsInChildren<Transform>(true))
                {
                    if (EditorUtility.IsDirty(child.gameObject)) m_DirtyObjects.Add(child.gameObject);
                    foreach (Component component in child.GetComponents<Component>())
                        if (component != null && EditorUtility.IsDirty(component)) m_DirtyObjects.Add(component);
                }
            }
            m_WasDirty = m_IsAsset ? m_DirtyObjects.Count != 0 : m_Scene.isDirty;
            m_OwnerFile = new FileSnapshot(Path.GetFullPath(ownerPath));
            m_CodeFile = new FileSnapshot(outputPath);
            m_InstanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(target);
            if (m_InstanceRoot != null) m_Overrides = PrefabUtility.GetPropertyModifications(m_InstanceRoot);
        }

        internal void Commit(AutoBindBuildInput input, AutoBindPipelineReport report)
        {
            bool codeChanged = !m_CodeFile.Exists || File.ReadAllText(m_CodeFile.Path) != m_Source;
            // 在触碰对象前检查文件权限，并完成临时文件写入。
            m_OwnerFile.CheckWritable();
            if (codeChanged) m_CodeFile.CheckWritable();
            AssetDatabase.DisallowAutoRefresh();
            m_RefreshSuspended = true;
            bool objectTouched = false;
            try
            {
                if (codeChanged)
                {
                    EnsureOutputDirectory(System.IO.Path.GetDirectoryName(m_CodeFile.Path));
                    m_TemporaryFile = m_CodeFile.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(m_TemporaryFile, m_Source, new UTF8Encoding(false));
                }
                objectTouched = true;
                // 生成是对象与文件的持久化提交；仅撤销对象会使两份产物失步。
                report.BindingsUpdated = AutoBindSerializedBindingUtility.ApplyCandidate(input, false);
                if (report.BindingsUpdated && m_InstanceRoot != null)
                    PrefabUtility.RecordPrefabInstancePropertyModifications(m_Target);
                if (report.BindingsUpdated || m_WasDirty) SaveOwner();
                if (codeChanged)
                {
                    if (m_CodeFile.Exists) File.Replace(m_TemporaryFile, m_CodeFile.Path, null);
                    else File.Move(m_TemporaryFile, m_CodeFile.Path);
                    m_TemporaryFile = null;
                }
                if (codeChanged)
                    AssetDatabase.ImportAsset(AutoBindPathUtility.ToAssetRelativePath(m_CodeFile.Path));
                report.CodeFileUpdated = codeChanged;
            }
            catch (Exception failure)
            {
                var recoveryErrors = new List<string>();
                void Restore(Action action)
                {
                    try { action(); }
                    catch (Exception ex) { recoveryErrors.Add(ex.Message); }
                }
                if (objectTouched)
                {
                    Restore(() => EditorJsonUtility.FromJsonOverwrite(m_ObjectSnapshot, m_Target));
                    Restore(() =>
                    {
                        if (m_InstanceRoot != null) PrefabUtility.SetPropertyModifications(m_InstanceRoot, m_Overrides);
                        // 保存后失败可能清除了原来的脏标记；保留所有先前未保存的场景编辑。
                        if (!m_IsAsset && m_WasDirty) EditorSceneManager.MarkSceneDirty(m_Scene);
                        else if (m_IsAsset)
                        {
                            foreach (UnityEngine.Object dirty in m_DirtyObjects) EditorUtility.SetDirty(dirty);
                            if (!m_DirtyObjects.Contains(m_Target)) EditorUtility.ClearDirty(m_Target);
                        }
                    });
                }
                Restore(m_OwnerFile.Restore);
                Restore(m_CodeFile.Restore);
                throw new IOException(failure.Message + (recoveryErrors.Count == 0 ? "" :
                    "；恢复时发生异常：" + string.Join("；", recoveryErrors)), failure);
            }
        }

        private void SaveOwner()
        {
            if (m_Stage != null)
            {
                PrefabUtility.SaveAsPrefabAsset(m_Stage.prefabContentsRoot, m_Stage.assetPath, out bool saved);
                if (!saved) throw new IOException("Prefab 保存失败。");
                m_Stage.ClearDirtiness();
            }
            else if (m_IsAsset)
            {
                PrefabUtility.SavePrefabAsset(m_Target.transform.root.gameObject, out bool saved);
                if (!saved) throw new IOException("Prefab 保存失败。");
            }
            else if (!EditorSceneManager.SaveScene(m_Scene))
            {
                throw new IOException("场景保存失败。");
            }
        }

        private void EnsureOutputDirectory(string path)
        {
            if (Directory.Exists(path)) return;
            EnsureOutputDirectory(System.IO.Path.GetDirectoryName(path));
            Directory.CreateDirectory(path);
            m_NewDirectories.Add(path);
        }

        public void Dispose()
        {
            try
            {
                if (m_TemporaryFile != null && File.Exists(m_TemporaryFile)) File.Delete(m_TemporaryFile);
                for (int i = m_NewDirectories.Count - 1; i >= 0; i--)
                    if (Directory.Exists(m_NewDirectories[i]) && !Directory.EnumerateFileSystemEntries(m_NewDirectories[i]).Any())
                        Directory.Delete(m_NewDirectories[i]);
            }
            finally
            {
                if (m_RefreshSuspended) AssetDatabase.AllowAutoRefresh();
            }
        }

        private sealed class FileSnapshot
        {
            public string Path { get; }
            public bool Exists => m_Contents != null;
            private readonly byte[] m_Contents;
            private readonly DateTime m_WriteTime;
            public FileSnapshot(string path)
            {
                Path = path;
                if (File.Exists(path))
                {
                    m_Contents = File.ReadAllBytes(path);
                    m_WriteTime = File.GetLastWriteTimeUtc(path);
                }
            }
            public void CheckWritable()
            {
                if (Exists)
                {
                    using (new FileStream(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) { }
                }
            }
            public void Restore()
            {
                if (Exists)
                {
                    if (!File.Exists(Path) || !m_Contents.SequenceEqual(File.ReadAllBytes(Path)))
                        File.WriteAllBytes(Path, m_Contents);
                    File.SetLastWriteTimeUtc(Path, m_WriteTime);
                }
                else if (File.Exists(Path)) File.Delete(Path);
            }
        }
    }
}
