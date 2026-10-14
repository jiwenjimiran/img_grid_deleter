# Changelog

## 0.2.23

- Retry unsupported or damaged image decodes through Cove's thumbnail pipeline, which supports FFmpeg recovery, before skipping classification.
- Log successful recovery and retain detailed skip warnings when recovery also fails. Original files remain untouched.

## 0.2.22

- Log a warning for every skipped image with image ID, run ID, source path, reason, and full exception details in Cove's logs.

## 0.2.21

- Retrieve candidate IDs directly from Cove image records, using stored tag arrays to exclude classified images before pagination.
- Remove file-table deduplication and separate per-page tag queries from candidate discovery.
- Only query files for candidate discovery when Include/Exclude paths require it; retain authorized image queries, scope-safe file loading, and resumable ID cursors.

## 0.2.20

- Start classification after the first candidate batch, removing the preliminary library-wide counting pass.
- Show live processed, grid, and skipped counts with an activity indicator instead of an unknown-total percentage.
- Preserve cancellation/resume behavior for runs whose total is unknown, and finalize totals when classification finishes.

## 0.2.19

- Set the minimum Cove version to the development build used to compile the extension, allowing 1.5.2-dev.167-segments.1, later compatible development builds, and stable 1.5.2 to install.

## 0.2.18

- Replace library-wide classification-tag exclusion with bounded image and tag queries for counting and inference, retaining Cove authorization filters.
- Continue scanning when a full candidate page is already classified.
- Default packages to the manifest version and allow packaging the bundled model without a private training repository.

## 0.2.17

- Skip images that already have the Image Grid Deleter Grid or Not grid tag before counting or running model inference.
- Keep resumed-run progress totals accurate while excluding classifications that were added before the run resumes.

## 0.2.16

- Give the training-data modal an explicit opaque background, including the Legacy/Stash and light-theme surfaces, so transparent theme tokens cannot override it.
- Add the developer training-data action to the individual image detail page's overflow menu.

## 0.2.15

- Reconcile each successfully collected training image's Cove classification tag with its chosen grid or non-grid dataset label.
- Record training-label corrections as extension-owned manual provenance without rewriting the model's saved probability field.

## 0.2.14

- Add a developer-only image bulk action for adding arbitrary Cove images to grid or non-grid training data with confirmation and content-hash deduplication.
- Give the image-page training modal an opaque, self-contained theme when Cove mounts it in the global floating UI layer.
- Persist the latest training log, progress, and active-versus-candidate evaluation report across Cove restarts.
- Add an installed-extension setting, disabled by default, that avoids loading and rendering classification results and thumbnails for memory-intensive runs.

## 0.2.13

- Suppress persisted failures created by older extension/model versions.
- Replace raw exception dumps from current failures with a concise retry message.

## 0.2.12

- Fix classification runs failing on the first candidate batch because PostgreSQL could not translate ordering over a projected candidate record.
- Show an immediate starting state, live processed/candidate progress, and terminal classification errors in the extension page.

## 0.2.11

- Stream classification in bounded batches and persist indexed results outside RAM for large libraries.
- Publish namespaced Grid/Not grid tags and sortable one-decimal grid probability metadata to Cove by default.
- Preserve exact confidence, model version, and run provenance while allowing metadata publication to be disabled.
- Add resumable partial runs, current/previous run retention, server-side all-selection semantics, and bounded deletion/training actions.
- Keep result pages mounted while loading and document exactly how to find, sort, disable, and remove classification metadata.

## 0.2.7

- Preserve immutable, versioned model checkpoints and display candidate model versions during training.
- Compare candidates against the active model on the same prediction set before packaging or promotion.
- Reject candidates when overall accuracy or grid recall declines, leaving the active model untouched.

## 0.2.5

- Add viewport-aware large previews when hovering over result thumbnails and direct links to Cove image pages.
- Replace delete-all behavior with explicit selected-image deletion.
- Add page and all-pages selection while preserving manual deselection across pagination.
- Verify repeat training corrections remain content-hash deduplicated.

## 0.2.4

- Fix retraining completion when metric output ends in punctuation.
- Select tied checkpoints by lower validation loss and stop after three non-improving epochs.
- Report the active epoch and selected checkpoint's epoch, validation accuracy, and validation loss.

## 0.2.0

- Keep partial results selectable after cancelling classification and allow those images to be added directly to grid or non-grid training data.
- Produce standard and private developer-edition packages from every build.
- Add cross-page result selection and correction ingestion into `data/grid` and `data/not_grid`.
- Add developer repository settings, one-click 384px retraining, evaluation, ONNX verification, and live model reload.
- Automatically build numbered normal and `_dev` artifacts after training and stage the dev edition for the next Cove restart.
- Add confirmed grid/non-grid labelling, blue thumbnail selection outlines, live build output, active model versions, and clickable grid/classified review filters.

## 0.1.3

- Handle a null settings response by falling back to All folders.
- Fix the folder picker and job-cancellation URLs to use Cove's `/api` prefix.

## 0.1.2

- Fix the initial page render when no classification run exists yet.

## 0.1.1

- Fix direct navigation and page reloads at `/extensions/delete-image-grids` by registering Cove's `extensions` base route and receiving `delete-image-grids` as its child slug.

## 0.1.0

- Add ONNX-powered image-grid classification for Cove images.
- Add All, Include, and Exclude library-folder scopes.
- Add compact paginated review UI and scope-safe bulk deletion.
