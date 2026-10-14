import json
import tempfile
import unittest
from pathlib import Path

from PIL import Image
from torchvision import datasets

from preprocessing import Letterbox, LEGACY_CENTER_CROP, preprocessing_version
from train import load_or_create_split


class PreprocessingTests(unittest.TestCase):
    def test_letterbox_preserves_landscape_and_portrait_images(self):
        transform = Letterbox(384, [0.485, 0.456, 0.406])
        landscape = transform(Image.new("RGB", (800, 400), (255, 0, 0)))
        portrait = transform(Image.new("RGB", (400, 800), (0, 255, 0)))
        self.assertEqual(landscape.size, (384, 384))
        self.assertEqual(portrait.size, (384, 384))
        self.assertEqual(landscape.getpixel((192, 0)), (124, 116, 104))
        self.assertEqual(landscape.getpixel((192, 192)), (255, 0, 0))
        self.assertEqual(portrait.getpixel((0, 192)), (124, 116, 104))
        self.assertEqual(portrait.getpixel((192, 192)), (0, 255, 0))

    def test_old_checkpoints_default_to_legacy_preprocessing(self):
        self.assertEqual(preprocessing_version({}), LEGACY_CENTER_CROP)

    def test_split_manifest_is_stratified_and_reused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for label, color in (("grid", "red"), ("not_grid", "blue")):
                folder = root / "data" / label
                folder.mkdir(parents=True)
                for index in range(10):
                    Image.new("RGB", (16, 16), color).save(folder / f"{index}.png")
            dataset = datasets.ImageFolder(root / "data")
            manifest = root / "split.json"
            first = load_or_create_split(dataset, str(manifest), 0.2, 42)
            second = load_or_create_split(dataset, str(manifest), 0.2, 999)
            self.assertEqual(first, second)
            saved = json.loads(manifest.read_text(encoding="utf-8"))
            self.assertEqual(len(saved["validation"]), 4)
            self.assertEqual(sum(path.startswith("grid/") for path in saved["validation"]), 2)
            self.assertEqual(sum(path.startswith("not_grid/") for path in saved["validation"]), 2)
            self.assertFalse(set(saved["train"]) & set(saved["validation"]))


if __name__ == "__main__":
    unittest.main()
