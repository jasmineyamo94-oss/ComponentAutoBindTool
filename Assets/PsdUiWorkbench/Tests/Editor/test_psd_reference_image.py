"""验证 PNG 原样读取、同条件比较与历史保存预览算法，区分视觉基准来源。"""
import unittest
import struct
import tempfile
import hashlib
from pathlib import Path
import numpy as np
from PIL import Image

from psd_reference_image import (extract_saved_preview, unmatte_merged_rgb, load_reference,
                                 describe_reference, prepare_comparison, PHOTOSHOP_REFERENCE)
from compare_psd_text_appearance import compare, shift_mask


class PsdReferenceTests(unittest.TestCase):
    def test_png_reference_is_read_without_second_unmatte(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "photoshop-export.png"
            original = np.array([[[127, 127, 127, 128], [51, 102, 204, 85]]], dtype=np.uint8)
            Image.fromarray(original).save(path)
            np.testing.assert_array_equal(np.asarray(load_reference(path)), original)
            metadata = describe_reference(path)
            self.assertEqual(metadata["sha256"], hashlib.sha256(path.read_bytes()).hexdigest())
            self.assertEqual(metadata["size"], [2, 1])
            self.assertIn("无去白底", metadata["processing"])

    def test_default_reference_is_native_photoshop_png(self):
        with Image.open(PHOTOSHOP_REFERENCE) as original:
            np.testing.assert_array_equal(np.asarray(load_reference()), np.asarray(original.convert("RGBA")))
        self.assertEqual(describe_reference()["path"], str(PHOTOSHOP_REFERENCE.resolve()))

    def test_both_images_are_composited_on_same_background(self):
        transparent = Image.fromarray(np.array([[[127, 127, 127, 128], [255, 255, 255, 0]]], dtype=np.uint8))
        reference, candidate = prepare_comparison(transparent, transparent)
        np.testing.assert_array_equal(np.asarray(reference), np.asarray(candidate))
        self.assertEqual(reference.getpixel((0, 0)), (79, 79, 79, 255))
        self.assertEqual(reference.getpixel((1, 0)), (31, 31, 31, 255))

    def test_comparison_rejects_different_canvas_sizes(self):
        with self.assertRaisesRegex(ValueError, "尺寸不同"):
            prepare_comparison(Image.new("RGBA", (2, 1)), Image.new("RGBA", (1, 2)))

    def test_diagnostic_shift_drops_pixels_instead_of_wrapping(self):
        mask = np.zeros((3, 4), dtype=bool)
        mask[0, 3] = True
        self.assertFalse(shift_mask(mask, 1, 0).any())
        self.assertFalse(shift_mask(mask, 0, -1).any())
        self.assertFalse(shift_mask(mask, -4, 0).any())
        shifted = shift_mask(mask, -2, 1)
        self.assertTrue(shifted[1, 1])
        self.assertEqual(int(shifted.sum()), 1)

    def test_native_png_self_comparison_has_no_quantity_or_alignment_error(self):
        report = compare(PHOTOSHOP_REFERENCE)
        self.assertEqual(report["meanFillError"], 0)
        self.assertEqual(report["meanBlackError"], 0)
        self.assertTrue(report["rows"])
        for row in report["rows"]:
            self.assertEqual(row["fillIoU"], 1)
            self.assertEqual(row["blackIoU"], 1)

    @staticmethod
    def preview_fixture(version=1, rle=False, layer_count=-1):
        # 夹具只包含本读取器消费的结构，不是可导入 Photoshop 的完整文档。
        long = 8 if version == 2 else 4
        header = struct.pack(">4sH6sHIIHH", b"8BPS", version, b"\0"*6, 4, 1, 2, 8, 3)
        info = (2).to_bytes(long, "big") + struct.pack(">h", layer_count)
        channels = [bytes([127, 51]), bytes([127, 102]), bytes([127, 204]), bytes([128, 255])]
        if rle:
            rows = [b"\x01" + c for c in channels]
            payload = b"\0\1" + b"".join(len(row).to_bytes(4 if version == 2 else 2, "big") for row in rows) + b"".join(rows)
        else:
            payload = b"\0\0" + b"".join(channels)
        return header + b"\0"*8 + len(info).to_bytes(long, "big") + info + payload

    def test_saved_preview_reads_psd_psb_raw_and_rle(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "fixture.psd"
            for version in (1, 2):
                for rle in (False, True):
                    with self.subTest(version=version, rle=rle):
                        path.write_bytes(self.preview_fixture(version, rle))
                        pixels = np.asarray(extract_saved_preview(path))
                        self.assertEqual(pixels[0].tolist(), [[0, 0, 0, 128], [51, 102, 204, 255]])

    def test_saved_preview_rejects_truncation_and_unconfirmed_alpha(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "bad.psd"
            for data in (self.preview_fixture()[:-1], self.preview_fixture(rle=True)[:-1], self.preview_fixture(layer_count=1)):
                path.write_bytes(data)
                with self.assertRaises(ValueError):
                    extract_saved_preview(path)

    def test_current_psd_matches_unmatted_historical_raw_preview(self):
        raw_path = Path("Assets/PsdUiWorkbench/Documentation/PsdTextAudit/StrokeDiagnosis/psd-saved-composite.png")
        actual = extract_saved_preview("Assets/Psd/战斗-主菜单界面.psd")
        expected = unmatte_merged_rgb(Image.open(raw_path).convert("RGBA"))
        np.testing.assert_array_equal(np.asarray(actual), np.asarray(expected))

    def test_black_edge_stored_over_white_is_black_after_unmatte(self):
        raw = Image.fromarray(np.array([[[127, 127, 127, 128]]], dtype=np.uint8), "RGBA")
        fixed = np.asarray(unmatte_merged_rgb(raw))
        self.assertEqual(fixed[0, 0].tolist(), [0, 0, 0, 128])

    def test_colored_edge_recovers_original_without_changing_alpha(self):
        color = np.array([51, 102, 204], dtype=float)
        alpha = 85
        raw_rgb = np.rint(color * alpha / 255 + 255 - alpha).astype(np.uint8)
        raw = Image.fromarray(np.array([[list(raw_rgb) + [alpha]]], dtype=np.uint8), "RGBA")
        fixed = np.asarray(unmatte_merged_rgb(raw))
        np.testing.assert_allclose(fixed[0, 0, :3], color, atol=1)
        self.assertEqual(int(fixed[0, 0, 3]), alpha)

    def test_opaque_pixel_is_unchanged_and_empty_pixel_has_no_nan(self):
        raw = Image.fromarray(np.array([[[25, 50, 75, 255], [255, 255, 255, 0]]], dtype=np.uint8), "RGBA")
        fixed = np.asarray(unmatte_merged_rgb(raw))
        self.assertEqual(fixed[0, 0].tolist(), [25, 50, 75, 255])
        self.assertEqual(fixed[0, 1].tolist(), [0, 0, 0, 0])


if __name__ == "__main__":
    unittest.main()
