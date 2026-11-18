using System;
using System.Collections.Generic;
using System.Linq;
using PsdUiWorkbench.Core;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.U2D.PSD;
using UnityEditor.U2D.Sprites;
using UnityEngine;
using UnityEngine.U2D;

namespace PsdUiWorkbench.Infrastructure
{
    // 包内序列化字段仅在此版本适配器读取，不传播到应用层。
    public sealed class UnityPsdDocumentSource : IPsdDocumentSource
    {
        private readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();
        public Sprite Resolve(string spriteId) => _sprites.TryGetValue(spriteId, out var sprite) ? sprite : null;
        public PsdLoadResult Load(string assetGuid)
        {
            _sprites.Clear();
            try
            {
                string path = AssetDatabase.GUIDToAssetPath(assetGuid);
                var importer = AssetImporter.GetAtPath(path) as PSDImporter;
                if (importer == null) return new PsdLoadResult(null, "请选择项目内 PSD/PSB，并使用官方 PSD Importer。");
                string version = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(PSDImporter).Assembly)?.version;
                if (version != "12.0.2") throw new InvalidOperationException("当前适配器仅验证 PSD Importer 12.0.2，检测到：" + version);
                if (importer.textureType != TextureImporterType.Sprite || importer.spriteImportMode != SpriteImportMode.Multiple || !importer.useMosaicMode)
                    return new PsdLoadResult(null, "原位预览需要 Sprite / Multiple / Individual Sprites (Mosaic)，请在官方面板设置并 Apply。");
                var assets = AssetDatabase.LoadAllAssetsAtPath(path);
                var typeAssets = assets.OfType<PsdLayerMetadataAsset>().ToArray();
                var types = typeAssets.Length > 1 ? new PsdMetadataResult(PsdMetadataStatus.Invalid, "存在重复的类型元数据子资源。") :
                    importer is WorkbenchPsdImporter && typeAssets.Length == 1 ? typeAssets[0].Read() :
                    new PsdMetadataResult(PsdMetadataStatus.Partial, importer is WorkbenchPsdImporter ? "缺少类型元数据，请重新导入。" :
                        "类型未知；可通过 Assets/PSD UI Workbench 的保留设置切换命令启用类型识别。请勿直接使用 Importer 下拉框切换已有资源。");
                var data = assets.FirstOrDefault(a => a != null && a.GetType().FullName == "UnityEditor.U2D.PSD.PSDImportData");
                if (data == null) throw new InvalidOperationException("缺少 PSDImportData；请重新导入资源。");
                using var dataObject = new SerializedObject(data);
                var size = Required(dataObject, "m_DocumentSize").vector2IntValue;
                using var serialized = new SerializedObject(importer);
                var layers = Required(serialized, "m_PsdLayers");
                var metadata = Required(serialized, "m_LayeredSpriteImportData");
                var positions = new Dictionary<string, PixelRect>();
                var provider = (ISpriteEditorDataProvider)importer;
                provider.InitSpriteEditorDataProvider();
                var spriteRects = provider.GetSpriteRects().ToDictionary(r => r.spriteID.ToString());
                for (int i = 0; i < metadata.arraySize; i++)
                {
                    var m = metadata.GetArrayElementAtIndex(i);
                    string id = Required(m, "m_SpriteID").stringValue;
                    var position = Required(m, "spritePosition").vector2Value;
                    if (!spriteRects.TryGetValue(id, out var rect)) throw new InvalidOperationException("Sprite 元数据映射不完整：" + id);
                    positions.Add(id, PixelRect.FromBottomLeft(position.x, position.y, rect.rect.width, rect.rect.height, size.y));
                }
                foreach (var sprite in assets.OfType<Sprite>())
                {
                    using var spriteObject = new SerializedObject(sprite);
                    _sprites.Add(Required(spriteObject, "m_SpriteID").stringValue, sprite);
                }
                var ids = new int[layers.arraySize];
                for (int i = 0; i < ids.Length; i++) ids[i] = Required(layers.GetArrayElementAtIndex(i), "m_LayerID").intValue;
                var nodes = new List<PsdNode>();
                for (int i = 0; i < ids.Length; i++)
                {
                    var layer = layers.GetArrayElementAtIndex(i);
                    int parent = Required(layer, "m_ParentIndex").intValue;
                    if (parent < -1 || parent >= ids.Length) throw new InvalidOperationException("父图层索引越界。");
                    string spriteId = Required(layer, "m_SpriteID").stringValue;
                    bool imported = Required(layer, "m_IsImported").boolValue;
                    positions.TryGetValue(spriteId, out var bounds);
                    if (imported && (!_sprites.ContainsKey(spriteId) || !bounds.IsValid))
                        throw new InvalidOperationException("已导入图层缺少 Sprite 或原始矩形：" + ids[i]);
                    nodes.Add(new PsdNode(ids[i], parent < 0 ? (int?)null : ids[parent], Required(layer, "m_Name").stringValue,
                        Required(layer, "m_IsGroup").boolValue, imported, Required(layer, "m_IsVisible").boolValue,
                        Required(layer, "m_Flatten").boolValue, imported ? spriteId : "", bounds, i,
                        types.Layers.TryGetValue(ids[i], out var kind) ? kind : PsdLayerKind.Unknown));
                }
                int unmapped = nodes.Count(n => !n.IsGroup && n.SourceKind == PsdLayerKind.Unknown);
                string diagnostic = types.Diagnostic;
                if (importer is WorkbenchPsdImporter && unmapped > 0) diagnostic += $" {unmapped} 个节点类型未知（ID 无法关联或元数据不可用）。";
                return new PsdLoadResult(new PsdDocumentSnapshot(assetGuid, size.x, size.y, nodes), "已应用的导入结果 · " + size.x + " × " + size.y + "　" + diagnostic);
            }
            catch (Exception exception)
            {
                _sprites.Clear();
                return new PsdLoadResult(null, "读取失败：" + exception.Message);
            }
        }
        private static SerializedProperty Required(SerializedObject value, string name)
            => value.FindProperty(name) ?? throw new InvalidOperationException("缺少适配字段：" + name);
        private static SerializedProperty Required(SerializedProperty value, string name)
            => value.FindPropertyRelative(name) ?? throw new InvalidOperationException("缺少适配字段：" + name);
    }
}
