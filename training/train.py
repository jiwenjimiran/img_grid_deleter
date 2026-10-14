"""Train an image classifier via transfer learning.

Reads images from data/<class>/, auto-splits into train/validation, fine-tunes
(or feature-extracts from) a pretrained backbone, and saves the best model to
model.pt. The checkpoint embeds the class names and preprocessing config so
predict.py needs no extra setup.
"""

import argparse
import json
import os
import random

import torch
import torch.nn as nn
from PIL import Image
from torch.utils.data import DataLoader
from torchvision import datasets, models, transforms
from preprocessing import LETTERBOX_IMAGENET, build_transform
from training_policy import is_better_checkpoint, should_stop

# Our own trusted images; disable PIL's decompression-bomb guard so very large
# images don't spam DecompressionBombWarning. (ImageFolder decodes via PIL.)
Image.MAX_IMAGE_PIXELS = None

# ImageNet normalization stats — the pretrained backbones expect these.
IMAGENET_MEAN = [0.485, 0.456, 0.406]
IMAGENET_STD = [0.229, 0.224, 0.225]
IMG_SIZE = 224


def pick_device():
    if torch.cuda.is_available():
        return torch.device("cuda")
    if torch.backends.mps.is_available():
        return torch.device("mps")
    return torch.device("cpu")


def build_model(arch, num_classes, finetune):
    """Load a pretrained backbone and swap in a fresh head for our classes."""
    if arch == "resnet18":
        model = models.resnet18(weights=models.ResNet18_Weights.DEFAULT)
        in_features = model.fc.in_features
        head_attr = "fc"
    elif arch == "mobilenet_v3_small":
        model = models.mobilenet_v3_small(
            weights=models.MobileNet_V3_Small_Weights.DEFAULT
        )
        in_features = model.classifier[-1].in_features
        head_attr = None  # handled below
    else:
        raise ValueError(f"Unknown arch: {arch}")

    # Feature extraction: freeze everything, then the new head (added next)
    # trains with requires_grad=True by default.
    if not finetune:
        for param in model.parameters():
            param.requires_grad = False

    if arch == "resnet18":
        model.fc = nn.Linear(in_features, num_classes)
    else:
        model.classifier[-1] = nn.Linear(in_features, num_classes)

    return model


def run_epoch(model, loader, criterion, optimizer, device, train, class_count):
    model.train() if train else model.eval()
    total_loss, correct, count = 0.0, 0, 0
    confusion = [[0 for _ in range(class_count)] for _ in range(class_count)]

    torch.set_grad_enabled(train)
    for images, labels in loader:
        images, labels = images.to(device), labels.to(device)
        if train:
            optimizer.zero_grad()
        outputs = model(images)
        loss = criterion(outputs, labels)
        if train:
            loss.backward()
            optimizer.step()

        total_loss += loss.item() * images.size(0)
        predictions = outputs.argmax(1)
        correct += (predictions == labels).sum().item()
        for truth, prediction in zip(labels.detach().cpu().tolist(),
                                     predictions.detach().cpu().tolist()):
            confusion[truth][prediction] += 1
        count += images.size(0)
    torch.set_grad_enabled(True)

    return total_loss / count, correct / count, confusion


def load_or_create_split(dataset, manifest_path, val_split, split_seed):
    relative = [os.path.relpath(path, dataset.root).replace("\\", "/")
                for path, _ in dataset.samples]
    by_class = {name: [] for name in dataset.classes}
    for index, (_, label) in enumerate(dataset.samples):
        by_class[dataset.classes[label]].append(index)

    if os.path.exists(manifest_path):
        with open(manifest_path, encoding="utf-8") as source:
            manifest = json.load(source)
        if manifest.get("classes") != dataset.classes:
            raise ValueError("Split manifest classes do not match the dataset")
        path_to_index = {path: index for index, path in enumerate(relative)}
        try:
            train_indices = [path_to_index[path] for path in manifest["train"]]
            val_indices = [path_to_index[path] for path in manifest["validation"]]
        except KeyError as error:
            raise ValueError(f"Split manifest references a missing image: {error}") from error
        if set(train_indices) & set(val_indices) or len(train_indices) + len(val_indices) != len(dataset):
            raise ValueError("Split manifest must contain every dataset image exactly once")
        return train_indices, val_indices

    rng = random.Random(split_seed)
    train_indices, val_indices = [], []
    for indices in by_class.values():
        shuffled = list(indices)
        rng.shuffle(shuffled)
        val_count = max(1, int(len(shuffled) * val_split))
        val_indices.extend(shuffled[:val_count])
        train_indices.extend(shuffled[val_count:])
    train_indices.sort()
    val_indices.sort()
    manifest = {
        "version": 1,
        "splitSeed": split_seed,
        "validationFraction": val_split,
        "classes": dataset.classes,
        "train": [relative[index] for index in train_indices],
        "validation": [relative[index] for index in val_indices],
    }
    os.makedirs(os.path.dirname(os.path.abspath(manifest_path)), exist_ok=True)
    temporary = manifest_path + ".tmp"
    with open(temporary, "w", encoding="utf-8") as output:
        json.dump(manifest, output, indent=2, sort_keys=True)
        output.write("\n")
    os.replace(temporary, manifest_path)
    return train_indices, val_indices


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data-dir", default="data")
    parser.add_argument("--out", default="model.pt")
    parser.add_argument("--arch", default="resnet18",
                        choices=["resnet18", "mobilenet_v3_small"])
    parser.add_argument("--epochs", type=int, default=10)
    parser.add_argument("--patience", type=int, default=3,
                        help="stop after this many epochs without validation improvement; 0 disables")
    parser.add_argument("--batch-size", type=int, default=32)
    parser.add_argument("--img-size", type=int, default=IMG_SIZE,
                        help=f"square input size the model sees (default {IMG_SIZE}; "
                             f"try 384 or 512 for fine detail like grid lines)")
    parser.add_argument("--lr", type=float, default=1e-3)
    parser.add_argument("--val-split", type=float, default=0.2)
    parser.add_argument("--finetune", action="store_true",
                        help="train the whole network, not just the head")
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--split-seed", type=int, default=42)
    parser.add_argument("--split-manifest", default="split-manifest.json")
    parser.add_argument("--summary-output")
    args = parser.parse_args()

    torch.manual_seed(args.seed)
    device = pick_device()
    print(f"Device: {device}")

    if not os.path.isdir(args.data_dir):
        raise SystemExit(f"No data directory at '{args.data_dir}'. "
                         f"Add images under {args.data_dir}/<class>/ first.")

    train_tf = build_transform(args.img_size, IMAGENET_MEAN, IMAGENET_STD,
                               LETTERBOX_IMAGENET, training=True)
    val_tf = build_transform(args.img_size, IMAGENET_MEAN, IMAGENET_STD,
                             LETTERBOX_IMAGENET, training=False)
    print(f"Model input size: {args.img_size}x{args.img_size}")

    # One ImageFolder per transform so train/val get different augmentation,
    # then split by index so the same images stay in the same partition.
    full_train = datasets.ImageFolder(args.data_dir, transform=train_tf)
    full_val = datasets.ImageFolder(args.data_dir, transform=val_tf)
    class_names = full_train.classes
    if len(class_names) < 2:
        raise SystemExit(f"Need at least 2 class folders under '{args.data_dir}', "
                         f"found {len(class_names)}: {class_names}")
    print(f"Classes: {class_names}  ({len(full_train)} images)")

    train_indices, val_indices = load_or_create_split(
        full_train, args.split_manifest, args.val_split, args.split_seed)
    train_set = torch.utils.data.Subset(full_train, train_indices)
    val_set = torch.utils.data.Subset(full_val, val_indices)
    print(f"Train: {len(train_set)}   Val: {len(val_set)}")

    train_loader = DataLoader(train_set, batch_size=args.batch_size, shuffle=True)
    val_loader = DataLoader(val_set, batch_size=args.batch_size, shuffle=False)

    model = build_model(args.arch, len(class_names), args.finetune).to(device)
    criterion = nn.CrossEntropyLoss()
    trainable = [p for p in model.parameters() if p.requires_grad]
    optimizer = torch.optim.Adam(trainable, lr=args.lr)

    best_acc = float("-inf")
    best_loss = float("inf")
    best_false_grids = float("inf")
    best_grid_correct = -1
    best_grid_total = 0
    best_epoch = 0
    epochs_without_improvement = 0
    for epoch in range(1, args.epochs + 1):
        print(f"Starting epoch {epoch}/{args.epochs}", flush=True)
        tr_loss, tr_acc, _ = run_epoch(model, train_loader, criterion, optimizer,
                                       device, train=True, class_count=len(class_names))
        va_loss, va_acc, confusion = run_epoch(model, val_loader, criterion, optimizer,
                                               device, train=False, class_count=len(class_names))
        grid_index = class_names.index("grid")
        not_grid_index = class_names.index("not_grid")
        false_grids = confusion[not_grid_index][grid_index]
        grid_correct = confusion[grid_index][grid_index]
        grid_total = sum(confusion[grid_index])
        print(f"Epoch {epoch:2d}/{args.epochs}  "
              f"train loss {tr_loss:.3f} acc {tr_acc:.3f}  |  "
              f"val loss {va_loss:.3f} acc {va_acc:.3f}  |  "
              f"false grids {false_grids} grid recall {grid_correct}/{grid_total}")

        if is_better_checkpoint(false_grids, grid_correct, va_loss,
                                best_false_grids, best_grid_correct, best_loss):
            best_acc = va_acc
            best_loss = va_loss
            best_false_grids = false_grids
            best_grid_correct = grid_correct
            best_grid_total = grid_total
            best_epoch = epoch
            epochs_without_improvement = 0
            torch.save({
                "state_dict": model.state_dict(),
                "arch": args.arch,
                "class_names": class_names,
                "img_size": args.img_size,
                "mean": IMAGENET_MEAN,
                "std": IMAGENET_STD,
                "preprocess": LETTERBOX_IMAGENET,
                "seed": args.seed,
                "split_seed": args.split_seed,
                "validation": {
                    "false_grids": false_grids,
                    "grid_correct": grid_correct,
                    "grid_total": grid_total,
                    "accuracy": va_acc,
                    "loss": va_loss,
                    "epoch": epoch,
                },
            }, args.out)
        else:
            epochs_without_improvement += 1

        if should_stop(epochs_without_improvement, args.patience):
            print(f"Early stopping after epoch {epoch}: no validation improvement "
                  f"for {args.patience} epochs.", flush=True)
            break

    print(f"Best checkpoint: epoch {best_epoch}/{args.epochs}, "
          f"val acc {best_acc:.6f}, val loss {best_loss:.6f}", flush=True)
    print(f"Safety metrics: false grids {best_false_grids}, "
          f"grid recall {best_grid_correct}/{best_grid_total}", flush=True)
    print(f"Saved to {args.out}", flush=True)
    if args.summary_output:
        summary = {
            "seed": args.seed, "splitSeed": args.split_seed,
            "epoch": best_epoch, "validationAccuracy": best_acc,
            "validationLoss": best_loss, "falseGrids": best_false_grids,
            "gridCorrect": best_grid_correct, "gridTotal": best_grid_total,
            "checkpoint": os.path.abspath(args.out),
        }
        output_path = os.path.abspath(args.summary_output)
        os.makedirs(os.path.dirname(output_path), exist_ok=True)
        temporary = output_path + ".tmp"
        with open(temporary, "w", encoding="utf-8") as output:
            json.dump(summary, output, indent=2, sort_keys=True)
            output.write("\n")
        os.replace(temporary, output_path)


if __name__ == "__main__":
    main()
