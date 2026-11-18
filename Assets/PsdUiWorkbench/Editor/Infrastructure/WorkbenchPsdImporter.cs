using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.U2D.PSD;
using UnityEngine;

namespace PsdUiWorkbench.Infrastructure
{
    [ScriptedImporter(5, new string[] { }, new[] { "psd", "psb" }, AllowCaching = true)]
    public sealed class WorkbenchPsdImporter : PSDImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            base.OnImportAsset(ctx);
            var objects = new List<Object>();
            ctx.GetObjects(objects);
            if (ctx.mainObject == null || !objects.OfType<Texture2D>().Any()) return;
            PsdMetadataResult result;
            string version = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(PSDImporter).Assembly)?.version;
            if (version != "12.0.2") result = new PsdMetadataResult(PsdMetadataStatus.Unsupported, "类型扩展仅验证官方 PSD Importer 12.0.2，当前：" + version);
            else
            {
                try
                {
                    using var stream = File.OpenRead(ctx.assetPath);
                    result = new PsdLayerMetadataReader().Read(stream);
                }
                catch (IOException e) { result = new PsdMetadataResult(PsdMetadataStatus.Invalid, "读取源文件失败：" + e.Message); }
            }
            var metadata = ScriptableObject.CreateInstance<PsdLayerMetadataAsset>();
            metadata.name = "Workbench Layer Types";
            metadata.hideFlags = HideFlags.HideInHierarchy;
            metadata.Initialize(result);
            ctx.AddObjectToAsset("workbench.layerTypes", metadata);
            if (!string.IsNullOrEmpty(result.Diagnostic)) ctx.LogImportWarning(result.Diagnostic);
            PsdTextMetadataResult texts;
            if (version != "12.0.2") texts = new PsdTextMetadataResult(PsdMetadataStatus.Unsupported, "文本缓存仅验证 PSD Importer 12.0.2。");
            else
            {
                try
                {
                    using var stream = File.OpenRead(ctx.assetPath);
                    texts = new PsdTextMetadataReader().Read(stream);
                }
                catch (IOException e) { texts = new PsdTextMetadataResult(PsdMetadataStatus.Invalid, "读取文本失败：" + e.Message); }
            }
            var textMetadata = ScriptableObject.CreateInstance<PsdTextMetadataAsset>();
            textMetadata.name = "Workbench Text";
            textMetadata.hideFlags = HideFlags.HideInHierarchy;
            textMetadata.Initialize(texts);
            ctx.AddObjectToAsset("workbench.text", textMetadata);
            if (!string.IsNullOrEmpty(texts.Diagnostic)) ctx.LogImportWarning(texts.Diagnostic);
        }
    }
}
