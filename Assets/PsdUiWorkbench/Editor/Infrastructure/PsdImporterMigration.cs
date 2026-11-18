using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.U2D.PSD;
using UnityEngine;

namespace PsdUiWorkbench.Infrastructure
{
    public static class PsdImporterMigration
    {
        private static readonly FieldInfo[] _fields = typeof(PSDImporter).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(f => f.IsDefined(typeof(SerializeField), false) && f.Name != "m_ImportData").ToArray();
        [Serializable] private sealed class Box<T> { public T Value; }
        public static string SettingsSignature(PSDImporter importer)
        {
            return string.Join("\n", _fields.Select(f =>
            {
                object box = Activator.CreateInstance(typeof(Box<>).MakeGenericType(f.FieldType));
                box.GetType().GetField("Value").SetValue(box, f.GetValue(importer));
                return f.Name + ":" + JsonUtility.ToJson(box);
            }));
        }
        private static string SpriteSignature(string path) => string.Join("\n", AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().Select(s =>
        {
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(s, out string guid, out long local);
            using var so = new SerializedObject(s);
            return so.FindProperty("m_SpriteID").stringValue + ":" + local;
        }).OrderBy(s => s));
        public static void Switch(string path, bool workbench)
        {
            if (UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(PSDImporter).Assembly)?.version != "12.0.2")
                throw new InvalidOperationException("保留设置切换仅支持 PSD Importer 12.0.2。");
            var original = AssetImporter.GetAtPath(path) as PSDImporter;
            if (original == null || (original.GetType() != typeof(PSDImporter) && original.GetType() != typeof(WorkbenchPsdImporter)))
                throw new InvalidOperationException("请选择官方或 Workbench PSDImporter 资源。");
            if (Resources.FindObjectsOfTypeAll<PSDImporterEditor>().Any(e => e.target == original && e.HasModified()))
                throw new InvalidOperationException("请先在官方 Inspector 中 Apply 或 Revert 未应用的设置，再切换导入器。");
            bool wasWorkbench = original is WorkbenchPsdImporter;
            if (wasWorkbench == workbench) return;
            var values = _fields.ToDictionary(f => f, f => f.GetValue(original));
            string userData = original.userData, bundle = original.assetBundleName, variant = original.assetBundleVariant;
            var remaps = original.GetExternalObjectMap();
            string sprites = SpriteSignature(path), settings = SettingsSignature(original);
            var mainType = AssetDatabase.LoadMainAssetAtPath(path)?.GetType();
            void Restore(bool target)
            {
                if (target) AssetDatabase.SetImporterOverride<WorkbenchPsdImporter>(path);
                else AssetDatabase.SetImporterOverride<PSDImporter>(path);
                var importer = (PSDImporter)AssetImporter.GetAtPath(path);
                foreach (var value in values) value.Key.SetValue(importer, value.Value);
                importer.userData = userData;
                importer.SetAssetBundleNameAndVariant(bundle, variant);
                foreach (var remap in remaps) importer.AddRemap(remap.Key, remap.Value);
                EditorUtility.SetDirty(importer);
                importer.SaveAndReimport();
            }
            try
            {
                Restore(workbench);
                if (sprites != SpriteSignature(path) || settings != SettingsSignature((PSDImporter)AssetImporter.GetAtPath(path)) ||
                    mainType != AssetDatabase.LoadMainAssetAtPath(path)?.GetType())
                    throw new InvalidOperationException("切换后设置、Sprite 标识或主资源不一致。");
            }
            catch (Exception failure)
            {
                try { Restore(wasWorkbench); }
                catch (Exception rollback) { throw new AggregateException("切换及回退失败，请保留资源并检查诊断。", failure, rollback); }
                throw new InvalidOperationException("切换失败，已尝试恢复原导入器及设置。", failure);
            }
        }
        [MenuItem("Assets/PSD UI Workbench/保留设置切换到 Workbench Importer")]
        private static void ToWorkbench() => Switch(AssetDatabase.GetAssetPath(Selection.activeObject), true);
        [MenuItem("Assets/PSD UI Workbench/保留设置切换到官方 PSDImporter")]
        private static void ToOfficial() => Switch(AssetDatabase.GetAssetPath(Selection.activeObject), false);
        [MenuItem("Assets/PSD UI Workbench/保留设置切换到 Workbench Importer", true)]
        private static bool CanToWorkbench() => AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(Selection.activeObject))?.GetType() == typeof(PSDImporter);
        [MenuItem("Assets/PSD UI Workbench/保留设置切换到官方 PSDImporter", true)]
        private static bool CanToOfficial() => AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(Selection.activeObject)) is WorkbenchPsdImporter;
    }
}
