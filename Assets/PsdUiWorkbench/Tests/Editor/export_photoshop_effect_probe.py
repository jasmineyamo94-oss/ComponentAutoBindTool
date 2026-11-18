"""从磁盘 PSD 的隔离副本导出原缓存及图层效果，不改用户当前 Photoshop 文档或安装字体。"""
import argparse
import hashlib
import json
import shutil
from pathlib import Path
from urllib.parse import unquote


SOURCE = Path("Assets/Psd/战斗-主菜单界面.psd")
FOLDER = Path("Assets/PsdUiWorkbench/Documentation/PsdTextAudit/StrokeDiagnosis")
SANDBOX = Path("Assets/PsdUiWorkbench/Tests/PhotoshopEffectProbe~")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


SCRIPT = r'''(function () {
    var original = app.documents.length ? app.activeDocument : null;
    var source = null, work = null, dialogs = app.displayDialogs, results = [];
    function find(container, id) {
        for (var i = 0; i < container.layers.length; i++) {
            var layer = container.layers[i];
            if (layer.id == id) return layer;
            if (layer.typename == "LayerSet") { var nested = find(layer, id); if (nested) return nested; }
        }
        return null;
    }
    function isolate(container, keep) {
        for (var i = 0; i < container.layers.length; i++) {
            var layer = container.layers[i]; layer.visible = keep["id" + layer.id] === true;
            if (layer.typename == "LayerSet") isolate(layer, keep);
        }
    }
    try {
        app.displayDialogs = DialogModes.NO;
        source = app.open(new File(COPY_PATH));
        var jobs = JOBS;
        for (var i = 0; i < jobs.length; i++) {
            work = source.duplicate("PSD 原缓存效果验证", false);
            var layer = find(work, jobs[i].id);
            if (!layer || layer.kind != LayerKind.TEXT) throw new Error("源图层不是文字：" + jobs[i].id);
            var keep = {}, current = layer;
            while (current.typename != "Document") { keep["id" + current.id] = true; current = current.parent; }
            isolate(work, keep);
            // 不设置文字、字体、字号、效果或字重，保留磁盘中已有图层字面。
            work.saveAs(new File(jobs[i].output), new PNGSaveOptions(), true, Extension.LOWERCASE);
            var item = layer.textItem;
            var fields = [layer.id, layer.name, item.contents, item.font, item.size.as("pt"), item.tracking,
                item.fauxBold, item.position[0].as("px"), item.position[1].as("px"), item.antiAliasMethod];
            for (var j = 0; j < fields.length; j++) fields[j] = encodeURIComponent(String(fields[j]));
            results.push(fields.join("\t"));
            work.close(SaveOptions.DONOTSAVECHANGES); work = null;
        }
        return results.join("\n");
    } finally {
        try { if (work) work.close(SaveOptions.DONOTSAVECHANGES); }
        finally {
            try { if (source) source.close(SaveOptions.DONOTSAVECHANGES); }
            finally { app.displayDialogs = dialogs; if (original) app.activeDocument = original; }
        }
    }
})();'''


def run(ids):
    import win32com.client
    app = win32com.client.GetActiveObject("Photoshop.Application")
    before = app.DoJavaScript("[app.documents.length, app.activeDocument.id, app.activeDocument.saved].join('|')")
    paths = [SOURCE, Path(str(SOURCE) + ".meta")]
    hashes = {str(p): digest(p) for p in paths}
    outputs = [FOLDER / f"effects-ps-{id}-cached.png" for id in ids]
    if any(p.exists() or p.with_suffix('.json').exists() for p in outputs):
        raise ValueError("拒绝覆盖已有 Photoshop 原始证据")
    # 末尾 ~ 的临时目录不会进入 Unity 导入链；只清理本次创建的已知文件。
    sandbox = SANDBOX.resolve()
    if sandbox.parent != Path("Assets/PsdUiWorkbench/Tests").resolve():
        raise ValueError("隔离目录越界")
    sandbox.mkdir(exist_ok=False)
    copied = sandbox / SOURCE.name
    try:
        shutil.copyfile(SOURCE, copied)
        if digest(copied) != hashes[str(SOURCE)]:
            raise ValueError("磁盘副本哈希不匹配")
        jobs = [dict(id=id, output=output.resolve().as_posix()) for id, output in zip(ids, outputs)]
        script = SCRIPT.replace('COPY_PATH', json.dumps(copied.as_posix())).replace('JOBS', json.dumps(jobs))
        rows = app.DoJavaScript(script).split('\n')
        for output, row in zip(outputs, rows):
            values = [unquote(v) for v in row.split('\t')]
            report = dict(layerId=int(values[0]), name=values[1], text=values[2], font=values[3],
                          fontSize=float(values[4]), tracking=float(values[5]), fauxBold=values[6] == 'true',
                          origin=[float(values[7]), float(values[8])], antiAlias=values[9],
                          mode='cached-effects-from-disk-copy', sourceHashes=hashes,
                          png=str(output), pngSha256=digest(output), photoshopVersion=app.Version,
                          note='不重排文字、不切换字体或字重；保留原稿图层效果；不操作用户文档')
            output.with_suffix('.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
            print(json.dumps(dict(layerId=report['layerId'], output=str(output)), ensure_ascii=False))
    finally:
        if copied.exists(): copied.unlink()
        sandbox.rmdir()
    after = app.DoJavaScript("[app.documents.length, app.activeDocument.id, app.activeDocument.saved].join('|')")
    if before != after or any(digest(p) != hashes[str(p)] for p in paths):
        raise AssertionError("源文件或用户当前 Photoshop 文档状态意外变化")
    print('源文件及用户文档状态未变，隔离磁盘副本已清理')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('ids', nargs='+', type=int)
    run(parser.parse_args().ids)
