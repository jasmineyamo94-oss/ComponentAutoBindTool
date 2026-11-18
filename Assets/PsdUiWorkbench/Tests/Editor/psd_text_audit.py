"""只读的 PSD 文本审计实验；不接入 Importer，不解压像素，不写源文件。"""

import argparse
import hashlib
import io
import json
import math
import re
import struct
from pathlib import Path


MAX_BLOCK = 16 * 1024 * 1024
LONG_KEYS = set("LMsk Lr16 Lr32 Layr Mt16 Mt32 Mtrn Alph FMsk lnk2 FEid FXid PxSD lnkE extn cinf".split())


class FormatError(ValueError):
    pass


class Reader:
    def __init__(self, data):
        self.stream = io.BytesIO(data)
        self.end = len(data)

    def read(self, size):
        if size < 0 or size > self.end - self.stream.tell():
            raise FormatError("数据块越界或截断")
        return self.stream.read(size)

    def number(self, fmt):
        return struct.unpack(">" + fmt, self.read(struct.calcsize(">" + fmt)))[0]

    def key(self):
        return self.read(4).decode("latin1")

    def unicode(self):
        count = self.number("I")
        if count > 1000000:
            raise FormatError("Unicode 文本过长")
        return self.read(count * 2).decode("utf-16-be")

    def identifier(self):
        size = self.number("I") or 4
        return self.read(size).decode("latin1")

    def descriptor(self, depth=0):
        if depth > 64:
            raise FormatError("Descriptor 嵌套过深")
        name, class_id = self.unicode(), self.identifier()
        count = self.number("I")
        if count > 100000:
            raise FormatError("Descriptor 项数过多")
        result = {"_name": name, "_class": class_id}
        for _ in range(count):
            key = self.identifier()
            if key in result:
                raise FormatError("Descriptor 重复键：" + key)
            result[key] = self.value(self.key(), depth + 1, key)
        return result

    def value(self, kind, depth, key=""):
        if depth > 64:
            raise FormatError("Descriptor 嵌套过深")
        if kind in ("Objc", "GlbO"):
            return self.descriptor(depth)
        if kind == "VlLs":
            count = self.number("I")
            if count > 100000:
                raise FormatError("Descriptor 数组过长")
            return [self.value(self.key(), depth + 1) for _ in range(count)]
        if kind == "TEXT":
            return self.unicode()
        if kind in ("type", "GlbC"):
            return {"_name": self.unicode(), "_class": self.identifier()}
        if kind == "enum":
            return {"enumType": self.identifier(), "value": self.identifier()}
        if kind in ("UntF", "UnFl"):
            unit = self.key()
            value = self.number("d" if kind == "UntF" else "f")
            if not math.isfinite(value):
                raise FormatError("非有限单位浮点数")
            return {"unit": unit, "value": value}
        if kind == "doub":
            value = self.number("d")
            if not math.isfinite(value):
                raise FormatError("非有限浮点数")
            return value
        if kind in ("long", "comp", "bool"):
            value = self.number({"long": "i", "comp": "q", "bool": "B"}[kind])
            return bool(value) if kind == "bool" else value
        if kind in ("tdta", "alis", "Pth "):
            size = self.number("I")
            if size > MAX_BLOCK:
                raise FormatError("原始 Descriptor 负载过大")
            data = self.read(size)
            if key == "EngineData":
                return EngineReader(data).parse()
            return {"rawType": kind, "length": size, "sha256": hashlib.sha256(data).hexdigest()}
        raise FormatError("尚不支持 Descriptor 类型：" + repr(kind))


class EngineReader:
    def __init__(self, data):
        self.data, self.index, self.nodes = data, 0, 0

    def whitespace(self):
        while self.index < len(self.data) and self.data[self.index] in b" \t\r\n\x00":
            self.index += 1

    def parse(self):
        result = self.value()
        self.whitespace()
        if self.index != len(self.data):
            raise FormatError("EngineData 尾部有未识别数据")
        return result

    def value(self, depth=0):
        self.nodes += 1
        if depth > 64 or self.nodes > 100000:
            raise FormatError("EngineData 复杂度超限")
        self.whitespace()
        if self.data.startswith(b"<<", self.index):
            self.index += 2
            result = {}
            while True:
                self.whitespace()
                if self.data.startswith(b">>", self.index):
                    self.index += 2
                    return result
                key = self.token()
                if not key.startswith("/") or key[1:] in result:
                    raise FormatError("EngineData 字典键错误或重复")
                result[key[1:]] = self.value(depth + 1)
        if self.data[self.index:self.index + 1] == b"[":
            self.index += 1
            result = []
            while True:
                self.whitespace()
                if self.data[self.index:self.index + 1] == b"]":
                    self.index += 1
                    return result
                result.append(self.value(depth + 1))
        if self.data[self.index:self.index + 1] == b"(":
            return self.string()
        token = self.token()
        if token in ("true", "false"):
            return token == "true"
        if token == "null":
            return None
        if re.fullmatch(r"[-+]?\d+", token):
            return int(token)
        if re.fullmatch(r"[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?", token):
            value = float(token)
            if math.isfinite(value):
                return value
        if token.startswith("/"):
            return {"name": token[1:]}
        raise FormatError("未知 EngineData token：" + repr(token[:80]))

    def token(self):
        self.whitespace()
        start = self.index
        while self.index < len(self.data) and self.data[self.index] not in b" \t\r\n\x00[]<>()":
            self.index += 1
        if self.index == start:
            raise FormatError("EngineData token 截断或分隔符错误")
        return self.data[start:self.index].decode("ascii")

    def string(self):
        self.index += 1
        result = bytearray()
        nesting = 1
        while self.index < len(self.data):
            value = self.data[self.index]
            self.index += 1
            if value == 92:
                if self.index == len(self.data):
                    break
                value = self.data[self.index]
                self.index += 1
                if 48 <= value <= 55:
                    digits = bytes([value])
                    for _ in range(2):
                        if self.index < len(self.data) and 48 <= self.data[self.index] <= 55:
                            digits += self.data[self.index:self.index + 1]
                            self.index += 1
                        else:
                            break
                    result.append(int(digits, 8) & 255)
                elif value in (10, 13):
                    if value == 13 and self.data[self.index:self.index + 1] == b"\n":
                        self.index += 1
                else:
                    result.append({110: 10, 114: 13, 116: 9, 98: 8, 102: 12}.get(value, value))
            elif value == 40:
                nesting += 1
                result.append(value)
            elif value == 41:
                nesting -= 1
                if nesting == 0:
                    return bytes(result[2:]).decode("utf-16-be") if result.startswith(b"\xfe\xff") else result.decode("latin1")
                result.append(value)
            else:
                result.append(value)
        raise FormatError("EngineData 字符串截断")


def sized(stream, size, end):
    if size < 0 or size > end - stream.tell():
        raise FormatError("PSD 块边界越界")
    data = stream.read(size)
    if len(data) != size:
        raise FormatError("PSD 文件截断")
    return data


def num(stream, size, end):
    return int.from_bytes(sized(stream, size, end), "big")


def skip_section(stream, size_bytes, end):
    size = num(stream, size_bytes, end)
    if size > end - stream.tell():
        raise FormatError("PSD 区段越界")
    return stream.tell() + size


def audit(path):
    path = Path(path)
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    report = {"source": str(path), "sha256": digest, "diagnostics": [], "layers": []}
    with path.open("rb") as stream:
        end = path.stat().st_size
        signature, version, _, channels, height, width, depth, color = struct.unpack(">4sH6sHIIHH", sized(stream, 26, end))
        if signature != b"8BPS" or version not in (1, 2) or depth != 8:
            raise FormatError("本审计仅支持 8 位 PSD/PSB")
        large = version == 2
        report.update(width=width, height=height, depth=depth, colorMode=color, version=version)
        stream.seek(skip_section(stream, 4, end))
        resources_end = skip_section(stream, 4, end)
        while stream.tell() < resources_end:
            if sized(stream, 4, resources_end) != b"8BIM":
                raise FormatError("资源签名错误")
            resource_id = num(stream, 2, resources_end)
            name_size = num(stream, 1, resources_end)
            sized(stream, name_size + (name_size + 1) % 2, resources_end)
            size = num(stream, 4, resources_end)
            if resource_id in (1005, 1037, 1049):
                payload = sized(stream, size, resources_end)
                if resource_id == 1005:
                    h, hu, wu, v, vu, htu = struct.unpack(">IHHIHH", payload)
                    report["resolution"] = dict(horizontal=h / 65536, vertical=v / 65536, horizontalUnit=hu, verticalUnit=vu, widthUnit=wu, heightUnit=htu)
                else:
                    report["globalAngle" if resource_id == 1037 else "globalAltitude"] = int.from_bytes(payload, "big", signed=True)
            else:
                if size > resources_end - stream.tell():
                    raise FormatError("资源负载越界")
                stream.seek(size, 1)
            sized(stream, size % 2, resources_end)
        mask_end = skip_section(stream, 8 if large else 4, end)
        layer_end = skip_section(stream, 8 if large else 4, mask_end)
        if stream.tell() == layer_end:
            raise FormatError("审计实验尚不支持全局 Layr 区段")
        count = abs(int.from_bytes(sized(stream, 2, layer_end), "big", signed=True))
        pixel_bytes = 0
        for index in range(count):
            top, left, bottom, right = struct.unpack(">4i", sized(stream, 16, layer_end))
            for _ in range(num(stream, 2, layer_end)):
                sized(stream, 2, layer_end)
                pixel_bytes += num(stream, 8 if large else 4, layer_end)
            blend = sized(stream, 12, layer_end)
            if blend[:4] != b"8BIM":
                raise FormatError("图层混合签名错误")
            extra_end = skip_section(stream, 4, layer_end)
            for _ in range(2):
                stream.seek(skip_section(stream, 4, extra_end))
            name_size = num(stream, 1, extra_end)
            name = sized(stream, name_size, extra_end).decode("mac_roman")
            sized(stream, (4 - (name_size + 1) % 4) % 4, extra_end)
            layer = dict(index=index, id=None, name=name, rasterRect=[left, top, right - left, bottom - top], opacity=blend[8], flags=blend[10], blendMode=blend[4:8].decode("latin1"), blocks=[])
            while extra_end - stream.tell() >= 12:
                signature = sized(stream, 4, extra_end)
                key = sized(stream, 4, extra_end).decode("latin1")
                if signature not in (b"8BIM", b"8B64"):
                    raise FormatError("附加块签名错误")
                size = num(stream, 8 if large and key in LONG_KEYS else 4, extra_end)
                if size > extra_end - stream.tell():
                    raise FormatError("附加块越界")
                layer["blocks"].append(key)
                if key in ("lyid", "luni", "lsct", "lsdk", "TySh", "tySh", "lfx2", "lfxs"):
                    if size > MAX_BLOCK:
                        raise FormatError("审计元数据过大")
                    payload = sized(stream, size, extra_end)
                    reader = Reader(payload)
                    try:
                        if key == "lyid":
                            layer["id"] = reader.number("i")
                        elif key == "luni":
                            layer["name"] = reader.unicode()
                        elif key in ("lsct", "lsdk"):
                            layer["section"] = reader.number("I")
                        elif key == "TySh":
                            text_version = reader.number("H")
                            if text_version != 1:
                                raise FormatError("未知 TySh 版本")
                            layer["transform"] = [reader.number("d") for _ in range(6)]
                            text_version, descriptor_version = reader.number("H"), reader.number("I")
                            if text_version != 50 or descriptor_version != 16:
                                raise FormatError("未知文本 Descriptor 版本")
                            layer["textDescriptor"] = reader.descriptor()
                            warp_version, warp_descriptor_version = reader.number("H"), reader.number("I")
                            if warp_version != 1 or warp_descriptor_version != 16:
                                raise FormatError("未知 Warp Descriptor 版本")
                            layer["warp"] = reader.descriptor()
                            layer["typeBoundsRaw"] = [reader.number("i") for _ in range(4)]
                        elif key in ("lfx2", "lfxs"):
                            effect_version, descriptor_version = reader.number("I"), reader.number("I")
                            if effect_version != 0 or descriptor_version != 16:
                                raise FormatError("未知效果 Descriptor 版本")
                            layer["effects"] = reader.descriptor()
                        else:
                            layer.setdefault("diagnostics", []).append("旧版 tySh 尚未实现样式解析")
                    except (ValueError, UnicodeError, OverflowError) as error:
                        layer.setdefault("diagnostics", []).append(key + ": " + str(error))
                else:
                    stream.seek(size, 1)
                sized(stream, size % 2, extra_end)
            stream.seek(extra_end)
            report["layers"].append(layer)
        if pixel_bytes > layer_end - stream.tell():
            raise FormatError("通道像素长度越界")
        report["skippedPixelBytes"] = pixel_bytes
        report["pixelPayloadDecoded"] = False
    if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
        raise FormatError("审计期间源文件发生变化")
    ids = [layer["id"] for layer in report["layers"] if layer.get("section") != 3]
    if None in ids or len(ids) != len(set(ids)):
        report["diagnostics"].append("有效图层 ID 缺失或重复，禁止按名称猜测关联")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source")
    parser.add_argument("output")
    parser.add_argument("--scan-fonts", action="store_true", help="使用本机 fontTools 按 PostScript 名扫描项目和系统字体")
    arguments = parser.parse_args()
    report = audit(arguments.source)
    output = Path(arguments.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    inventory = build_inventory(report)
    (output.parent / "inventory.json").write_text(json.dumps(inventory, ensure_ascii=False, indent=2), encoding="utf-8")
    if arguments.scan_fonts:
        font_report = scan_fonts({name for job in inventory["jobs"] for name in job["fonts"]})
        (output.parent / "fonts.json").write_text(json.dumps(font_report, ensure_ascii=False, indent=2), encoding="utf-8")
    text_layers = [layer for layer in report["layers"] if "TySh" in layer["blocks"] or "tySh" in layer["blocks"]]
    print(json.dumps({"layers": len(report["layers"]), "textLayers": len(text_layers), "parsedTextLayers": sum("textDescriptor" in layer for layer in text_layers), "diagnostics": [layer.get("diagnostics", []) for layer in text_layers]}, ensure_ascii=True))


def build_inventory(report):
    jobs = []
    resolution = report.get("resolution", {})
    for layer in report["layers"]:
        if "textDescriptor" not in layer:
            continue
        descriptor = layer["textDescriptor"]
        engine = descriptor["EngineData"]["EngineDict"]
        resources = descriptor["EngineData"]["ResourceDict"]
        font_set = resources["FontSet"]
        text = engine["Editor"]["Text"]
        runs = engine["StyleRun"]
        diagnostics = list(layer.get("diagnostics", []))
        if sum(runs["RunLengthArray"]) != len(text.encode("utf-16-be")) // 2:
            diagnostics.append("样式段长度与 UTF-16 文本长度不一致")
        if len(runs["RunArray"]) != len(runs["RunLengthArray"]):
            diagnostics.append("样式段数组长度不一致")
        styles, segments, start = [], [], 0
        for run, length in zip(runs["RunArray"], runs["RunLengthArray"]):
            style = run["StyleSheet"]["StyleSheetData"]
            segment = text.encode("utf-16-be")[start * 2:(start + length) * 2].decode("utf-16-be")
            font_name = font_set[style["Font"]]["Name"]
            styles.append(style)
            segments.append(dict(start=start, length=length, text=segment, font=font_name, fontSize=style["FontSize"], fauxBold=style.get("FauxBold"), tracking=style.get("Tracking"), fill=style.get("FillColor")))
            start += length
        visible_text = text[:-1] if text.endswith("\r") else text
        if descriptor["Txt "].rstrip("\x00") != visible_text:
            diagnostics.append("Descriptor 与 EngineData 文字不一致")
        fonts = sorted({s["font"] for s in segments if s["text"].replace("\r", "")})
        transform = layer["transform"]
        a, b, c, d, tx, ty = transform
        sx, sy = math.hypot(a, b), math.hypot(c, d)
        if abs(a - d) > 1e-5 or abs(b) > 1e-5 or abs(c) > 1e-5:
            diagnostics.append("变换不是本次验证支持的正向均匀缩放")
        if resolution.get("horizontal") != 72 or resolution.get("vertical") != 72:
            diagnostics.append("当前探针只验证 72 DPI，不推测其他分辨率换算")
        paragraphs = engine["ParagraphRun"]["RunArray"]
        justification = paragraphs[0]["ParagraphSheet"]["Properties"]["Justification"]
        if len(paragraphs) != 1 or "\r" in visible_text or justification not in (0, 2):
            diagnostics.append("当前探针只验证单行左对齐或居中点文本")
        first = styles[0]
        if any(s != first for s in styles[1:]) and len(fonts) == 1:
            diagnostics.append("存在不同样式段，不使用统一 TMP 样式猜测")
        effects = layer.get("effects", {})
        active = []
        if effects.get("masterFXSwitch", False):
            for key, value in effects.items():
                for candidate in value if isinstance(value, list) else [value]:
                    if isinstance(candidate, dict) and candidate.get("enab", False):
                        active.append({"key": key, "value": candidate})
        stroke = effects.get("FrFX", {})
        if active and (len(active) != 1 or active[0]["key"] != "FrFX"):
            diagnostics.append("当前探针不支持外描边之外的活动效果")
        if stroke.get("Styl", {}).get("value") != "OutF" or stroke.get("PntT", {}).get("value") != "SClr":
            diagnostics.append("不是单色外描边")
        values = first["FillColor"]["Values"]
        stroke_color = stroke.get("Clr ", {})
        jobs.append(dict(
            id=layer["id"], name=layer["name"], text=visible_text, rawEngineText=text,
            fonts=fonts, segments=segments, fontSizePoints=first["FontSize"], fontSizeUi=first["FontSize"] * sy,
            fauxBold=first.get("FauxBold", False), tracking=first.get("Tracking", 0), justification=justification,
            transform=transform, originX=tx, originY=ty, rasterRect=layer["rasterRect"],
            color=[values[1], values[2], values[3], values[0]],
            strokePixels=stroke.get("Sz  ", {}).get("value", 0),
            strokeColor=[stroke_color.get(k, 0) / 255 for k in ("Rd  ", "Grn ", "Bl  ")] + [stroke.get("Opct", {}).get("value", 0) / 100],
            effectScalePercent=effects.get("Scl ", {}).get("value"), activeEffects=active,
            eligibleForToxiaProbe=fonts == ["TOXIA"] and not diagnostics,
            diagnostics=diagnostics))
    source = Path(report["source"])
    return dict(source=report["source"], sha256=report["sha256"],
                metaSha256=hashlib.sha256(Path(str(source) + ".meta").read_bytes()).hexdigest() if Path(str(source) + ".meta").exists() else None,
                width=report["width"], height=report["height"], resolution=resolution, jobs=jobs)


def scan_fonts(wanted):
    import os
    from fontTools.ttLib import TTFont, TTCollection

    roots = [Path("C:/Windows/Fonts"), Path(os.environ["LOCALAPPDATA"]) / "Microsoft/Windows/Fonts", Path("Assets")]
    hits, errors, count = [], [], 0
    for root in roots:
        if not root.exists():
            continue
        for path in root.rglob("*"):
            if path.suffix.lower() not in (".ttf", ".otf", ".ttc"):
                continue
            count += 1
            collection = single = None
            try:
                if path.suffix.lower() == ".ttc":
                    collection = TTCollection(path, lazy=True)
                    faces = collection.fonts
                else:
                    single = TTFont(path, lazy=True)
                    faces = [single]
                for index, font in enumerate(faces):
                    names = {item.toUnicode() for item in font["name"].names if item.nameID == 6}
                    matched = wanted & names
                    if matched:
                        hits.append(dict(postScriptNames=sorted(matched), path=str(path), faceIndex=index,
                                         sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                                         family=font["name"].getDebugName(1), style=font["name"].getDebugName(2)))
            except Exception as error:
                errors.append(dict(file=str(path), error=str(error)))
            finally:
                if collection:
                    collection.close()
                elif single:
                    single.close()
    return dict(searchedRoots=[str(root) for root in roots], filesScanned=count, hits=hits,
                missing=sorted(wanted - {name for hit in hits for name in hit["postScriptNames"]}), errors=errors)


if __name__ == "__main__":
    main()
