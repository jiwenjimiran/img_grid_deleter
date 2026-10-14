import React from "@cove/runtime/react";
import {
  AlertTriangle, ArrowLeft, ArrowRight, ChevronDown, ChevronRight, Folder,
  CheckSquare, Database, ExternalLink, Grid3X3, Info, Loader2, Play, RotateCw, Save, ShieldCheck,
  Square, Trash2, X,
} from "@cove/runtime/lucide-react";
import * as api from "./api.js";
import { basename, classificationProgressText, createSelection, DEFAULT_SETTINGS, formatConfidence, invertSelection, isSelected, normalizeSettings, PAGE_SIZE, positionHoverPreview, selectPage, selectionCount, serializeSelection, togglePath, toggleSelection } from "./core.js";
import { deleteSelection } from "./deletion.js";

const { useEffect, useMemo, useRef, useState } = React;
const TRAINING_DATA_ACTION_EVENT = "delete-image-grids:open-training-data";

export function openTrainingDataDialog(_action, payload = {}) {
  const imageIds = [...new Set((payload.selectedIds || payload.entityIds || payload.ids || [])
    .map(Number).filter((id) => Number.isInteger(id) && id > 0))];
  if (payload.entityType && payload.entityType !== "image") throw new Error("This action only supports images.");
  if (!imageIds.length) throw new Error("Select at least one image first.");
  return new Promise((resolve, reject) => {
    window.dispatchEvent(new CustomEvent(TRAINING_DATA_ACTION_EVENT, { detail: { imageIds, resolve, reject } }));
  });
}

export function DeleteImageGridsPage() {
  const [settings, setSettings] = useState(DEFAULT_SETTINGS);
  const [run, setRun] = useState(null);
  const [page, setPage] = useState(1);
  const [view, setView] = useState("grids");
  const [loading, setLoading] = useState(true);
  const [pageLoading, setPageLoading] = useState(false);
  const [starting, setStarting] = useState(false);
  const [folderOpen, setFolderOpen] = useState(false);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deleteProgress, setDeleteProgress] = useState(null);
  const [notice, setNotice] = useState("");
  const [error, setError] = useState("");
  const [selection, setSelection] = useState(() => createSelection());
  const [preview, setPreview] = useState(null);
  const [metadataHelpOpen, setMetadataHelpOpen] = useState(false);
  const [runs, setRuns] = useState([]);
  const [addingSamples, setAddingSamples] = useState(false);
  const [training, setTraining] = useState(null);
  const [sampleLabel, setSampleLabel] = useState(null);
  const pageRequest = useRef(null);

  async function refresh(targetPage = page, targetView = view, targetRunId = run?.runId, includeItems = settings.showResults) {
    pageRequest.current?.abort();
    const controller = new AbortController();
    pageRequest.current = controller;
    const value = await api.loadRun(targetPage, PAGE_SIZE, targetView, targetRunId, controller.signal, includeItems);
    setRun(value);
    setView(value.view || targetView);
    if (value.page !== targetPage) setPage(value.page);
    if (pageRequest.current === controller) pageRequest.current = null;
    return value;
  }

  useEffect(() => {
    let active = true;
    Promise.all([api.loadSettings(), api.loadRuns()])
      .then(async ([stored, availableRuns]) => {
        const normalized = normalizeSettings(stored);
        const current = await api.loadRun(1, PAGE_SIZE, "grids", null, null, normalized.showResults);
        if (!active) return;
        setSettings(normalized);
        setRun(current);
        setRuns(availableRuns || []);
      })
      .catch((reason) => active && setError(reason.message))
      .finally(() => active && setLoading(false));
    return () => { active = false; };
  }, []);

  useEffect(() => {
    if (!run || !["queued", "running"].includes(run.status)) return;
    const timer = setInterval(() => api.loadRunStatus(run.runId).then((status) => {
      setRun((current) => current?.runId === status.runId ? { ...current, ...status, classifiedCount: status.gridCount + status.nonGridCount } : current);
      if (!["queued", "running"].includes(status.status)) refresh(page, view, status.runId, settings.showResults).catch((reason) => setError(reason.message));
    }).catch((reason) => setError(reason.message)), 1500);
    return () => clearInterval(timer);
  }, [run?.status, run?.runId, page, view, settings.showResults]);

  useEffect(() => {
    if (!__IGD_DEV_EDITION__) return;
    let mounted = true;
    const poll = () => api.loadTrainingStatus().then((value) => mounted && setTraining(value)).catch((reason) => mounted && setError(reason.message));
    poll();
    const timer = setInterval(poll, 1500);
    return () => { mounted = false; clearInterval(timer); };
  }, []);

  async function start() {
    const scope = normalizeSettings(settings);
    setStarting(true);
    setError("");
    setNotice("");
    setPage(1);
    setView("grids");
    setSelection(createSelection());
    setPreview(null);
    try {
      await api.saveSettings(scope);
      const started = await api.startRun(scope);
      setRun(await api.loadRun(1, PAGE_SIZE, "grids", started.runId, null, scope.showResults));
      setRuns(await api.loadRuns());
    } catch (reason) { setError(reason.message); }
    finally { setStarting(false); }
  }

  async function cancel() {
    if (!run?.jobId) return;
    try { await api.cancelJob(run.jobId); await refresh(page, view, run.runId); }
    catch (reason) { setError(reason.message); }
  }

  async function confirmDelete() {
    setConfirmOpen(false);
    setDeleting(true);
    setDeleteProgress({ completed: 0, total: 0, deleted: 0, failed: [] });
    setError("");
    setNotice("");
    try {
      if (!run?.runId) throw new Error("No classification run is available to delete.");
      const descriptor = serializeSelection(selection, run.runId, "grids");
      const result = await deleteSelection(descriptor, run.runId, api, setDeleteProgress);
      await refresh(1, "grids", run.runId);
      setPage(1);
      setSelection(createSelection());
      setNotice(result.failed.length
        ? `Deleted ${result.deleted} grid image${result.deleted === 1 ? "" : "s"}; ${result.failed.length} could not be deleted.`
        : `Deleted ${result.deleted} grid image${result.deleted === 1 ? "" : "s"}.`);
    } catch (reason) { setError(reason.message); }
    finally { setDeleting(false); }
  }

  function toggleSelected(imageId) {
    setSelection((current) => toggleSelection(current, imageId));
  }

  async function changeView(targetView) {
    setView(targetView); setPage(1); setSelection(createSelection()); setPreview(null);
    try { await refresh(1, targetView, run?.runId); } catch (reason) { setError(reason.message); }
  }

  function selectAllResults() { setSelection(createSelection(true)); }
  function invertAllResults() { setSelection((current) => invertSelection(current)); }

  function showPreview(item, event) {
    const position = positionHoverPreview(event.clientX, event.clientY, window.innerWidth, window.innerHeight);
    setPreview({ item, ...position });
  }

  async function addSamples(label) {
    if (!run?.runId || selectedTotal === 0) return;
    setAddingSamples(true); setError(""); setNotice("");
    try {
      const result = await api.addTrainingSamples(run.runId, null, label, serializeSelection(selection, run.runId, view));
      setNotice(`Training data and Cove tags updated: ${result.added} added, ${result.relabeled} relabelled, ${result.skipped} already present${result.errors.length ? `, ${result.errors.length} failed` : ""}.`);
    } catch (reason) { setError(reason.message); }
    finally { setAddingSamples(false); }
  }

  async function retrain() {
    setError(""); setNotice("");
    try { await api.startTraining(); setTraining(await api.loadTrainingStatus()); }
    catch (reason) { setError(reason.message); }
  }

  if (loading) return <div className="igd-loading"><Loader2 className="igd-spin" />Loading Image Grid Deleter</div>;
  const active = ["queued", "running"].includes(run?.status);
  const working = starting || active;
  const currentFailure = !working && run?.status === "failed" && run.error
    && (!run.modelVersion || !run.activeModelVersion || run.modelVersion === run.activeModelVersion);
  const viewTotal = view === "grids" ? run?.gridCount : view === "non-grids" ? run?.nonGridCount : run?.classifiedCount;
  const selectedTotal = selectionCount(selection, viewTotal || 0);
  return <div className="igd-page">
    <header className="igd-header">
      <div><div className="igd-title"><Grid3X3 size={24} /><h1>Delete Image Grids</h1><span className="igd-version">Model {training?.activeModelVersion || run?.activeModelVersion || "loading"}</span></div><p>Classify Cove images in the selected folders, review the matches, then permanently delete them.</p></div>
      <div className="igd-header-actions">
        <button className="igd-danger" disabled={working || deleting || view !== "grids" || selectedTotal === 0} onClick={() => setConfirmOpen(true)}><Trash2 size={17} />Delete selected</button>
      </div>
    </header>

    {error && <div className="igd-alert igd-error"><AlertTriangle size={17} />{error}</div>}
    {notice && <div className="igd-alert igd-success"><ShieldCheck size={17} />{notice}</div>}
    {working && <div className="igd-alert igd-working" role="status"><Loader2 className="igd-spin" size={17} />
      {classificationProgressText(run, starting)}
    </div>}
    {currentFailure && <div className="igd-alert igd-error"><AlertTriangle size={17} />Classification failed. Press Run to try again.</div>}
    {!working && run?.status === "completed" && run.error && <div className="igd-alert igd-warning"><AlertTriangle size={17} />Classification completed with a warning: {run.error}</div>}

    <section className="igd-controls">
      <div className="igd-mode" role="group" aria-label="Folder scope">
        {[['all', 'All'], ['include', 'Include'], ['exclude', 'Exclude']].map(([mode, label]) =>
          <button key={mode} className={settings.folderMode === mode ? "igd-active" : ""} aria-pressed={settings.folderMode === mode}
            onClick={() => setSettings((value) => ({ ...value, folderMode: mode }))}>{label}</button>)}
      </div>
      <button className="igd-secondary" disabled={settings.folderMode === "all"} onClick={() => setFolderOpen(true)}>
        <Folder size={16} />{settings.folderMode === "all" ? "All library folders" : settings.paths.length ? `${settings.paths.length} folder${settings.paths.length === 1 ? "" : "s"}` : "Choose folders"}
      </button>
      <label className="igd-metadata-toggle"><input type="checkbox" checked={settings.publishMetadata !== false}
        onChange={(event) => setSettings((value) => ({ ...value, publishMetadata: event.target.checked }))} />
        <span>Add classification metadata to Cove</span></label>
      <button className="igd-icon-button" title="What metadata is added?" aria-label="What metadata is added?" onClick={() => setMetadataHelpOpen(true)}><Info size={16} /></button>
      <button className="igd-primary" disabled={working || deleting || (settings.folderMode !== "all" && settings.paths.length === 0)} onClick={start}>
        {working ? <Loader2 className="igd-spin" size={17} /> : <Play size={17} />}{starting ? "Starting…" : active ? "Running…" : "Run"}
      </button>
      {active && <button className="igd-secondary" onClick={cancel}><X size={16} />Cancel</button>}
      {!active && run?.resumable && <button className="igd-secondary" onClick={async () => {
        try { await api.resumeRun(run.runId); await refresh(page, view, run.runId); } catch (reason) { setError(reason.message); }
      }}><RotateCw size={16} />Resume</button>}
      {runs.length > 1 && <select className="igd-run-select" value={run?.runId || ""} onChange={async (event) => {
        const id = event.target.value; setPage(1); setSelection(createSelection()); setPreview(null);
        try { await refresh(1, view, id); } catch (reason) { setError(reason.message); }
      }}>{runs.map((item, index) => <option key={item.runId} value={item.runId}>{index === 0 ? "Current" : "Previous"} — {item.status} — model {item.modelVersion}</option>)}</select>}
    </section>

    {settings.folderMode !== "all" && settings.paths.length > 0 && <div className="igd-paths">
      <strong>{settings.folderMode === "exclude" ? "Excluding" : "Including"}</strong>
      {settings.paths.map((path) => <span key={path} title={path}>{path}</span>)}
    </div>}

    {run && run.status !== "idle" && <section className="igd-status">
      <button disabled={!settings.showResults} className={view === "grids" ? "igd-kpi-active" : ""} onClick={() => changeView("grids")}><strong>{run.gridCount || 0}</strong><span>grids</span></button>
      <button disabled={!settings.showResults} className={view === "non-grids" ? "igd-kpi-active" : ""} onClick={() => changeView("non-grids")}><strong>{run.nonGridCount || 0}</strong><span>non-grids</span></button>
      <button disabled={!settings.showResults} className={view === "classified" ? "igd-kpi-active" : ""} onClick={() => changeView("classified")}><strong>{run.classifiedCount || 0}</strong><span>classified</span></button>
      <div><strong>{run.skippedCount || 0}</strong><span>skipped</span></div>
      <div><strong>{run.processedCount || 0}</strong><span>processed</span></div>
      {active && <div className="igd-progress igd-progress-indeterminate" aria-label="Classification in progress"><span /></div>}
    </section>}

    {deleting && <div className="igd-alert igd-working"><Loader2 className="igd-spin" size={17} />
      Deleting… {deleteProgress?.total ? `${deleteProgress.completed}/${deleteProgress.total} batches` : "preparing batches"}
    </div>}

    {settings.showResults && run && run.status !== "idle" && <section className="igd-selection-bar">
      <strong>{selectedTotal} selected</strong>
      <button className="igd-secondary" disabled={!run?.items?.length} onClick={() => setSelection((current) => selectPage(current, run.items.map((item) => item.imageId)))}>Select this page</button>
      <button className="igd-secondary" disabled={!run?.classifiedCount || active} onClick={invertAllResults}>
        Invert selection
      </button>
      <button className="igd-secondary" disabled={!run?.classifiedCount || active} onClick={selectAllResults}>
        Select all {view === "grids" ? run.gridCount : view === "non-grids" ? run.nonGridCount : run.classifiedCount}
      </button>
      <button className="igd-secondary" disabled={!selectedTotal} onClick={() => setSelection(createSelection())}>Clear</button>
      {__IGD_DEV_EDITION__ && <><button className="igd-primary" disabled={!selectedTotal || addingSamples} onClick={() => setSampleLabel("grid")}><Database size={16} />Add selected images to grid training data</button>
      <button className="igd-primary" disabled={!selectedTotal || addingSamples} onClick={() => setSampleLabel("not_grid")}><Database size={16} />Add selected images to non-grid training data</button></>}
    </section>}

    {__IGD_DEV_EDITION__ && <TrainingPanel training={training} onTrain={retrain} onCancel={() => training?.jobId && api.cancelJob(training.jobId)} />}

    {!settings.showResults && run && run.status !== "idle" ? <div className="igd-results-hidden">Classification results are hidden by the installed-extension setting.</div>
    : run?.items?.length > 0 ? <>
      <div className={`igd-grid${pageLoading ? " igd-page-loading" : ""}`}>{run.items.map((item) => <GridTile key={item.imageId} item={item} selectable={!deleting}
        selected={isSelected(selection, item.imageId)} onToggle={() => toggleSelected(item.imageId)}
        onPreview={(event) => showPreview(item, event)} onPreviewEnd={() => setPreview(null)} />)}</div>
      <Pagination page={run.page} totalPages={run.totalPages} disabled={active || deleting} onChange={async (next) => {
        setPage(next); setPageLoading(true); setPreview(null); try { await refresh(next, view, run?.runId); } catch (reason) { setError(reason.message); } finally { setPageLoading(false); }
      }} />
    </> : run?.status === "completed" ? <div className="igd-empty"><Grid3X3 size={34} /><strong>No image grids found</strong><span>Try another path scope or run the classifier again.</span></div>
      : run?.status === "idle" ? <div className="igd-empty"><Grid3X3 size={34} /><strong>Ready to classify</strong><span>Choose a path scope and press Run.</span></div> : null}

    {folderOpen && <FolderDialog selected={settings.paths} onCancel={() => setFolderOpen(false)} onApply={(paths) => {
      setSettings((value) => ({ ...value, paths })); setFolderOpen(false);
    }} />}
    {confirmOpen && run && <ConfirmDelete count={selectedTotal} onCancel={() => setConfirmOpen(false)} onConfirm={confirmDelete} />}
    {sampleLabel && <ConfirmTrainingData count={selectedTotal} label={sampleLabel} onCancel={() => setSampleLabel(null)} onConfirm={() => {
      const label = sampleLabel; setSampleLabel(null); addSamples(label);
    }} />}
    {metadataHelpOpen && <MetadataHelp onClose={() => setMetadataHelpOpen(false)} />}
    {settings.showResults && preview && <ImageHoverPreview preview={preview} />}
  </div>;
}

function GridTile({ item, selectable, selected, onToggle, onPreview, onPreviewEnd }) {
  return <article className={`igd-tile${selected ? " igd-selected" : ""}`} title={item.path}
    onClick={selectable ? onToggle : undefined} role={selectable ? "checkbox" : undefined} aria-checked={selectable ? selected : undefined}
    tabIndex={selectable ? 0 : undefined} onKeyDown={selectable ? (event) => {
      if (event.key !== "Enter" && event.key !== " ") return;
      event.preventDefault(); onToggle();
    } : undefined}>
    {selectable && <span className="igd-tile-check">{selected ? <CheckSquare size={18} /> : <Square size={18} />}</span>}
    <div className="igd-thumb" onPointerEnter={onPreview} onPointerMove={onPreview} onPointerLeave={onPreviewEnd}><img src={`/api/stream/image/${item.imageId}/thumbnail?max=192`} alt={basename(item.path)} loading="lazy" /></div>
    <div className="igd-tile-meta"><div className="igd-classification"><strong className={item.isGrid ? "igd-grid-label" : "igd-not-grid-label"}>{item.isGrid ? "Grid" : "Not grid"} {formatConfidence(item.isGrid ? item.gridConfidence : 1 - item.gridConfidence)}</strong><a href={`/image/${item.imageId}`} target="_blank" rel="noreferrer" title="Open image in Cove" aria-label={`Open image ${item.imageId} in Cove`} onClick={(event) => event.stopPropagation()} onKeyDown={(event) => event.stopPropagation()}><ExternalLink size={14} /></a></div><span>{basename(item.path)}</span></div>
    {(item.inArchive || item.protectedFileCount > 0) && <div className="igd-badges">
      {item.inArchive && <span>archive</span>}{item.protectedFileCount > 0 && <span>{item.protectedFileCount} protected</span>}
    </div>}
  </article>;
}

function ImageHoverPreview({ preview }) {
  return <div className="igd-hover-preview" style={{ left: preview.left, top: preview.top, width: preview.size, height: preview.size }}>
    <img src={`/api/stream/image/${preview.item.imageId}/thumbnail?max=900`} alt="" />
  </div>;
}

function MetadataHelp({ onClose }) {
  return <Modal title="Classification metadata added to Cove" onClose={onClose}>
    <div className="igd-help-copy">
      <p>By default, each classified image receives exactly one tag: <strong>Image Grid Deleter: Grid</strong> or <strong>Image Grid Deleter: Not grid</strong>.</p>
      <p>The extension also creates the numeric image custom field <strong>Image grid probability (%)</strong>. It stores the raw likelihood that an image is a grid, rounded to one tenth of a percent. Sort descending to see the most grid-like images first, or use Cove's numeric custom-field filters.</p>
      <p>The exact unrounded probability, model version, and classification run ID are retained in tag provenance and the extension's local run database.</p>
      <h3>Removing the metadata later</h3>
      <ol><li>Turn off <strong>Add classification metadata to Cove</strong> here to stop future writes.</li>
        <li>Delete the two <strong>Image Grid Deleter:</strong> tags in Cove's Tags interface.</li>
        <li>Delete <strong>Image grid probability (%)</strong> in Cove's Custom Fields settings to remove its saved values.</li></ol>
      <p>Removing this metadata does not delete images, training samples, models, or saved extension runs. Enabling publication again recreates missing extension-owned definitions.</p>
    </div>
    <div className="igd-modal-actions"><button className="igd-primary" onClick={onClose}>Got it</button></div>
  </Modal>;
}

function TrainingPanel({ training, onTrain, onCancel }) {
  const active = ["queued", "running"].includes(training?.status);
  return <section className="igd-training-panel">
    <div className="igd-training-heading"><div><strong>Developer model training</strong><span>{training?.stage || "Ready"}</span></div>
      <div><button className="igd-primary" disabled={active} onClick={onTrain}>
        {active ? <Loader2 className="igd-spin" size={16} /> : <RotateCw size={16} />}Retrain and build</button>
        {active && <button className="igd-secondary" onClick={onCancel}>Cancel</button>}</div></div>
    {active && <div className="igd-training-progress" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow={Math.round((training?.progress || 0) * 100)}>
      <span style={{ width: `${Math.round((training?.progress || 0) * 100)}%` }} /></div>}
    {training?.candidateVersion && <span>Candidate model: {training.candidateVersion}</span>}
    {training?.bestValidationAccuracy != null && <span>Best checkpoint{training.bestEpoch ? `: epoch ${training.bestEpoch}` : ""} — {(training.bestValidationAccuracy * 100).toFixed(1)}% validation accuracy{training.bestValidationLoss != null ? `, ${training.bestValidationLoss.toFixed(3)} validation loss` : ""}</span>}
    {training?.evaluationAccuracy != null && <span>Prediction-set accuracy: {(training.evaluationAccuracy * 100).toFixed(1)}%</span>}
    {training?.winningSeed != null && <span>Winning seed: {training.winningSeed} — false grids {training.evaluationFalseGrids}, detected grids {training.evaluationGridCorrect}</span>}
    {training?.qualityGateMessage && <span>{training.qualityGateMessage}</span>}
    {(training?.baselineMetrics || training?.candidateMetrics) && <TrainingComparison training={training} />}
    {training?.error && <div className="igd-alert igd-error">{training.error}</div>}
    {training?.updateStaged && <div className="igd-alert igd-success"><ShieldCheck size={17} />Version {training.builtVersion} is built and staged. Restart Cove to load the new developer extension.</div>}
    {training?.output?.length > 0 && <TrainingConsole lines={training.output} />}
  </section>;
}

function TrainingComparison({ training }) {
  const rows = [
    ["Overall accuracy", "accuracy", (value) => `${(value * 100).toFixed(1)}%`],
    ["Grid recall", "gridRecall", (value) => `${(value * 100).toFixed(1)}%`],
    ["False-grid rate", "falseGridRate", (value) => `${(value * 100).toFixed(1)}%`],
    ["False grids", "falseGrids", String],
    ["Evaluated", "total", String],
    ["Skipped", "skipped", String],
  ];
  const render = (metrics, key, formatter) => metrics?.[key] == null ? "â€”" : formatter(metrics[key]);
  return <div className="igd-training-comparison">
    <div><strong>Prediction-set comparison</strong><span>Active {training.baselineVersion || training.activeModelVersion || "model"}</span><span>Candidate {training.candidateVersion || "model"}</span></div>
    {rows.map(([label, key, formatter]) => <div key={key}><span>{label}</span><span>{render(training.baselineMetrics, key, formatter)}</span><span>{render(training.candidateMetrics, key, formatter)}</span></div>)}
  </div>;
}

function TrainingConsole({ lines }) {
  const ref = useRef(null);
  useEffect(() => { if (ref.current) ref.current.scrollTop = ref.current.scrollHeight; }, [lines.length]);
  return <div className="igd-training-console"><div>Training and build output</div><pre ref={ref}>{lines.join("\n")}</pre></div>;
}

function Pagination({ page, totalPages, disabled, onChange }) {
  if (totalPages <= 1) return null;
  return <nav className="igd-pagination">
    <button disabled={disabled || page <= 1} onClick={() => onChange(page - 1)}><ArrowLeft size={17} /></button>
    <span>Page {page} of {totalPages}</span>
    <button disabled={disabled || page >= totalPages} onClick={() => onChange(page + 1)}><ArrowRight size={17} /></button>
  </nav>;
}

function FolderDialog({ selected, onCancel, onApply }) {
  const [draft, setDraft] = useState([...selected]);
  const [roots, setRoots] = useState([]);
  const [error, setError] = useState("");
  useEffect(() => { api.loadFolders().then(setRoots).catch((reason) => setError(reason.message)); }, []);
  return <Modal title="Choose library folders" onClose={onCancel}>
    <p className="igd-modal-copy">Selecting a folder applies to its entire subtree.</p>
    {error && <div className="igd-alert igd-error">{error}</div>}
    <div className="igd-folder-tree">{roots.length ? roots.map((root) => <FolderNode key={root.path} node={root} depth={0} selected={draft}
      onToggle={(path, checked) => setDraft((value) => togglePath(value, path, checked))} />) : <span>Loading folders…</span>}</div>
    <div className="igd-modal-actions"><button className="igd-secondary" onClick={onCancel}>Cancel</button><button className="igd-primary" onClick={() => onApply(draft)}>Apply</button></div>
  </Modal>;
}

function FolderNode({ node, depth, selected, onToggle }) {
  const [expanded, setExpanded] = useState(false);
  const [children, setChildren] = useState(null);
  async function expand() {
    const next = !expanded; setExpanded(next);
    if (next && node.hasChildren && children === null) setChildren(await api.loadFolders(node.path));
  }
  return <div>
    <div className="igd-folder-row" style={{ paddingLeft: depth * 16 + 4 }}>
      {node.hasChildren ? <button aria-label={expanded ? "Collapse folder" : "Expand folder"} onClick={expand}>{expanded ? <ChevronDown size={14} /> : <ChevronRight size={14} />}</button> : <i />}
      <label><input type="checkbox" checked={selected.includes(node.path)} onChange={(event) => onToggle(node.path, event.target.checked)} /><span title={node.path}>{node.name}</span></label>
    </div>
    {expanded && (children || []).map((child) => <FolderNode key={child.path} node={child} depth={depth + 1} selected={selected} onToggle={onToggle} />)}
  </div>;
}

function ConfirmDelete({ count, onCancel, onConfirm }) {
  return <Modal title="Delete selected grids" onClose={onCancel}>
    <p className="igd-confirm">Delete the {count} selected grid image{count === 1 ? "" : "s"}?</p>
    <p className="igd-modal-copy">This permanently deletes the selected Cove images and normal source files. Archive containers are left unchanged.</p>
    <div className="igd-modal-actions"><button className="igd-secondary" onClick={onCancel}>Cancel</button><button className="igd-danger" onClick={onConfirm}>Delete selected</button></div>
  </Modal>;
}

function ConfirmTrainingData({ count, label, onCancel, onConfirm }) {
  const name = label === "grid" ? "grid" : "non-grid";
  return <Modal title="Confirm training label" onClose={onCancel}>
    <p className="igd-confirm">Add {count} selected image{count === 1 ? "" : "s"} to {name} training data?</p>
    <p className="igd-modal-copy">Please verify the category. Incorrect labels make the next model worse. The Cove Grid/Not-grid tag will be changed to match, and content hashes prevent duplicate samples.</p>
    <div className="igd-modal-actions"><button className="igd-secondary" onClick={onCancel}>Cancel</button><button className="igd-primary" onClick={onConfirm}>Yes, add to {name}</button></div>
  </Modal>;
}

function Modal({ title, onClose, children, dismissible = true }) {
  useEffect(() => { const key = (event) => event.key === "Escape" && dismissible && onClose(); window.addEventListener("keydown", key); return () => window.removeEventListener("keydown", key); }, [onClose, dismissible]);
  return <div className="igd-modal-backdrop" role="presentation" onMouseDown={(event) => event.target === event.currentTarget && dismissible && onClose()}>
    <section className="igd-modal" role="dialog" aria-modal="true" aria-label={title}><header><h2>{title}</h2>{dismissible && <button onClick={onClose}><X size={18} /></button>}</header>{children}</section>
  </div>;
}

export function TrainingDataActionHost() {
  const [request, setRequest] = useState(null);
  const [label, setLabel] = useState(null);
  const [stage, setStage] = useState("choose");
  const [result, setResult] = useState(null);
  const [error, setError] = useState("");
  const settled = useRef(false);

  useEffect(() => {
    const open = (event) => {
      if (request && !settled.current) request.resolve({ cancelled: true, suppressToast: true });
      settled.current = false; setRequest(event.detail); setLabel(null); setStage("choose"); setResult(null); setError("");
    };
    window.addEventListener(TRAINING_DATA_ACTION_EVENT, open);
    return () => window.removeEventListener(TRAINING_DATA_ACTION_EVENT, open);
  }, [request]);

  function finish(value) {
    if (!settled.current) { settled.current = true; request?.resolve(value); }
  }
  function close() {
    if (stage === "working") return;
    finish({ cancelled: stage !== "complete", suppressToast: true });
    setRequest(null);
  }
  function choose(nextLabel) { setLabel(nextLabel); setStage("confirm"); setError(""); }
  async function add() {
    setStage("working"); setError("");
    try {
      const value = await api.addImageTrainingSamples(request.imageIds, label);
      setResult(value); setStage("complete"); finish({ suppressToast: true });
    } catch (reason) { setError(reason.message); setStage("confirm"); }
  }
  if (!request) return null;
  const name = label === "grid" ? "grid" : "non-grid";
  return <Modal title="Add to training data" onClose={close} dismissible={stage !== "working"}>
    {stage === "choose" && <><p className="igd-modal-copy">Choose the correct classification for the {request.imageIds.length} selected image{request.imageIds.length === 1 ? "" : "s"}.</p>
      <div className="igd-training-labels"><button className="igd-primary" onClick={() => choose("grid")}><Grid3X3 size={17} />Add to grid training data</button>
        <button className="igd-primary" onClick={() => choose("not_grid")}><Database size={17} />Add to non-grid training data</button></div>
      <div className="igd-modal-actions"><button className="igd-secondary" onClick={close}>Cancel</button></div></>}
    {(stage === "confirm" || stage === "working") && <><p className="igd-confirm">Are you sure you want to add {request.imageIds.length} selected image{request.imageIds.length === 1 ? "" : "s"} to {name} training data?</p>
      <p className="igd-modal-copy">Please verify the category. Incorrect labels make the next model worse. The Cove Grid/Not-grid tag will be changed to match, and content hashes prevent duplicate samples.</p>
      {error && <div className="igd-alert igd-error"><AlertTriangle size={17} />{error}</div>}
      <div className="igd-modal-actions"><button className="igd-secondary" disabled={stage === "working"} onClick={() => setStage("choose")}>Back</button><button className="igd-primary" disabled={stage === "working"} onClick={add}>{stage === "working" && <Loader2 className="igd-spin" size={16} />}Yes, add to {name}</button></div></>}
    {stage === "complete" && <><div className="igd-alert igd-success"><ShieldCheck size={17} />Training data and Cove tags updated.</div>
      <p className="igd-modal-copy">{result.added} added, {result.relabeled} relabelled, {result.skipped} already present{result.errors?.length ? `, ${result.errors.length} failed` : ""}.</p>
      {result.errors?.length > 0 && <details><summary>Show failures</summary><ul>{result.errors.map((item, index) => <li key={index}>{item}</li>)}</ul></details>}
      <div className="igd-modal-actions"><button className="igd-primary" onClick={close}>Done</button></div></>}
  </Modal>;
}

export function DeleteImageGridsSettingsPanel() {
  if (__IGD_DEV_EDITION__) return <DeveloperSettingsPanel />;
  return <div className="igd-dev-settings"><div><strong>Image Grid Deleter</strong><p>Run the bundled classifier against selected Cove library folders and review matches before permanent deletion.</p></div>
    <ClassificationDisplaySetting />
    <div className="igd-settings-actions"><button className="igd-primary" onClick={() => window.location.assign("/extensions/delete-image-grids")}><Grid3X3 size={17} />Open Image Grid Deleter</button></div>
  </div>;
}

function ClassificationDisplaySetting() {
  const [settings, setSettings] = useState(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  useEffect(() => { api.loadSettings().then((value) => setSettings(normalizeSettings(value))).catch((reason) => setError(reason.message)); }, []);
  async function toggle(showResults) {
    if (!settings) return;
    const previous = settings;
    const next = { ...settings, showResults };
    setSettings(next); setSaving(true); setError(""); setNotice("");
    try { setSettings(normalizeSettings(await api.saveSettings(next))); setNotice("Result display preference saved."); }
    catch (reason) { setSettings(previous); setError(reason.message); }
    finally { setSaving(false); }
  }
  return <div className="igd-display-setting">
    {error && <div className="igd-alert igd-error">{error}</div>}{notice && <div className="igd-alert igd-success">{notice}</div>}
    <label><input type="checkbox" disabled={!settings || saving} checked={settings?.showResults === true} onChange={(event) => toggle(event.target.checked)} />
      <span><strong>Show results of classification on the extension page</strong><small>Warning: enabling this setting increases memory footprint.</small></span>
    </label>
  </div>;
}

function DeveloperSettingsPanel() {
  const [settings, setSettings] = useState({ trainingRepoPath: "", extensionRepoPath: "" });
  const [status, setStatus] = useState(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  useEffect(() => { api.loadDeveloperSettings().then((value) => { setSettings(value.settings); setStatus(value); }).catch((reason) => setError(reason.message)); }, []);
  async function save() {
    setSaving(true); setError(""); setNotice("");
    try { const value = await api.saveDeveloperSettings(settings); setSettings(value.settings); setStatus(value); setNotice("Developer paths saved and validated."); }
    catch (reason) { setError(reason.message); } finally { setSaving(false); }
  }
  return <div className="igd-dev-settings">
    <div><strong>Image Grid Deleter — Developer Edition</strong><p>Collect corrections, retrain the 384px model, and build updated extension packages without leaving Cove.</p></div>
    {error && <div className="igd-alert igd-error">{error}</div>}{notice && <div className="igd-alert igd-success">{notice}</div>}
    <ClassificationDisplaySetting />
    <label><span>Model training repository</span><input value={settings.trainingRepoPath || ""} onChange={(event) => setSettings({ ...settings, trainingRepoPath: event.target.value })}
      placeholder="C:\\path\\to\\img_grid_classifier" /></label>
    <label><span>Extension source repository</span><input value={settings.extensionRepoPath || ""} onChange={(event) => setSettings({ ...settings, extensionRepoPath: event.target.value })}
      placeholder="C:\\path\\to\\img_grid_deleter" /></label>
    {status?.repository?.valid && <p className="igd-repo-status">Dataset: {status.repository.gridCount} grids / {status.repository.notGridCount} non-grids</p>}
    <div className="igd-settings-actions"><button className="igd-secondary" disabled={saving} onClick={save}><Save size={16} />Save and validate</button>
      <button className="igd-primary" onClick={() => window.location.assign("/extensions/delete-image-grids")}><Grid3X3 size={17} />Open Image Grid Deleter</button></div>
  </div>;
}

export default {
  components: { DeleteImageGridsPage, DeleteImageGridsSettingsPanel, ...(__IGD_DEV_EDITION__ ? { TrainingDataActionHost } : {}) },
  actionHandlers: __IGD_DEV_EDITION__ ? { openTrainingDataDialog } : {},
};
