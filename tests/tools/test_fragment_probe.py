"""The test-only fragment experiment must use the product serializer verbatim apart from its hook."""
from pathlib import Path
import unittest


class FragmentProbeSerializer(unittest.TestCase):
    def test_experimental_serializer_keeps_product_byte_witnesses(self):
        root = Path(__file__).resolve().parents[2]
        product = (root / "NVEncVideoWriterPlugin/FrameDescriptionJson.cs").read_text(encoding="utf-8-sig")
        experiment = (root / "tests/HostCacheProbe/ExperimentalFrameDescriptionJson.cs").read_text(encoding="utf-8-sig")
        experiment = experiment.removeprefix(
            "// Test-only copy of FrameDescriptionJson with the fragment converter hook.\n"
            "// tests/tools/test_fragment_probe.py enforces equality after removing the three hook lines.\n")
        experiment = experiment.replace(
            "    // A tracker supplies this only for its own scene description. Fresh full descriptions have no converter.\n"
            "    [ThreadStatic] internal static JsonConverter? ItemFragments = null;\n", "", 1)
        experiment = experiment.replace(
            "        if (ItemFragments is { } fragments) settings.Converters.Add(fragments);\n", "", 1)
        self.assertEqual(product, experiment,
                         "The experimental serializer diverged from product payload, precision or clone guards")


if __name__ == "__main__":
    unittest.main()
