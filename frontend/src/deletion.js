import { chunks, partitionMatches } from "./core.js";

export async function deleteAllMatches(matches, runId, api, onProgress = () => {}) {
  const { wholeImageIds, mixed } = partitionMatches(matches);
  const operations = chunks(wholeImageIds, 100).map((ids) => ({ kind: "whole", ids }));
  operations.push(...mixed.map((item) => ({ kind: "mixed", item })));
  let completed = 0;
  let deleted = 0;
  const failed = [];

  for (const operation of operations) {
    try {
      if (operation.kind === "whole") {
        await api.deleteImages(operation.ids);
        deleted += operation.ids.length;
      } else {
        const ids = operation.item.inScopeFiles.map((file) => file.id);
        const result = await api.pruneFiles(runId, operation.item.imageId, ids);
        if (result.failedFileIds?.length) failed.push(operation.item.imageId);
        else deleted++;
      }
    } catch {
      failed.push(...(operation.kind === "whole" ? operation.ids : [operation.item.imageId]));
    }
    completed++;
    onProgress({ completed, total: operations.length, deleted, failed: [...failed] });
  }
  await api.reconcileRun(runId);
  return { deleted, failed };
}

export async function deleteSelection(selection, runId, api, onProgress = () => {}) {
  let cursorConfidence = null;
  let cursorImageId = 0;
  let completed = 0;
  let deleted = 0;
  const failed = [];
  while (true) {
    const page = await api.loadSelectionBatch(selection, cursorConfidence, cursorImageId, 100);
    if (!page.items.length) break;
    const result = await deleteMatchBatch(page.items, runId, api);
    deleted += result.deleted;
    failed.push(...result.failed);
    completed += page.items.length;
    onProgress({ completed, total: null, deleted, failed: [...failed] });
    if (!page.hasMore) break;
    cursorConfidence = page.nextConfidence;
    cursorImageId = page.nextImageId;
  }
  await api.reconcileRun(runId);
  return { deleted, failed };
}

async function deleteMatchBatch(matches, runId, api) {
  const { wholeImageIds, mixed } = partitionMatches(matches);
  let deleted = 0;
  const failed = [];
  for (const ids of chunks(wholeImageIds, 100)) {
    try { await api.deleteImages(ids); deleted += ids.length; } catch { failed.push(...ids); }
  }
  for (const item of mixed) {
    try {
      const result = await api.pruneFiles(runId, item.imageId, item.inScopeFiles.map((file) => file.id));
      if (result.failedFileIds?.length) failed.push(item.imageId); else deleted++;
    } catch { failed.push(item.imageId); }
  }
  return { deleted, failed };
}
