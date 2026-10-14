# Delete Image Grids for Cove

Delete Image Grids is a Cove extension that runs a fine-tuned 384px ResNet18
classifier over Cove-indexed images. It presents likely grids in a dense,
paginated review page and permanently deletes the confirmed result set.

Classification is streamed into an extension-owned SQLite database so large
libraries do not remain resident in memory. By default the extension also adds
one namespaced classification tag and a sortable numeric **Image grid
probability (%)** custom field to each processed Cove image. This publication
can be disabled before starting a run; the in-extension review remains
persistent and pageable either way. Later runs skip images that already have
the extension's **Image Grid Deleter: Grid** or **Image Grid Deleter: Not grid**
tag, avoiding repeated model inference. Remove either tag from an image if it
needs to be classified again.

Version 0.2.19 supports Cove 1.5.2-dev.167-segments.1 and newer, including stable
1.5.2. The minimum explicitly includes the development build used for compilation.

The developer edition can also copy selected corrections back into the
classifier dataset, retrain and evaluate a fresh 384px model, build both package
variants, and stage itself for replacement when Cove next stops.

## Safety behavior

- The model's top class is used and its grid confidence is shown for review.
- Include and Exclude scopes are path-segment aware.
- If a Cove image record also has files outside the selected scope, those files
  and the image record are preserved; only in-scope files are removed.
- Archive-backed Cove entries can be removed from Cove, but ZIP files are never
  rewritten or deleted.

## Build

Install frontend dependencies and package against a running Cove instance:

```powershell
Set-Location frontend
npm install
Set-Location ..
.\scripts\package.ps1
```

The package command exports and verifies the training repository's current
`model.pt`, then creates both:

- `io.github.jiwenjimiran.delete-image-grids-<version>.zip`
- `io.github.jiwenjimiran.delete-image-grids-<version>_dev.zip`

Use `-TrainingRepo` to override the default
`..\..\gitea\img_grid_classifier` location. `-AutoVersion` allocates the next
local `0.2.x` build number.

Only the `_dev` artifact contains dataset/training/build controls. Neither
artifact contains training datasets or the Python virtual environment.

For a code-only release using the existing model, run
`.\scripts\package.ps1 -UseBundledModel`.
The package version defaults to `src/DeleteImageGrids/extension.json`.

## Public releases and the official registry

Publish both ZIPs as release assets and direct registry installs to the standard
ZIP. The `_dev` ZIP exposes local dataset, training, and extension replacement controls. These are
two editions of the same extension, with the same ID and version; installing
either replaces the other. The `_dev` filename does not denote a prerelease.

Before publishing, verify a large-library classification run, a resumed run,
an already-classified library, and Include/Exclude scopes in Cove. Confirm that
review and selected deletion work and out-of-scope files remain protected.
The standard build should have no training actions or developer API routes.

The source repository includes training scripts in `training/`, with no training
images, prediction images, evaluation logs, or private environment. See
[training setup](training/README.md) to build a model with your own data.
Include screenshots of review and deletion confirmation before the registry
submission. Tag the tested source as `v<version>` and upload
`io.github.jiwenjimiran.delete-image-grids-<version>.zip` to that release.

Submit an extension metadata PR to
[the official registry](https://github.com/yourcove/officialextensionregistry).
Add `extensions/io.github.jiwenjimiran.delete-image-grids.json` with the existing
ID, public repository URL, source manifest URL, summary, categories, and a
`versions` entry containing version, changelog, minimum Cove version, and the
standard ZIP download URL. Registry CI generates checksums, release dates, and
the index. Keep this extension's existing ID so installed users retain their
upgrade path.

Install the generated ZIP from Cove's Extensions settings page. The installed
extension contributes an **Open Image Grid Deleter** button under
`/settings/extensions/installed`.
