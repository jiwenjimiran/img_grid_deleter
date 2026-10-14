var __defProp = Object.defineProperty;
var __export = (target, all) => {
  for (var name in all)
    __defProp(target, name, { get: all[name], enumerable: !0 });
};

// src/index.jsx
import React from "@cove/runtime/react";
import {
  AlertTriangle,
  ArrowLeft,
  ArrowRight,
  ChevronDown,
  ChevronRight,
  Folder,
  CheckSquare,
  Database,
  ExternalLink,
  Grid3X3,
  Info,
  Loader2,
  Play,
  RotateCw,
  Save,
  ShieldCheck,
  Square,
  Trash2,
  X
} from "@cove/runtime/lucide-react";

// src/api.js
var api_exports = {};
__export(api_exports, {
  ApiRequestError: () => ApiRequestError,
  addImageTrainingSamples: () => addImageTrainingSamples,
  addTrainingSamples: () => addTrainingSamples,
  authenticatedFetch: () => authenticatedFetch,
  cancelJob: () => cancelJob,
  deleteImages: () => deleteImages,
  loadDeveloperSettings: () => loadDeveloperSettings,
  loadFolders: () => loadFolders,
  loadRun: () => loadRun,
  loadRunStatus: () => loadRunStatus,
  loadRuns: () => loadRuns,
  loadSelectionBatch: () => loadSelectionBatch,
  loadSettings: () => loadSettings,
  loadTrainingStatus: () => loadTrainingStatus,
  pruneFiles: () => pruneFiles,
  reconcileRun: () => reconcileRun,
  request: () => request,
  resumeRun: () => resumeRun,
  saveDeveloperSettings: () => saveDeveloperSettings,
  saveSettings: () => saveSettings,
  startRun: () => startRun,
  startTraining: () => startTraining
});
var BASE_URL = "/api/ext/delete-image-grids", ACCESS_TOKEN_KEY = "cove_access_token", REFRESH_TOKEN_KEY = "cove_refresh_token", SHARE_TOKEN_KEY = "cove_share_token", SHARE_PASSWORD_KEY = "cove_share_password";
var refreshInFlight = null, ApiRequestError = class extends Error {
  constructor(status, message, body = null) {
    super(message || `API request failed with status ${status}.`), this.name = "ApiRequestError", this.status = status, this.body = body;
  }
};
async function request(path, options = {}) {
  let { timeoutMs = 0, ...fetchOptions } = options, controller = timeoutMs > 0 ? new AbortController() : null, timeout = controller ? setTimeout(() => controller.abort(), timeoutMs) : null;
  controller && fetchOptions.signal && fetchOptions.signal.addEventListener("abort", () => controller.abort(), { once: !0 });
  let response;
  try {
    response = await authenticatedFetch(path, {
      ...fetchOptions,
      signal: controller?.signal || fetchOptions.signal,
      headers: { "Content-Type": "application/json", ...fetchOptions.headers || {} }
    });
  } catch (reason) {
    throw controller?.signal.aborted && !fetchOptions.signal?.aborted ? new Error("The request timed out. Try again.") : reason;
  } finally {
    timeout && clearTimeout(timeout);
  }
  let text = await response.text(), body = null;
  try {
    body = text ? JSON.parse(text) : null;
  } catch {
    body = text;
  }
  if (!response.ok) throw new ApiRequestError(response.status, body?.message || body?.detail || body || response.statusText, body);
  return body;
}
async function authenticatedFetch(path, options = {}) {
  await ensureFreshAccessToken();
  let initial = readAuthState(), response = await fetchWithAuth(path, options, initial);
  if (response.status !== 401) return response;
  let latest = readAuthState();
  return latest.accessToken && latest.accessToken !== initial.accessToken && (response = await fetchWithAuth(path, options, latest), response.status !== 401) ? response : !latest.refreshToken || latest.shareToken || (await refreshAccessToken(latest.accessToken, latest.refreshToken), response = await fetchWithAuth(path, options, readAuthState()), response.status === 401) ? failAuthentication() : response;
}
async function ensureFreshAccessToken() {
  let auth = readAuthState();
  auth.shareToken || !auth.accessToken || !auth.refreshToken || !tokenExpiresSoon(auth.accessToken) || await refreshAccessToken(auth.accessToken, auth.refreshToken);
}
async function refreshAccessToken(expectedAccessToken, expectedRefreshToken) {
  let latest = readAuthState();
  if (latest.accessToken && latest.accessToken !== expectedAccessToken && !tokenExpiresSoon(latest.accessToken)) return latest.accessToken;
  if (refreshInFlight) return refreshInFlight;
  refreshInFlight = (async () => {
    let response = await fetch("/api/auth/refresh", {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken: expectedRefreshToken })
    });
    if (!response.ok) throw new Error("Cove authentication could not be refreshed.");
    let body = await response.json();
    if (!body?.token) throw new Error("Cove did not return a refreshed access token.");
    return writeStoredToken(ACCESS_TOKEN_KEY, body.token), writeStoredToken(REFRESH_TOKEN_KEY, body.refreshToken || expectedRefreshToken), body.token;
  })();
  try {
    return await refreshInFlight;
  } finally {
    refreshInFlight = null;
  }
}
function fetchWithAuth(path, options, auth) {
  let headers = new Headers(options.headers || {});
  return auth.shareToken ? (headers.set("X-Share-Token", auth.shareToken), auth.sharePassword && headers.set("X-Share-Password", auth.sharePassword)) : auth.accessToken && !headers.has("Authorization") && headers.set("Authorization", `Bearer ${auth.accessToken}`), fetch(path, { ...options, credentials: "same-origin", headers });
}
function readAuthState() {
  return {
    accessToken: readStoredToken("localStorage", ACCESS_TOKEN_KEY),
    refreshToken: readStoredToken("localStorage", REFRESH_TOKEN_KEY),
    shareToken: readStoredToken("sessionStorage", SHARE_TOKEN_KEY),
    sharePassword: readStoredToken("sessionStorage", SHARE_PASSWORD_KEY)
  };
}
function readStoredToken(storage, key) {
  try {
    return globalThis[storage]?.getItem(key) || null;
  } catch {
    return null;
  }
}
function writeStoredToken(key, value) {
  try {
    globalThis.localStorage?.setItem(key, value);
  } catch {
  }
}
function tokenExpiresSoon(token, now = Date.now()) {
  try {
    let payload = token.split(".")[1], normalized = payload.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(payload.length / 4) * 4, "=");
    return Number(JSON.parse(globalThis.atob(normalized)).exp || 0) * 1e3 <= now + 6e4;
  } catch {
    return !1;
  }
}
function failAuthentication() {
  try {
    globalThis.window?.dispatchEvent(new CustomEvent("cove-auth-required"));
  } catch {
  }
  throw new Error("Authentication is required.");
}
var loadSettings = () => request(`${BASE_URL}/settings`), saveSettings = (settings) => request(`${BASE_URL}/settings`, { method: "PUT", body: JSON.stringify(settings) }), startRun = (settings) => request(`${BASE_URL}/runs${settings.publishMetadata === !1 ? "" : "/published"}`, { method: "POST", body: JSON.stringify(settings) }), resumeRun = (runId) => request(`${BASE_URL}/runs/resume`, { method: "POST", body: JSON.stringify({ runId }) }), loadRuns = () => request(`${BASE_URL}/runs`), loadRunStatus = (runId) => request(`${BASE_URL}/runs/status${runId ? `?runId=${encodeURIComponent(runId)}` : ""}`), loadRun = (page = 1, pageSize = 200, view = "grids", runId = null, signal = null, includeItems = !0) => request(`${BASE_URL}/runs/current?page=${page}&pageSize=${pageSize}&view=${encodeURIComponent(view)}&includeItems=${includeItems ? "true" : "false"}${runId ? `&runId=${encodeURIComponent(runId)}` : ""}`, { timeoutMs: 3e4, signal }), loadSelectionBatch = (selection, cursorConfidence = null, cursorImageId = 0, batchSize = 100) => request(`${BASE_URL}/runs/selection/batch`, {
  method: "POST",
  body: JSON.stringify({ selection, cursorConfidence, cursorImageId, batchSize })
}), loadFolders = (path) => request(`/api/metadata/library-folders${path ? `?path=${encodeURIComponent(path)}` : ""}`), cancelJob = (jobId) => request(`/api/jobs/${encodeURIComponent(jobId)}`, { method: "DELETE" }), deleteImages = (ids) => request("/api/images/bulk", {
  method: "DELETE",
  body: JSON.stringify({ ids, deleteFiles: !0, deleteGenerated: !0 })
}), pruneFiles = (runId, imageId, fileIds) => request(`${BASE_URL}/runs/prune`, {
  method: "POST",
  body: JSON.stringify({ runId, imageId, fileIds })
}), reconcileRun = (runId) => request(`${BASE_URL}/runs/reconcile`, {
  method: "POST",
  body: JSON.stringify({ runId })
}), loadDeveloperSettings = () => request(`${BASE_URL}/developer-settings`), saveDeveloperSettings = (settings) => request(`${BASE_URL}/developer-settings`, {
  method: "PUT",
  body: JSON.stringify(settings)
}), addTrainingSamples = (runId, imageIds, label, selection = null) => request(`${BASE_URL}/training-samples`, {
  method: "POST",
  body: JSON.stringify({ runId, imageIds, label, selection })
}), addImageTrainingSamples = (imageIds, label) => request(`${BASE_URL}/training-samples/images`, {
  method: "POST",
  body: JSON.stringify({ imageIds, label }),
  timeoutMs: 0
}), startTraining = () => request(`${BASE_URL}/training`, { method: "POST" }), loadTrainingStatus = () => request(`${BASE_URL}/training/current`);

// src/core.js
var DEFAULT_SETTINGS = Object.freeze({ folderMode: "all", paths: [], publishMetadata: !0, showResults: !1 });
function classificationProgressText(run, starting = !1) {
  return starting ? "Starting classification\u2026" : run?.status === "queued" ? "Classification queued\u2026" : `Classifying\u2026 ${run?.processedCount || 0} images processed; ${run?.gridCount || 0} grids found; ${run?.skippedCount || 0} skipped`;
}
function normalizeSettings(value = {}) {
  value ??= {};
  let folderMode = ["include", "exclude"].includes(value.folderMode) ? value.folderMode : "all", paths = [...new Set((value.paths || []).map(normalizePath).filter(Boolean))].sort((a, b) => a.localeCompare(b));
  return folderMode !== "all" && paths.length === 0 && (folderMode = "all"), { folderMode, paths, publishMetadata: value.publishMetadata !== !1, showResults: value.showResults === !0 };
}
function normalizePath(value) {
  return String(value || "").trim().replaceAll("\\", "/").replace(/\/+$/, "");
}
function togglePath(paths, path, checked) {
  let normalized = normalizePath(path), values = new Set((paths || []).map(normalizePath));
  return checked ? values.add(normalized) : values.delete(normalized), [...values].filter(Boolean).sort((a, b) => a.localeCompare(b));
}
function createSelection(allMatching = !1, exceptions = []) {
  return { allMatching: !!allMatching, exceptions: new Set(exceptions || []) };
}
function isSelected(selection, imageId) {
  return selection.allMatching !== selection.exceptions.has(imageId);
}
function toggleSelection(selection, imageId) {
  let next = createSelection(selection.allMatching, selection.exceptions);
  return next.exceptions.has(imageId) ? next.exceptions.delete(imageId) : next.exceptions.add(imageId), next;
}
function selectPage(selection, imageIds) {
  let next = createSelection(selection.allMatching, selection.exceptions);
  for (let id of imageIds || [])
    next.allMatching ? next.exceptions.delete(id) : next.exceptions.add(id);
  return next;
}
function invertSelection(selection) {
  return createSelection(!selection.allMatching, selection.exceptions);
}
function selectionCount(selection, total) {
  return Math.max(0, selection.allMatching ? Number(total || 0) - selection.exceptions.size : selection.exceptions.size);
}
function serializeSelection(selection, runId, view) {
  return { runId, view, allMatching: selection.allMatching, exceptions: [...selection.exceptions] };
}
function partitionMatches(matches) {
  return {
    wholeImageIds: matches.filter((item) => item.protectedFileCount === 0).map((item) => item.imageId),
    mixed: matches.filter((item) => item.protectedFileCount > 0)
  };
}
function chunks(values, size = 100) {
  let result = [];
  for (let index = 0; index < values.length; index += size) result.push(values.slice(index, index + size));
  return result;
}
function positionHoverPreview(clientX, clientY, viewportWidth, viewportHeight, preferredSize = 420) {
  let size = Math.max(1, Math.min(preferredSize, viewportWidth - 24, viewportHeight - 24)), left = clientX + 16;
  left + size > viewportWidth - 12 && (left = clientX - 16 - size);
  let top = clientY + 16;
  return top + size > viewportHeight - 12 && (top = clientY - 16 - size), {
    left: Math.max(12, Math.min(left, viewportWidth - 12 - size)),
    top: Math.max(12, Math.min(top, viewportHeight - 12 - size)),
    size
  };
}
function formatConfidence(value) {
  return `${(Number(value || 0) * 100).toFixed(1)}%`;
}
function basename(path) {
  return normalizePath(path).split("/").pop() || path || "Untitled image";
}

// src/deletion.js
async function deleteSelection(selection, runId, api, onProgress = () => {
}) {
  let cursorConfidence = null, cursorImageId = 0, completed = 0, deleted = 0, failed = [];
  for (; ; ) {
    let page = await api.loadSelectionBatch(selection, cursorConfidence, cursorImageId, 100);
    if (!page.items.length) break;
    let result = await deleteMatchBatch(page.items, runId, api);
    if (deleted += result.deleted, failed.push(...result.failed), completed += page.items.length, onProgress({ completed, total: null, deleted, failed: [...failed] }), !page.hasMore) break;
    cursorConfidence = page.nextConfidence, cursorImageId = page.nextImageId;
  }
  return await api.reconcileRun(runId), { deleted, failed };
}
async function deleteMatchBatch(matches, runId, api) {
  let { wholeImageIds, mixed } = partitionMatches(matches), deleted = 0, failed = [];
  for (let ids of chunks(wholeImageIds, 100))
    try {
      await api.deleteImages(ids), deleted += ids.length;
    } catch {
      failed.push(...ids);
    }
  for (let item of mixed)
    try {
      (await api.pruneFiles(runId, item.imageId, item.inScopeFiles.map((file) => file.id))).failedFileIds?.length ? failed.push(item.imageId) : deleted++;
    } catch {
      failed.push(item.imageId);
    }
  return { deleted, failed };
}

// src/index.jsx
var { useEffect, useMemo, useRef, useState } = React, TRAINING_DATA_ACTION_EVENT = "delete-image-grids:open-training-data";
function openTrainingDataDialog(_action, payload = {}) {
  let imageIds = [...new Set((payload.selectedIds || payload.entityIds || payload.ids || []).map(Number).filter((id) => Number.isInteger(id) && id > 0))];
  if (payload.entityType && payload.entityType !== "image") throw new Error("This action only supports images.");
  if (!imageIds.length) throw new Error("Select at least one image first.");
  return new Promise((resolve, reject) => {
    window.dispatchEvent(new CustomEvent(TRAINING_DATA_ACTION_EVENT, { detail: { imageIds, resolve, reject } }));
  });
}
function DeleteImageGridsPage() {
  let [settings, setSettings] = useState(DEFAULT_SETTINGS), [run, setRun] = useState(null), [page, setPage] = useState(1), [view, setView] = useState("grids"), [loading, setLoading] = useState(!0), [pageLoading, setPageLoading] = useState(!1), [starting, setStarting] = useState(!1), [folderOpen, setFolderOpen] = useState(!1), [confirmOpen, setConfirmOpen] = useState(!1), [deleting, setDeleting] = useState(!1), [deleteProgress, setDeleteProgress] = useState(null), [notice, setNotice] = useState(""), [error, setError] = useState(""), [selection, setSelection] = useState(() => createSelection()), [preview, setPreview] = useState(null), [metadataHelpOpen, setMetadataHelpOpen] = useState(!1), [runs, setRuns] = useState([]), [addingSamples, setAddingSamples] = useState(!1), [training, setTraining] = useState(null), [sampleLabel, setSampleLabel] = useState(null), pageRequest = useRef(null);
  async function refresh(targetPage = page, targetView = view, targetRunId = run?.runId, includeItems = settings.showResults) {
    pageRequest.current?.abort();
    let controller = new AbortController();
    pageRequest.current = controller;
    let value = await loadRun(targetPage, 200, targetView, targetRunId, controller.signal, includeItems);
    return setRun(value), setView(value.view || targetView), value.page !== targetPage && setPage(value.page), pageRequest.current === controller && (pageRequest.current = null), value;
  }
  useEffect(() => {
    let active2 = !0;
    return Promise.all([loadSettings(), loadRuns()]).then(async ([stored, availableRuns]) => {
      let normalized = normalizeSettings(stored), current = await loadRun(1, 200, "grids", null, null, normalized.showResults);
      active2 && (setSettings(normalized), setRun(current), setRuns(availableRuns || []));
    }).catch((reason) => active2 && setError(reason.message)).finally(() => active2 && setLoading(!1)), () => {
      active2 = !1;
    };
  }, []), useEffect(() => {
    if (!run || !["queued", "running"].includes(run.status)) return;
    let timer = setInterval(() => loadRunStatus(run.runId).then((status) => {
      setRun((current) => current?.runId === status.runId ? { ...current, ...status, classifiedCount: status.gridCount + status.nonGridCount } : current), ["queued", "running"].includes(status.status) || refresh(page, view, status.runId, settings.showResults).catch((reason) => setError(reason.message));
    }).catch((reason) => setError(reason.message)), 1500);
    return () => clearInterval(timer);
  }, [run?.status, run?.runId, page, view, settings.showResults]), useEffect(() => {
    let mounted = !0, poll = () => loadTrainingStatus().then((value) => mounted && setTraining(value)).catch((reason) => mounted && setError(reason.message));
    poll();
    let timer = setInterval(poll, 1500);
    return () => {
      mounted = !1, clearInterval(timer);
    };
  }, []);
  async function start() {
    let scope = normalizeSettings(settings);
    setStarting(!0), setError(""), setNotice(""), setPage(1), setView("grids"), setSelection(createSelection()), setPreview(null);
    try {
      await saveSettings(scope);
      let started = await startRun(scope);
      setRun(await loadRun(1, 200, "grids", started.runId, null, scope.showResults)), setRuns(await loadRuns());
    } catch (reason) {
      setError(reason.message);
    } finally {
      setStarting(!1);
    }
  }
  async function cancel() {
    if (run?.jobId)
      try {
        await cancelJob(run.jobId), await refresh(page, view, run.runId);
      } catch (reason) {
        setError(reason.message);
      }
  }
  async function confirmDelete() {
    setConfirmOpen(!1), setDeleting(!0), setDeleteProgress({ completed: 0, total: 0, deleted: 0, failed: [] }), setError(""), setNotice("");
    try {
      if (!run?.runId) throw new Error("No classification run is available to delete.");
      let descriptor = serializeSelection(selection, run.runId, "grids"), result = await deleteSelection(descriptor, run.runId, api_exports, setDeleteProgress);
      await refresh(1, "grids", run.runId), setPage(1), setSelection(createSelection()), setNotice(result.failed.length ? `Deleted ${result.deleted} grid image${result.deleted === 1 ? "" : "s"}; ${result.failed.length} could not be deleted.` : `Deleted ${result.deleted} grid image${result.deleted === 1 ? "" : "s"}.`);
    } catch (reason) {
      setError(reason.message);
    } finally {
      setDeleting(!1);
    }
  }
  function toggleSelected(imageId) {
    setSelection((current) => toggleSelection(current, imageId));
  }
  async function changeView(targetView) {
    setView(targetView), setPage(1), setSelection(createSelection()), setPreview(null);
    try {
      await refresh(1, targetView, run?.runId);
    } catch (reason) {
      setError(reason.message);
    }
  }
  function selectAllResults() {
    setSelection(createSelection(!0));
  }
  function invertAllResults() {
    setSelection((current) => invertSelection(current));
  }
  function showPreview(item, event) {
    let position = positionHoverPreview(event.clientX, event.clientY, window.innerWidth, window.innerHeight);
    setPreview({ item, ...position });
  }
  async function addSamples(label) {
    if (!(!run?.runId || selectedTotal === 0)) {
      setAddingSamples(!0), setError(""), setNotice("");
      try {
        let result = await addTrainingSamples(run.runId, null, label, serializeSelection(selection, run.runId, view));
        setNotice(`Training data and Cove tags updated: ${result.added} added, ${result.relabeled} relabelled, ${result.skipped} already present${result.errors.length ? `, ${result.errors.length} failed` : ""}.`);
      } catch (reason) {
        setError(reason.message);
      } finally {
        setAddingSamples(!1);
      }
    }
  }
  async function retrain() {
    setError(""), setNotice("");
    try {
      await startTraining(), setTraining(await loadTrainingStatus());
    } catch (reason) {
      setError(reason.message);
    }
  }
  if (loading) return /* @__PURE__ */ React.createElement("div", { className: "igd-loading" }, /* @__PURE__ */ React.createElement(Loader2, { className: "igd-spin" }), "Loading Image Grid Deleter");
  let active = ["queued", "running"].includes(run?.status), working = starting || active, currentFailure = !working && run?.status === "failed" && run.error && (!run.modelVersion || !run.activeModelVersion || run.modelVersion === run.activeModelVersion), viewTotal = view === "grids" ? run?.gridCount : view === "non-grids" ? run?.nonGridCount : run?.classifiedCount, selectedTotal = selectionCount(selection, viewTotal || 0);
  return /* @__PURE__ */ React.createElement("div", { className: "igd-page" }, /* @__PURE__ */ React.createElement("header", { className: "igd-header" }, /* @__PURE__ */ React.createElement("div", null, /* @__PURE__ */ React.createElement("div", { className: "igd-title" }, /* @__PURE__ */ React.createElement(Grid3X3, { size: 24 }), /* @__PURE__ */ React.createElement("h1", null, "Delete Image Grids"), /* @__PURE__ */ React.createElement("span", { className: "igd-version" }, "Model ", training?.activeModelVersion || run?.activeModelVersion || "loading")), /* @__PURE__ */ React.createElement("p", null, "Classify Cove images in the selected folders, review the matches, then permanently delete them.")), /* @__PURE__ */ React.createElement("div", { className: "igd-header-actions" }, /* @__PURE__ */ React.createElement("button", { className: "igd-danger", disabled: working || deleting || view !== "grids" || selectedTotal === 0, onClick: () => setConfirmOpen(!0) }, /* @__PURE__ */ React.createElement(Trash2, { size: 17 }), "Delete selected"))), error && /* @__PURE__ */ React.createElement("div", { className: "igd-alert igd-error" }, /* @__PURE__ */ React.createElement(AlertTriangle, { size: 17 }), error), notice && /* @__PURE__ */ React.createElement("div", { className: "igd-alert igd-success" }, /* @__PURE__ */ React.createElement(ShieldCheck, { size: 17 }), notice), working && /* @__PURE__ */ React.createElement("div", { className: "igd-alert igd-working", role: "status" }, /* @__PURE__ */ React.createElement(Loader2, { className: "igd-spin", size: 17 }), classificationProgressText(run, starting)), currentFailure && /* @__PURE__ */ React.createElement("div", { className: "igd-alert igd-error" }, /* @__PURE__ */ React.createElement(AlertTriangle, { size: 17 }), "Classification failed. Press Run to try again."), !working && run?.status === "completed" && run.error && /* @__PURE__ */ React.createElement("div", { className: "igd-alert igd-warning" }, /* @__PURE__ */ React.createElement(AlertTriangle, { size: 17 }), "Classification completed with a warning: ", run.error), /* @__PURE__ */ React.createElement("section", { className: "igd-controls" }, /* @__PURE__ */ React.createElement("div", { className: "igd-mode", role: "group", "aria-label": "Folder scope" }, [["all", "All"], ["include", "Include"], ["exclude", "Exclude"]].map(([mode, label]) => /* @__PURE__ */ React.createElement(
    "button",
    {
      key: mode,
      className: settings.folderMode === mode ? "igd-active" : "",
      "aria-pressed": settings.folderMode === mode,
      onClick: () => setSettings((value) => ({ ...value, folderMode: mode }))
    },
    label
  ))), /* @__PURE__ */ React.createElement("button", { className: "igd-secondary", disabled: settings.folderMode === "all", onClick: () => setFolderOpen(!0) }, /* @__PURE__ */ React.createElement(Folder, { size: 16 }), settings.folderMode === "all" ? "All library folders" : settings.paths.length ? `${settings.paths.length} folder${settings.paths.length === 1 ? "" : "s"}` : "Choose folders"), /* @__PURE__ */ React.createElement("label", { className: "igd-metadata-toggle" }, /* @__PURE__ */ React.createElement(
    "input",
    {
      type: "checkbox",
      checked: settings.publishMetadata !== !1,
      onChange: (event) => setSettings((value) => ({ ...value, publishMetadata: event.target.checked }))
    }
  ), /* @__PURE__ */ React.createElement("span", null, "Add classification metadata to Cove")), /* @__PURE__ */ React.createElement("button", { className: "igd-icon-button", title: "What metadata is added?", "aria-label": "What metadata is added?", onClick: () => setMetadataHelpOpen(!0) }, /* @__PURE__ */ React.createElement(Info, { size: 16 })), /* @__PURE__ */ React.createElement("button", { className: "igd-primary", disabled: working || deleting || settings.folderMode !== "all" && settings.paths.length === 0, onClick: start }, working ? /* @__PURE__ */ React.createElement(Loader2, { className: "igd-spin", size: 17 }) : /* @__PURE__ */ React.createElement(Play, { size: 17 }), starting ? "Starting\u2026" : active ? "Running\u2026" : "Run"), active && /* @__PURE__ */ React.createElement("button", { className: "igd-secondary", onClick: cancel }, /* @__PURE__ */ React.createElement(X, { size: 16 }), "Cancel"), !active && run?.resumable && /* @__PURE__ */ React.createElement("button", { className: "igd-secondary", onClick: async () => {
    try {
      await resumeRun(run.runId), await refresh(page, view, run.runId);
    } catch (reason) {
      setError(reason.message);
    }
  } }, /* @__PURE__ */ React.createElement(RotateCw, { size: 16 }), "Resume"), runs.length > 1 && /* @__PURE__ */ React.createElement("select", { className: "igd-run-select", value: run?.runId || "", onChange: async (event) => {
    let id = event.target.value;
    setPage(1), setSelection(createSelection()), setPreview(null);
    try {
      await refresh(1, view, id);
    } catch (reason) {
      setError(reason.message);
    }
  } }, runs.map((item, index) => /* @__PURE__ */ React.createElement("option", { key: item.runId, value: item.runId }, index === 0 ? "Current" : "Previous", " \u2014 ", item.status, " \u2014 model ", item.modelVersion)))), settings.folderMode !== "all" && settings.paths.length > 0 && /* @__PURE__ */ React.createElement("div", { className: "igd-paths" }, /* @__PURE__ */ React.createElement("strong", null, settings.folderMode === "exclude" ? "Excluding" : "Including"), settings.paths.map((path) => /* @__PURE__ */ React.createElement("span", { key: path, title: path }, path))), run && run.status !== "idle" && /* @__PURE__ */ React.createElement("section", { className: "igd-status" }, /* @__PURE__ */ React.createElement("button", { disabled: !settings.showResults, className: view === "grids" ? "igd-kpi-active" : "", onClick: () => changeView("grids") }, /* @__PURE__ */ React.createElement("strong", null, run.gridCount || 0), /* @__PURE__ */ React.createElement("span", null, "grids")), /* @__PURE__ */ React.createElement("button", { disabled: !settings.showResults, className: view === "non-grids" ? "igd-kpi-active" : "", onClick: () => changeView("non-grids") }, /* @__PURE__ */ React.createElement("strong", null, run.nonGridCount || 0), /* @__PURE__ */ React.createElement("span", null, "non-grids")), /* @__PURE__ */ React.createElement("button", { disabled: !settings.showResults, className: view === "classified" ? "igd-kpi-active" : "", onClick: () => changeView("classified") }, /* @__PURE__ */ React.createElement("strong", null, run.classifiedCount || 0), /* @__PURE__ */ React.createElement("span", null, "classified")), /* @__PURE__ */ React.createElement("div", null, /* @__PURE__ */ React.createElement("strong", null, run.skippedCount || 0), /* @__PURE__ */ React.createElement("span", null, "skipped")), /* @__PURE__ */ React.createElement("div", null, /* @__PURE__ */ React.createElement("strong", null, run.processedCount || 0), /* @__PURE__ */ React.createElement("span", null, "processed")), active && /* @__PURE__ */ React.createElement("div", { className: "igd-progress igd-progress-indeterminate", "aria-label": "Classification in progress" }, /* @__PURE__ */ React.createElement("span", null))), deleting && /* @__PURE__ */ React.createElement("div", { className: "igd-alert igd-working" }, /* @__PURE__ */ React.createElement(Loader2, { className: "igd-spin", size: 17 }), "Deleting\u2026 ", deleteProgress?.total ? `${deleteProgress.completed}/${deleteProgress.total} batches` : "preparing batches"), settings.showResults && run && run.status !== "idle" && /* @__PURE__ */ React.createElement("section", { className: "igd-selection-bar" }, /* @__PURE__ */ React.createElement("strong", null, selectedTotal, " selected"), /* @__PURE__ */ React.createElement("button", { className: "igd-secondary", disabled: !run?.items?.length, onClick: () => setSelection((current) => selectPage(current, run.items.map((item) => item.imageId))) }, "Select this page"), /* @__PURE__ */ React.createElement("button", { className: "igd-secondary", disabled: !run?.classifiedCount || active, onClick: invertAllResults }, "Invert selection"), /* @__PURE__ */ React.createElement("button", { className: "igd-secondary", disabled: !run?.classifiedCount || active, onClick: selectAllResults }, "Select all ", view === "grids" ? run.gridCount : view === "non-grids" ? run.nonGridCount : run.classifiedCount), /* @__PURE__ */ React.createElement("button", { className: "igd-secondary", disabled: !selectedTotal, onClick: () => setSelection(createSelection()) }, "Clear"), /* @__PURE__ */ React.createElement(React.Fragment, null, /* @__PURE__ */ React.createElement("button", { className: "igd-primary", disabled: !selectedTotal || addingSamples, onClick: () => setSampleLabel("grid") }, /* @__PURE__ */ React.createElement(Database, { size: 16 }), "Add selected images to grid training data"), /* @__PURE__ */ React.createElement("button", { className: "igd-primary", disabled: !selectedTotal || addingSamples, onClick: () => setSampleLabel("not_grid") }, /* @__PURE__ */ React.createElement(Database, { size: 16 }), "Add selected images to non-grid training data"))), /* @__PURE__ */ React.createElement(TrainingPanel, { training, onTrain: retrain, onCancel: () => training?.jobId && cancelJob(training.jobId) }), !settings.showResults && run && run.status !== "idle" ? /* @__PURE__ */ React.createElement("div", { className: "igd-results-hidden" }, "Classification results are hidden by the installed-extension setting.") : run?.items?.length > 0 ? /* @__PURE__ */ React.createElement(React.Fragment, null, /* @__PURE__ */ React.createElement("div", { className: `igd-grid${pageLoading ? " igd-page-loading" : ""}` }, run.items.map((item) => /* @__PURE__ */ React.createElement(
    GridTile,
    {
      key: item.imageId,
      item,
      selectable: !deleting,
      selected: isSelected(selection, item.imageId),
      onToggle: () => toggleSelected(item.imageId),
      onPreview: (event) => showPreview(item, event),
      onPreviewEnd: () => setPreview(null)
    }
  ))), /* @__PURE__ */ React.createElement(Pagination, { page: run.page, totalPages: run.totalPages, disabled: active || deleting, onChange: async (next) => {
    setPage(next), setPageLoading(!0), setPreview(null);
    try {
      await refresh(next, view, run?.runId);
    } catch (reason) {
      setError(reason.message);
    } finally {
      setPageLoading(!1);
    }
  } })) : run?.status === "completed" ? /* @__PURE__ */ React.createElement("div", { className: "igd-empty" }, /* @__PURE__ */ React.createElement(Grid3X3, { size: 34 }), /* @__PURE__ */ React.createElement("strong", null, "No image grids found"), /* @__PURE__ */ React.createElement("span", null, "Try another path scope or run the classifier again.")) : run?.status === "idle" ? /* @__PURE__ */ React.createElement("div", { className: "igd-empty" }, /* @__PURE__ */ React.createElement(Grid3X3, { size: 34 }), /* @__PURE__ */ React.createElement("strong", null, "Ready to classify"), /* @__PURE__ */ React.createElement("span", null, "Choose a path scope and press Run.")) : null, folderOpen && /* @__PURE__ */ React.createElement(FolderDialog, { selected: settings.paths, onCancel: () => setFolderOpen(!1), onApply: (paths) => {
    setSettings((value) => ({ ...value, paths })), setFolderOpen(!1);
  } }), confirmOpen && run && /* @__PURE__ */ React.createElement(ConfirmDelete, { count: selectedTotal, onCancel: () => setConfirmOpen(!1), onConfirm: confirmDelete }), sampleLabel && /* @__PURE__ */ React.createElement(ConfirmTrainingData, { count: selectedTotal, label: sampleLabel, onCancel: () => setSampleLabel(null), onConfirm: () => {
    let label = sampleLabel;
    setSampleLabel(null), addSamples(label);
  } }), metadataHelpOpen && /* @__PURE__ */ React.createElement(MetadataHelp, { onClose: () => setMetadataHelpOpen(!1) }), settings.showResults && preview && /* @__PURE__ */ React.createElement(ImageHoverPreview, { preview }));
}
function GridTile({ item, selectable, selected, onToggle, onPreview, onPreviewEnd }) {
  return /* @__PURE__ */ React.createElement(
    "article",
    {
      className: `igd-tile${selected ? " igd-selected" : ""}`,
      title: item.path,
      onClick: selectable ? onToggle : void 0,
      role: selectable ? "checkbox" : void 0,
      "aria-checked": selectable ? selected : void 0,
      tabIndex: selectable ? 0 : void 0,
      onKeyDown: selectable ? (event) => {
        event.key !== "Enter" && event.key !== " " || (event.preventDefault(), onToggle());
      } : void 0
    },
    selectable && /* @__PURE__ */ React.createElement("span", { className: "igd-tile-check" }, selected ? /* @__PURE__ */ React.createElement(CheckSquare, { size: 18 }) : /* @__PURE__ */ React.createElement(Square, { size: 18 })),
    /* @__PURE__ */ React.createElement("div", { className: "igd-thumb", onPointerEnter: onPreview, onPointerMove: onPreview, onPointerLeave: onPreviewEnd }, /* @__PURE__ */ React.createElement("img", { src: `/api/stream/image/${item.imageId}/thumbnail?max=192`, alt: basename(item.path), loading: "lazy" })),
    /* @__PURE__ */ React.createElement("div", { className: "igd-tile-meta" }, /* @__PURE__ */ React.createElement("div", { className: "igd-classification" }, /* @__PURE__ */ React.createElement("strong", { className: item.isGrid ? "igd-grid-label" : "igd-not-grid-label" }, item.isGrid ? "Grid" : "Not grid", " ", formatConfidence(item.isGrid ? item.gridConfidence : 1 - item.gridConfidence)), /* @__PURE__ */ React.createElement("a", { href: `/image/${item.imageId}`, target: "_blank", rel: "noreferrer", title: "Open image in Cove", "aria-label": `Open image ${item.imageId} in Cove`, onClick: (event) => event.stopPropagation(), onKeyDown: (event) => event.stopPropagation() }, /* @__PURE__ */ React.createElement(ExternalLink, { size: 14 }))), /* @__PURE__ */ React.createElement("span", null, basename(item.path))),
    (item.inArchive || item.protectedFileCount > 0) && /* @__PURE__ */ React.createElement("div", { className: "igd-badges" }, item.inArchive && /* @__PURE__ */ React.createElement("span", null, "archive"), item.protectedFileCount > 0 && /* @__PURE__ */ React.createElement("span", null, item.protectedFileCount, " protected"))
  );
}
function ImageHoverPreview({ preview }) {
  return /* @__PURE__ */ React.createElement("div", { className: "igd-hover-preview", style: { left: preview.left, top: preview.top, width: preview.size, height: preview.size } }, /* @__PURE__ */ React.createElement("img", { src: `/api/stream/image/${preview.item.imageId}/thumbnail?max=900`, alt: "" }));
}
function MetadataHelp({ onClose }) {
  return /* @__PURE__ */ React.createElement(Modal, { title: "Classification metadata added to Cove", onClose }, /* @__PURE__ */ React.createElement("div", { className: "igd-help-copy" }, /* @__PURE__ */ React.createElement("p", null, "By default, each classified image receives exactly one tag: ", /* @__PURE__ */ React.createElement("strong", null, "Image Grid Deleter: Grid"), " or ", /* @__PURE__ */ React.createElement("strong", null, "Image Grid Deleter: Not grid"), "."), /* @__PURE__ */ React.createElement("p", null, "The extension also creates the numeric image custom field ", /* @__PURE__ */ React.createElement("strong", null, "Image grid probability (%)"), ". It stores the raw likelihood that an image is a grid, rounded to one tenth of a percent. Sort descending to see the most grid-like images first, or use Cove's numeric custom-field filters."), /* @__PURE__ */ React.createElement("p", null, "The exact unrounded probability, model version, and classification run ID are retained in tag provenance and the extension's local run database."), /* @__PURE__ */ React.createElement("h3", null, "Removing the metadata later"), /* @__PURE__ */ React.createElement("ol", null, /* @__PURE__ */ React.createElement("li", null, "Turn off ", /* @__PURE__ */ React.createElement("strong", null, "Add classification metadata to Cove"), " here to stop future writes."), /* @__PURE__ */ React.createElement("li", null, "Delete the two ", /* @__PURE__ */ React.createElement("strong", null, "Image Grid Deleter:"), " tags in Cove's Tags interface."), /* @__PURE__ */ React.createElement("li", null, "Delete ", /* @__PURE__ */ React.createElement("strong", null, "Image grid probability (%)"), " in Cove's Custom Fields settings to remove its saved values.")), /* @__PURE__ */ React.createElement("p", null, "Removing this metadata does not delete images, training samples, models, or saved extension runs. Enabling publication again recreates missing extension-owned definitions.")), /* @__PURE__ */ React.createElement("div", { className: "igd-modal-actions" }, /* @__PURE__ */ React.createElement("button", { className: "igd-primary", onClick: onClose }, "Got it")));
}
function TrainingPanel({ training, onTrain, onCancel }) {
  let active = ["queued", "running"].includes(training?.status);
  return /* @__PURE__ */ React.createElement("section", { className: "igd-training-panel" }, /* @__PURE__ */ React.createElement("div", { className: "igd-training-heading" }, /* @__PURE__ */ React.createElement("div", null, /* @__PURE__ */ React.createElement("strong", null, "Developer model training"), /* @__PURE__ */ React.createElement("span", null, training?.stage || "Ready")), /* @__PURE__ */ React.createElement("div", null, /* @__PURE__ */ React.createElement("button", { className: "igd-primary", disabled: active, onClick: onTrain }, active ? /* @__PURE__ */ React.createElement(Loader2, { className: "igd-spin", size: 16 }) : /* @__PURE__ */ React.createElement(RotateCw, { size: 16 }), "Retrain and build"), active && /* @__PURE__ */ React.createElement("button", { className: "igd-secondary", onClick: onCancel }, "Cancel"))), active && /* @__PURE__ */ React.createElement("div", { className: "igd-training-progress", role: "progressbar", "aria-valuemin": "0", "aria-valuemax": "100", "aria-valuenow": Math.round((training?.progress || 0) * 100) }, /* @__PURE__ */ React.createElement("span", { style: { width: `${Math.round((training?.progress || 0) * 100)}%` } })), training?.candidateVersion && /* @__PURE__ */ React.createElement("span", null, "Candidate model: ", training.candidateVersion), training?.bestValidationAccuracy != null && /* @__PURE__ */ React.createElement("span", null, "Best checkpoint", training.bestEpoch ? `: epoch ${training.bestEpoch}` : "", " \u2014 ", (training.bestValidationAccuracy * 100).toFixed(1), "% validation accuracy", training.bestValidationLoss != null ? `, ${training.bestValidationLoss.toFixed(3)} validation loss` : ""), training?.evaluationAccuracy != null && /* @__PURE__ */ React.createElement("span", null, "Prediction-set accuracy: ", (training.evaluationAccuracy * 100).toFixed(1), "%"), training?.winningSeed != null && /* @__PURE__ */ React.createElement("span", null, "Winning seed: ", training.winningSeed, " \u2014 false grids ", training.evaluationFalseGrids, ", detected grids ", training.evaluationGridCorrect), training?.qualityGateMessage && /* @__PURE__ */ React.createElement("span", null, training.qualityGateMessage), (training?.baselineMetrics || training?.candidateMetrics) && /* @__PURE__ */ React.createElement(TrainingComparison, { training }), training?.error && /* @__PURE__ */ React.createElement("div", { className: "igd-alert igd-error" }, training.error), training?.updateStaged && /* @__PURE__ */ React.createElement("div", { className: "igd-alert igd-success" }, /* @__PURE__ */ React.createElement(ShieldCheck, { size: 17 }), "Version ", training.builtVersion, " is built and staged. Restart Cove to load the new developer extension."), training?.output?.length > 0 && /* @__PURE__ */ React.createElement(TrainingConsole, { lines: training.output }));
}
function TrainingComparison({ training }) {
  let rows = [
    ["Overall accuracy", "accuracy", (value) => `${(value * 100).toFixed(1)}%`],
    ["Grid recall", "gridRecall", (value) => `${(value * 100).toFixed(1)}%`],
    ["False-grid rate", "falseGridRate", (value) => `${(value * 100).toFixed(1)}%`],
    ["False grids", "falseGrids", String],
    ["Evaluated", "total", String],
    ["Skipped", "skipped", String]
  ], render = (metrics, key, formatter) => metrics?.[key] == null ? "\xE2\u20AC\u201D" : formatter(metrics[key]);
  return /* @__PURE__ */ React.createElement("div", { className: "igd-training-comparison" }, /* @__PURE__ */ React.createElement("div", null, /* @__PURE__ */ React.createElement("strong", null, "Prediction-set comparison"), /* @__PURE__ */ React.createElement("span", null, "Active ", training.baselineVersion || training.activeModelVersion || "model"), /* @__PURE__ */ React.createElement("span", null, "Candidate ", training.candidateVersion || "model")), rows.map(([label, key, formatter]) => /* @__PURE__ */ React.createElement("div", { key }, /* @__PURE__ */ React.createElement("span", null, label), /* @__PURE__ */ React.createElement("span", null, render(training.baselineMetrics, key, formatter)), /* @__PURE__ */ React.createElement("span", null, render(training.candidateMetrics, key, formatter)))));
}
function TrainingConsole({ lines }) {
  let ref = useRef(null);
  return useEffect(() => {
    ref.current && (ref.current.scrollTop = ref.current.scrollHeight);
  }, [lines.length]), /* @__PURE__ */ React.createElement("div", { className: "igd-training-console" }, /* @__PURE__ */ React.createElement("div", null, "Training and build output"), /* @__PURE__ */ React.createElement("pre", { ref }, lines.join(`
`)));
}
function Pagination({ page, totalPages, disabled, onChange }) {
  return totalPages <= 1 ? null : /* @__PURE__ */ React.createElement("nav", { className: "igd-pagination" }, /* @__PURE__ */ React.createElement("button", { disabled: disabled || page <= 1, onClick: () => onChange(page - 1) }, /* @__PURE__ */ React.createElement(ArrowLeft, { size: 17 })), /* @__PURE__ */ React.createElement("span", null, "Page ", page, " of ", totalPages), /* @__PURE__ */ React.createElement("button", { disabled: disabled || page >= totalPages, onClick: () => onChange(page + 1) }, /* @__PURE__ */ React.createElement(ArrowRight, { size: 17 })));
}
function FolderDialog({ selected, onCancel, onApply }) {
  let [draft, setDraft] = useState([...selected]), [roots, setRoots] = useState([]), [error, setError] = useState("");
  return useEffect(() => {
    loadFolders().then(setRoots).catch((reason) => setError(reason.message));
  }, []), /* @__PURE__ */ React.createElement(Modal, { title: "Choose library folders", onClose: onCancel }, /* @__PURE__ */ React.createElement("p", { className: "igd-modal-copy" }, "Selecting a folder applies to its entire subtree."), error && /* @__PURE__ */ React.createElement("div", { className: "igd-alert igd-error" }, error), /* @__PURE__ */ React.createElement("div", { className: "igd-folder-tree" }, roots.length ? roots.map((root) => /* @__PURE__ */ React.createElement(
    FolderNode,
    {
      key: root.path,
      node: root,
      depth: 0,
      selected: draft,
      onToggle: (path, checked) => setDraft((value) => togglePath(value, path, checked))
    }
  )) : /* @__PURE__ */ React.createElement("span", null, "Loading folders\u2026")), /* @__PURE__ */ React.createElement("div", { className: "igd-modal-actions" }, /* @__PURE__ */ React.createElement("button", { className: "igd-secondary", onClick: onCancel }, "Cancel"), /* @__PURE__ */ React.createElement("button", { className: "igd-primary", onClick: () => onApply(draft) }, "Apply")));
}
function FolderNode({ node, depth, selected, onToggle }) {
  let [expanded, setExpanded] = useState(!1), [children, setChildren] = useState(null);
  async function expand() {
    let next = !expanded;
    setExpanded(next), next && node.hasChildren && children === null && setChildren(await loadFolders(node.path));
  }
  return /* @__PURE__ */ React.createElement("div", null, /* @__PURE__ */ React.createElement("div", { className: "igd-folder-row", style: { paddingLeft: depth * 16 + 4 } }, node.hasChildren ? /* @__PURE__ */ React.createElement("button", { "aria-label": expanded ? "Collapse folder" : "Expand folder", onClick: expand }, expanded ? /* @__PURE__ */ React.createElement(ChevronDown, { size: 14 }) : /* @__PURE__ */ React.createElement(ChevronRight, { size: 14 })) : /* @__PURE__ */ React.createElement("i", null), /* @__PURE__ */ React.createElement("label", null, /* @__PURE__ */ React.createElement("input", { type: "checkbox", checked: selected.includes(node.path), onChange: (event) => onToggle(node.path, event.target.checked) }), /* @__PURE__ */ React.createElement("span", { title: node.path }, node.name))), expanded && (children || []).map((child) => /* @__PURE__ */ React.createElement(FolderNode, { key: child.path, node: child, depth: depth + 1, selected, onToggle })));
}
function ConfirmDelete({ count, onCancel, onConfirm }) {
  return /* @__PURE__ */ React.createElement(Modal, { title: "Delete selected grids", onClose: onCancel }, /* @__PURE__ */ React.createElement("p", { className: "igd-confirm" }, "Delete the ", count, " selected grid image", count === 1 ? "" : "s", "?"), /* @__PURE__ */ React.createElement("p", { className: "igd-modal-copy" }, "This permanently deletes the selected Cove images and normal source files. Archive containers are left unchanged."), /* @__PURE__ */ React.createElement("div", { className: "igd-modal-actions" }, /* @__PURE__ */ React.createElement("button", { className: "igd-secondary", onClick: onCancel }, "Cancel"), /* @__PURE__ */ React.createElement("button", { className: "igd-danger", onClick: onConfirm }, "Delete selected")));
}
function ConfirmTrainingData({ count, label, onCancel, onConfirm }) {
  let name = label === "grid" ? "grid" : "non-grid";
  return /* @__PURE__ */ React.createElement(Modal, { title: "Confirm training label", onClose: onCancel }, /* @__PURE__ */ React.createElement("p", { className: "igd-confirm" }, "Add ", count, " selected image", count === 1 ? "" : "s", " to ", name, " training data?"), /* @__PURE__ */ React.createElement("p", { className: "igd-modal-copy" }, "Please verify the category. Incorrect labels make the next model worse. The Cove Grid/Not-grid tag will be changed to match, and content hashes prevent duplicate samples."), /* @__PURE__ */ React.createElement("div", { className: "igd-modal-actions" }, /* @__PURE__ */ React.createElement("button", { className: "igd-secondary", onClick: onCancel }, "Cancel"), /* @__PURE__ */ React.createElement("button", { className: "igd-primary", onClick: onConfirm }, "Yes, add to ", name)));
}
function Modal({ title, onClose, children, dismissible = !0 }) {
  return useEffect(() => {
    let key = (event) => event.key === "Escape" && dismissible && onClose();
    return window.addEventListener("keydown", key), () => window.removeEventListener("keydown", key);
  }, [onClose, dismissible]), /* @__PURE__ */ React.createElement("div", { className: "igd-modal-backdrop", role: "presentation", onMouseDown: (event) => event.target === event.currentTarget && dismissible && onClose() }, /* @__PURE__ */ React.createElement("section", { className: "igd-modal", role: "dialog", "aria-modal": "true", "aria-label": title }, /* @__PURE__ */ React.createElement("header", null, /* @__PURE__ */ React.createElement("h2", null, title), dismissible && /* @__PURE__ */ React.createElement("button", { onClick: onClose }, /* @__PURE__ */ React.createElement(X, { size: 18 }))), children));
}
function TrainingDataActionHost() {
  let [request2, setRequest] = useState(null), [label, setLabel] = useState(null), [stage, setStage] = useState("choose"), [result, setResult] = useState(null), [error, setError] = useState(""), settled = useRef(!1);
  useEffect(() => {
    let open = (event) => {
      request2 && !settled.current && request2.resolve({ cancelled: !0, suppressToast: !0 }), settled.current = !1, setRequest(event.detail), setLabel(null), setStage("choose"), setResult(null), setError("");
    };
    return window.addEventListener(TRAINING_DATA_ACTION_EVENT, open), () => window.removeEventListener(TRAINING_DATA_ACTION_EVENT, open);
  }, [request2]);
  function finish(value) {
    settled.current || (settled.current = !0, request2?.resolve(value));
  }
  function close() {
    stage !== "working" && (finish({ cancelled: stage !== "complete", suppressToast: !0 }), setRequest(null));
  }
  function choose(nextLabel) {
    setLabel(nextLabel), setStage("confirm"), setError("");
  }
  async function add() {
    setStage("working"), setError("");
    try {
      let value = await addImageTrainingSamples(request2.imageIds, label);
      setResult(value), setStage("complete"), finish({ suppressToast: !0 });
    } catch (reason) {
      setError(reason.message), setStage("confirm");
    }
  }
  if (!request2) return null;
  let name = label === "grid" ? "grid" : "non-grid";
  return /* @__PURE__ */ React.createElement(Modal, { title: "Add to training data", onClose: close, dismissible: stage !== "working" }, stage === "choose" && /* @__PURE__ */ React.createElement(React.Fragment, null, /* @__PURE__ */ React.createElement("p", { className: "igd-modal-copy" }, "Choose the correct classification for the ", request2.imageIds.length, " selected image", request2.imageIds.length === 1 ? "" : "s", "."), /* @__PURE__ */ React.createElement("div", { className: "igd-training-labels" }, /* @__PURE__ */ React.createElement("button", { className: "igd-primary", onClick: () => choose("grid") }, /* @__PURE__ */ React.createElement(Grid3X3, { size: 17 }), "Add to grid training data"), /* @__PURE__ */ React.createElement("button", { className: "igd-primary", onClick: () => choose("not_grid") }, /* @__PURE__ */ React.createElement(Database, { size: 17 }), "Add to non-grid training data")), /* @__PURE__ */ React.createElement("div", { className: "igd-modal-actions" }, /* @__PURE__ */ React.createElement("button", { className: "igd-secondary", onClick: close }, "Cancel"))), (stage === "confirm" || stage === "working") && /* @__PURE__ */ React.createElement(React.Fragment, null, /* @__PURE__ */ React.createElement("p", { className: "igd-confirm" }, "Are you sure you want to add ", request2.imageIds.length, " selected image", request2.imageIds.length === 1 ? "" : "s", " to ", name, " training data?"), /* @__PURE__ */ React.createElement("p", { className: "igd-modal-copy" }, "Please verify the category. Incorrect labels make the next model worse. The Cove Grid/Not-grid tag will be changed to match, and content hashes prevent duplicate samples."), error && /* @__PURE__ */ React.createElement("div", { className: "igd-alert igd-error" }, /* @__PURE__ */ React.createElement(AlertTriangle, { size: 17 }), error), /* @__PURE__ */ React.createElement("div", { className: "igd-modal-actions" }, /* @__PURE__ */ React.createElement("button", { className: "igd-secondary", disabled: stage === "working", onClick: () => setStage("choose") }, "Back"), /* @__PURE__ */ React.createElement("button", { className: "igd-primary", disabled: stage === "working", onClick: add }, stage === "working" && /* @__PURE__ */ React.createElement(Loader2, { className: "igd-spin", size: 16 }), "Yes, add to ", name))), stage === "complete" && /* @__PURE__ */ React.createElement(React.Fragment, null, /* @__PURE__ */ React.createElement("div", { className: "igd-alert igd-success" }, /* @__PURE__ */ React.createElement(ShieldCheck, { size: 17 }), "Training data and Cove tags updated."), /* @__PURE__ */ React.createElement("p", { className: "igd-modal-copy" }, result.added, " added, ", result.relabeled, " relabelled, ", result.skipped, " already present", result.errors?.length ? `, ${result.errors.length} failed` : "", "."), result.errors?.length > 0 && /* @__PURE__ */ React.createElement("details", null, /* @__PURE__ */ React.createElement("summary", null, "Show failures"), /* @__PURE__ */ React.createElement("ul", null, result.errors.map((item, index) => /* @__PURE__ */ React.createElement("li", { key: index }, item)))), /* @__PURE__ */ React.createElement("div", { className: "igd-modal-actions" }, /* @__PURE__ */ React.createElement("button", { className: "igd-primary", onClick: close }, "Done"))));
}
function DeleteImageGridsSettingsPanel() {
  return /* @__PURE__ */ React.createElement(DeveloperSettingsPanel, null);
}
function ClassificationDisplaySetting() {
  let [settings, setSettings] = useState(null), [saving, setSaving] = useState(!1), [error, setError] = useState(""), [notice, setNotice] = useState("");
  useEffect(() => {
    loadSettings().then((value) => setSettings(normalizeSettings(value))).catch((reason) => setError(reason.message));
  }, []);
  async function toggle(showResults) {
    if (!settings) return;
    let previous = settings, next = { ...settings, showResults };
    setSettings(next), setSaving(!0), setError(""), setNotice("");
    try {
      setSettings(normalizeSettings(await saveSettings(next))), setNotice("Result display preference saved.");
    } catch (reason) {
      setSettings(previous), setError(reason.message);
    } finally {
      setSaving(!1);
    }
  }
  return /* @__PURE__ */ React.createElement("div", { className: "igd-display-setting" }, error && /* @__PURE__ */ React.createElement("div", { className: "igd-alert igd-error" }, error), notice && /* @__PURE__ */ React.createElement("div", { className: "igd-alert igd-success" }, notice), /* @__PURE__ */ React.createElement("label", null, /* @__PURE__ */ React.createElement("input", { type: "checkbox", disabled: !settings || saving, checked: settings?.showResults === !0, onChange: (event) => toggle(event.target.checked) }), /* @__PURE__ */ React.createElement("span", null, /* @__PURE__ */ React.createElement("strong", null, "Show results of classification on the extension page"), /* @__PURE__ */ React.createElement("small", null, "Warning: enabling this setting increases memory footprint."))));
}
function DeveloperSettingsPanel() {
  let [settings, setSettings] = useState({ trainingRepoPath: "", extensionRepoPath: "" }), [status, setStatus] = useState(null), [saving, setSaving] = useState(!1), [error, setError] = useState(""), [notice, setNotice] = useState("");
  useEffect(() => {
    loadDeveloperSettings().then((value) => {
      setSettings(value.settings), setStatus(value);
    }).catch((reason) => setError(reason.message));
  }, []);
  async function save() {
    setSaving(!0), setError(""), setNotice("");
    try {
      let value = await saveDeveloperSettings(settings);
      setSettings(value.settings), setStatus(value), setNotice("Developer paths saved and validated.");
    } catch (reason) {
      setError(reason.message);
    } finally {
      setSaving(!1);
    }
  }
  return /* @__PURE__ */ React.createElement("div", { className: "igd-dev-settings" }, /* @__PURE__ */ React.createElement("div", null, /* @__PURE__ */ React.createElement("strong", null, "Image Grid Deleter \u2014 Developer Edition"), /* @__PURE__ */ React.createElement("p", null, "Collect corrections, retrain the 384px model, and build updated extension packages without leaving Cove.")), error && /* @__PURE__ */ React.createElement("div", { className: "igd-alert igd-error" }, error), notice && /* @__PURE__ */ React.createElement("div", { className: "igd-alert igd-success" }, notice), /* @__PURE__ */ React.createElement(ClassificationDisplaySetting, null), /* @__PURE__ */ React.createElement("label", null, /* @__PURE__ */ React.createElement("span", null, "Model training repository"), /* @__PURE__ */ React.createElement(
    "input",
    {
      value: settings.trainingRepoPath || "",
      onChange: (event) => setSettings({ ...settings, trainingRepoPath: event.target.value }),
      placeholder: "C:\\\\path\\\\to\\\\img_grid_classifier"
    }
  )), /* @__PURE__ */ React.createElement("label", null, /* @__PURE__ */ React.createElement("span", null, "Extension source repository"), /* @__PURE__ */ React.createElement(
    "input",
    {
      value: settings.extensionRepoPath || "",
      onChange: (event) => setSettings({ ...settings, extensionRepoPath: event.target.value }),
      placeholder: "C:\\\\path\\\\to\\\\img_grid_deleter"
    }
  )), status?.repository?.valid && /* @__PURE__ */ React.createElement("p", { className: "igd-repo-status" }, "Dataset: ", status.repository.gridCount, " grids / ", status.repository.notGridCount, " non-grids"), /* @__PURE__ */ React.createElement("div", { className: "igd-settings-actions" }, /* @__PURE__ */ React.createElement("button", { className: "igd-secondary", disabled: saving, onClick: save }, /* @__PURE__ */ React.createElement(Save, { size: 16 }), "Save and validate"), /* @__PURE__ */ React.createElement("button", { className: "igd-primary", onClick: () => window.location.assign("/extensions/delete-image-grids") }, /* @__PURE__ */ React.createElement(Grid3X3, { size: 17 }), "Open Image Grid Deleter")));
}
var index_default = {
  components: { DeleteImageGridsPage, DeleteImageGridsSettingsPanel, TrainingDataActionHost },
  actionHandlers: { openTrainingDataDialog }
};
export {
  DeleteImageGridsPage,
  DeleteImageGridsSettingsPanel,
  TrainingDataActionHost,
  index_default as default,
  openTrainingDataDialog
};
