export const PAGE_SIZE = 200;
export const DEFAULT_SETTINGS = Object.freeze({ folderMode: "all", paths: [], publishMetadata: true, showResults: false });

export function normalizeSettings(value = {}) {
  value ??= {};
  let folderMode = ["include", "exclude"].includes(value.folderMode) ? value.folderMode : "all";
  const paths = [...new Set((value.paths || []).map(normalizePath).filter(Boolean))].sort((a, b) => a.localeCompare(b));
  if (folderMode !== "all" && paths.length === 0) folderMode = "all";
  return { folderMode, paths, publishMetadata: value.publishMetadata !== false, showResults: value.showResults === true };
}

export function normalizePath(value) {
  return String(value || "").trim().replaceAll("\\", "/").replace(/\/+$/, "");
}

export function togglePath(paths, path, checked) {
  const normalized = normalizePath(path);
  const values = new Set((paths || []).map(normalizePath));
  if (checked) values.add(normalized); else values.delete(normalized);
  return [...values].filter(Boolean).sort((a, b) => a.localeCompare(b));
}

export function createSelection(allMatching = false, exceptions = []) {
  return { allMatching: Boolean(allMatching), exceptions: new Set(exceptions || []) };
}
export function isSelected(selection, imageId) { return selection.allMatching !== selection.exceptions.has(imageId); }
export function toggleSelection(selection, imageId) {
  const next = createSelection(selection.allMatching, selection.exceptions);
  if (next.exceptions.has(imageId)) next.exceptions.delete(imageId); else next.exceptions.add(imageId);
  return next;
}
export function selectPage(selection, imageIds) {
  const next = createSelection(selection.allMatching, selection.exceptions);
  for (const id of imageIds || []) {
    if (next.allMatching) next.exceptions.delete(id); else next.exceptions.add(id);
  }
  return next;
}
export function invertSelection(selection) { return createSelection(!selection.allMatching, selection.exceptions); }
export function selectionCount(selection, total) {
  return Math.max(0, selection.allMatching ? Number(total || 0) - selection.exceptions.size : selection.exceptions.size);
}
export function serializeSelection(selection, runId, view) {
  return { runId, view, allMatching: selection.allMatching, exceptions: [...selection.exceptions] };
}

export function partitionMatches(matches) {
  return {
    wholeImageIds: matches.filter((item) => item.protectedFileCount === 0).map((item) => item.imageId),
    mixed: matches.filter((item) => item.protectedFileCount > 0),
  };
}

export function chunks(values, size = 100) {
  const result = [];
  for (let index = 0; index < values.length; index += size) result.push(values.slice(index, index + size));
  return result;
}

export function positionHoverPreview(clientX, clientY, viewportWidth, viewportHeight, preferredSize = 420) {
  const padding = 12;
  const gap = 16;
  const size = Math.max(1, Math.min(preferredSize, viewportWidth - padding * 2, viewportHeight - padding * 2));
  let left = clientX + gap;
  if (left + size > viewportWidth - padding) left = clientX - gap - size;
  let top = clientY + gap;
  if (top + size > viewportHeight - padding) top = clientY - gap - size;
  return {
    left: Math.max(padding, Math.min(left, viewportWidth - padding - size)),
    top: Math.max(padding, Math.min(top, viewportHeight - padding - size)),
    size,
  };
}

export function formatConfidence(value) { return `${(Number(value || 0) * 100).toFixed(1)}%`; }
export function basename(path) { return normalizePath(path).split("/").pop() || path || "Untitled image"; }
