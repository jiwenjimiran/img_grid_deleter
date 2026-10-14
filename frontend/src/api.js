const BASE_URL = "/api/ext/delete-image-grids";
const ACCESS_TOKEN_KEY = "cove_access_token";
const REFRESH_TOKEN_KEY = "cove_refresh_token";
const SHARE_TOKEN_KEY = "cove_share_token";
const SHARE_PASSWORD_KEY = "cove_share_password";
const REFRESH_BEFORE_EXPIRY_MS = 60_000;
let refreshInFlight = null;

export class ApiRequestError extends Error {
  constructor(status, message, body = null) {
    super(message || `API request failed with status ${status}.`);
    this.name = "ApiRequestError";
    this.status = status;
    this.body = body;
  }
}

export async function request(path, options = {}) {
  const { timeoutMs = 0, ...fetchOptions } = options;
  const controller = timeoutMs > 0 ? new AbortController() : null;
  const timeout = controller ? setTimeout(() => controller.abort(), timeoutMs) : null;
  if (controller && fetchOptions.signal) fetchOptions.signal.addEventListener("abort", () => controller.abort(), { once: true });
  let response;
  try {
    response = await authenticatedFetch(path, {
      ...fetchOptions, signal: controller?.signal || fetchOptions.signal,
      headers: { "Content-Type": "application/json", ...(fetchOptions.headers || {}) },
    });
  } catch (reason) {
    if (controller?.signal.aborted && !fetchOptions.signal?.aborted) throw new Error("The request timed out. Try again.");
    throw reason;
  } finally { if (timeout) clearTimeout(timeout); }
  const text = await response.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = text; }
  if (!response.ok) throw new ApiRequestError(response.status, body?.message || body?.detail || body || response.statusText, body);
  return body;
}

export async function authenticatedFetch(path, options = {}) {
  await ensureFreshAccessToken();
  const initial = readAuthState();
  let response = await fetchWithAuth(path, options, initial);
  if (response.status !== 401) return response;
  const latest = readAuthState();
  if (latest.accessToken && latest.accessToken !== initial.accessToken) {
    response = await fetchWithAuth(path, options, latest);
    if (response.status !== 401) return response;
  }
  if (!latest.refreshToken || latest.shareToken) return failAuthentication();
  await refreshAccessToken(latest.accessToken, latest.refreshToken);
  response = await fetchWithAuth(path, options, readAuthState());
  if (response.status === 401) return failAuthentication();
  return response;
}

async function ensureFreshAccessToken() {
  const auth = readAuthState();
  if (auth.shareToken || !auth.accessToken || !auth.refreshToken || !tokenExpiresSoon(auth.accessToken)) return;
  await refreshAccessToken(auth.accessToken, auth.refreshToken);
}

async function refreshAccessToken(expectedAccessToken, expectedRefreshToken) {
  const latest = readAuthState();
  if (latest.accessToken && latest.accessToken !== expectedAccessToken && !tokenExpiresSoon(latest.accessToken)) return latest.accessToken;
  if (refreshInFlight) return refreshInFlight;
  refreshInFlight = (async () => {
    const response = await fetch("/api/auth/refresh", {
      method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken: expectedRefreshToken }),
    });
    if (!response.ok) throw new Error("Cove authentication could not be refreshed.");
    const body = await response.json();
    if (!body?.token) throw new Error("Cove did not return a refreshed access token.");
    writeStoredToken(ACCESS_TOKEN_KEY, body.token);
    writeStoredToken(REFRESH_TOKEN_KEY, body.refreshToken || expectedRefreshToken);
    return body.token;
  })();
  try { return await refreshInFlight; } finally { refreshInFlight = null; }
}

function fetchWithAuth(path, options, auth) {
  const headers = new Headers(options.headers || {});
  if (auth.shareToken) {
    headers.set("X-Share-Token", auth.shareToken);
    if (auth.sharePassword) headers.set("X-Share-Password", auth.sharePassword);
  } else if (auth.accessToken && !headers.has("Authorization")) {
    headers.set("Authorization", `Bearer ${auth.accessToken}`);
  }
  return fetch(path, { ...options, credentials: "same-origin", headers });
}

function readAuthState() {
  return {
    accessToken: readStoredToken("localStorage", ACCESS_TOKEN_KEY),
    refreshToken: readStoredToken("localStorage", REFRESH_TOKEN_KEY),
    shareToken: readStoredToken("sessionStorage", SHARE_TOKEN_KEY),
    sharePassword: readStoredToken("sessionStorage", SHARE_PASSWORD_KEY),
  };
}
function readStoredToken(storage, key) { try { return globalThis[storage]?.getItem(key) || null; } catch { return null; } }
function writeStoredToken(key, value) { try { globalThis.localStorage?.setItem(key, value); } catch { /* unavailable */ } }
function tokenExpiresSoon(token, now = Date.now()) {
  try {
    const payload = token.split(".")[1];
    const normalized = payload.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(payload.length / 4) * 4, "=");
    return Number(JSON.parse(globalThis.atob(normalized)).exp || 0) * 1000 <= now + REFRESH_BEFORE_EXPIRY_MS;
  } catch { return false; }
}
function failAuthentication() {
  try { globalThis.window?.dispatchEvent(new CustomEvent("cove-auth-required")); } catch { /* tests */ }
  throw new Error("Authentication is required.");
}

export const loadSettings = () => request(`${BASE_URL}/settings`);
export const saveSettings = (settings) => request(`${BASE_URL}/settings`, { method: "PUT", body: JSON.stringify(settings) });
export const startRun = (settings) => request(`${BASE_URL}/runs${settings.publishMetadata === false ? "" : "/published"}`, { method: "POST", body: JSON.stringify(settings) });
export const resumeRun = (runId) => request(`${BASE_URL}/runs/resume`, { method: "POST", body: JSON.stringify({ runId }) });
export const loadRuns = () => request(`${BASE_URL}/runs`);
export const loadRunStatus = (runId) => request(`${BASE_URL}/runs/status${runId ? `?runId=${encodeURIComponent(runId)}` : ""}`);
export const loadRun = (page = 1, pageSize = 200, view = "grids", runId = null, signal = null, includeItems = true) => request(`${BASE_URL}/runs/current?page=${page}&pageSize=${pageSize}&view=${encodeURIComponent(view)}&includeItems=${includeItems ? "true" : "false"}${runId ? `&runId=${encodeURIComponent(runId)}` : ""}`, { timeoutMs: 30000, signal });
export const loadSelectionBatch = (selection, cursorConfidence = null, cursorImageId = 0, batchSize = 100) => request(`${BASE_URL}/runs/selection/batch`, {
  method: "POST", body: JSON.stringify({ selection, cursorConfidence, cursorImageId, batchSize }),
});
export const loadFolders = (path) => request(`/api/metadata/library-folders${path ? `?path=${encodeURIComponent(path)}` : ""}`);
export const cancelJob = (jobId) => request(`/api/jobs/${encodeURIComponent(jobId)}`, { method: "DELETE" });
export const deleteImages = (ids) => request("/api/images/bulk", {
  method: "DELETE", body: JSON.stringify({ ids, deleteFiles: true, deleteGenerated: true }),
});
export const pruneFiles = (runId, imageId, fileIds) => request(`${BASE_URL}/runs/prune`, {
  method: "POST", body: JSON.stringify({ runId, imageId, fileIds }),
});
export const reconcileRun = (runId) => request(`${BASE_URL}/runs/reconcile`, {
  method: "POST", body: JSON.stringify({ runId }),
});
export const loadDeveloperSettings = () => request(`${BASE_URL}/developer-settings`);
export const saveDeveloperSettings = (settings) => request(`${BASE_URL}/developer-settings`, {
  method: "PUT", body: JSON.stringify(settings),
});
export const addTrainingSamples = (runId, imageIds, label, selection = null) => request(`${BASE_URL}/training-samples`, {
  method: "POST", body: JSON.stringify({ runId, imageIds, label, selection }),
});
export const addImageTrainingSamples = (imageIds, label) => request(`${BASE_URL}/training-samples/images`, {
  method: "POST", body: JSON.stringify({ imageIds, label }), timeoutMs: 0,
});
export const startTraining = () => request(`${BASE_URL}/training`, { method: "POST" });
export const loadTrainingStatus = () => request(`${BASE_URL}/training/current`);
