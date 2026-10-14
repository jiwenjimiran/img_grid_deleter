"""Export the final PyTorch image-grid checkpoint to the extension's ONNX asset."""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
import torch
from PIL import Image
from torchvision import models, transforms
from torchvision.transforms import functional as TF

LEGACY_CENTER_CROP = "legacy_center_crop_v1"
LETTERBOX_IMAGENET = "letterbox_imagenet_v1"


class Letterbox:
    def __init__(self, size: int, mean: list[float]):
        self.size = size
        self.fill = tuple(round(channel * 255) for channel in mean)

    def __call__(self, image: Image.Image) -> Image.Image:
        width, height = image.size
        scale = min(self.size / width, self.size / height)
        resized_width = max(1, min(self.size, round(width * scale)))
        resized_height = max(1, min(self.size, round(height * scale)))
        resized = TF.resize(image, [resized_height, resized_width],
                            interpolation=transforms.InterpolationMode.BILINEAR,
                            antialias=True)
        canvas = Image.new("RGB", (self.size, self.size), self.fill)
        canvas.paste(resized, ((self.size - resized_width) // 2,
                               (self.size - resized_height) // 2))
        return canvas


def build_model(checkpoint: dict) -> torch.nn.Module:
    architecture = checkpoint["arch"]
    classes = checkpoint["class_names"]
    if architecture == "resnet18":
        model = models.resnet18(weights=None)
        model.fc = torch.nn.Linear(model.fc.in_features, len(classes))
    elif architecture == "mobilenet_v3_small":
        model = models.mobilenet_v3_small(weights=None)
        model.classifier[-1] = torch.nn.Linear(model.classifier[-1].in_features, len(classes))
    else:
        raise ValueError(f"Unsupported architecture: {architecture}")
    model.load_state_dict(checkpoint["state_dict"])
    return model.eval()


def preprocessing(checkpoint: dict):
    size = int(checkpoint["img_size"])
    version = checkpoint.get("preprocess", LEGACY_CENTER_CROP)
    if version == LETTERBOX_IMAGENET:
        geometry = Letterbox(size, checkpoint["mean"])
    elif version == LEGACY_CENTER_CROP:
        geometry = transforms.Compose([
            transforms.Resize(int(size * 256 / 224)),
            transforms.CenterCrop(size),
        ])
    else:
        raise ValueError(f"Unsupported preprocessing version: {version}")
    return transforms.Compose([
        geometry,
        transforms.ToTensor(),
        transforms.Normalize(checkpoint["mean"], checkpoint["std"]),
    ])


def verify(source: Path, output: Path, checkpoint: dict, model: torch.nn.Module, limit: int) -> None:
    import onnxruntime as ort

    candidates = sorted(path for path in source.rglob("*") if path.suffix.lower() in {".jpg", ".jpeg", ".png", ".webp", ".bmp"})[:limit]
    session = ort.InferenceSession(str(output), providers=["CPUExecutionProvider"])
    prepare = preprocessing(checkpoint)
    largest_delta = 0.0
    for path in candidates:
        with Image.open(path) as image:
            tensor = prepare(image.convert("RGB")).unsqueeze(0)
        with torch.inference_mode():
            expected = model(tensor).detach().cpu().numpy()
        actual = session.run(["logits"], {"input": tensor.numpy()})[0]
        largest_delta = max(largest_delta, float(np.max(np.abs(expected - actual))))
        if int(expected.argmax(1)[0]) != int(actual.argmax(1)[0]):
            raise RuntimeError(f"ONNX label differs from PyTorch for {path}")
    print(f"Verified {len(candidates)} images; largest logit delta: {largest_delta:.8f}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path, help="Path to the final model.pt checkpoint")
    parser.add_argument("output", type=Path, help="Destination .onnx path")
    parser.add_argument("--verify-dir", type=Path)
    parser.add_argument("--verify-limit", type=int, default=64)
    args = parser.parse_args()

    checkpoint = torch.load(args.source, map_location="cpu", weights_only=False)
    if checkpoint.get("class_names") != ["grid", "not_grid"] or int(checkpoint.get("img_size", 0)) != 384:
        raise ValueError("Expected the final 384px ['grid', 'not_grid'] checkpoint")
    model = build_model(checkpoint)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    dummy = torch.zeros(1, 3, 384, 384)
    torch.onnx.export(
        model,
        dummy,
        args.output,
        input_names=["input"],
        output_names=["logits"],
        dynamic_axes={"input": {0: "batch"}, "logits": {0: "batch"}},
        opset_version=17,
        dynamo=False,
    )
    print(f"Exported {args.output} ({args.output.stat().st_size:,} bytes)")
    sidecar = args.output.with_suffix(".preprocess")
    sidecar.write_text(checkpoint.get("preprocess", LEGACY_CENTER_CROP) + "\n", encoding="ascii")
    print(f"Preprocessing: {sidecar.read_text(encoding='ascii').strip()}")
    if args.verify_dir:
        verify(args.verify_dir, args.output, checkpoint, model, args.verify_limit)


if __name__ == "__main__":
    main()
