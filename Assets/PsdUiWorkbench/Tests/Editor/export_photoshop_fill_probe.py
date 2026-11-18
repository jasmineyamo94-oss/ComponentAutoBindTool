"""隔离 PSD 字面与模拟粗体；仅改 Photoshop 临时副本，临时字体注册在退出时撤销。"""
import argparse
import hashlib
import json
from pathlib import Path
from urllib.parse import unquote
from datetime import datetime, timezone

from photoshop_probe_font import temporary_toxia_font


SOURCE = Path("Assets/Psd/战斗-主菜单界面.psd")
FONT = Path("Assets/AAGame/UI/Fonts/Toxia_FRE.ttf")
FOLDER = Path("Assets/PsdUiWorkbench/Documentation/PsdTextAudit/StrokeDiagnosis")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


SCRIPT = r'''(function () {
    var original = app.activeDocument, temporary = null, dialogs = app.displayDialogs;
    function find(container, id) {
        for (var i = 0; i < container.layers.length; i++) {
            var l = container.layers[i];
            if (l.id == id) return l;
            if (l.typename == "LayerSet") { var nested = find(l, id); if (nested) return nested; }
        }
        return null;
    }
    function descriptor(layer) {
        var ref = new ActionReference(); ref.putIdentifier(charIDToTypeID("Lyr "), layer.id);
        return executeActionGet(ref);
    }
    function isolate(container, keep) {
        for (var i = 0; i < container.layers.length; i++) {
            var l = container.layers[i]; l.visible = keep["id" + l.id] === true;
            if (l.typename == "LayerSet") isolate(l, keep);
        }
    }
    function position(item) { return [item.position[0].as("px"), item.position[1].as("px")].join(","); }
    try {
        app.displayDialogs = DialogModes.NO;
        temporary = original.duplicate("PSD 纯填充隔离验证", false);
        var layer = find(temporary, TARGET_ID);
        if (!layer || layer.kind != LayerKind.TEXT) throw new Error("源图层 ID 没有对应文字层");
        var keep = {}, chain = [], current = layer;
        while (current.typename != "Document") {
            keep["id" + current.id] = true; chain.push(current); current = current.parent;
        }
        isolate(temporary, keep);
        var beforePosition = position(layer.textItem), beforeSize = layer.textItem.size.as("pt");
        var beforeTracking = layer.textItem.tracking, beforeBold = layer.textItem.fauxBold;
        if (MODE != "rerender-effects") {
            for (var i = 0; i < chain.length; i++) {
                temporary.activeLayer = chain[i];
                if (descriptor(chain[i]).hasKey(stringIDToTypeID("layerEffects")))
                    executeAction(stringIDToTypeID("disableLayerFX"), undefined, DialogModes.NO);
            }
        }
        temporary.activeLayer = layer;
        if (MODE != "cached") {
            layer.textItem.font = "TOXIA";
            layer.textItem.fauxBold = false;
            if (MODE != "normal") layer.textItem.fauxBold = true;
        }
        var desc = descriptor(layer);
        var style = desc.getObjectValue(stringIDToTypeID("textKey"))
            .getList(stringIDToTypeID("textStyleRange")).getObjectValue(0)
            .getObjectValue(stringIDToTypeID("textStyle"));
        var available = style.getBoolean(stringIDToTypeID("fontAvailable"));
        if (!available || layer.textItem.font != "TOXIA") throw new Error("实际渲染未确认使用可用 TOXIA 字体");
        if (beforePosition != position(layer.textItem) || beforeSize != layer.textItem.size.as("pt") ||
            beforeTracking != layer.textItem.tracking) throw new Error("粗体切换意外改变位置、字号或 Tracking");
        temporary.saveAs(new File(OUTPUT), new PNGSaveOptions(), true, Extension.LOWERCASE);
        var fields = [layer.id, layer.name, layer.textItem.contents, layer.textItem.font,
            available, beforePosition, beforeSize, beforeTracking, beforeBold,
            layer.textItem.fauxBold, layer.textItem.antiAliasMethod,
            layer.bounds[0].as("px"), layer.bounds[1].as("px"), layer.bounds[2].as("px"), layer.bounds[3].as("px"),
            desc.hasKey(stringIDToTypeID("layerEffects"))];
        for (var i = 0; i < fields.length; i++) fields[i] = encodeURIComponent(String(fields[i]));
        return fields.join("\t");
    } finally {
        try { if (temporary) temporary.close(SaveOptions.DONOTSAVECHANGES); }
        finally { try { app.activeDocument = original; } finally { app.displayDialogs = dialogs; } }
    }
})();'''


def run(layer_ids):
    import win32com.client
    app = win32com.client.GetActiveObject("Photoshop.Application")
    if Path(app.DoJavaScript("app.activeDocument.fullName.fsName")).resolve() != SOURCE.resolve():
        raise ValueError("当前 Photoshop 文档不是指定的项目 PSD")
    if app.DoJavaScript("String(app.activeDocument.saved)") != "true":
        raise ValueError("源文档有未保存修改，拒绝与磁盘基准混用")
    hashes = {str(p): digest(p) for p in (SOURCE, Path(str(SOURCE) + ".meta"), FONT)}
    outputs = [(id, mode, FOLDER / f"fill-ps-{id}-{mode}.png")
               for id in layer_ids for mode in ("cached", "normal", "bold", "rerender-effects")]
    if any(p.exists() or p.with_suffix(".json").exists() for _, _, p in outputs):
        raise ValueError("拒绝覆盖已有探针证据")
    reports = []
    with temporary_toxia_font():
        for id, mode, output in outputs:
            script = SCRIPT.replace("TARGET_ID", str(int(id))).replace("MODE", json.dumps(mode))
            script = script.replace("OUTPUT", json.dumps(output.resolve().as_posix()))
            fields = [unquote(v) for v in app.DoJavaScript(script).split("\t")]
            report = dict(createdUtc=datetime.now(timezone.utc).isoformat(), photoshopVersion=app.Version,
                          source=str(SOURCE), sourceHashes=hashes, layerId=int(fields[0]), name=fields[1],
                          text=fields[2], actualFont=fields[3], fontAvailable=fields[4] == "true", mode=mode,
                          origin=[float(v) for v in fields[5].split(",")], fontSize=float(fields[6]),
                          tracking=float(fields[7]), sourceFauxBold=fields[8] == "true", fauxBold=fields[9] == "true",
                          antiAlias=fields[10], bounds=[float(v) for v in fields[11:15]],
                          hasEffects=fields[15] == "true", png=str(output), pngSha256=digest(output),
                          note="原位透明 PNG；Alpha 用于字面覆盖率，普通与粗体可能有不同字符 advance，未平移或拉伸")
            output.with_suffix(".json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
            reports.append(report)
            print(json.dumps(dict(layerId=id, mode=mode, fontAvailable=report["fontAvailable"],
                                  fauxBold=report["fauxBold"], bounds=report["bounds"]), ensure_ascii=True), flush=True)
    if any(digest(Path(p)) != value for p, value in hashes.items()):
        raise AssertionError("源 PSD、meta 或字体被改写")
    if app.DoJavaScript("String(app.activeDocument.saved)") != "true":
        raise AssertionError("源 Photoshop 文档状态发生改变")
    print("临时字体注册已撤销，源 PSD／meta／字体哈希均未变化", flush=True)
    return reports


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("ids", type=int, nargs="+", choices=(1057, 1058, 1091))
    run(parser.parse_args().ids)
