# Train with your own images

The repository shares training code and the extension's inference model. No
training or evaluation images are distributed. Training data stays on your
machine; the extension does not upload it.

On Windows, from this directory:

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
New-Item -ItemType Directory -Force data/grid, data/not_grid, prediction/grid, prediction/not_grid
```

Put your own labelled training images under `data/grid` and `data/not_grid`.
Put separate held-out images under the corresponding `prediction` folders.
Keep prediction images out of the training dataset so model comparisons remain
meaningful. Both classes need images in both sets.

Create an initial baseline checkpoint with your own dataset:

```powershell
.\.venv\Scripts\python.exe train.py --data-dir data --out model.pt --arch resnet18 --epochs 10 --batch-size 32 --img-size 384 --lr 0.001 --patience 3 --finetune --seed 42
.\.venv\Scripts\python.exe evaluate.py prediction --model model.pt --no-log --quiet
.\.venv\Scripts\python.exe -m unittest discover -p "test_*.py"
```

The first fine-tuning run downloads torchvision's pretrained ResNet18 weights.
In the developer edition, select this directory as the training repository and
the clone's root as the extension build repository. The baseline `model.pt` is
required; ONNX is an inference export and cannot replace that checkpoint.
Developer retraining evaluates candidate and baseline checkpoints against your
prediction set and rejects regressions in accuracy or grid recall.

To package your model, from the clone's root:

```powershell
.\scripts\package.ps1 -TrainingRepo .\training -Checkpoint .\training\model.pt
```

To build the extension with the already bundled model, use `-UseBundledModel`.
Install Node.js, the .NET 10 SDK, frontend dependencies (`npm ci` in `frontend`),
and run Cove 1.5.2 or set `COVE_HOST_ASSEMBLY_DIR` to its assemblies.

Dataset folders, checkpoints, prediction folders, image files, and evaluation
CSV files are ignored by Git. Never force-add your data or publish a working
training directory as a release asset. Release packages contain compiled code,
UI assets, and model weights only.
