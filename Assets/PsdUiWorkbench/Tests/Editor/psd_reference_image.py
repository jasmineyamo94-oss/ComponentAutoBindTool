"""离线视觉基准及保存预览读取；正式对照默认使用 Photoshop 原生 PNG。"""
from pathlib import Path
import hashlib
import struct
import numpy as np
from PIL import Image


RAW_REFERENCE = Path("Assets/PsdUiWorkbench/Documentation/PsdTextAudit/StrokeDiagnosis/psd-saved-composite.png")
PHOTOSHOP_REFERENCE = RAW_REFERENCE.with_name("photoshop-native-reference.png")
COMPARISON_BACKGROUND = (31, 31, 31, 255)


def unmatte_merged_rgb(image):
    if image.mode != "RGBA":
        raise ValueError("仅处理已确认带白底的 RGB PSD 合成 RGBA 预览。")
    raw = np.asarray(image, dtype=np.float64)
    alpha = raw[:, :, 3:4]
    # 存储值 = 原色×透明度 + 白色×(1−透明度)。只对该保存预览执行一次。
    rgb = np.divide((raw[:, :, :3] + alpha - 255) * 255, alpha,
                    out=np.zeros_like(raw[:, :, :3]), where=alpha > 0)
    fixed = np.concatenate([np.clip(np.rint(rgb), 0, 255), alpha], axis=2).astype(np.uint8)
    return Image.fromarray(fixed)


def load_reference(path=None):
    """PNG 按原值读取；不得将 Photoshop 导出再次去白底。"""
    with Image.open(path if path is not None else PHOTOSHOP_REFERENCE) as image:
        return image.convert("RGBA")


def describe_reference(path=None):
    path = Path(path) if path is not None else PHOTOSHOP_REFERENCE
    with Image.open(path) as image:
        profile = image.info.get("icc_profile")
        return dict(path=str(path.resolve()), sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                    size=list(image.size), processing="PNG 原值读取；无去白底、无色彩转换",
                    colorAssumption="sRGB；调用者须提供相同色彩空间的图像",
                    iccProfileSha256=hashlib.sha256(profile).hexdigest() if profile else None,
                    srgbChunk=image.info.get("srgb"))


def composite_for_comparison(image):
    if not isinstance(image, Image.Image):
        image = load_reference(image)
    return Image.alpha_composite(Image.new("RGBA", image.size, COMPARISON_BACKGROUND), image.convert("RGBA"))


def prepare_comparison(reference, candidate):
    if not isinstance(reference, Image.Image):
        reference = load_reference(reference)
    if not isinstance(candidate, Image.Image):
        candidate = load_reference(candidate)
    if reference.size != candidate.size:
        raise ValueError(f"比较图尺寸不同：参考 {reference.size}，候选 {candidate.size}；禁止自动缩放")
    return composite_for_comparison(reference), composite_for_comparison(candidate)


def extract_saved_preview(path):
    """只解码文件末尾已保存的 RGB 预览，不重绘图层效果；支持 8 位 PSD/PSB raw/RLE。"""
    with Path(path).open("rb") as stream:
        end = stream.seek(0, 2)
        stream.seek(0)

        def read(size):
            if size < 0 or size > end - stream.tell():
                raise ValueError("PSD 合成预览截断或越界")
            return stream.read(size)

        def number(size):
            return int.from_bytes(read(size), "big")

        signature, version, _, channels, height, width, depth, mode = struct.unpack(">4sH6sHIIHH", read(26))
        if signature != b"8BPS" or version not in (1, 2) or depth != 8 or mode != 3 or channels not in (3, 4):
            raise ValueError("保存预览读取器仅支持 8 位 RGB、3/4 通道 PSD/PSB")
        if width <= 0 or height <= 0 or width * height > 32_000_000:
            raise ValueError("保存预览尺寸超出离线验收范围")
        for _ in range(2):
            size = number(4)
            if size > end - stream.tell():
                raise ValueError("PSD 颜色或资源区越界")
            stream.seek(size, 1)
        section_size = number(8 if version == 2 else 4)
        section_start = stream.tell()
        if section_size > end - section_start:
            raise ValueError("PSD 图层区越界")
        if channels == 4:
            length_bytes = 8 if version == 2 else 4
            if section_size < length_bytes + 2:
                raise ValueError("无法确认第四通道是否为合成透明度")
            layer_info_size = number(length_bytes)
            if layer_info_size < 2 or layer_info_size > section_size - length_bytes:
                raise ValueError("PSD 图层信息区越界")
            if int.from_bytes(read(2), "big", signed=True) >= 0:
                raise ValueError("第四通道未声明为合成透明度，不能当作 Alpha")
        stream.seek(section_start + section_size)
        compression = number(2)
        if compression == 0:
            data = read(width * height * channels)
        elif compression == 1:
            lengths = [number(4 if version == 2 else 2) for _ in range(channels * height)]
            data = bytearray()
            for length in lengths:
                payload = read(length)
                row = bytearray()
                at = 0
                while at < len(payload):
                    control = payload[at]
                    at += 1
                    if control == 128:
                        continue
                    count = control + 1 if control < 128 else 257 - control
                    consumed = count if control < 128 else 1
                    if at + consumed > len(payload) or len(row) + count > width:
                        raise ValueError("PSD RLE 行截断或解码越界")
                    row.extend(payload[at:at+count] if control < 128 else payload[at:at+1] * count)
                    at += consumed
                if len(row) != width:
                    raise ValueError("PSD RLE 行宽不匹配")
                data.extend(row)
        else:
            raise ValueError("离线保存预览仅支持 raw/RLE；未对其他压缩作猜测")
    pixels = np.frombuffer(data, dtype=np.uint8).reshape(channels, height, width).transpose(1, 2, 0)
    image = Image.fromarray(pixels)
    return unmatte_merged_rgb(image) if channels == 4 else image.convert("RGBA")
