"""Versioned image preprocessing shared by training, evaluation, and prediction."""

from PIL import Image
from torchvision import transforms
from torchvision.transforms import functional as TF

LEGACY_CENTER_CROP = "legacy_center_crop_v1"
LETTERBOX_IMAGENET = "letterbox_imagenet_v1"


class Letterbox:
    def __init__(self, size, mean):
        self.size = int(size)
        self.fill = tuple(round(channel * 255) for channel in mean)

    def __call__(self, image):
        width, height = image.size
        if width <= 0 or height <= 0:
            raise ValueError("Image dimensions must be positive")
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


def preprocessing_version(checkpoint):
    return checkpoint.get("preprocess", LEGACY_CENTER_CROP)


def build_transform(size, mean, std, version, training=False):
    if version == LEGACY_CENTER_CROP:
        geometry = [
            transforms.RandomResizedCrop(size, scale=(0.7, 1.0))
            if training else transforms.Compose([
                transforms.Resize(int(size * 256 / 224)),
                transforms.CenterCrop(size),
            ])
        ]
    elif version == LETTERBOX_IMAGENET:
        geometry = [Letterbox(size, mean)]
    else:
        raise ValueError(f"Unsupported preprocessing version: {version}")

    augmentation = []
    if training:
        augmentation = [
            transforms.RandomHorizontalFlip(),
            transforms.ColorJitter(brightness=0.08, contrast=0.08,
                                   saturation=0.05, hue=0.01),
        ]
    return transforms.Compose([
        *geometry, *augmentation, transforms.ToTensor(),
        transforms.Normalize(mean, std),
    ])
