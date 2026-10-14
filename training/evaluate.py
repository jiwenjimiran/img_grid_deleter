"""Evaluate a trained model against a labeled folder, live.

Point it at a directory whose sub-folders are the true class labels
(the same layout as data/), e.g.:

    prediction/
      grid/        <- images that really ARE grids
      not_grid/    <- images that really are NOT grids

It runs the model over every image, prints each result as it goes
(green check / red cross + confidence), and finishes with a per-class
breakdown, a confusion matrix, and overall accuracy.

Every run also appends one summary row to eval_log.csv so you can track
accuracy across retrains over time. Use --detail to also dump a per-image
CSV for the run.

Usage:
    python evaluate.py                      # defaults to ./prediction
    python evaluate.py some_other_dir
    python evaluate.py --model model.pt
    python evaluate.py --detail             # also write per-image CSV
    python evaluate.py --no-log             # don't append to eval_log.csv
"""

import argparse
import csv
import json
import os
import sys
from datetime import datetime

import torch
import torch.nn as nn
from PIL import Image
from torchvision import models
from preprocessing import build_transform, preprocessing_version

# Our own trusted images; disable PIL's decompression-bomb guard so very large
# images don't spam DecompressionBombWarning.
Image.MAX_IMAGE_PIXELS = None

# Windows consoles default to cp1252, which crashes on emoji/non-Latin filenames.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

IMG_EXTS = {".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"}

# ANSI colors — disabled automatically if output isn't a terminal.
_USE_COLOR = sys.stdout.isatty()
def _c(code, text):
    return f"\033[{code}m{text}\033[0m" if _USE_COLOR else text
def green(t): return _c("32", t)
def red(t):   return _c("31", t)
def dim(t):   return _c("2", t)
def bold(t):  return _c("1", t)


def pick_device():
    if torch.cuda.is_available():
        return torch.device("cuda")
    if torch.backends.mps.is_available():
        return torch.device("mps")
    return torch.device("cpu")


def build_model(arch, num_classes):
    if arch == "resnet18":
        model = models.resnet18(weights=None)
        model.fc = nn.Linear(model.fc.in_features, num_classes)
    elif arch == "mobilenet_v3_small":
        model = models.mobilenet_v3_small(weights=None)
        model.classifier[-1] = nn.Linear(
            model.classifier[-1].in_features, num_classes)
    else:
        raise ValueError(f"Unknown arch: {arch}")
    return model


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("data_dir", nargs="?", default="prediction",
                        help="folder of labeled sub-folders (default: prediction)")
    parser.add_argument("--model", default="model.pt")
    parser.add_argument("--log", default="eval_log.csv",
                        help="summary CSV appended once per run (default: eval_log.csv)")
    parser.add_argument("--no-log", action="store_true",
                        help="don't append a summary row to the log")
    parser.add_argument("--detail", action="store_true",
                        help="also write a per-image CSV for this run")
    parser.add_argument("--quiet", action="store_true",
                        help="suppress per-image output while retaining the summary")
    parser.add_argument("--json-output",
                        help="write machine-readable evaluation metrics to this path")
    args = parser.parse_args()

    if not os.path.isdir(args.data_dir):
        raise SystemExit(f"No such directory: {args.data_dir}")
    if not os.path.exists(args.model):
        raise SystemExit(f"No model at '{args.model}'. Train first: python train.py")

    device = pick_device()
    ckpt = torch.load(args.model, map_location=device)
    class_names = ckpt["class_names"]
    class_to_idx = {c: i for i, c in enumerate(class_names)}

    model = build_model(ckpt["arch"], len(class_names)).to(device)
    model.load_state_dict(ckpt["state_dict"])
    model.eval()

    img_size = int(ckpt["img_size"])
    tf = build_transform(img_size, ckpt["mean"], ckpt["std"],
                         preprocessing_version(ckpt), training=False)

    print(bold(f"\nEvaluating '{args.model}' on '{args.data_dir}'  "
               f"(device: {device})"))
    print(dim(f"Classes: {class_names}\n"))

    run_ts = datetime.now().isoformat(timespec="seconds")

    # confusion[true][pred] = count
    confusion = {t: {p: 0 for p in class_names} for t in class_names}
    detail_rows = []  # per-image rows, written only with --detail
    n_total = correct = skipped = 0

    # Only evaluate sub-folders whose name is a known class label.
    true_labels = [d for d in sorted(os.listdir(args.data_dir))
                   if os.path.isdir(os.path.join(args.data_dir, d))]

    for true_label in true_labels:
        if true_label not in class_to_idx:
            print(dim(f"(skipping '{true_label}/': not a model class)"))
            continue
        folder = os.path.join(args.data_dir, true_label)
        files = sorted(f for f in os.listdir(folder)
                       if os.path.splitext(f)[1].lower() in IMG_EXTS)
        if not args.quiet:
            print(bold(f"== {true_label}/  ({len(files)} images) =="))

        for fname in files:
            path = os.path.join(folder, fname)
            try:
                img = Image.open(path).convert("RGB")
            except (OSError, ValueError) as e:
                if not args.quiet:
                    print(dim(f"  ?  {fname}: could not open ({e})"))
                skipped += 1
                continue

            x = tf(img).unsqueeze(0).to(device)
            with torch.no_grad():
                probs = torch.softmax(model(x), dim=1)[0]
            idx = int(probs.argmax())
            pred = class_names[idx]
            conf = float(probs[idx])

            confusion[true_label][pred] += 1
            n_total += 1
            is_right = pred == true_label
            detail_rows.append({
                "timestamp": run_ts, "true": true_label, "file": fname,
                "pred": pred, "confidence": round(conf, 4),
                "correct": int(is_right),
            })
            if is_right:
                correct += 1
                mark = green("[OK] ")
            else:
                mark = red("[XX] ")

            running = correct / n_total
            line = (f"  {mark} {fname:<28}  pred={pred:<10} "
                    f"{conf*100:5.1f}%   "
                    + dim(f"running acc {running*100:5.1f}% ({correct}/{n_total})"))
            if not args.quiet:
                print(line, flush=True)
        if not args.quiet:
            print()

    # ---- summary ----
    if n_total == 0:
        print(red("No images evaluated."))
        return

    print(bold("================ SUMMARY ================"))
    print(f"Overall accuracy: {bold(f'{correct/n_total*100:.1f}%')} "
          f"({correct}/{n_total})" + (f"   [{skipped} skipped]" if skipped else ""))

    print("\nPer-class:")
    for t in class_names:
        row_total = sum(confusion[t].values())
        if row_total == 0:
            continue
        right = confusion[t][t]
        print(f"  {t:<12} {right}/{row_total}  ({right/row_total*100:5.1f}%)")

    metrics = {
        "model": os.path.abspath(args.model),
        "dataDir": os.path.abspath(args.data_dir),
        "total": n_total,
        "correct": correct,
        "accuracy": correct / n_total,
        "skipped": skipped,
        "preprocess": preprocessing_version(ckpt),
        "perClass": {},
        "confusion": confusion,
    }
    for label in class_names:
        row_total = sum(confusion[label].values())
        right = confusion[label][label]
        metrics["perClass"][label] = {
            "total": row_total,
            "correct": right,
            "accuracy": right / row_total if row_total else 0,
        }

    if args.json_output:
        output_path = os.path.abspath(args.json_output)
        os.makedirs(os.path.dirname(output_path), exist_ok=True)
        temporary_path = output_path + ".tmp"
        with open(temporary_path, "w", encoding="utf-8") as f:
            json.dump(metrics, f, indent=2, sort_keys=True)
            f.write("\n")
        os.replace(temporary_path, output_path)

    # confusion matrix
    print("\nConfusion matrix (rows = true, cols = predicted):")
    col_w = max(10, max(len(c) for c in class_names) + 1)
    header = " " * 14 + "".join(f"{c:>{col_w}}" for c in class_names)
    print(dim(header))
    for t in class_names:
        cells = "".join(f"{confusion[t][p]:>{col_w}}" for p in class_names)
        print(f"  {t:<12}{cells}")
    print()

    # ---- write logs ----
    if not args.no_log:
        write_summary_log(args.log, run_ts, args.model, args.data_dir,
                          class_names, confusion, n_total, correct)
        print(dim(f"Appended summary to {args.log}"))

    if args.detail:
        detail_path = f"eval_detail_{run_ts.replace(':', '-')}.csv"
        with open(detail_path, "w", newline="", encoding="utf-8") as f:
            writer = csv.DictWriter(f, fieldnames=[
                "timestamp", "true", "file", "pred", "confidence", "correct"])
            writer.writeheader()
            writer.writerows(detail_rows)
        print(dim(f"Wrote per-image detail to {detail_path}"))


def write_summary_log(path, run_ts, model, data_dir, class_names,
                      confusion, n_total, correct):
    """Append one row per run. One column per class holds that class's accuracy."""
    fieldnames = ["timestamp", "model", "data_dir", "total", "correct",
                  "accuracy"] + [f"acc_{c}" for c in class_names]
    row = {
        "timestamp": run_ts, "model": model, "data_dir": data_dir,
        "total": n_total, "correct": correct,
        "accuracy": round(correct / n_total, 4) if n_total else 0,
    }
    for c in class_names:
        row_total = sum(confusion[c].values())
        row[f"acc_{c}"] = round(confusion[c][c] / row_total, 4) if row_total else ""

    write_header = not os.path.exists(path)
    with open(path, "a", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        if write_header:
            writer.writeheader()
        writer.writerow(row)


if __name__ == "__main__":
    main()
