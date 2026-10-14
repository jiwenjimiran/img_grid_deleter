# Delete Image Grids 0.2.19

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

This version replaces a library-wide classification-tag exclusion query with
bounded image/tag queries to address the 30-second startup database timeout.
Requires Cove 1.5.2-dev.167-segments.1 or newer, including stable 1.5.2.
Fixes the installer rejecting the supported development host as older than
stable 1.5.2.

Validation: frontend tests, C# regression checks, Python training policy and
preprocessing tests, both edition builds, and ZIP content checks passed.
Large-library runtime validation remains pending. Build audit reports
ImageSharp 3.1.12 advisories; production shares the host's ImageSharp assembly,
so host dependency compatibility needs review before a stable public release.

Registry submission: after runtime validation and release publication, copy
`registry/io.github.jiwenjimiran.delete-image-grids.json` into the official
registry's `extensions/` directory and submit a PR. Registry CI adds checksums
and release timestamps. The registry install URL selects the standard edition.
