"""核验 Photoshop 原生 PNG 的来源及 PSD 参考差异；统计值不是视觉相似率。"""
import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from PIL import Image


SOURCE = Path("Assets/Psd/战斗-主菜单界面.psd")
FOLDER = Path("Assets/PsdUiWorkbench/Documentation/PsdTextAudit/StrokeDiagnosis")
FILES = {
    "native": "photoshop-native-reference.png",
    "layeredNative": "photoshop-native-layered-reference.png",
    "raw": "psd-saved-composite.png",
    "unmatted": "psd-saved-composite-unmatted.png",
}
TITLE_RECT = (320, 90, 765, 155)


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def project_relative(recorded_path):
    # 原始导出记录含 Windows 绝对路径；只认项目内 Assets 路径，允许项目整体迁移。
    path = str(recorded_path).replace("\\", "/")
    if path.lower().startswith("assets/"):
        return path
    marker = path.lower().rfind("/assets/")
    if marker < 0:
        raise ValueError(f"来源记录不是 Assets 项目路径：{path}")
    return path[marker + 1:]


def verify_native_record(root, png_path, source_hashes, image):
    record_path = png_path.with_suffix(".json")
    record = json.loads((root / record_path).read_text(encoding="utf-8-sig"))
    for field, expected in (("source", SOURCE), ("output", png_path)):
        if project_relative(record[field]).casefold() != expected.as_posix().casefold():
            raise ValueError(f"{record_path} 的 {field} 路径不匹配")
    png_hash = sha256(root / png_path)
    if png_hash != record["outputSha256"].lower():
        raise ValueError(f"{png_path} 与 Photoshop 导出记录的 SHA256 不匹配")
    for field in ("sourceHashesBefore", "sourceHashesAfter"):
        hashes = {}
        for path, value in record[field].items():
            relative = project_relative(path).casefold()
            if relative in hashes:
                raise ValueError(f"{record_path} 的 {field} 存在重复路径")
            hashes[relative] = value.lower()
        for path, expected in source_hashes.items():
            if hashes.get(path.casefold()) != expected:
                raise ValueError(f"{record_path} 的 {field} 与当前 {path} 不匹配")
    if (record["width"], record["height"]) != (image.shape[1], image.shape[0]):
        raise ValueError(f"{record_path} 的画布尺寸与 PNG 不匹配")
    return {
        "png": png_path.as_posix(),
        "pngSha256": png_hash,
        "record": record_path.as_posix(),
        "recordSha256": sha256(root / record_path),
        "sourceHashesBeforeAndAfterMatchCurrent": True,
        "outputHashMatchesRecord": True,
        "photoshopVersion": record["photoshopVersion"],
        "method": record["method"],
        "profile": record["profile"],
    }


def difference_stats(difference, mask):
    values = difference[mask]
    count = len(values)
    if not count:
        return {"pixels": 0, "meanAbsoluteChannelDifference": None}
    maximum = values.max(axis=1)
    return {
        "pixels": count,
        "meanAbsoluteChannelDifference": float(values.mean()),
        "p95AbsoluteChannelDifference": float(np.percentile(values, 95)),
        "maxAbsoluteChannelDifference": float(values.max()),
        "pixelsWithAnyChannelDifferenceAbove5": int((maximum > 5).sum()),
        "pixelsWithAnyChannelDifferenceAbove20": int((maximum > 20).sum()),
    }


def compare_region(native, candidate, rect):
    x1, y1, x2, y2 = rect
    original = native[y1:y2, x1:x2].astype(np.int16)
    actual = candidate[y1:y2, x1:x2].astype(np.int16)
    alpha = original[:, :, 3]
    masks = {
        "visible": alpha > 0,
        "partial": (alpha > 0) & (alpha < 255),
        "opaque": alpha == 255,
    }
    difference = np.abs(original[:, :, :3] - actual[:, :, :3])
    alpha_difference = np.abs(alpha - actual[:, :, 3])
    result = {
        "rectX1Y1X2Y2": list(rect),
        "alphaMismatchPixels": int((alpha_difference != 0).sum()),
        "alphaMaxDifference": int(alpha_difference.max()),
        "transparentRgbIgnoredPixels": int((alpha == 0).sum()),
        "rgb": {name: difference_stats(difference, mask) for name, mask in masks.items()},
    }
    # 同背景、同原位、无缩放。透明像素的 RGB 从不参与统计。
    original_alpha = alpha[:, :, None] / 255.0
    actual_alpha = actual[:, :, 3:4] / 255.0
    yy, xx = np.indices(alpha.shape)
    checker = np.where(((yy + y1) // 8 + (xx + x1) // 8) % 2 == 0, 192, 255)[:, :, None]
    result["compositedVisibleRgb"] = {}
    for name, background in (("gray31", 31), ("white255", 255), ("checker192_255_size8", checker)):
        original_rgb = original[:, :, :3] * original_alpha + background * (1 - original_alpha)
        actual_rgb = actual[:, :, :3] * actual_alpha + background * (1 - actual_alpha)
        result["compositedVisibleRgb"][name] = difference_stats(np.abs(original_rgb - actual_rgb), masks["visible"])
    return result


def build_report(root):
    source_hashes = {path.as_posix(): sha256(root / path) for path in (SOURCE, Path(str(SOURCE) + ".meta"))}
    images = {}
    inputs = {}
    for name, filename in FILES.items():
        path = FOLDER / filename
        with Image.open(root / path) as image:
            images[name] = np.asarray(image.convert("RGBA"))
            inputs[name] = {
                "path": path.as_posix(),
                "sha256": sha256(root / path),
                "size": list(image.size),
                "mode": image.mode,
                "hasEmbeddedIccProfile": bool(image.info.get("icc_profile")),
            }
    native = images["native"]
    if any(image.shape != native.shape for image in images.values()):
        raise ValueError("参考图画布尺寸不一致，禁止自动缩放或裁剪对齐")
    if native.shape[1] < TITLE_RECT[2] or native.shape[0] < TITLE_RECT[3]:
        raise ValueError("参考图尺寸不包含约定的标题区域")
    provenance = {
        name: verify_native_record(root, FOLDER / FILES[name], source_hashes, images[name])
        for name in ("native", "layeredNative")
    }
    full = (0, 0, native.shape[1], native.shape[0])
    comparisons = {
        name: {
            region: compare_region(native, candidate, rect)
            for region, rect in (("full", full), ("title", TITLE_RECT))
        }
        for name, candidate in images.items() if name != "native"
    }
    return {
        "formatVersion": 1,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "sourceHashesCurrent": source_hashes,
        "provenance": provenance,
        "inputs": inputs,
        "nativeAndLayeredNativeRgbaExactlyEqual": bool(np.array_equal(native, images["layeredNative"])),
        "comparisonsAgainstNative": comparisons,
        "interpretation": [
            "统计差值的单位为 0～255 RGB 通道值；这些数值不是视觉相似率或还原百分比。",
            "各区域的可见、半透明、不透明集合由 Photoshop 原生 PNG 的 Alpha 定义；Alpha=0 的 RGB 忽略。",
            "所有 PNG 均直接读取，不调用 PSD 去白底函数，不修改 Alpha，不缩放、不平移。",
            "合成使用 sRGB 编码通道值直接插值，不进行 ICC 转换；用途是同条件离线诊断，不模拟所有应用的色彩管理。",
            "Alpha 相等时，同背景合成的背景项相消；三种背景的相同差值不构成三份独立证据。",
            "原生导出文件 SHA 与记录一致，且两份记录中的源 PSD/.meta 导出前后 SHA 均与当前文件匹配。",
            "半透明外边缘吻合不代表文字内部填充／描边交界完全吻合，应分别检查 partial 与 opaque 数据。",
        ],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-root", type=Path, default=Path(__file__).resolve().parents[4])
    parser.add_argument("--output", type=Path, default=FOLDER / "photoshop-reference-verification.json")
    args = parser.parse_args()
    root = args.project_root.resolve()
    report = build_report(root)
    output = args.output if args.output.is_absolute() else root / args.output
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "output": args.output.as_posix(),
        "provenanceVerified": True,
        "nativeAndLayeredNativeRgbaExactlyEqual": report["nativeAndLayeredNativeRgbaExactlyEqual"],
        "fullVisibleMeanRgbDifference": {
            name: regions["full"]["rgb"]["visible"]["meanAbsoluteChannelDifference"]
            for name, regions in report["comparisonsAgainstNative"].items()
        },
    }, ensure_ascii=False))


if __name__ == "__main__":
    main()
