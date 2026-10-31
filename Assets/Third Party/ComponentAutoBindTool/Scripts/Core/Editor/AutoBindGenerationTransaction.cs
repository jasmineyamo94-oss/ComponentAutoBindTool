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
        private readonly bool m_WasSceneDirty;
        private readonly bool m_WasTargetDirty;
        private readonly FileSnapshot m_CodeFile;
        private readonly GameObject m_InstanceRoot;
        private readonly PropertyModification[] m_Overrides;
        private readonly List<string> m_NewDirectories = new List<string>();
        private string m_TemporaryFile;
        private bool m_RefreshSuspended;

        internal AutoBindGenerationTransaction(Core.ComponentAutoBindTool target, string outputPath, string source)
        {
            // 完成之前的 Undo 记录，隔离本次生成事务。
            Undo.FlushUndoRecordObjects();
            m_Target = target;
            m_Source = source;
            m_ObjectSnapshot = EditorJsonUtility.ToJson(target);
            m_Scene = target.gameObject.scene;
            m_WasSceneDirty = m_Scene.IsValid() && m_Scene.isDirty;
            m_WasTargetDirty = EditorUtility.IsDirty(target);
            m_CodeFile = new FileSnapshot(outputPath);
            m_InstanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(target);
            if (m_InstanceRoot != null) m_Overrides = PrefabUtility.GetPropertyModifications(m_InstanceRoot);
        }

        internal void Commit(AutoBindBuildInput input, AutoBindPipelineReport report)
        {
            bool codeChanged = !m_CodeFile.Exists || File.ReadAllText(m_CodeFile.Path) != m_Source;
            // 在触碰对象前检查文件权限，并完成临时文件写入。
            if (codeChanged) m_CodeFile.CheckWritable();
            AssetDatabase.DisallowAutoRefresh();
            m_RefreshSuspended = true;
            bool objectTouched = false;
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("生成 AutoBind 代码");
            try
            {
                if (codeChanged)
                {
                    EnsureOutputDirectory(System.IO.Path.GetDirectoryName(m_CodeFile.Path));
                    m_TemporaryFile = m_CodeFile.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(m_TemporaryFile, m_Source, new UTF8Encoding(false));
                }
                objectTouched = true;
                // 引用留在 Unity 内存序列化状态，场景与 Prefab 由用户自行保存。
                report.BindingsUpdated = AutoBindSerializedBindingUtility.ApplyCandidate(input, true);
                if (report.BindingsUpdated && m_InstanceRoot != null)
                    PrefabUtility.RecordPrefabInstancePropertyModifications(m_Target);
                if (report.BindingsUpdated)
                {
                    EditorUtility.SetDirty(m_Target);
                    if (!EditorUtility.IsPersistent(m_Target) && m_Scene.IsValid())
                        EditorSceneManager.MarkSceneDirty(m_Scene);
                }
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
                    Restore(() => Undo.RevertAllDownToGroup(undoGroup));
                    Restore(() => EditorJsonUtility.FromJsonOverwrite(m_ObjectSnapshot, m_Target));
                    Restore(() =>
                    {
                        if (m_InstanceRoot != null) PrefabUtility.SetPropertyModifications(m_InstanceRoot, m_Overrides);
                        if (!m_WasTargetDirty) EditorUtility.ClearDirty(m_Target);
                        else EditorUtility.SetDirty(m_Target);
                        if (m_Scene.IsValid() && !EditorUtility.IsPersistent(m_Target))
                        {
                            if (m_WasSceneDirty) EditorSceneManager.MarkSceneDirty(m_Scene);
                            // Unity 未公开场景清脏 API；失败后保留保守的未保存提示。
                        }
                    });
                }
                Restore(m_CodeFile.Restore);
                throw new IOException(failure.Message + (recoveryErrors.Count == 0 ? "" :
                    "；恢复时发生异常：" + string.Join("；", recoveryErrors)), failure);
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
