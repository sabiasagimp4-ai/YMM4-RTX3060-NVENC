import importlib.util
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('color_matrix_check', Path(__file__).resolve().parents[2] / 'tools/color-matrix-check.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ColorMatrixTests(unittest.TestCase):
    def test_reference_values(self):
        # White and black land on the nominal limits; BT.601 limited red is (81, 90, 240) per Rec. 601.
        self.assertEqual([round(v) for v in module.to_ycbcr((255, 255, 255), 'bt709', 'limited')], [235, 128, 128])
        self.assertEqual([round(v) for v in module.to_ycbcr((0, 0, 0), 'bt601', 'full')], [0, 128, 128])
        self.assertEqual([round(v) for v in module.to_ycbcr((255, 0, 0), 'bt601', 'limited')], [81, 90, 240])

    def test_round_trip(self):
        for matrix in module.MATRICES:
            for color_range in module.RANGES:
                rgb = (200, 40, 90)
                back = module.to_rgb(module.to_ycbcr(rgb, matrix, color_range), matrix, color_range)
                for expected, actual in zip(rgb, back):
                    self.assertAlmostEqual(expected, actual, places=9)

    def test_fit_finds_the_conversion_used(self):
        for matrix in module.MATRICES:
            for color_range in module.RANGES:
                rows = []
                for frame in range(30):
                    for pattern in ('managed', 'native'):
                        rgb = module.source_means(pattern, frame, 320, 180)
                        rows.append((rgb, tuple(v + 0.3 for v in module.to_ycbcr(rgb, matrix, color_range))))
                best = module.fit(rows)
                self.assertEqual((best[0]['Matrix'], best[0]['Range']), (matrix, color_range))
                self.assertGreater(best[1]['Rms'] - best[0]['Rms'], 1.5)

    def test_native_pattern_wraps_like_the_smoke_test(self):
        red, green, blue = module.source_means('native', 29, 320, 180)
        self.assertEqual(red, 203)
        self.assertAlmostEqual(blue, sum((x + 29) % 256 for x in range(320)) / 320)
        self.assertAlmostEqual(green, sum(y + 29 for y in range(180)) / 180)

    def test_plane_means_read_planar_and_interleaved_chroma(self):
        luma = bytes([10]) * 16
        self.assertEqual(module.plane_means(luma + bytes([20] * 4 + [30] * 4), 4, 4, 'yuv420p'), (10, 20, 30))
        self.assertEqual(module.plane_means(luma + bytes([20, 30] * 4), 4, 4, 'nv12'), (10, 20, 30))


if __name__ == '__main__':
    unittest.main()
