using System;
using System.Collections.Generic;
using System.Linq;
using PsdUiWorkbench.Infrastructure;
using TMPro;
using UnityEngine;
using UnityEditor;
using System.IO;

namespace PsdUiWorkbench.Tests
{
    // 仅验证静态邻字数据，不进入正式导出；运行时改字需要重新生成布局数据。
    public static class PsdGlyphUnionPrototype
    {
        public const string ShaderName = "TextMeshPro/Mobile/Distance Field - PSD Glyph Union Prototype";
        public static Texture2D BuildLayout(TextMeshProUGUI text, out int count)
        {
            var characters = text.textInfo.characterInfo.Take(text.textInfo.characterCount).Where(c => c.isVisible).ToArray();
            count = characters.Length;
            if (count == 0 || count > 32) throw new InvalidOperationException("邻字原型仅支持 1～32 个可见字形。");
            var rectangles = new List<Rect>(); var colors = new Color[64];
            for (int i = 0; i < count; i++)
            {
                var c = characters[i]; var mesh = text.textInfo.meshInfo[c.materialReferenceIndex];
                int k = c.vertexIndex; var bl = mesh.vertices[k]; var tr = mesh.vertices[k + 2];
                if (c.materialReferenceIndex != 0 || tr.x <= bl.x || tr.y <= bl.y ||
                    Mathf.Abs(mesh.vertices[k + 1].x - bl.x) > 0.0001f || Mathf.Abs(mesh.vertices[k + 3].y - bl.y) > 0.0001f)
                    throw new InvalidOperationException("邻字原型仅支持单材质、未斜切的字形。");
                var uv0 = mesh.uvs0[k]; var uv1 = mesh.uvs0[k + 2];
                // Canvas 会批处理变换顶点位置，使用 TMP 自带的段落 UV，避免依赖对象／世界坐标。
                var at0 = mesh.uvs2[k]; var at1 = mesh.uvs2[k + 2];
                colors[i] = new Color(at0.x, at0.y, at1.x, at1.y);
                colors[32 + i] = new Color(uv0.x, uv0.y, uv1.x, uv1.y);
                rectangles.Add(Rect.MinMaxRect(bl.x, bl.y, tr.x, tr.y));
            }
            // 只采样相邻字形；禁止非邻接四边形相交，不能将复杂排版静默套用。
            for (int i = 0; i < count; i++)
            for (int j = i + 2; j < count; j++)
                if (rectangles[i].Overlaps(rectangles[j])) throw new InvalidOperationException("存在非邻接字形网格交叠，超出原型范围。");
            var texture = new Texture2D(32, 2, TextureFormat.RGBAFloat, false, true)
            { name = "Static Glyph Layout", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels(colors); texture.Apply(false, false); return texture;
        }

        public static string ExportStatic(string assetPath = "Assets/PsdUiWorkbench/Tests/Editor/ArchivedGlyphUnion/Assets/单Pass邻字合成静态原型.prefab", string evidenceName = "union-static")
        {
            const string source = "Assets/PsdUiWorkbench/Exports/战斗-主菜单界面_单Pass字形校验.prefab";
            if (!assetPath.StartsWith("Assets/PsdUiWorkbench/", StringComparison.Ordinal) || assetPath.Contains("..") || assetPath.Contains('\\') || !assetPath.EndsWith(".prefab", StringComparison.Ordinal))
                throw new ArgumentException("原型资产必须保存在工作台目录内。");
            if (evidenceName.IndexOfAny(new[] {'/', '\\'}) >= 0 || evidenceName.Contains("..")) throw new ArgumentException("原型截图名称无效。");
            string output = AssetDatabase.GenerateUniqueAssetPath(assetPath);
            string folder = Path.ChangeExtension(output, null) + " Materials";
            if (AssetDatabase.IsValidFolder(folder)) throw new IOException("静态原型材质目录已存在。");
            if (!AssetDatabase.CopyAsset(source, output)) throw new IOException("静态原型复制失败。");
            bool createdFolder = false, complete = false; GameObject contents = null;
            var replacements = new Dictionary<string, Material>();
            var transient = new List<UnityEngine.Object>();
            try
            {
                createdFolder = !string.IsNullOrEmpty(AssetDatabase.CreateFolder(Path.GetDirectoryName(folder).Replace('\\', '/'), Path.GetFileName(folder)));
                if (!createdFolder) throw new IOException("静态原型材质目录创建失败。");
                PsdTmpCandidateExport.Capture(output, PsdStrokeVisualDiagnosis.Folder + evidenceName + ".png", root =>
                {
                    foreach (var text in root.GetComponentsInChildren<TextMeshProUGUI>(true))
                    {
                        if (text.name == "Placeholder") continue;
                        var material = new Material(text.fontSharedMaterial); transient.Add(material);
                        material.shader = Shader.Find(ShaderName);
                        text.fontSharedMaterial = material;
                        text.horizontalMapping = TextureMappingOptions.Paragraph; text.verticalMapping = TextureMappingOptions.Paragraph;
                        text.UpdateMeshPadding(); text.ForceMeshUpdate(true, true);
                        var data = BuildLayout(text, out int count); transient.Add(data);
                        material.SetTexture("_PsdGlyphData", data); material.SetFloat("_PsdGlyphCount", count);
                        material.SetFloat("_PsdUnionDebug", 0);
                        material.name = "Static Union " + (replacements.Count + 1);
                        AssetDatabase.CreateAsset(material, folder + "/" + material.name + ".mat");
                        AssetDatabase.AddObjectToAsset(data, material);
                        // 创建材质资产时未注册的临时纹理引用会被清除，登记子资源后重新绑定。
                        material.SetTexture("_PsdGlyphData", data);
                        EditorUtility.SetDirty(data);
                        EditorUtility.SetDirty(material); AssetDatabase.SaveAssetIfDirty(material);
                        var indices = new Stack<int>(); var at = text.transform;
                        while (at != root.transform) { indices.Push(at.GetSiblingIndex()); at = at.parent; }
                        replacements.Add(string.Join("/", indices), material);
                        text.SetMaterialDirty(); text.ForceMeshUpdate(true, true);
                    }
                });
                contents = PrefabUtility.LoadPrefabContents(output); contents.SetActive(false);
                var rootRect = contents.GetComponent<RectTransform>(); rootRect.localScale = Vector3.one;
                rootRect.sizeDelta = contents.GetComponent<UnityEngine.UI.CanvasScaler>().referenceResolution;
                foreach (var pair in replacements)
                {
                    Transform at = contents.transform;
                    foreach (string index in pair.Key.Split('/')) at = at.GetChild(int.Parse(index));
                    var text = at.GetComponent<TextMeshProUGUI>();
                    text.fontSharedMaterial = pair.Value;
                    text.horizontalMapping = TextureMappingOptions.Paragraph; text.verticalMapping = TextureMappingOptions.Paragraph;
                }
                var prefab = PrefabUtility.SaveAsPrefabAsset(contents, output, out bool saved);
                if (!saved || prefab == null) throw new IOException("静态原型保存失败。");
                prefab.SetActive(true); PrefabUtility.SavePrefabAsset(prefab, out saved);
                if (!saved) throw new IOException("静态原型激活保存失败。");
                complete = true;
                return output;
            }
            finally
            {
                if (contents != null) PrefabUtility.UnloadPrefabContents(contents);
                if (!complete) { AssetDatabase.DeleteAsset(output); if (createdFolder) AssetDatabase.DeleteAsset(folder); }
                foreach (var obj in transient) if (obj != null && !EditorUtility.IsPersistent(obj)) UnityEngine.Object.DestroyImmediate(obj);
            }
        }

        public static string Capture(float stroke, string name, bool debug = false, bool enabledUnion = true)
        {
            var materials = new List<Material>(); var textures = new List<Texture2D>();
            try
            {
                return PsdTmpCandidateExport.Capture("Assets/PsdUiWorkbench/Exports/战斗-主菜单界面_单Pass字形校验.prefab",
                    PsdStrokeVisualDiagnosis.Folder + name + ".png", root =>
                    {
                        foreach (var go in root.scene.GetRootGameObjects()) if (go.TryGetComponent<Camera>(out var camera)) camera.backgroundColor = Color.black;
                        foreach (var image in root.GetComponentsInChildren<UnityEngine.UI.Image>(true)) image.enabled = false;
                        foreach (var text in root.GetComponentsInChildren<TextMeshProUGUI>(true))
                        {
                            if (text.name == "Placeholder") continue;
                            text.ForceMeshUpdate(true, true);
                            var material = new Material(text.fontSharedMaterial); materials.Add(material);
                            float scale = text.textInfo.characterInfo.First(c => c.isVisible).scale;
                            PsdTmpOuterStroke.Configure(material, stroke, scale, PsdTmpOuterStroke.DefaultBoldWeight);
                            material.SetColor("_FaceColor", Color.red); material.SetColor("_OutlineColor", Color.green);
                            if (enabledUnion) material.shader = Shader.Find(ShaderName);
                            text.fontSharedMaterial = material;
                            text.horizontalMapping = TextureMappingOptions.Paragraph; text.verticalMapping = TextureMappingOptions.Paragraph;
                            text.UpdateMeshPadding(); text.ForceMeshUpdate(true, true);
                            if (enabledUnion)
                            {
                                var data = BuildLayout(text, out int count); textures.Add(data);
                                material.SetTexture("_PsdGlyphData", data); material.SetFloat("_PsdGlyphCount", count);
                                material.SetFloat("_PsdUnionDebug", debug ? 1 : 0);
                            }
                            text.SetMaterialDirty(); text.ForceMeshUpdate(true, true);
                        }
                    });
            }
            finally
            {
                foreach (var material in materials) UnityEngine.Object.DestroyImmediate(material);
                foreach (var texture in textures) UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
