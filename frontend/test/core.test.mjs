import test from "node:test";
import assert from "node:assert/strict";
import { classificationProgressText } from "../src/core.js";

test("streaming classification reports counts without implying a known total", () => {
  assert.equal(classificationProgressText(null, true), "Starting classification…");
  assert.equal(classificationProgressText({ status: "queued" }), "Classification queued…");
  assert.equal(classificationProgressText({ status: "running", candidateCount: -1 }),
    "Classifying… 0 images processed; 0 grids found; 0 skipped");
  assert.equal(classificationProgressText({ status: "running", processedCount: 129, gridCount: 12, skippedCount: 3, candidateCount: -1 }),
    "Classifying… 129 images processed; 12 grids found; 3 skipped");
});
import { chunks, createSelection, invertSelection, isSelected, normalizePath, normalizeSettings, partitionMatches, positionHoverPreview, selectPage, selectionCount, serializeSelection, togglePath, toggleSelection } from "../src/core.js";

test("settings normalize modes and deduplicate paths", () => {
  assert.deepEqual(normalizeSettings({ folderMode: "include", paths: ["C:\\library\\grid\\", "C:/library/grid"] }), { folderMode: "include", paths: ["C:/library/grid"], publishMetadata: true, showResults: false });
  assert.equal(normalizeSettings({ folderMode: "exclude", paths: [] }).folderMode, "all");
  assert.equal(normalizePath(" C:\\library\\ "), "C:/library");
  assert.deepEqual(normalizeSettings(null), { folderMode: "all", paths: [], publishMetadata: true, showResults: false });
  assert.equal(normalizeSettings({ publishMetadata: false }).publishMetadata, false);
  assert.equal(normalizeSettings({ showResults: true }).showResults, true);
});

test("togglePath adds and removes exact selections", () => {
  assert.deepEqual(togglePath([], "C:/b", true), ["C:/b"]);
  assert.deepEqual(togglePath(["C:/a", "C:/b"], "C:/a", false), ["C:/b"]);
});

test("selection represents all matching results with only manual exceptions", () => {
  let selection = createSelection();
  selection = selectPage(selection, [1, 2]);
  assert.equal(selectionCount(selection, 10), 2);
  assert.equal(isSelected(selection, 1), true);
  selection = invertSelection(selection);
  assert.equal(selectionCount(selection, 10), 8);
  assert.equal(isSelected(selection, 1), false);
  assert.equal(isSelected(selection, 3), true);
  selection = toggleSelection(selection, 3);
  assert.equal(selectionCount(selection, 10), 7);
  assert.deepEqual(serializeSelection(selection, "run", "grids"), {
    runId: "run", view: "grids", allMatching: true, exceptions: [1, 2, 3],
  });
});

test("partitionMatches protects mixed-scope image records", () => {
  const value = partitionMatches([{ imageId: 1, protectedFileCount: 0 }, { imageId: 2, protectedFileCount: 3 }]);
  assert.deepEqual(value.wholeImageIds, [1]);
  assert.deepEqual(value.mixed.map((item) => item.imageId), [2]);
});

test("chunks deletion batches", () => {
  assert.deepEqual(chunks([1, 2, 3, 4, 5], 2), [[1, 2], [3, 4], [5]]);
});

test("hover preview flips and clamps inside the viewport", () => {
  assert.deepEqual(positionHoverPreview(100, 100, 1200, 900), { left: 116, top: 116, size: 420 });
  assert.deepEqual(positionHoverPreview(1180, 880, 1200, 900), { left: 744, top: 444, size: 420 });
  const small = positionHoverPreview(150, 100, 300, 200);
  assert.deepEqual(small, { left: 12, top: 12, size: 176 });
});
