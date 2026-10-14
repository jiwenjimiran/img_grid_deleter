# Delete Image Grids 0.2.23

When ImageSharp rejects an unsupported or damaged image, classification now
retries through Cove's thumbnail pipeline, including its FFmpeg recovery path.
Successful recovery is logged; unrecoverable files still log detailed warnings.
Original files are not modified.

Every skipped image now produces a warning in Cove's logs with its image ID,
run ID, source path, reason, and full exception. Cancellation is not logged as
an image failure.

Classify likely image grids with a bundled model, review results, and delete
selected images through Cove. Include and Exclude folder scopes protect files
outside the selected paths. Archive-backed entries can be removed from Cove;
ZIP archives are never rewritten or deleted.

- Standard ZIP: classification, review, and selected deletion.
- Developer ZIP (`_dev`): adds local dataset corrections, training, evaluation,
  and model/package building. See `training/README.md` in the source repository.

Both editions use the same extension ID; install one edition at a time.
Training images and evaluation data are not included in either ZIP or the
source repository. Contributors provide their own labelled datasets.

Classification now starts after the first candidate batch without a preliminary
library-wide counting pass. The page shows live processed, grid, and skipped
counts with an activity indicator. Unknown-total runs remain resumable after
cancellation, interruption, or failure; completed runs save the final total.
Candidate discovery now queries image records directly and excludes classification
tags through Cove's denormalized tag arrays before pagination. This eliminates
file-table deduplication and walking batches of already-classified images.
All-folder runs avoid file-table queries until loading a candidate batch's files;
Include/Exclude runs retain exact file-path scope checks. Cove's image authorization
filters remain enabled. Batches stay bounded to 128 image IDs.
Requires Cove 1.5.2-dev.167-segments.1 or newer, including stable 1.5.2.

Validation: frontend tests, C# regression checks, Python training policy and
preprocessing tests, both edition builds, and ZIP content checks passed.
Large-library runtime validation remains pending. Build audit reports
ImageSharp 3.1.12 advisories; production shares the host's ImageSharp assembly,
so host dependency compatibility needs review before a stable public release.

Registry submission: after runtime validation and release publication, copy
`registry/io.github.jiwenjimiran.delete-image-grids.json` into the official
registry's `extensions/` directory and submit a PR. Registry CI adds checksums
and release timestamps. The registry install URL selects the standard edition.
