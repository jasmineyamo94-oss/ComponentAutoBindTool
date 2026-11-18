using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PsdUiWorkbench.Core;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace PsdUiWorkbench.Infrastructure
{
    public sealed class PsdTextPrefabExportResult
    {
        public string AssetPath { get; internal set; }
        public int TextLayers { get; internal set; }
        public int TextComponents { get; internal set; }
        public int PlaceholderRuns { get; internal set; }
        public int ImageCount { get; internal set; }
        public int FontSubstitutedLayers { get; internal set; }
        public int SkippedTextLayers { get; internal set; }
        public int ExcludedNodeCount { get; internal set; }
        public string Diagnostic { get; internal set; }
    }

    // 正式入口只消费已导入的文本缓存；字体资产在确认导出路径后才解析和生成。
    public static class PsdTextPrefabExport
    {
        public static PsdTextPrefabExportResult Export(string assetGuid, PsdVisibilityState visibility, string assetPath,
            PsdTmpRenderingMode renderingMode = PsdTmpRenderingMode.OuterOutline, PsdExportState exportState = null,
            PsdNodeNameState names = null)
        {
            if (!Enum.IsDefined(typeof(PsdTmpRenderingMode), renderingMode)) throw new ArgumentOutOfRangeException(nameof(renderingMode));
            var source = new UnityPsdDocumentSource(); var loaded = source.Load(assetGuid);
            if (loaded.Document == null) throw new InvalidOperationException(loaded.Diagnostic);
            var plan = PsdPrefabPlanner.Build(loaded.Document, visibility, exportState, names);
            string sourcePath = AssetDatabase.GUIDToAssetPath(assetGuid);
            var metadata = AssetDatabase.LoadAllAssetsAtPath(sourcePath).OfType<PsdTextMetadataAsset>().SingleOrDefault();
            var cache = metadata != null ? metadata.Read() : new PsdTextMetadataResult(PsdMetadataStatus.Unsupported,
                "未找到已应用的文本缓存，请切换到 Workbench Importer 并重新导入 PSD 后再导出文本。");
            if (cache.Status != PsdMetadataStatus.Success && cache.Status != PsdMetadataStatus.Partial)
                throw new InvalidOperationException(cache.Diagnostic);
            var ids = new HashSet<int>(plan.Nodes.Where(n => n.IsImage).Select(n => n.Id));
            var texts = cache.Sources.Where(p => ids.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            var rejected = cache.RejectedLayers.Where(p => ids.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            // 在字体生成前校验路径；不覆盖现有 Prefab 或旁边的手工资源目录。
            assetPath = assetPath?.Replace('\\', '/');
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                !assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || assetPath.Split('/').Any(p => p == ".." || p == "."))
                throw new ArgumentException("请选择 Assets 内的 .prefab 路径。");
            string parent = Path.GetDirectoryName(assetPath).Replace('\\', '/'); EnsureFolder(parent);
            string output = AssetDatabase.GenerateUniqueAssetPath(assetPath);
            string fontFolder = AssetDatabase.GenerateUniqueAssetPath(Path.ChangeExtension(output, null) + " Fonts");
            var fonts = new Dictionary<string, TMP_FontAsset>(); bool folderCreated = false, complete = false;
            try
            {
                var wanted = new HashSet<string>(texts.Values.SelectMany(t => t.Spans.Select(s => s.Style.FontKey)));
                var matches = FindFonts(wanted);
                var available = new HashSet<string>(matches.Keys);
                var normalized = new Dictionary<int, PsdTextLayoutPlan>();
                foreach (var node in plan.Nodes.Where(n => texts.ContainsKey(n.Id)))
                {
                    if (!PsdTextLayoutPlanner.TryBuild(texts[node.Id], node.Bounds, available, out var textPlan, out string reason))
                    {
                        rejected[node.Id] = reason;
                        continue;
                    }
                    var run = textPlan.Runs[0];
                    if (!HasCharacters(matches[run.Style.FontKey], run.Text))
                    {
                        rejected[node.Id] = "选定源字体无法覆盖完整文本：" + run.Style.FontKey;
                        continue;
                    }
                    normalized.Add(node.Id, textPlan);
                }
                foreach (var group in normalized.Values.GroupBy(p => p.Runs[0].Style.FontKey).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    if (!folderCreated)
                    {
                        if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(fontFolder)))) throw new IOException("无法创建导出字库目录。");
                        folderCreated = true;
                    }
                    string required = string.Concat(group.Select(p => p.Runs[0].Text));
                    string fontPath = AssetDatabase.GenerateUniqueAssetPath(fontFolder + "/Font " + (fonts.Count + 1) + ".asset");
                    fonts.Add(group.Key, CreateFont(matches[group.Key], required, fontPath,
                        PsdTmpGlyphLocalBold.IsEnabled(renderingMode, group.Key), renderingMode == PsdTmpRenderingMode.OuterOutline));
                }
                var result = PsdTmpPrefabExporter.Export(plan, texts, fonts, null, output, source.Resolve, renderingMode, rejected);
                complete = true;
                return new PsdTextPrefabExportResult
                {
                    AssetPath = result.AssetPath, TextLayers = result.TextLayers, TextComponents = result.TextComponents,
                    PlaceholderRuns = 0, ImageCount = result.ImageCount,
                    FontSubstitutedLayers = result.FontSubstitutedLayers, SkippedTextLayers = result.SkippedTextLayers.Count,
                    ExcludedNodeCount = plan.ExcludedNodeCount,
                    Diagnostic = Join(renderingMode == PsdTmpRenderingMode.OuterOutline
                            ? "使用 Outer Outline Shader；每层一个 TMP，填充与外描边独立。" : "",
                        string.Join("\n", normalized.Values.Where(p => p.FontSubstituted)
                            .Select(p => $"字体替代（ID {p.LayerId}）：整层使用 {p.Runs[0].Style.FontKey}。")),
                        string.Join("\n", result.SkippedTextLayers.OrderBy(p => p.Key)
                            .Select(p => $"跳过文本层 {p.Key}：{p.Value}")),
                        plan.ExcludedNodeCount == 0 ? "" : $"按用户设置排除 {plan.ExcludedNodeCount} 个 PSD 节点。",
                        plan.Skipped.Count == plan.ExcludedNodeCount ? "" : $"另有 {plan.Skipped.Count - plan.ExcludedNodeCount} 个节点无独立导出内容。")
                };
            }
            finally { if (!complete && folderCreated) AssetDatabase.DeleteAsset(fontFolder); }
        }

        private static string Join(params string[] messages) => string.Join("\n", messages.Where(m => !string.IsNullOrEmpty(m)));
        private static bool HasCharacters(Font font, string text)
        {
            if (FontEngine.LoadFontFace(font, 96) != FontEngineError.Success) return false;
            for (int i = 0; i < text.Length; i++)
            {
                int codepoint = char.ConvertToUtf32(text, i);
                if (!FontEngine.TryGetGlyphIndex((uint)codepoint, out uint glyphIndex) || glyphIndex == 0) return false;
                if (char.IsHighSurrogate(text[i])) i++;
            }
            return true;
        }
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent)) throw new IOException("无法创建 Assets 目录。");
            EnsureFolder(parent);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(path)))) throw new IOException("导出目录创建失败。");
        }
        private static Dictionary<string, Font> FindFonts(HashSet<string> wanted)
        {
            var matches = new Dictionary<string, List<Font>>();
            foreach (string guid in AssetDatabase.FindAssets("t:Font", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string name in ReadPostScriptNames(path).Where(wanted.Contains))
                {
                    if (!matches.TryGetValue(name, out var fonts)) matches.Add(name, fonts = new List<Font>());
                    var font = AssetDatabase.LoadAssetAtPath<Font>(path);
                    if (font != null && !fonts.Contains(font)) fonts.Add(font);
                }
            }
            // 同名多个源字体也不猜测，以缺失字体处理，避免项目搜索顺序改变结果。
            return matches.Where(p => p.Value.Count == 1).ToDictionary(p => p.Key, p => p.Value[0]);
        }
        private static IEnumerable<string> ReadPostScriptNames(string path)
        {
            var result = new HashSet<string>();
            try
            {
                using var stream = File.OpenRead(path);
                uint Number(int count)
                {
                    uint value = 0;
                    for (int i = 0; i < count; i++) { int b = stream.ReadByte(); if (b < 0) throw new EndOfStreamException(); value = (value << 8) | (byte)b; }
                    return value;
                }
                uint magic = Number(4);
                if (magic != 0x00010000 && magic != 0x4F54544F) return result;
                int count = (int)Number(2); Number(2); Number(2); Number(2);
                long offset = 0, length = 0;
                for (int i = 0; i < count; i++)
                {
                    uint tag = Number(4); Number(4); uint start = Number(4), size = Number(4);
                    if (tag == 0x6E616D65) { offset = start; length = size; }
                }
                if (offset == 0 || length < 6 || offset + length > stream.Length) return result;
                stream.Position = offset; uint format = Number(2); int records = (int)Number(2); long storage = Number(2);
                if (format > 1 || storage > length || 6L + records * 12L > length) return result;
                var entries = new List<Tuple<uint, uint, uint>>();
                for (int i = 0; i < records; i++)
                {
                    uint platform = Number(2); Number(2); Number(2); uint id = Number(2), size = Number(2), start = Number(2);
                    if (id == 6 && (platform == 0 || platform == 3 || platform == 1) && size <= 1024 && storage + start + size <= length)
                        entries.Add(Tuple.Create(platform, size, start));
                }
                foreach (var entry in entries)
                {
                    stream.Position = offset + storage + entry.Item3; var bytes = new byte[entry.Item2];
                    for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)Number(1);
                    string name = entry.Item1 == 1 ? Encoding.ASCII.GetString(bytes) : new UnicodeEncoding(true, false, true).GetString(bytes);
                    if (!string.IsNullOrEmpty(name)) result.Add(name.TrimEnd('\0'));
                }
            }
            catch (Exception e) when (e is IOException || e is ArgumentException || e is OverflowException) { result.Clear(); }
            return result;
        }
        private static TMP_FontAsset CreateFont(Font source, string required, string path, bool glyphLocalBold, bool independentOutline)
        {
            string ascii = new string(Enumerable.Range(32, 95).Where(c => source.HasCharacter((char)c)).Select(c => (char)c).ToArray());
            var font = TMP_FontAsset.CreateFontAsset(source, 96, 24, GlyphRenderMode.SDFAA, 2048, 2048, AtlasPopulationMode.Dynamic, false);
            if (font == null) throw new InvalidOperationException("SDF 字库创建失败。");
            bool saved = false;
            try
            {
                // 交给 TMP 按 Unicode 码点去重，不能按 char 去重而拆开 UTF-16 代理对。
                if (!font.TryAddCharacters(required + ascii, out string missing, true)) throw new InvalidOperationException("源字体缺字，停止导出：" + missing);
                font.atlasTexture.Apply(false, false); font.atlasPopulationMode = AtlasPopulationMode.Static;
                // 几何粗体会让 TMP 的 Y 向 UV 随描边 padding 变化；正式模式仅由 Shader 负责字重。
                if (glyphLocalBold || independentOutline) font.boldStyle = 0;
                if (independentOutline) font.normalStyle = 0;
                font.name = source.name + " Workbench SDF"; font.material.name = font.name + " Default";
                AssetDatabase.CreateAsset(font, path); saved = true;
                AssetDatabase.AddObjectToAsset(font.material, font);
                foreach (var atlas in font.atlasTextures) { atlas.name = font.name + " Atlas"; AssetDatabase.AddObjectToAsset(atlas, font); }
                EditorUtility.SetDirty(font); AssetDatabase.SaveAssetIfDirty(font); return font;
            }
            catch
            {
                if (saved) AssetDatabase.DeleteAsset(path);
                else
                {
                    if (font.material != null) UnityEngine.Object.DestroyImmediate(font.material);
                    foreach (var atlas in font.atlasTextures) if (atlas != null) UnityEngine.Object.DestroyImmediate(atlas);
                    UnityEngine.Object.DestroyImmediate(font);
                }
                throw;
            }
        }
    }
}
