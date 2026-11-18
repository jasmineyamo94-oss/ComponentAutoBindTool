"""通过本机 Photoshop 导出独立视觉证据；只操作临时副本，不保存源 PSD。"""
import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def export_reference(source, output, merge_layers=True):
    import win32com.client

    source = Path(source).resolve(strict=True)
    output = Path(output).resolve()
    if output.exists() or output.with_suffix(".json").exists():
        raise ValueError("拒绝覆盖已有参考图或证据记录")
    if not output.parent.is_dir():
        raise ValueError("输出目录必须已存在")
    app = win32com.client.GetActiveObject("Photoshop.Application")
    opened = Path(app.DoJavaScript("app.activeDocument.fullName.fsName")).resolve()
    if opened != source:
        raise ValueError("Photoshop 当前文档与指定源文件不同")
    saved = app.DoJavaScript("String(app.activeDocument.saved)")
    if saved != "true":
        raise ValueError("当前文档有未保存修改，不能与磁盘原稿混作同一基准")
    before = {str(p): sha256(p) for p in (source, Path(str(source) + ".meta")) if p.exists()}
    # 使用官方文档副本及 PNG 保存接口，避免依赖我们自己的去白底公式。
    script = r'''(function () {
        var original = app.activeDocument;
        var previousDialogs = app.displayDialogs;
        var duplicate = null;
        try {
            var target = new File(OUTPUT_PATH);
            if (target.exists) throw new Error("参考图已存在");
            var facts = [app.version, original.width.as("px"), original.height.as("px"),
                original.resolution, original.colorProfileName, String(original.saved)];
            app.displayDialogs = DialogModes.NO;
            duplicate = original.duplicate("PSD Workbench 独立参考临时副本", MERGE_LAYERS);
            var options = new PNGSaveOptions();
            options.interlaced = false;
            duplicate.saveAs(target, options, true, Extension.LOWERCASE);
            return facts.join("\t");
        } finally {
            try {
                if (duplicate !== null) duplicate.close(SaveOptions.DONOTSAVECHANGES);
            } finally {
                try { app.activeDocument = original; }
                finally { app.displayDialogs = previousDialogs; }
            }
        }
    })();'''.replace("OUTPUT_PATH", json.dumps(output.as_posix())).replace("MERGE_LAYERS", str(merge_layers).lower())
    facts = app.DoJavaScript(script).split("\t")
    after = {p: sha256(Path(p)) for p in before}
    if before != after:
        raise AssertionError("源文件或导入配置发生变化")
    if app.DoJavaScript("String(app.activeDocument.saved)") != saved:
        raise AssertionError("源文档的已保存状态发生变化")
    record = dict(createdUtc=datetime.now(timezone.utc).isoformat(),
                  photoshopVersion=facts[0], source=str(source), output=str(output),
                  width=int(float(facts[1])), height=int(float(facts[2])),
                  resolution=float(facts[3]), profile=facts[4], sourceSaved=True,
                  method=f"Document.duplicate(mergeLayersOnly={str(merge_layers).lower()}) + PNGSaveOptions/saveAs(asCopy=true)",
                  sourceHashesBefore=before, sourceHashesAfter=after, outputSha256=sha256(output))
    output.with_suffix(".json").write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding="utf-8")
    return record


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("source")
    parser.add_argument("output")
    parser.add_argument("--keep-layers", action="store_true", help="保留临时副本层级，用于排查预先合并是否影响结果")
    arguments = parser.parse_args()
    print(json.dumps(export_reference(arguments.source, arguments.output, not arguments.keep_layers), ensure_ascii=True))
