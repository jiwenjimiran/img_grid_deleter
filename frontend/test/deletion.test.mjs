import test from "node:test";
import assert from "node:assert/strict";
import { deleteAllMatches, deleteSelection } from "../src/deletion.js";

test("deletes whole images in Cove batches and prunes mixed records", async () => {
  const calls = [];
  const api = {
    deleteImages: async (ids) => calls.push(["delete", ids]),
    pruneFiles: async (runId, imageId, ids) => { calls.push(["prune", runId, imageId, ids]); return { failedFileIds: [] }; },
    reconcileRun: async (runId) => calls.push(["reconcile", runId]),
  };
  const result = await deleteAllMatches([
    { imageId: 1, protectedFileCount: 0, inScopeFiles: [{ id: 11 }] },
    { imageId: 2, protectedFileCount: 1, inScopeFiles: [{ id: 22 }] },
  ], "run", api);
  assert.deepEqual(result, { deleted: 2, failed: [] });
  assert.deepEqual(calls, [["delete", [1]], ["prune", "run", 2, [22]], ["reconcile", "run"]]);
});

test("deletes an all-matching selection through bounded server pages", async () => {
  const calls = [];
  let page = 0;
  const api = {
    loadSelectionBatch: async () => page++ === 0
      ? { items: [{ imageId: 1, protectedFileCount: 0, inScopeFiles: [] }], nextConfidence: .9, nextImageId: 1, hasMore: true }
      : { items: [{ imageId: 2, protectedFileCount: 0, inScopeFiles: [] }], nextConfidence: .8, nextImageId: 2, hasMore: false },
    deleteImages: async (ids) => calls.push(ids),
    pruneFiles: async () => ({ failedFileIds: [] }),
    reconcileRun: async () => calls.push("reconcile"),
  };
  const result = await deleteSelection({ runId: "run", view: "grids", allMatching: true, exceptions: [9] }, "run", api);
  assert.deepEqual(result, { deleted: 2, failed: [] });
  assert.deepEqual(calls, [[1], [2], "reconcile"]);
});

test("continues after a failed deletion batch", async () => {
  const api = {
    deleteImages: async () => { throw new Error("nope"); },
    pruneFiles: async () => ({ failedFileIds: [] }),
    reconcileRun: async () => {},
  };
  const result = await deleteAllMatches([{ imageId: 7, protectedFileCount: 0, inScopeFiles: [] }], "run", api);
  assert.deepEqual(result, { deleted: 0, failed: [7] });
});
