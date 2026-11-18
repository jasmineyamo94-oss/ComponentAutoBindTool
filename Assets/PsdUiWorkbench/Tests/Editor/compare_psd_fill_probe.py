"""纯字面覆盖率诊断：原位比较、位置误差单列，不把数值解释为视觉还原率。"""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont


FOLDER = Path("Assets/PsdUiWorkbench/Documentation/PsdTextAudit/StrokeDiagnosis")
THRESHOLDS = (0.1, 0.5, 0.9)
# H 的右端覆盖至 x=683，下一字最早从 x=688 开始；保留边缘及位置诊断余量。
# 此窗口只适用于当前源 ID 1058，不按每张候选图移动或缩放。
HELL_FIRST_H = (645, 90, 686, 150)


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_probe(path, photoshop, layer_id, normal_style=False):
    path = Path(path)
    metadata_path = path.with_suffix(".json")
    metadata = json.loads(metadata_path.read_text(encoding="utf-8-sig"))
    if metadata["layerId"] != layer_id:
        raise ValueError(f"图层 ID 不匹配：{path}")
    with Image.open(path) as image:
        rgba = np.asarray(image.convert("RGBA"))
    if rgba.shape != (2400, 1080, 4):
        raise ValueError(f"画布不是 1080×2400，禁止自动缩放：{path}")
    if photoshop:
        if not metadata.get("fontAvailable") or metadata.get("actualFont") != "TOXIA":
            raise ValueError(f"Photoshop 没有确认可用 TOXIA：{path}")
        if metadata.get("hasEffects"):
            raise ValueError(f"纯字面对照不能包含图层效果：{path}")
        if metadata.get("pngSha256") != sha256(path):
            raise ValueError(f"Photoshop PNG 与原始导出记录哈希不匹配：{path}")
        coverage = rgba[:, :, 3].astype(np.float64) / 255
        channel = "Alpha；透明 Photoshop PNG 原值，不去白底"
    else:
        if not metadata.get("whiteCoverage"):
            raise ValueError(f"TMP 探针必须为白字黑底：{path}")
        if bool(metadata.get("normalStyle")) != normal_style:
            raise ValueError(f"TMP 探针样式与指定实验不符：{path}")
        if not normal_style and not metadata.get("layoutPreserved"):
            raise ValueError(f"仅 Shader 权重探针意外改变布局：{path}")
        if normal_style and metadata["after"]["fontStyle"] != "Normal":
            raise ValueError(f"真正 Normal 探针没有采用 FontStyles.Normal：{path}")
        if not np.all(rgba[:, :, 3] == 255) or not np.array_equal(rgba[:, :, 0], rgba[:, :, 1]) \
                or not np.array_equal(rgba[:, :, 0], rgba[:, :, 2]):
            raise ValueError(f"TMP 探针不是不透明灰度覆盖率图：{path}")
        coverage = rgba[:, :, 0].astype(np.float64) / 255
        channel = "黑底白字 RGB 灰度；当前 Gamma 渲染路径下的覆盖率代理"
    if not coverage.any():
        raise ValueError(f"探针没有可见字面：{path}")
    return coverage, metadata, {
        "path": str(path), "sha256": sha256(path), "metadataSha256": sha256(metadata_path),
        "coverageSource": channel, "canvas": [1080, 2400],
    }


def crop(array, rect):
    x1, y1, x2, y2 = rect
    return array[y1:y2, x1:x2]


def bounding_box(mask, offset=(0, 0)):
    ys, xs = np.where(mask)
    if not len(xs):
        return None
    return [int(xs.min() + offset[0]), int(ys.min() + offset[1]),
            int(xs.max() + 1 + offset[0]), int(ys.max() + 1 + offset[1])]


def statistics(array, offset=(0, 0)):
    total = float(array.sum())
    yy, xx = np.indices(array.shape)
    return {
        "coverageSum": total,
        "coverageCentroidPsd": [float((xx * array).sum() / total + offset[0]),
                                float((yy * array).sum() / total + offset[1])] if total else None,
        "thresholds": {str(t): {"area": int((array >= t).sum()),
                                "boundsX1Y1X2Y2": bounding_box(array >= t, offset)} for t in THRESHOLDS},
    }


def zero_shift(array, dx, dy):
    shifted = np.zeros_like(array)
    height, width = array.shape
    if abs(dx) < width and abs(dy) < height:
        shifted[max(dy, 0):min(height, height + dy), max(dx, 0):min(width, width + dx)] = \
            array[max(-dy, 0):min(height, height - dy), max(-dx, 0):min(width, width - dx)]
    return shifted


def mask_iou(reference, actual, threshold):
    reference, actual = reference >= threshold, actual >= threshold
    union = int((reference | actual).sum())
    return float((reference & actual).sum() / union) if union else 1.0


def pair_metrics(reference, actual, rect):
    reference, actual = crop(reference, rect), crop(actual, rect)
    difference = actual - reference
    shifts = []
    for dy in range(-2, 3):
        for dx in range(-2, 3):
            shifted = zero_shift(actual, dx, dy)
            shifts.append((mask_iou(reference, shifted, 0.5), -float(np.abs(shifted-reference).sum()),
                           -abs(dx)-abs(dy), dx, dy))
    best = max(shifts)
    return {
        "rectX1Y1X2Y2": list(rect),
        "reference": statistics(reference, rect[:2]), "actual": statistics(actual, rect[:2]),
        "actualMinusReferenceCoverage": float(difference.sum()),
        "absoluteCoverageDifference": float(np.abs(difference).sum()),
        "addedCoverage": float(np.maximum(difference, 0).sum()),
        "lostCoverage": float(np.maximum(-difference, 0).sum()),
        "rawIouByThreshold": {str(t): mask_iou(reference, actual, t) for t in THRESHOLDS},
        "positionDiagnosticOnly": {
            "bestIntegerShiftOfActual": [best[3], best[4]], "bestIouAtHalfCoverage": best[0],
            "absoluteCoverageDifferenceAfterShift": -best[1],
            "note": "±2像素零填充平移仅用于诊断位置误差；不修改原位输出，也不是验收结果",
        },
    }


def increment(normal, bold, rect):
    normal, bold = crop(normal, rect), crop(bold, rect)
    difference = bold - normal
    before, after = statistics(normal), statistics(bold)
    return {
        "coverageIncrease": float(difference.sum()),
        "positiveCoverageChange": float(np.maximum(difference, 0).sum()),
        "negativeCoverageChange": float(np.maximum(-difference, 0).sum()),
        "coverageCentroidDeltaRightDown": [float(b-a) for a, b in zip(before["coverageCentroidPsd"],
                                                                      after["coverageCentroidPsd"])],
        "areaIncreaseByThreshold": {str(t): int((bold >= t).sum() - (normal >= t).sum()) for t in THRESHOLDS},
    }


def build_report(layer_id, folder):
    files = {
        "psCachedBold": f"fill-ps-{layer_id}-cached.png",
        "psNormal": f"fill-ps-{layer_id}-normal.png",
        "psRerenderedBold": f"fill-ps-{layer_id}-bold.png",
        "tmpWeight0": f"fill-tmp-{layer_id}-w000.png",
        "tmpWeight035": f"fill-tmp-{layer_id}-w035.png",
    }
    normal_file = f"fill-tmp-{layer_id}-normalstyle.png"
    if layer_id == 1058 or (folder / normal_file).exists():
        files["tmpNormalStyle"] = normal_file
    arrays, metadata, identities = {}, {}, {}
    for name, filename in files.items():
        arrays[name], metadata[name], identities[name] = read_probe(
            folder / filename, name.startswith("ps"), layer_id, name == "tmpNormalStyle")
        if name.startswith("tmp"):
            identities[name]["fontStyle"] = metadata[name]["after"]["fontStyle"]
            identities[name]["requestedShaderWeight"] = metadata[name]["requestedWeight"]
            identities[name]["layoutPreserved"] = metadata[name]["layoutPreserved"]
    tmp_a, tmp_b = metadata["tmpWeight0"], metadata["tmpWeight035"]
    for first, second in zip(tmp_a["after"]["glyphs"], tmp_b["after"]["glyphs"]):
        for key in ("unicode", "originPsd", "advancePsd"):
            if first[key] != second[key]:
                raise ValueError("TMP 两个 Shader 字重探针改变了字符身份、原点或 advance")
    if len(tmp_a["after"]["glyphs"]) != len(tmp_b["after"]["glyphs"]):
        raise ValueError("TMP 两个探针的字符数不同")
    for key in ("origin", "fontSize", "tracking"):
        if metadata["psNormal"][key] != metadata["psRerenderedBold"][key]:
            raise ValueError("Photoshop 两个字重改变了基线、字号或 Tracking")
    if "tmpNormalStyle" in metadata:
        normal = metadata["tmpNormalStyle"]["after"]
        bold = tmp_b["after"]
        if normal["fontSize"] != bold["fontSize"] or normal["characterSpacing"] != bold["characterSpacing"]:
            raise ValueError("TMP 真正 Normal 改变了字号或 Character Spacing")
        if normal["glyphs"][0]["originPsd"] != bold["glyphs"][0]["originPsd"]:
            raise ValueError("TMP 真正 Normal 改变了首字基线/原点，不能直接使用首H窗口")
    support = np.logical_or.reduce([a > 0 for a in arrays.values()])
    x1, y1, x2, y2 = bounding_box(support)
    rect = (max(0, x1-6), max(0, y1-6), min(1080, x2+6), min(2400, y2+6))
    regions = {"wholeText": rect}
    if layer_id == 1058:
        if tmp_a["after"]["glyphs"][0]["unicode"] != ord("H"):
            raise ValueError("首字符不是 H，不能使用固定 H 窗口")
        regions["firstH"] = HELL_FIRST_H
    pairs = {
        "cachedVsRerenderedBold": ("psCachedBold", "psRerenderedBold"),
        "controlledPsNormalVsTmpBoldStyleWeight0": ("psNormal", "tmpWeight0"),
        "boldPsVsTmp": ("psRerenderedBold", "tmpWeight035"),
        "cachedPsVsTmpBold": ("psCachedBold", "tmpWeight035"),
    }
    if "tmpNormalStyle" in arrays:
        pairs["normalPsVsTmp"] = ("psNormal", "tmpNormalStyle")
        pairs["tmpTrueNormalVsBoldStyleWeight0"] = ("tmpNormalStyle", "tmpWeight0")
    report = {
        "formatVersion": 2, "layerId": layer_id, "inputs": identities,
        "metricScope": "覆盖率、阈值面积、原位IoU及位置诊断；所有数值均非整体视觉还原率",
        "regions": {name: list(value) for name, value in regions.items()},
        "comparisons": {name: {region: pair_metrics(arrays[left], arrays[right], bounds)
                               for region, bounds in regions.items()} for name, (left, right) in pairs.items()},
        "boldMinusNormal": {region: {
            "photoshop": increment(arrays["psNormal"], arrays["psRerenderedBold"], bounds),
            "tmpTrueNormalToBold": increment(arrays["tmpNormalStyle"], arrays["tmpWeight035"], bounds)
                if "tmpNormalStyle" in arrays else None,
            "tmpControlledWeightChangeInBoldStyle": increment(arrays["tmpWeight0"], arrays["tmpWeight035"], bounds),
        } for region, bounds in regions.items()},
        "limitations": [
            "WeightBold=0 且仍为 FontStyles.Bold 不等于 FontStyles.Normal：Bold 样式的网格/UV路径仍可能影响覆盖率。",
            "真正普通字重使用 tmpNormalStyle；权重0保留为固定Bold样式下的受控对照，不用于计算真实普通→粗体增量。",
            "Photoshop 和 TMP 真正 Normal 都可能改变后续字符advance；首H窗口用于排除后续advance的影响。",
            "所有TMP对照保持字号、显式Character Spacing和首字原点；整段Normal比较仍包含排版差异。",
            "PS cached 与重绘bold的全段差异不直接等同字体版本差异，需要结合首字形与后续字符位置判断。",
            "首H固定窗口覆盖该字完整边缘；无逐字平移、无缩放。最佳平移只作为独立诊断字段。",
            "PS使用Alpha，TMP使用黑底白字灰度；两者的抗锯齿覆盖函数本身可能不同。",
        ],
    }
    return report, arrays


def visual_panel(report, arrays, output):
    # 分别显示原尺寸与4倍最近邻；每对先原位显示，再以红/青差分定位，不使用最佳平移。
    font_path = Path("C:/Windows/Fonts/msyh.ttc")
    font = ImageFont.truetype(str(font_path), 16) if font_path.exists() else ImageFont.load_default()
    pairs = [("缓存粗体 / Photoshop重绘粗体", "psCachedBold", "psRerenderedBold")]
    if "tmpNormalStyle" in arrays:
        pairs.append(("A 真正普通：Photoshop / TMP Normal", "psNormal", "tmpNormalStyle"))
    pairs.append(("B 粗体：Photoshop / TMP Bold 权重0.35", "psRerenderedBold", "tmpWeight035"))
    if "tmpNormalStyle" in arrays:
        pairs.append(("受控对照：TMP Normal / TMP Bold 权重0", "tmpNormalStyle", "tmpWeight0"))
    else:
        pairs.append(("仅诊断：PS普通 / TMP Bold 权重0（非Normal）", "psNormal", "tmpWeight0"))
    sections = [("整段原位 100%", report["regions"]["wholeText"], 1)]
    if "firstH" in report["regions"]:
        sections.append(("首字 H 原位 4×（排除后续字符advance差异）", report["regions"]["firstH"], 4))
    else:
        sections.append(("整段原位 4×（包含字符advance差异）", report["regions"]["wholeText"], 4))
    col_width = max(330, max((r[2]-r[0])*scale + 20 for _, r, scale in sections))
    height = 45 + sum(35 + len(pairs)*((r[3]-r[1])*scale + 40) for _, r, scale in sections)
    canvas = Image.new("RGB", (col_width*3, height), (24, 24, 24))
    draw = ImageDraw.Draw(canvas)
    draw.text((10, 8), "左：参考  中：候选  右：覆盖差（红=参考更多，青=候选更多）；无自动对齐", font=font, fill="white")
    top = 45
    for title, rect, scale in sections:
        draw.text((10, top), title, font=font, fill="white")
        top += 35
        for label, left, right in pairs:
            a, b = crop(arrays[left], rect), crop(arrays[right], rect)
            delta = b-a
            diff = np.zeros((*delta.shape, 3), dtype=np.uint8)
            diff[:, :, 0] = np.rint(np.maximum(-delta, 0)*255).astype(np.uint8)
            diff[:, :, 1] = diff[:, :, 2] = np.rint(np.maximum(delta, 0)*255).astype(np.uint8)
            tiles = [Image.fromarray(np.rint(x*255).astype(np.uint8)).convert("RGB") for x in (a, b)]
            tiles.append(Image.fromarray(diff))
            draw.text((10, top), label, font=font, fill="white")
            for column, tile in enumerate(tiles):
                tile = tile.resize((tile.width*scale, tile.height*scale), Image.Resampling.NEAREST)
                canvas.paste(tile, (column*col_width+10, top+30))
            top += tiles[0].height*scale + 40
    canvas.save(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--layer-id", type=int, default=1058)
    parser.add_argument("--folder", type=Path, default=FOLDER)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--panel", type=Path)
    args = parser.parse_args()
    report, arrays = build_report(args.layer_id, args.folder)
    output = args.report or args.folder / f"fill-comparison-{args.layer_id}.json"
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.panel:
        visual_panel(report, arrays, args.panel)
    print(json.dumps({"report": str(output), "layerId": args.layer_id,
                      "wholeText": {key: {
                          "rawIou50": regions["wholeText"]["rawIouByThreshold"]["0.5"],
                          "coverageDelta": regions["wholeText"]["actualMinusReferenceCoverage"],
                      } for key, regions in report["comparisons"].items()},
                      "firstH": {key: regions.get("firstH") for key, regions in report["comparisons"].items()},
                      "boldMinusNormal": report["boldMinusNormal"]}, ensure_ascii=True))


if __name__ == "__main__":
    main()
