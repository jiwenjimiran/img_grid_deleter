import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

test("registers the Cove base route used by /extensions/delete-image-grids", async () => {
  const source = await readFile(new URL("../../src/DeleteImageGrids/DeleteImageGridsExtension.cs", import.meta.url), "utf8");
  assert.match(source, /Route:\s*"extensions"/);
  assert.doesNotMatch(source, /Route:\s*"extensions\/delete-image-grids"/);
});

test("initial page render never dereferences a null run", async () => {
  const source = await readFile(new URL("../src/index.jsx", import.meta.url), "utf8");
  assert.match(source, /run && run\.status !== "idle"/);
  assert.match(source, /confirmOpen && run &&/);
  assert.doesNotMatch(source, /run\?\.status !== "idle"/);
});

test("folder and job requests use Cove's API prefix", async () => {
  const source = await readFile(new URL("../src/api.js", import.meta.url), "utf8");
  assert.match(source, /`\/api\/metadata\/library-folders/);
  assert.match(source, /`\/api\/jobs\//);
  assert.doesNotMatch(source, /request\(`\/metadata\/library-folders/);
});

test("developer workflow is compile-time gated and dual-packaged", async () => {
  const [ui, build, packageScript] = await Promise.all([
    readFile(new URL("../src/index.jsx", import.meta.url), "utf8"),
    readFile(new URL("../build.mjs", import.meta.url), "utf8"),
    readFile(new URL("../../scripts/package.ps1", import.meta.url), "utf8"),
  ]);
  assert.match(ui, /__IGD_DEV_EDITION__/);
  assert.match(build, /IGD_DEV_EDITION/);
  assert.match(packageScript, /extension-standard/);
  assert.match(packageScript, /extension-dev/);
  assert.match(packageScript, /_dev\.zip/);
  assert.match(packageScript, /latest-build\.json/);
});

test("developer API covers settings, corrections, and training", async () => {
  const source = await readFile(new URL("../src/api.js", import.meta.url), "utf8");
  assert.match(source, /developer-settings/);
  assert.match(source, /training-samples/);
  assert.match(source, /training\/current/);
});

test("result views are explicit and selected deletion reloads only grids", async () => {
  const [api, ui] = await Promise.all([
    readFile(new URL("../src/api.js", import.meta.url), "utf8"),
    readFile(new URL("../src/index.jsx", import.meta.url), "utf8"),
  ]);
  assert.match(api, /view = "grids"/);
  assert.match(api, /view=\$\{encodeURIComponent\(view\)\}/);
  assert.match(api, /runs\/selection\/batch/);
  assert.match(ui, /serializeSelection\(selection, run\.runId, "grids"\)/);
  assert.doesNotMatch(ui, /loadEveryMatch/);
  assert.match(ui, /run\.classifiedCount/);
  assert.match(ui, /run\.nonGridCount/);
  assert.match(ui, /"non-grids"/);
});

test("developer correction tiles stay selectable without a separate selection mode", async () => {
  const ui = await readFile(new URL("../src/index.jsx", import.meta.url), "utf8");
  assert.match(ui, /selectable=\{!deleting\}/);
  assert.match(ui, /settings\.showResults && run && run\.status !== "idle" && <section className="igd-selection-bar">/);
  assert.match(ui, /role=\{selectable \? "checkbox" : undefined\}/);
  assert.match(ui, /tabIndex=\{selectable \? 0 : undefined\}/);
  assert.doesNotMatch(ui, /selectionMode/);
});

test("selection supports the page, every result page, selected deletion, previews, and Cove links", async () => {
  const ui = await readFile(new URL("../src/index.jsx", import.meta.url), "utf8");
  assert.match(ui, /Select this page/);
  assert.match(ui, /Invert selection/);
  assert.match(ui, /invertAllResults/);
  assert.match(ui, /setSelection\(\(current\) => invertSelection\(current\)\)/);
  assert.match(ui, /selectAllResults/);
  assert.match(ui, /Select all \{view === "grids"/);
  assert.match(ui, /Delete selected/);
  assert.match(ui, /ImageHoverPreview/);
  assert.match(ui, /href=\{`\/image\/\$\{item\.imageId\}`\}/);
});

test("adding training samples preserves selection for invert-then-delete workflow", async () => {
  const ui = await readFile(new URL("../src/index.jsx", import.meta.url), "utf8");
  const addSamplesBody = ui.slice(ui.indexOf("async function addSamples"), ui.indexOf("async function retrain"));
  assert.doesNotMatch(addSamplesBody, /setSelected\(new Set\(\)\)/);
});

test("partial classification results remain available for training corrections", async () => {
  const coordinator = await readFile(new URL("../../src/DeleteImageGrids/GridRunCoordinator.cs", import.meta.url), "utf8");
  assert.match(coordinator, /"completed" or "cancelled" or "interrupted" or "failed"/);
});

test("metadata publishing is default, documented, and creates sortable probability", async () => {
  const [core, ui, publisher, contracts] = await Promise.all([
    readFile(new URL("../src/core.js", import.meta.url), "utf8"),
    readFile(new URL("../src/index.jsx", import.meta.url), "utf8"),
    readFile(new URL("../../src/DeleteImageGrids/GridMetadataPublisher.cs", import.meta.url), "utf8"),
    readFile(new URL("../../src/DeleteImageGrids/Contracts.cs", import.meta.url), "utf8"),
  ]);
  assert.match(core, /publishMetadata: true/);
  assert.match(ui, /Add classification metadata to Cove/);
  assert.match(ui, /Removing the metadata later/);
  assert.match(publisher, /Image Grid Deleter: Grid/);
  assert.match(publisher, /Image Grid Deleter: Not grid/);
  assert.match(publisher, /Sortable = true/);
  assert.match(publisher, /GridMetadataValues\.ToStoredPercent/);
  assert.match(contracts, /Math\.Round\([^\n]+, 1,/);
});

test("classification and selection stay batch bounded", async () => {
  const [coordinator, ui] = await Promise.all([
    readFile(new URL("../../src/DeleteImageGrids/GridRunCoordinator.cs", import.meta.url), "utf8"),
    readFile(new URL("../src/index.jsx", import.meta.url), "utf8"),
  ]);
  assert.match(coordinator, /CandidateBatchSize = 128/);
  assert.match(coordinator, /MetadataBatchSize = 32/);
  assert.doesNotMatch(coordinator, /db\.ImageFiles[\s\S]{0,180}ToListAsync\(ct\);\s*\n\s*var groups = files/);
  assert.doesNotMatch(ui, /for \(let page = 2; page <=/);
});

test("classification orders database entities before projecting batch candidates", async () => {
  const coordinator = await readFile(new URL("../../src/DeleteImageGrids/GridRunCoordinator.cs", import.meta.url), "utf8");
  assert.match(coordinator, /\.OrderBy\(file => file\.ImageId\)\.ThenBy\(file => file\.Id\)\s*\n\s*\.Select\(file => new ImageFileCandidate/);
  assert.doesNotMatch(coordinator, /new ImageFileCandidate[\s\S]{0,180}\.OrderBy\(file => file\.ImageId\)/);
});

test("classification excludes images already tagged as grid or not grid", async () => {
  const coordinator = await readFile(new URL("../../src/DeleteImageGrids/GridRunCoordinator.cs", import.meta.url), "utf8");
  assert.match(coordinator, /GridMetadataPublisher\.GridTagName/);
  assert.match(coordinator, /GridMetadataPublisher\.NonGridTagName/);
  assert.match(coordinator, /imageIds\.Contains\(link\.ImageId\) && classificationTagIds\.Contains\(link\.TagId\)/);
  assert.match(coordinator, /imageIds\.RemoveAll\(classified\.Contains\)/);
  assert.doesNotMatch(coordinator, /\.CountAsync\(|!classifiedImageIds\.Contains|IgnoreQueryFilters/);
  assert.ok(coordinator.indexOf("afterImageId = imageIds[^1]") < coordinator.indexOf("imageIds.RemoveAll"));
  assert.equal((coordinator.match(/await foreach \(var .* in ReadCandidateBatchesAsync/g) || []).length, 1);
  assert.doesNotMatch(coordinator, /remainingCount|Counting unclassified|SetCandidateCountAsync/);
});

test("classification displays live progress and terminal failures", async () => {
  const ui = await readFile(new URL("../src/index.jsx", import.meta.url), "utf8");
  assert.match(ui, /classificationProgressText\(run, starting\)/);
  assert.doesNotMatch(ui, /Finding candidate images|Math\.round\(progress\)/);
  assert.match(ui, /run\.modelVersion === run\.activeModelVersion/);
  assert.match(ui, /Classification failed\. Press Run to try again\./);
  assert.doesNotMatch(ui, /Classification failed: \{run\.error\}/);
  assert.doesNotMatch(ui, /Technical details/);
  assert.match(ui, /disabled=\{working \|\| deleting/);
});

test("training UI explains which validation checkpoint was selected", async () => {
  const ui = await readFile(new URL("../src/index.jsx", import.meta.url), "utf8");
  assert.match(ui, /Best checkpoint/);
  assert.match(ui, /training\.bestEpoch/);
  assert.match(ui, /training\.bestValidationLoss/);
  assert.match(ui, /training\.candidateVersion/);
  assert.match(ui, /training\.qualityGateMessage/);
  assert.match(ui, /training\.winningSeed/);
  assert.match(ui, /training\.evaluationFalseGrids/);
  assert.match(ui, /TrainingComparison/);
  assert.match(ui, /training\.baselineMetrics/);
  assert.match(ui, /training\.candidateMetrics/);
});

test("installed-extension settings can suppress result rows and thumbnails", async () => {
  const [core, api, ui] = await Promise.all([
    readFile(new URL("../src/core.js", import.meta.url), "utf8"),
    readFile(new URL("../src/api.js", import.meta.url), "utf8"),
    readFile(new URL("../src/index.jsx", import.meta.url), "utf8"),
  ]);
  assert.match(core, /showResults: false/);
  assert.match(api, /includeItems=/);
  assert.match(ui, /Show results of classification on the extension page/);
  assert.match(ui, /Warning: enabling this setting increases memory footprint/);
  assert.match(ui, /<ClassificationDisplaySetting \/>/);
});

test("developer image action opens a confirmed direct-training workflow", async () => {
  const [ui, api, extension] = await Promise.all([
    readFile(new URL("../src/index.jsx", import.meta.url), "utf8"),
    readFile(new URL("../src/api.js", import.meta.url), "utf8"),
    readFile(new URL("../../src/DeleteImageGrids/DeleteImageGridsExtension.cs", import.meta.url), "utf8"),
  ]);
  assert.match(ui, /openTrainingDataDialog/);
  assert.match(ui, /TrainingDataActionHost/);
  assert.match(ui, /Are you sure you want to add/);
  assert.match(api, /training-samples\/images/);
  assert.match(extension, /Label: "Add to training data"/);
  assert.match(extension, /EntityTypes: \["image"\]/);
  assert.match(extension, /add-training-data-image[\s\S]*?ActionType: "toolbar"[\s\S]*?EntityTypes: \["image"\]/);
});

test("training-data corrections reconcile Cove classification tags", async () => {
  const [collector, publisher, extension, ui] = await Promise.all([
    readFile(new URL("../../src/DeleteImageGrids/DeveloperServices.cs", import.meta.url), "utf8"),
    readFile(new URL("../../src/DeleteImageGrids/GridMetadataPublisher.cs", import.meta.url), "utf8"),
    readFile(new URL("../../src/DeleteImageGrids/DeleteImageGridsExtension.cs", import.meta.url), "utf8"),
    readFile(new URL("../src/index.jsx", import.meta.url), "utf8"),
  ]);
  assert.match(collector, /ApplyTrainingLabelAsync\(db, labeledImageIds, label == "grid"/);
  assert.match(publisher, /ApplyTrainingLabelAsync/);
  assert.match(publisher, /links\.Where\(link => link\.TagId != targetTag\.Id\)/);
  assert.match(publisher, /ModelKey = "manual-training-label"/);
  assert.match(extension, /training-samples\/images[\s\S]*?RequireCovePermission\(Permissions\.TagsWrite\)/);
  assert.match(ui, /Cove Grid\/Not-grid tag will be changed to match/);
});

test("floating training modal carries its own opaque theme", async () => {
  const css = await readFile(new URL("../src/styles.css", import.meta.url), "utf8");
  assert.match(css, /\.igd-modal-backdrop[^\{]*\{[^\}]*--igd-surface:/);
  assert.match(css, /\.igd-modal-backdrop button/);
  assert.match(css, /\.igd-modal-backdrop > \.igd-modal[^\{]*\{[^\}]*background-color:\s*#1a1c23\s*!important/);
  assert.match(css, /html\[data-theme="legacy"\][^\{]*\.igd-modal[^\{]*\{[^\}]*background-color:\s*#283540\s*!important/);
  assert.doesNotMatch(css, /\.igd-modal[^\{]*\{[^\}]*background-color:\s*var\(--igd-surface/);
});
