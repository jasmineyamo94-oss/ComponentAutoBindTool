"""文本审计格式测试；纯内存夹具不触发 Unity 导入。"""

import io
import struct
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from psd_text_audit import EngineReader, FormatError, Reader, audit, build_inventory


def unicode_string(text):
    data = text.encode("utf-16-be")
    return struct.pack(">I", len(data) // 2) + data


def identifier(text):
    data = text.encode("ascii")
    return struct.pack(">I", 0 if len(data) == 4 else len(data)) + data


def descriptor(items):
    return unicode_string("") + identifier("null") + struct.pack(">I", len(items)) + b"".join(identifier(key) + kind + value for key, kind, value in items)


def engine_string(text):
    data = b"\xfe\xff" + text.encode("utf-16-be")
    escaped = data.replace(b"\\", b"\\\\").replace(b"(", b"\\(").replace(b")", b"\\)")
    return b"(" + escaped + b")"


def fixture(large=False, unknown=True):
    def block(key, data):
        return b"8BIM" + key + struct.pack(">I", len(data)) + data + b"\0" * (len(data) % 2)
    engine = b"<< /EngineDict << /Editor << /Text " + engine_string("A)\\\r") + b" >> >> >>"
    text = descriptor([("Txt ", b"TEXT", unicode_string("A)\\\0")), ("EngineData", b"tdta", struct.pack(">I", len(engine)) + engine)])
    tysh = struct.pack(">H6dHI", 1, 1, 0, 0, 1, 10.5, 20.25, 50, 16) + text
    tysh += struct.pack(">HI", 1, 16) + descriptor([]) + bytes(16)
    extra = bytes(8) + b"\x01A\x00\x00" + block(b"lyid", struct.pack(">i", 1091))
    if unknown:
        extra += block(b"zzzz", b"odd")
    extra += block(b"TySh", tysh)
    record = struct.pack(">4iHh", 0, 0, 8, 16, 1, -1)
    record += (struct.pack(">Q", 4) if large else struct.pack(">I", 4))
    record += b"8BIMnorm\xff\x00\x00\x00" + struct.pack(">I", len(extra)) + extra
    layers = struct.pack(">h", 1) + record + b"\x00\x00\xaa\xaa"
    pack_length = lambda length: struct.pack(">Q" if large else ">I", length)
    mask = pack_length(len(layers)) + layers + bytes(4)
    return struct.pack(">4sH6sHIIHH", b"8BPS", 2 if large else 1, bytes(6), 3, 64, 64, 8, 3) + bytes(8) + pack_length(len(mask)) + mask


def memory_audit(data):
    with patch.object(Path, "read_bytes", return_value=data), patch.object(Path, "open", side_effect=lambda *a, **k: io.BytesIO(data)), patch.object(Path, "stat", return_value=SimpleNamespace(st_size=len(data))):
        return audit("memory.psd")


class AuditTests(unittest.TestCase):
    def test_engine_unicode_escapes_arrays_and_booleans(self):
        text = "A)\\(中😀\r"
        parsed = EngineReader(b"<< /Text " + engine_string(text) + b" /Items [ 1 -2 .5 true false ] >>").parse()
        self.assertEqual(parsed["Text"], text)
        self.assertEqual(parsed["Items"], [1, -2, 0.5, True, False])

    def test_engine_rejects_truncation_and_duplicate_keys(self):
        for data in (b"<< /A (", b"<< /A [ 1", b"<< /A 1 /A 2 >>", b"<< /A unknown >>"):
            with self.assertRaises(FormatError):
                EngineReader(data).parse()

    def test_descriptor_preserves_units_and_nested_values(self):
        data = descriptor([("Sz  ", b"UntF", b"#Pxl" + struct.pack(">d", 3)), ("Txt ", b"TEXT", unicode_string("😀"))])
        result = Reader(data).descriptor()
        self.assertEqual(result["Sz  "], {"unit": "#Pxl", "value": 3})
        self.assertEqual(result["Txt "], "😀")

    def test_unknown_descriptor_is_not_silently_guessed(self):
        with self.assertRaises(FormatError):
            Reader(descriptor([("test", b"BAD!", b"")])).descriptor()

    def test_psd_and_psb_skip_pixel_data_and_odd_unknown_block(self):
        for large in (False, True):
            result = memory_audit(fixture(large))
            layer = result["layers"][0]
            self.assertEqual(layer["id"], 1091)
            self.assertEqual(layer["textDescriptor"]["EngineData"]["EngineDict"]["Editor"]["Text"], "A)\\\r")
            self.assertNotIn("diagnostics", layer)
            self.assertEqual(result["skippedPixelBytes"], 4)
            self.assertFalse(result["pixelPayloadDecoded"])

    def test_psd_truncation_is_rejected(self):
        data = fixture()
        for length in range(len(data)):
            with self.assertRaises((FormatError, struct.error)):
                memory_audit(data[:length])

    def test_actual_battle_inventory_matches_ids_runs_and_disabled_effects(self):
        result = audit("Assets/Psd/战斗-主菜单界面.psd")
        inventory = build_inventory(result)
        self.assertEqual(len(inventory["jobs"]), 20)
        self.assertEqual(sum(job["eligibleForToxiaProbe"] for job in inventory["jobs"]), 13)
        jobs = {job["id"]: job for job in inventory["jobs"]}
        self.assertEqual(jobs[1091]["text"], "Energy Level")
        self.assertAlmostEqual(jobs[1091]["fontSizeUi"], 34, places=4)
        self.assertEqual(jobs[936]["segments"][1]["font"], "Rubik-Medium")
        self.assertEqual(jobs[936]["segments"][1]["text"], "/")
        self.assertTrue(all([effect["key"] for effect in job["activeEffects"]] == ["FrFX"] for job in inventory["jobs"]))


if __name__ == "__main__":
    unittest.main()
