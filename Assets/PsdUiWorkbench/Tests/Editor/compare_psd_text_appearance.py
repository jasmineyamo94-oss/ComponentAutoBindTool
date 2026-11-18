"""同时比较填充、黑描边和轮廓；只用于当前战斗 PSD 的离线视觉诊断。"""
import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from scipy.ndimage import distance_transform_edt
from psd_reference_image import load_reference, describe_reference, prepare_comparison, COMPARISON_BACKGROUND


ROOT = Path("Assets/PsdUiWorkbench/Documentation/PsdTextAudit")
FOLDER = ROOT / "StrokeDiagnosis"


def masks(image, job):
    x, y, w, h = job["rasterRect"]
    rgb = np.asarray(image.crop((x - 7, y - 7, x + w + 7, y + h + 7)))[:, :, :3].astype(float)
    fill = np.linalg.norm(rgb - np.asarray(job["color"][:3]) * 255, axis=2) < 40
    distance = distance_transform_edt(~fill)
    black = (rgb.max(axis=2) < 15) & (distance < 8)
    return fill, black


def iou(a, b):
    return float((a & b).sum() / max(1, (a | b).sum()))


def shift_mask(mask, dx, dy):
    """平移仅用于定位误差诊断，离开图像的像素丢弃，不循环到另一侧。"""
    result = np.zeros_like(mask)
    height, width = mask.shape
    if abs(dx) >= width or abs(dy) >= height:
        return result
    result[max(0, dy):min(height, height+dy), max(0, dx):min(width, width+dx)] = \
        mask[max(0, -dy):min(height, height-dy), max(0, -dx):min(width, width-dx)]
    return result


def compare(path, reference_path=None):
    reference, actual = prepare_comparison(load_reference(reference_path), path)
    if reference.size != (1080, 2400):
        raise ValueError("当前战斗 PSD 的固定裁剪要求画布为 1080×2400")
    jobs = json.loads((ROOT / "inventory.json").read_text(encoding="utf-8"))["jobs"]
    rows = []
    for job in jobs:
        if not job["eligibleForToxiaProbe"]:
            continue
        rf, rb = masks(reference, job)
        af, ab = masks(actual, job)
        if not rf.any() or not rb.any():
            raise ValueError(f"参考图层 {job['id']} 没有可比较的填充或黑描边像素")
        shifts = [(iou(rf, shift_mask(af, dx, dy)), dx, dy)
                  for dy in range(-2, 3) for dx in range(-2, 3)]
        best = max(shifts)
        rows.append(dict(id=job["id"], text=job["text"], fillRatio=float(af.sum()/rf.sum()),
                         blackRatio=float(ab.sum()/rb.sum()), fillIoU=iou(rf, af), blackIoU=iou(rb, ab),
                         bestShiftFillIoU=best[0], bestShift=[best[1], best[2]],
                         referenceFill=int(rf.sum()), actualFill=int(af.sum()),
                         referenceBlack=int(rb.sum()), actualBlack=int(ab.sum())))
    return dict(path=str(path), reference=describe_reference(reference_path),
                background=list(COMPARISON_BACKGROUND),
                metricScope="阈值像素数量与原位掩码重合度；平移后指标只用于诊断，均非整体视觉还原率",
                meanFillError=float(np.mean([abs(r["fillRatio"]-1) for r in rows])),
                meanBlackError=float(np.mean([abs(r["blackRatio"]-1) for r in rows])), rows=rows)


def comparison(paths, output, reference_path=None):
    reference = load_reference(reference_path)
    samples = [("Reference PNG", prepare_comparison(reference, reference)[0])]
    samples.extend((Path(p).stem, prepare_comparison(reference, p)[1]) for p in paths)
    # 原位标题的固定裁剪与整数倍放大，禁止为匹配原稿拉伸或逐字对齐。
    result = Image.new("RGB", (900, len(samples) * 165), (31, 31, 31))
    draw = ImageDraw.Draw(result)
    for i, (label, image) in enumerate(samples):
        draw.text((10, i * 165 + 5), label, fill="white")
        crop = image.crop((320, 90, 765, 155)).resize((890, 130), Image.Resampling.NEAREST)
        result.paste(crop, (5, i * 165 + 28))
    result.save(output)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("paths", nargs="+")
    parser.add_argument("--report")
    parser.add_argument("--comparison")
    parser.add_argument("--reference", help="显式指定参考 PNG；默认 Photoshop 原生导出，不执行去白底")
    parser.add_argument("--check-title-stroke", action="store_true", help="只检查 Hell 黑像素数量回归，不判定视觉还原度")
    args = parser.parse_args()
    reports = [compare(path, args.reference) for path in args.paths]
    if args.report:
        Path(args.report).write_text(json.dumps(reports, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.comparison:
        comparison(args.paths, args.comparison, args.reference)
    print(json.dumps([dict(path=r["path"], reference=r["reference"], metricScope=r["metricScope"],
                           fillError=r["meanFillError"], blackError=r["meanBlackError"],
                           titles=[x for x in r["rows"] if x["id"] in (1057, 1058)]) for r in reports], ensure_ascii=False))
    if args.check_title_stroke:
        for report in reports:
            hell = next(r for r in report["rows"] if r["id"] == 1058)
            assert abs(hell["blackRatio"]-1) <= 0.10, "Hell 黑色阈值像素数量与参考相差超过 10%；仅数量回归，不是视觉还原度判定"


if __name__ == "__main__":
    main()
