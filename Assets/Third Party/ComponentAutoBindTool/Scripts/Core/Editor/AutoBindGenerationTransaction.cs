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
        private readonly Core.ComponentAutoBindTool _target;
        private readonly string _source;
        private readonly string _objectSnapshot;
        private readonly Scene _scene;
        private readonly bool _wasSceneDirty;
        private readonly bool _wasTargetDirty;
        private readonly FileSnapshot _codeFile;
        private readonly GameObject _instanceRoot;
        private readonly PropertyModification[] _overrides;
        private readonly List<string> _newDirectories = new List<string>();
        private string _temporaryFile;
        private bool _refreshSuspended;

        internal AutoBindGenerationTransaction(Core.ComponentAutoBindTool target, string outputPath, string source)
        {
            // 完成之前的 Undo 记录，隔离本次生成事务。
            Undo.FlushUndoRecordObjects();
            _target = target;
            _source = source;
            _objectSnapshot = EditorJsonUtility.ToJson(target);
            _scene = target.gameObject.scene;
            _wasSceneDirty = _scene.IsValid() && _scene.isDirty;
            _wasTargetDirty = EditorUtility.IsDirty(target);
            _codeFile = new FileSnapshot(outputPath);
            _instanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(target);
            if (_instanceRoot != null) _overrides = PrefabUtility.GetPropertyModifications(_instanceRoot);
        }

        internal void Commit(AutoBindBuildInput input, AutoBindPipelineReport report)
        {
            bool codeChanged = !_codeFile.Exists || File.ReadAllText(_codeFile.Path) != _source;
            // 在触碰对象前检查文件权限，并完成临时文件写入。
            if (codeChanged) _codeFile.CheckWritable();
            AssetDatabase.DisallowAutoRefresh();
            _refreshSuspended = true;
            bool objectTouched = false;
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("生成 AutoBind 代码");
            try
            {
                if (codeChanged)
                {
                    EnsureOutputDirectory(System.IO.Path.GetDirectoryName(_codeFile.Path));
                    _temporaryFile = _codeFile.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(_temporaryFile, _source, new UTF8Encoding(false));
                }
                objectTouched = true;
                // 引用留在 Unity 内存序列化状态，场景与 Prefab 由用户自行保存。
                report.BindingsUpdated = AutoBindSerializedBindingUtility.ApplyCandidate(input, true);
                if (report.BindingsUpdated && _instanceRoot != null)
                    PrefabUtility.RecordPrefabInstancePropertyModifications(_target);
                if (report.BindingsUpdated)
                {
                    EditorUtility.SetDirty(_target);
                    if (!EditorUtility.IsPersistent(_target) && _scene.IsValid())
                        EditorSceneManager.MarkSceneDirty(_scene);
                }
                if (codeChanged)
                {
                    if (_codeFile.Exists) File.Replace(_temporaryFile, _codeFile.Path, null);
                    else File.Move(_temporaryFile, _codeFile.Path);
                    _temporaryFile = null;
                }
                if (codeChanged)
                    AssetDatabase.ImportAsset(AutoBindPathUtility.ToAssetRelativePath(_codeFile.Path));
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
                    Restore(() => EditorJsonUtility.FromJsonOverwrite(_objectSnapshot, _target));
                    Restore(() =>
                    {
                        if (_instanceRoot != null) PrefabUtility.SetPropertyModifications(_instanceRoot, _overrides);
                        if (!_wasTargetDirty) EditorUtility.ClearDirty(_target);
                        else EditorUtility.SetDirty(_target);
                        if (_scene.IsValid() && !EditorUtility.IsPersistent(_target))
                        {
                            if (_wasSceneDirty) EditorSceneManager.MarkSceneDirty(_scene);
                            // Unity 未公开场景清脏 API；失败后保留保守的未保存提示。
                        }
                    });
                }
                Restore(_codeFile.Restore);
                throw new IOException(failure.Message + (recoveryErrors.Count == 0 ? "" :
                    "；恢复时发生异常：" + string.Join("；", recoveryErrors)), failure);
            }
        }

        private void EnsureOutputDirectory(string path)
        {
            if (Directory.Exists(path)) return;
            EnsureOutputDirectory(System.IO.Path.GetDirectoryName(path));
            Directory.CreateDirectory(path);
            _newDirectories.Add(path);
        }

        public void Dispose()
        {
            try
            {
                if (_temporaryFile != null && File.Exists(_temporaryFile)) File.Delete(_temporaryFile);
                for (int i = _newDirectories.Count - 1; i >= 0; i--)
                    if (Directory.Exists(_newDirectories[i]) && !Directory.EnumerateFileSystemEntries(_newDirectories[i]).Any())
                        Directory.Delete(_newDirectories[i]);
            }
            finally
            {
                if (_refreshSuspended) AssetDatabase.AllowAutoRefresh();
            }
        }

        private sealed class FileSnapshot
        {
            public string Path { get; }
            public bool Exists => _contents != null;
            private readonly byte[] _contents;
            private readonly DateTime _writeTime;
            public FileSnapshot(string path)
            {
                Path = path;
                if (File.Exists(path))
                {
                    _contents = File.ReadAllBytes(path);
                    _writeTime = File.GetLastWriteTimeUtc(path);
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
                    if (!File.Exists(Path) || !_contents.SequenceEqual(File.ReadAllBytes(Path)))
                        File.WriteAllBytes(Path, _contents);
                    File.SetLastWriteTimeUtc(Path, _writeTime);
                }
                else if (File.Exists(Path)) File.Delete(Path);
            }
        }
    }
}
