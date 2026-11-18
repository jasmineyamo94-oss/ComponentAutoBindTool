"""Normal 源 Tracking 实验：固定画布、固定窗口、原位比较，不拟合平移或缩放。"""
import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from compare_psd_fill_probe import (
    FOLDER, HELL_FIRST_H, THRESHOLDS, crop, mask_iou, read_probe, statistics,
)


# 预先固定的 PSD 坐标窗口；所有候选共用，禁止按候选字形自动对齐。
REGIONS = {1057: (315, 88, 635, 155), 1058: (635, 88, 775, 155)}
PARAMETERS = (
    "characterSpacing", "wordSpacing", "lineSpacing", "paragraphSpacing", "characterHorizontalScale",
)
LABELS = {
    "psNormal": "Photoshop Normal / 原始 Tracking",
    "tmpOldNormal": "旧 TMP Normal / 仍携带 Bold 字距校准",
    "source": "新 TMP Normal / 源 Tracking",
    "kern": "新 TMP Normal / 源 Tracking + kern",
    "word": "新 TMP Normal / 源 Tracking + Word 对照",
}


def approximately(actual, expected, label, tolerance=0.0001):
    if not np.isfinite(float(actual)) or abs(float(actual) - float(expected)) > tolerance:
        raise ValueError(f"{label} 不满足控制条件：实际 {actual}，要求 {expected}")


def material_parameters(metadata):
    material = json.loads(metadata["probeMaterialJson"])
    material = material.get("Material", material)
    values = {p["first"]: p["second"] for p in material["m_SavedProperties"]["m_Floats"]}
    return material, values


def verify_source(metadata, photoshop, old, variant):
    """新样本必须记录五参数；旧探针缺失字段只能记为未知，不能补假定值。"""
    layout = metadata["after"]
    for key in PARAMETERS:
        if key not in layout:
            raise ValueError(f"{variant} 缺少参数记录 {key}，不能作为五参数受控证据")
    if layout["fontStyle"] != "Normal" or layout["text"] != photoshop["text"]:
        raise ValueError(f"{variant} 没有保持真实 Normal 或源文本（包括空格）")
    approximately(layout["fontSize"], photoshop["fontSize"], variant + " 字号")
    approximately(metadata["sourceTracking"], photoshop["tracking"], variant + " Tracking")
    approximately(layout["characterSpacing"], photoshop["tracking"] / 10, variant + " Character")
    for key, expected in (("characterHorizontalScale", 1), ("lineSpacing", 0), ("paragraphSpacing", 0)):
        approximately(layout[key], expected, variant + " " + key)
    if variant != "word":
        approximately(layout["wordSpacing"], 0, variant + " Word")
    if variant == "kern":
        if layout["fontFeatures"] != ["kern"]:
            raise ValueError("kern 对照必须仅启用 kern 特性")
    elif layout["fontFeatures"]:
        raise ValueError(f"{variant} 必须固定关闭 kern")
    for key in ("fontSourceGuid", "fontGuid", "sourceSamplingPoint", "sourcePadding", "sourceRenderMode"):
        if metadata[key] != old[key]:
            raise ValueError(f"{variant} 改变了 {key}，不能只归因于字距")
    for axis, value in zip(("x", "y"), photoshop["origin"]):
        approximately(layout["glyphs"][0]["originPsd"][axis], value, variant + " 首字 origin / baseline")
    material, values = material_parameters(metadata)
    for key in ("_OutlineWidth", "_OutlineSoftness", "_UnderlayDilate", "_UnderlaySoftness",
                "_FaceDilate", "_WeightNormal", "_WeightBold"):
        approximately(values[key], 0, variant + " 无效果 " + key)
    forbidden = {"OUTLINE_ON", "UNDERLAY_ON", "UNDERLAY_INNER", "GLOW_ON", "BEVEL_ON"}
    if forbidden.intersection(material.get("m_ValidKeywords", [])):
        raise ValueError(f"{variant} 仍启用描边、阴影或其他效果关键词")
    return {"fiveParametersRecorded": True, "normal": True, "horizontalScaleOne": True,
            "sourceTrackingRestored": True, "effectsDisabled": True,
            "sameFontAndAtlas": True, "sourceOriginAndBaselinePreserved": True}


def original_position_metrics(reference, actual, rect):
    a, b = crop(reference, rect), crop(actual, rect)
    delta = b - a
    return {
        "rectX1Y1X2Y2": list(rect),
        "reference": statistics(a, rect[:2]), "actual": statistics(b, rect[:2]),
        "actualMinusReferenceCoverage": float(delta.sum()),
        "absoluteCoverageDifference": float(np.abs(delta).sum()),
        "addedCoverage": float(np.maximum(delta, 0).sum()),
        "lostCoverage": float(np.maximum(-delta, 0).sum()),
        "rawIouByThreshold": {str(t): mask_iou(a, b, t) for t in THRESHOLDS},
    }


def parameter_record(metadata, photoshop=False):
    if photoshop:
        return {key: metadata[key] for key in ("text", "fontSize", "tracking", "origin", "antiAlias", "fauxBold")}
    layout = metadata["after"]
    return {
        "text": layout["text"], "fontSize": layout["fontSize"], "fontStyle": layout["fontStyle"],
        "sourceTracking": metadata["sourceTracking"], "fontFeatures": layout["fontFeatures"],
        "parameters": {key: layout.get(key) for key in PARAMETERS},
        "missingParameterRecords": [key for key in PARAMETERS if key not in layout],
    }


def glyph_record(metadata):
    result = []
    for glyph in metadata["after"]["glyphs"]:
        result.append({
            "index": glyph["index"], "unicode": glyph["unicode"],
            "character": chr(glyph["unicode"]), "visible": glyph["visible"],
            "originPsd": glyph["originPsd"], "advancePsd": glyph["advancePsd"],
            "advanceWidth": glyph["advancePsd"]["x"] - glyph["originPsd"]["x"],
        })
    return result


def build_report(layer_id, folder):
    paths = {"psNormal": folder / f"fill-ps-{layer_id}-normal.png",
             "tmpOldNormal": folder / f"fill-tmp-{layer_id}-normalstyle.png",
             "source": folder / f"fill-spacing-{layer_id}-source.png"}
    for variant in ("kern", "word"):
        path = folder / f"fill-spacing-{layer_id}-{variant}.png"
        if path.exists():
            paths[variant] = path
    arrays, metadata, identities = {}, {}, {}
    for name, path in paths.items():
        arrays[name], metadata[name], identities[name] = read_probe(path, name == "psNormal", layer_id, name != "psNormal")
    rect = REGIONS[layer_id]
    for name, array in arrays.items():
        if abs(float(array.sum()) - float(crop(array, rect).sum())) > 0.00001:
            raise ValueError(f"{name} 存在固定窗口外字面，拒绝静默裁切；需要人工审查窗口")
    regions = {"wholeText": rect}
    if layer_id == 1058:
        regions["firstH"] = HELL_FIRST_H
    controls = {name: verify_source(metadata[name], metadata["psNormal"], metadata["tmpOldNormal"], name)
                for name in paths if name not in ("psNormal", "tmpOldNormal")}
    glyphs = {name: glyph_record(value) for name, value in metadata.items() if name != "psNormal"}
    for name, values in glyphs.items():
        if [g["unicode"] for g in values] != [g["unicode"] for g in glyphs["tmpOldNormal"]]:
            raise ValueError(f"{name} 字符序列不同（空格也参与比较）")
    report = {
        "formatVersion": 1, "layerId": layer_id, "inputs": identities,
        "scope": "真实 Normal 的源 Tracking 排版验证；原位，不进行平移或缩放拟合；数值不是视觉还原百分比",
        "regions": {key: list(value) for key, value in regions.items()},
        "parameters": {key: parameter_record(value, key == "psNormal") for key, value in metadata.items()},
        "controls": controls, "glyphLayouts": glyphs,
        "comparisons": {name: {region: original_position_metrics(arrays["psNormal"], array, bounds)
                               for region, bounds in regions.items()}
                        for name, array in arrays.items() if name != "psNormal"},
        "candidateVsOldNormal": {
            name: original_position_metrics(arrays["tmpOldNormal"], arrays[name], rect) for name in controls},
        "candidateVsSourceNormal": {
            name: original_position_metrics(arrays["source"], arrays[name], rect)
            for name in controls if name != "source"},
        "limitations": [
            "旧 Normal 只去除 Bold 样式，仍保留旧粗体补偿及宽度校准；未记录的参数用 null，不补写默认值。",
            "新样本固定 Normal、字号、同一字体图集、Horizontal Scale=1、Line=0、Paragraph=0，关闭材质效果。",
            "source 固定 Character=Tracking/10、Word=0、kern关闭；可选 kern 与 word 仅改变名称指定的变量。",
            "PNG 不含可直接读取的 Photoshop 逐字 origin；报告不从轮廓猜测 PS 字距。",
            "PS Alpha 与 TMP 灰度覆盖代理的抗锯齿不同，IoU不能解释为整体视觉还原百分比。",
            "当前单行且 Horizontal Scale=1，本实验不支持对行距、段距或横向压缩效果下结论。",
        ],
    }
    source_iou = report["comparisons"]["source"]["wholeText"]["rawIouByThreshold"]["0.5"]
    old_iou = report["comparisons"]["tmpOldNormal"]["wholeText"]["rawIouByThreshold"]["0.5"]
    report["controlledFindings"] = [
        f"恢复源 Tracking 后，原位 IoU@0.5 从 {old_iou:.6f} 变为 {source_iou:.6f}；这不是视觉还原率。",
        "source 固定 Character=Tracking/10、Word=0、Line=0、Paragraph=0、Horizontal Scale=1。",
        "此处验证 Normal 排版，不验证粗体渲染、描边效果或所有字体适配。",
    ]
    if "kern" in arrays:
        kern_iou = report["comparisons"]["kern"]["wholeText"]["rawIouByThreshold"]["0.5"]
        report["controlledFindings"].append(
            f"其他参数不变时开启 kern，原位 IoU@0.5 为 {kern_iou:.6f}；"
            + ("低于关闭 kern 的 source 基线，本样本不支持开启 kern。" if kern_iou < source_iou
               else "不低于关闭 kern 的 source 基线，仍需结合原尺寸对照判断。"))
    return report, arrays


def font(size=16):
    path = Path("C:/Windows/Fonts/msyh.ttc")
    return ImageFont.truetype(str(path), size) if path.exists() else ImageFont.load_default()


def tile(array, rect, scale):
    result = Image.fromarray(np.rint(crop(array, rect) * 255).astype(np.uint8)).convert("RGB")
    return result.resize((result.width * scale, result.height * scale), Image.Resampling.NEAREST)


def visual_panel(report, arrays, output, scale):
    rect = report["regions"]["wholeText"]
    row_height = (rect[3] - rect[1]) * scale + 64
    canvas = Image.new("RGB", (max(960, (rect[2] - rect[0]) * scale + 40), 80 + len(arrays) * row_height), (24, 24, 24))
    draw, label_font = ImageDraw.Draw(canvas), font()
    draw.text((12, 10), f"ID {report['layerId']} · Normal 源 Tracking 对照 · {scale}×等比最近邻", font=font(19), fill="white")
    draw.text((12, 42), f"固定 PSD 窗口 {rect}；无平移、无宽度拟合；所有截图使用同一位置", font=label_font, fill="#cccccc")
    y = 80
    for name, array in arrays.items():
        params = report["parameters"][name]
        if name == "psNormal":
            detail = f"FontSize={params['fontSize']:g}  Tracking={params['tracking']:g}  Normal"
        else:
            p = params["parameters"]
            values = "  ".join(f"{short}={'未记录' if p[key] is None else format(p[key], '.5g')}" for short, key in zip(("C", "W", "L", "P", "H"), PARAMETERS))
            detail = f"{values}  kern={params['fontFeatures']}"
        draw.text((12, y), LABELS[name], font=label_font, fill="white")
        draw.text((12, y + 24), detail, font=label_font, fill="#cccccc")
        canvas.paste(tile(array, rect, scale), (12, y + 52))
        y += row_height
    canvas.save(output)


def markdown_report(report, path):
    lines = [f"# ID {report['layerId']}：Normal 源 Tracking 隔离验证", "", report["scope"], "",
             "覆盖率、阈值面积与原位 IoU 为诊断指标，不是视觉还原率。所有输入、PNG/JSON 哈希及完整参数见同名 JSON。", "",
             "| 样本 | Character | Word | Line | Paragraph | Horizontal Scale | kern |", "|---|---:|---:|---:|---:|---:|---|"]
    for name, parameters in report["parameters"].items():
        if name == "psNormal":
            continue
        values = ["未记录" if parameters["parameters"][key] is None else f"{parameters['parameters'][key]:.6f}" for key in PARAMETERS]
        lines.append("| " + " | ".join([LABELS[name], *values, str(parameters["fontFeatures"])]) + " |")
    lines += ["", "| 与 Photoshop Normal 比较 | 覆盖量差 | 原位 IoU@0.5 | 0.5阈值包围盒 |", "|---|---:|---:|---|"]
    for name, regions in report["comparisons"].items():
        m = regions["wholeText"]
        lines.append(f"| {LABELS[name]} | {m['actualMinusReferenceCoverage']:.3f} | {m['rawIouByThreshold']['0.5']:.6f} | {m['actual']['thresholds']['0.5']['boundsX1Y1X2Y2']} |")
    lines += ["", "## 本轮受控结论", ""] + ["- " + value for value in report["controlledFindings"]]
    lines += ["", "## TMP 逐字排版", "", "空格也列出；advance 为该字符排版终点，非字面包围盒。坐标是原始 PSD 坐标。", ""]
    for name, glyphs in report["glyphLayouts"].items():
        lines += [f"### {LABELS[name]}", "", "| 索引 | 字符 | Unicode | origin X | baseline Y | advance X | advance差值 |", "|---:|---|---|---:|---:|---:|---:|"]
        for glyph in glyphs:
            label = "空格" if glyph["character"] == " " else glyph["character"]
            lines.append(f"| {glyph['index']} | {label} | U+{glyph['unicode']:04X} | {glyph['originPsd']['x']:.6f} | {glyph['originPsd']['y']:.6f} | {glyph['advancePsd']['x']:.6f} | {glyph['advanceWidth']:.6f} |")
        lines.append("")
    lines += ["## 范围与限制", ""] + ["- " + value for value in report["limitations"]]
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def repair_h_summary(folder):
    """明确修复旧图：左上 PS Normal，右上真正 TMP Normal，左下 PS Bold，右下 TMP Bold。"""
    definitions = [
        ("PS Normal", "fill-ps-1058-normal.png", True, True),
        ("TMP Normal (real FontStyles.Normal)", "fill-tmp-1058-normalstyle.png", False, True),
        ("PS Bold", "fill-ps-1058-bold.png", True, False),
        ("TMP Bold (WeightBold=0.35)", "fill-tmp-1058-w035.png", False, False),
    ]
    rect, scale = HELL_FIRST_H, 4
    width, height = 400, (rect[3] - rect[1]) * scale + 45
    canvas = Image.new("RGB", (width * 2, height * 2 + 100), (24, 24, 24))
    draw, identities = ImageDraw.Draw(canvas), {}
    draw.text((12, 10), "首字 H · 真正 Normal / Bold 四格对照 · 原位 4×", font=font(20), fill="white")
    draw.text((12, 42), "旧 Normal 后续字距含粗体校准；首字 H 的 origin / baseline 相同。", font=font(), fill="#cccccc")
    for i, (label, filename, ps, normal) in enumerate(definitions):
        array, metadata, identity = read_probe(folder / filename, ps, 1058, normal if not ps else False)
        if ps and bool(metadata["fauxBold"]) == normal:
            raise ValueError(f"Photoshop 字重与四格标签不匹配：{filename}")
        x, y = (i % 2) * width, 100 + (i // 2) * height
        draw.text((x + 12, y), label, font=font(15), fill="white")
        canvas.paste(tile(array, rect, scale), (x + 12, y + 32))
        identities[label] = identity
    output = folder / "fill-h-ab-summary.png"
    canvas.save(output)
    output.with_suffix(".json").write_text(json.dumps({
        "formatVersion": 2, "purpose": "修复旧假 Normal 图；四格均使用真实 FontStyles.Normal / Bold 对照",
        "rectX1Y1X2Y2": list(rect), "displayScale": scale, "resampling": "nearest; same uniform scale",
        "inputs": identities, "note": "无字形对齐或缩放拟合。旧 Normal 只用于首H，不为整词Tracking正确性背书。",
    }, ensure_ascii=False, indent=2), encoding="utf-8")
    return output


def review_panel(folder):
    """两层严格按原 PSD 位置合成，不拼接或调整文本之间的距离。"""
    rect, scale = (315, 88, 775, 155), 2
    loaded = {layer_id: build_report(layer_id, folder) for layer_id in (1057, 1058)}
    rows = [
        ("psNormal", "Photoshop 普通字重 / 原始 Tracking=100"),
        ("tmpOldNormal", "旧 TMP Normal / 仍沿用粗体字距校准"),
        ("source", "TMP Normal / 恢复源 Tracking：Character=10，Word/Line/Paragraph=0，H=1"),
    ]
    row_height = (rect[3] - rect[1]) * scale + 44
    canvas = Image.new("RGB", ((rect[2] - rect[0]) * scale + 24, 92 + len(rows) * row_height), (24, 24, 24))
    draw = ImageDraw.Draw(canvas)
    draw.text((12, 10), "源 Tracking 恢复验证 · 原位 2×等比", font=font(20), fill="white")
    draw.text((12, 42), "仅 TOXIA 两个文本层（1057、1058）；不含缺字体标点；未挪动或拉伸文字。", font=font(), fill="#cccccc")
    y = 92
    for key, label in rows:
        left, right = (loaded[layer_id][1][key] for layer_id in (1057, 1058))
        if np.any((left > 0) & (right > 0)):
            raise ValueError("两层字面重叠；不能按无重叠覆盖率合成")
        combined = np.maximum(left, right)
        draw.text((12, y), label, font=font(), fill="white")
        canvas.paste(tile(combined, rect, scale), (12, y + 32))
        y += row_height
    output = folder / "fill-spacing-review.png"
    canvas.save(output)
    output.with_suffix(".json").write_text(json.dumps({
        "formatVersion": 1, "layerIds": [1057, 1058], "rectX1Y1X2Y2": list(rect),
        "displayScale": scale, "resampling": "nearest; same uniform scale",
        "composition": "同一1080x2400 PSD画布，已验证字面不重叠，取覆盖率逐像素max；无层间位移或拉伸",
        "inputs": {str(layer_id): {key: loaded[layer_id][0]["inputs"][key] for key, _ in rows}
                   for layer_id in loaded},
        "note": "仅两个已找到真实TOXIA字体的普通字重文本层，不含缺字体标点；不是原PSD完整带效果外观。",
    }, ensure_ascii=False, indent=2), encoding="utf-8")
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--folder", type=Path, default=FOLDER)
    parser.add_argument("--layer-id", type=int, choices=tuple(REGIONS), action="append")
    parser.add_argument("--repair-h-summary", action="store_true")
    parser.add_argument("--repair-only", action="store_true")
    parser.add_argument("--review", action="store_true", help="生成两层原位合成的三行紧凑对照")
    args = parser.parse_args()
    if args.repair_h_summary or args.repair_only:
        print(json.dumps({"repairedSummary": str(repair_h_summary(args.folder))}, ensure_ascii=False))
    if args.repair_only:
        return
    for layer_id in args.layer_id or REGIONS:
        report, arrays = build_report(layer_id, args.folder)
        prefix = args.folder / f"fill-spacing-comparison-{layer_id}"
        prefix.with_suffix(".json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        markdown_report(report, prefix.with_suffix(".md"))
        for scale in (1, 4):
            visual_panel(report, arrays, Path(str(prefix) + f"-{scale}x.png"), scale)
        print(json.dumps({"report": str(prefix.with_suffix('.json')), "layerId": layer_id,
                          "wholeText": {name: {"iou50": regions["wholeText"]["rawIouByThreshold"]["0.5"],
                                               "coverageDelta": regions["wholeText"]["actualMinusReferenceCoverage"]}
                                        for name, regions in report["comparisons"].items()}}, ensure_ascii=False))
    if args.review:
        print(json.dumps({"review": str(review_panel(args.folder))}, ensure_ascii=False))


if __name__ == "__main__":
    main()
