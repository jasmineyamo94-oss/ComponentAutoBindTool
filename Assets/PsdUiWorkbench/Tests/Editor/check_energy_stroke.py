"""对照独立参考 PNG 检查 Energy Level 的填充密度和行宽，不代表整体还原率。"""
import argparse
import json
from pathlib import Path
import numpy as np
from psd_reference_image import load_reference, describe_reference, composite_for_comparison, prepare_comparison


def measure(path):
    image = composite_for_comparison(path)
    # 只统计接近源蓝色填充的像素，排除黑描边与后面的蓝色传送门。
    pixels = np.asarray(image)[418:456, 420:665, :3].astype(float)
    mask = np.linalg.norm(pixels - [119, 193, 224], axis=2) < 40
    y, x = np.where(mask)
    if not len(x):
        raise ValueError("没有找到 Energy Level 填充")
    width = int(x.max() - x.min() + 1)
    return dict(area=int(mask.sum()), width=width, height=int(y.max() - y.min() + 1), inkPerWidth=float(mask.sum()/width))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("candidate")
    parser.add_argument("--all-known", action="store_true")
    parser.add_argument("--reference", help="显式指定参考 PNG；默认 Photoshop 原生导出，不执行去白底")
    args = parser.parse_args()
    reference_image, actual_image = prepare_comparison(load_reference(args.reference), args.candidate)
    if reference_image.size != (1080, 2400):
        raise ValueError("当前战斗 PSD 的固定裁剪要求画布为 1080×2400")
    reference_info = describe_reference(args.reference)
    reference = measure(reference_image)
    actual = measure(actual_image)
    density = actual["inkPerWidth"] / reference["inkPerWidth"]
    width = actual["width"] / reference["width"]
    print(json.dumps(dict(referenceImage=reference_info, reference=reference, actual=actual,
                          metricScope="填充阈值像素密度和行宽回归，不是视觉还原率",
                          densityRatio=density, widthRatio=width), ensure_ascii=False))
    assert 0.92 <= density <= 1.08, "蓝色笔画密度与原稿差异超过 8%"
    assert 0.95 <= width <= 1.05, "文字行宽与原稿差异超过 5%"
    if args.all_known:
        root = Path("Assets/PsdUiWorkbench/Documentation/PsdTextAudit")
        inventory = json.loads((root / "inventory.json").read_text(encoding="utf-8"))
        rows = []
        for job in inventory["jobs"]:
            if not job["eligibleForToxiaProbe"] or job["id"] == 947:
                continue
            x, y, w, h = job["rasterRect"]
            values = []
            for image in (reference_image, actual_image):
                pixels = np.asarray(image.crop((x-5, y-3, x+w+5, y+h+3)))[:, :, :3].astype(float)
                mask = np.linalg.norm(pixels - np.asarray(job["color"][:3])*255, axis=2) < 40
                yy, xx = np.where(mask)
                if not len(xx):
                    raise ValueError(f"图层 {job['id']} 没有可比较的填充像素")
                values.append(float(mask.sum()/(xx.max()-xx.min()+1)))
            rows.append(dict(id=job["id"], densityRatio=values[1]/values[0]))
        errors = [abs(row["densityRatio"]-1) for row in rows]
        report = dict(referenceImage=reference_info, samples=len(rows),
                      metricScope="填充阈值像素密度回归，不是视觉还原率",
                      meanAbsoluteDensityError=float(np.mean(errors)), worstAbsoluteDensityError=max(errors), rows=rows)
        print(json.dumps(report, ensure_ascii=False))
        assert report["meanAbsoluteDensityError"] <= 0.06, "已有字体样本的平均笔画密度差异超过 6%"
        assert report["worstAbsoluteDensityError"] <= 0.15, "已有字体样本的最差笔画密度差异超过 15%"


if __name__ == "__main__":
    main()
