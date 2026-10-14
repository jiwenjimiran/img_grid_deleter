using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.DeleteImageGrids;

public sealed class DeleteImageGridsExtension : IExtension, IUIExtension, IStatefulExtension, IApiExtension
{
    public const string ExtensionId = "io.github.jiwenjimiran.delete-image-grids";
    private const string SettingsKey = "settings";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private IExtensionStore? _store;
    private GridRunCoordinator? _coordinator;
#if DELETE_IMAGE_GRIDS_DEV
    private const string DeveloperSettingsKey = "developer-settings";
    private ModelTrainingCoordinator? _training;
    private TrainingSampleCollector? _samples;
#endif

    public string Id => ExtensionId;
    public string Name => "Delete Image Grids"
#if DELETE_IMAGE_GRIDS_DEV
        + " (Developer Edition)"
#endif
        ;
    public string Version => typeof(DeleteImageGridsExtension).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.2.0";
    public string? Description => "Classify image grids with a bundled vision model, review them, and safely delete them from Cove.";
    public string? Author => "jiwenji";
    public string? Url => null;
    public string? IconUrl => null;
    public IReadOnlyList<string> Categories => ["ai", "tools", "library", "content-management", "ui"];
    public string? MinCoveVersion => "1.1.0";
    public IReadOnlyDictionary<string, string> Dependencies => new Dictionary<string, string>();

    public void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        var extensionRoot = Path.Combine(context.DataDirectory, ExtensionId);
        var modelPath = Path.Combine(extensionRoot, "model", "image-grid-classifier.onnx");
        services.AddSingleton(new ImageGridClassifier(modelPath, Version));
        services.AddSingleton(new GridResultStore(extensionRoot));
        services.AddSingleton<GridRunCoordinator>();
#if DELETE_IMAGE_GRIDS_DEV
        services.AddSingleton<TrainingSampleCollector>();
        services.AddSingleton(provider => new ModelTrainingCoordinator(
            provider.GetRequiredService<ImageGridClassifier>(), extensionRoot));
#endif
    }

    public async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _coordinator = services.GetRequiredService<GridRunCoordinator>();
#if DELETE_IMAGE_GRIDS_DEV
        _training = services.GetRequiredService<ModelTrainingCoordinator>();
        _samples = services.GetRequiredService<TrainingSampleCollector>();
        try
        {
            var settings = await LoadDeveloperSettingsAsync(ct);
            _training.TryLoadRepositoryModel(settings);
            _training.TryRestoreStatus(settings);
        }
        catch { /* A bad local override must not prevent the bundled model from loading. */ }
#endif
    }

    public Task ShutdownAsync(CancellationToken ct = default)
    {
#if DELETE_IMAGE_GRIDS_DEV
        _training?.Stop();
#endif
        return Task.CompletedTask;
    }

    public void SetStore(IExtensionStore store) => _store = store;

    public UIManifest GetUIManifest()
    {
        var manifest = new UIManifest();
        manifest.Pages.Add(new UIPageDefinition(
            Route: "extensions",
            Label: "Delete Image Grids",
            Icon: "Grid3X3",
            ShowInNav: false,
            NavOrder: 100,
            ComponentName: "DeleteImageGridsPage",
            ExtensionId: ExtensionId)
        {
            RequiredPermissions = [Permissions.ImagesRead, Permissions.ImagesDelete, Permissions.ImagesDeleteFile, Permissions.LibraryScan]
        });
        manifest.SettingsPanels.Add(new UISettingsPanel(
            Id: $"{ExtensionId}:installed",
            Label: "Delete Image Grids",
            ExtensionId: ExtensionId,
            ComponentName: "DeleteImageGridsSettingsPanel",
            Order: 270,
            TargetTab: "extensions"));
#if DELETE_IMAGE_GRIDS_DEV
        manifest.Actions.Add(new ExtensionAction(
            Id: $"{ExtensionId}:add-training-data",
            Label: "Add to training data",
            ExtensionId: ExtensionId,
            ActionType: "bulk",
            EntityTypes: ["image"],
            Icon: "Database",
            HandlerName: "openTrainingDataDialog",
            Order: 850,
            SuppressSuccessAlert: true)
        {
            RequiredPermission = Permissions.TagsWrite
        });
        manifest.Actions.Add(new ExtensionAction(
            Id: $"{ExtensionId}:add-training-data-image",
            Label: "Add to training data",
            ExtensionId: ExtensionId,
            ActionType: "toolbar",
            EntityTypes: ["image"],
            Icon: "Database",
            HandlerName: "openTrainingDataDialog",
            Order: 850,
            SuppressSuccessAlert: true)
        {
            RequiredPermission = Permissions.TagsWrite
        });
        manifest.Slots.Add(new UISlotContribution(
            Id: $"{ExtensionId}:training-dialog",
            Slot: "app-floating-ui",
            ExtensionId: ExtensionId,
            ComponentName: "TrainingDataActionHost",
            Order: 100));
#endif
        return manifest;
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/ext/delete-image-grids");

        group.MapGet("/settings", async (HttpContext ctx) =>
            Results.Json(await LoadSettingsAsync(ctx.RequestAborted), JsonOptions))
            .RequireCovePermission(Permissions.ImagesRead);

        group.MapPut("/settings", async (HttpContext ctx) =>
        {
            var incoming = await JsonSerializer.DeserializeAsync<GridSettings>(ctx.Request.Body, JsonOptions, ctx.RequestAborted);
            var settings = GridSettings.Normalize(incoming);
            if (_store is null) return Results.Problem("Extension storage is not initialized.");
            await _store.SetAsync(SettingsKey, JsonSerializer.Serialize(settings, JsonOptions), ctx.RequestAborted);
            return Results.Json(settings, JsonOptions);
        }).RequireCovePermission(Permissions.LibraryScan);

        group.MapPost("/runs", async (HttpContext ctx) =>
        {
            if (_coordinator is null) return Results.Problem("The classifier is not initialized.");
#if DELETE_IMAGE_GRIDS_DEV
            if (_training?.IsActive == true) return Results.Conflict(new { message = "Wait for model training to finish." });
#endif
            var request = await JsonSerializer.DeserializeAsync<StartGridRunRequest>(ctx.Request.Body, JsonOptions, ctx.RequestAborted)
                ?? new StartGridRunRequest();
            var settings = GridSettings.Normalize(new GridSettings(request.FolderMode, request.Paths, false, request.ShowResults));
            if (!_coordinator.TryStart(settings, ctx.RequestServices.GetRequiredService<IJobService>(), out var response))
                return Results.Conflict(new { message = "An image-grid classification run is already active." });
            if (_store is not null)
                await _store.SetAsync(SettingsKey, JsonSerializer.Serialize(settings, JsonOptions), ctx.RequestAborted);
            return Results.Accepted(value: response);
        }).RequireCovePermission(Permissions.ImagesRead).RequireCovePermission(Permissions.LibraryScan);

        group.MapPost("/runs/published", async (HttpContext ctx) =>
        {
            if (_coordinator is null) return Results.Problem("The classifier is not initialized.");
#if DELETE_IMAGE_GRIDS_DEV
            if (_training?.IsActive == true) return Results.Conflict(new { message = "Wait for model training to finish." });
#endif
            var request = await JsonSerializer.DeserializeAsync<StartGridRunRequest>(ctx.Request.Body, JsonOptions, ctx.RequestAborted)
                ?? new StartGridRunRequest();
            var settings = GridSettings.Normalize(new GridSettings(request.FolderMode, request.Paths, true, request.ShowResults));
            if (!_coordinator.TryStart(settings, ctx.RequestServices.GetRequiredService<IJobService>(), out var response))
                return Results.Conflict(new { message = "An image-grid classification run is already active." });
            if (_store is not null)
                await _store.SetAsync(SettingsKey, JsonSerializer.Serialize(settings, JsonOptions), ctx.RequestAborted);
            return Results.Accepted(value: response);
        }).RequireCovePermission(Permissions.ImagesRead)
          .RequireCovePermission(Permissions.ImagesWrite)
          .RequireCovePermission(Permissions.TagsWrite)
          .RequireCovePermission(Permissions.SystemSettingsWrite)
          .RequireCovePermission(Permissions.LibraryScan);

        group.MapGet("/runs/current", async (HttpContext ctx, string? runId, int? page, int? pageSize, string? view, bool? includeItems) =>
        {
            if (_coordinator is null) return Results.Problem("The classifier is not initialized.");
            return Results.Json(await _coordinator.GetPageAsync(
                runId, page ?? 1, pageSize ?? 200, view, includeItems ?? true, ctx.RequestAborted), JsonOptions);
        }).RequireCovePermission(Permissions.ImagesRead);

        group.MapGet("/runs", async (HttpContext ctx) =>
            _coordinator is null ? Results.Problem("The classifier is not initialized.")
                : Results.Json(await _coordinator.ListRunsAsync(ctx.RequestAborted), JsonOptions))
            .RequireCovePermission(Permissions.ImagesRead);

        group.MapGet("/runs/status", async (HttpContext ctx, string? runId) =>
            _coordinator is null ? Results.Problem("The classifier is not initialized.")
                : Results.Json(await _coordinator.GetStatusAsync(runId, ctx.RequestAborted), JsonOptions))
            .RequireCovePermission(Permissions.ImagesRead);

        group.MapPost("/runs/resume", async (HttpContext ctx) =>
        {
            if (_coordinator is null) return Results.Problem("The classifier is not initialized.");
            var request = await JsonSerializer.DeserializeAsync<ResumeGridRunRequest>(ctx.Request.Body, JsonOptions, ctx.RequestAborted);
            if (request is null || !_coordinator.TryResume(request.RunId, ctx.RequestServices.GetRequiredService<IJobService>(), out var response))
                return Results.Conflict(new { message = "The run is not resumable or another classification is active." });
            return Results.Accepted(value: response);
        }).RequireCovePermission(Permissions.ImagesRead).RequireCovePermission(Permissions.LibraryScan);

        group.MapPost("/runs/selection/batch", async (HttpContext ctx) =>
        {
            if (_coordinator is null) return Results.Problem("The classifier is not initialized.");
            var request = await JsonSerializer.DeserializeAsync<GridSelectionBatchRequest>(ctx.Request.Body, JsonOptions, ctx.RequestAborted);
            if (request is null) return Results.BadRequest(new { message = "A selection is required." });
            var resultStore = ctx.RequestServices.GetRequiredService<GridResultStore>();
            return Results.Json(await resultStore.GetSelectionBatchAsync(request, ctx.RequestAborted), JsonOptions);
        }).RequireCovePermission(Permissions.ImagesRead);

        group.MapPost("/runs/prune", async (HttpContext ctx) =>
        {
            if (_coordinator is null) return Results.Problem("The classifier is not initialized.");
            var request = await JsonSerializer.DeserializeAsync<PruneGridFilesRequest>(ctx.Request.Body, JsonOptions, ctx.RequestAborted);
            var fileIds = request?.FileIds?.Where(id => id > 0).Distinct().ToArray() ?? [];
            if (request is null || !await _coordinator.ValidatePruneAsync(request.RunId, request.ImageId, fileIds, ctx.RequestAborted))
                return Results.BadRequest(new { message = "The requested files are not a protected mixed-scope result in the active run." });
            try
            {
                var result = await ScopedFilePruner.PruneAsync(
                    ctx.RequestServices.GetRequiredService<CoveContext>(),
                    request.ImageId,
                    fileIds,
                    ctx.RequestAborted);
                await CoveHostImageServices.DeleteGeneratedAsync(ctx.RequestServices, request.ImageId, ctx.RequestAborted);
                return Results.Json(result, JsonOptions);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        }).RequireCovePermission(Permissions.ImagesDeleteFile);

        group.MapPost("/runs/reconcile", async (HttpContext ctx) =>
        {
            if (_coordinator is null) return Results.Problem("The classifier is not initialized.");
            var request = await JsonSerializer.DeserializeAsync<ReconcileGridRunRequest>(ctx.Request.Body, JsonOptions, ctx.RequestAborted);
            if (request is null) return Results.BadRequest(new { message = "runId is required." });
            var removed = await _coordinator.ReconcileAsync(request.RunId, ctx.RequestAborted);
            return removed < 0 ? Results.NotFound() : Results.Json(new { removed }, JsonOptions);
        }).RequireCovePermission(Permissions.ImagesDelete);

#if DELETE_IMAGE_GRIDS_DEV
        group.MapGet("/developer-settings", async (HttpContext ctx) =>
        {
            var settings = await LoadDeveloperSettingsAsync(ctx.RequestAborted);
            var active = ctx.RequestServices.GetRequiredService<ImageGridClassifier>().ActiveModelPath;
            return Results.Json(new { settings, repository = TrainingRepository.Inspect(settings.TrainingRepoPath, active),
                buildRepository = ExtensionBuildRepository.Inspect(settings.ExtensionRepoPath) }, JsonOptions);
        }).RequireCovePermission(Permissions.LibraryScan);

        group.MapPut("/developer-settings", async (HttpContext ctx) =>
        {
            var incoming = await JsonSerializer.DeserializeAsync<DeveloperSettings>(ctx.Request.Body, JsonOptions, ctx.RequestAborted);
            var settings = DeveloperSettings.Normalize(incoming);
            var active = ctx.RequestServices.GetRequiredService<ImageGridClassifier>().ActiveModelPath;
            var repository = TrainingRepository.Inspect(settings.TrainingRepoPath, active);
            if (!repository.Valid) return Results.BadRequest(new { message = repository.Error });
            var buildRepository = ExtensionBuildRepository.Inspect(settings.ExtensionRepoPath);
            if (!buildRepository.Valid) return Results.BadRequest(new { message = buildRepository.Error });
            if (_store is null) return Results.Problem("Extension storage is not initialized.");
            await _store.SetAsync(DeveloperSettingsKey, JsonSerializer.Serialize(settings, JsonOptions), ctx.RequestAborted);
            try { _training?.TryLoadRepositoryModel(settings); }
            catch (Exception ex) { return Results.BadRequest(new { message = $"The repository model could not be loaded: {ex.Message}" }); }
            _training?.TryRestoreStatus(settings);
            repository = TrainingRepository.Inspect(settings.TrainingRepoPath,
                ctx.RequestServices.GetRequiredService<ImageGridClassifier>().ActiveModelPath);
            return Results.Json(new { settings, repository, buildRepository }, JsonOptions);
        }).RequireCovePermission(Permissions.LibraryScan);

        group.MapPost("/training-samples", async (HttpContext ctx) =>
        {
            if (_samples is null) return Results.Problem("Developer dataset services are not initialized.");
            if (_training?.IsActive == true) return Results.Conflict(new { message = "Wait for model training to finish." });
            var request = await JsonSerializer.DeserializeAsync<AddTrainingSamplesRequest>(ctx.Request.Body, JsonOptions, ctx.RequestAborted);
            if (request is null) return Results.BadRequest(new { message = "A sample request is required." });
            try
            {
                return Results.Json(await _samples.AddAsync(await LoadDeveloperSettingsAsync(ctx.RequestAborted), request, ctx.RequestAborted), JsonOptions);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        }).RequireCovePermission(Permissions.ImagesRead)
          .RequireCovePermission(Permissions.TagsWrite)
          .RequireCovePermission(Permissions.LibraryScan);

        group.MapPost("/training-samples/images", async (HttpContext ctx) =>
        {
            if (_samples is null) return Results.Problem("Developer dataset services are not initialized.");
            if (_training?.IsActive == true) return Results.Conflict(new { message = "Wait for model training to finish." });
            var request = await JsonSerializer.DeserializeAsync<AddImageTrainingSamplesRequest>(ctx.Request.Body, JsonOptions, ctx.RequestAborted);
            if (request is null) return Results.BadRequest(new { message = "A sample request is required." });
            try
            {
                return Results.Json(await _samples.AddImagesAsync(
                    await LoadDeveloperSettingsAsync(ctx.RequestAborted), request, ctx.RequestAborted), JsonOptions);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        }).RequireCovePermission(Permissions.ImagesRead)
          .RequireCovePermission(Permissions.TagsWrite)
          .RequireCovePermission(Permissions.LibraryScan);

        group.MapPost("/training", async (HttpContext ctx) =>
        {
            if (_training is null || _coordinator is null) return Results.Problem("Developer training is not initialized.");
            if (_coordinator.IsActive) return Results.Conflict(new { message = "Wait for image classification to finish." });
            var settings = await LoadDeveloperSettingsAsync(ctx.RequestAborted);
            var repository = TrainingRepository.Inspect(settings.TrainingRepoPath);
            if (!repository.Valid) return Results.BadRequest(new { message = repository.Error });
            var buildRepository = ExtensionBuildRepository.Inspect(settings.ExtensionRepoPath);
            if (!buildRepository.Valid) return Results.BadRequest(new { message = buildRepository.Error });
            if (!_training.TryStart(settings, ctx.RequestServices.GetRequiredService<IJobService>(), out var response))
                return Results.Conflict(new { message = "Model training is already active." });
            return Results.Accepted(value: response);
        }).RequireCovePermission(Permissions.LibraryScan);

        group.MapGet("/training/current", () =>
            Results.Json(_training?.GetStatus() ?? new TrainingStatus(
                null, "idle", null, null, null, 0,
                null, null, null, null, null, null, null,
                null, null, null, null, null, [],
                null, null, null, null, null, false), JsonOptions))
            .RequireCovePermission(Permissions.LibraryScan);
#endif
    }

    private async Task<GridSettings> LoadSettingsAsync(CancellationToken ct)
    {
        if (_store is null) return GridSettings.Normalize(null);
        var json = await _store.GetAsync(SettingsKey, ct);
        if (string.IsNullOrWhiteSpace(json)) return GridSettings.Normalize(null);
        try { return GridSettings.Normalize(JsonSerializer.Deserialize<GridSettings>(json, JsonOptions)); }
        catch { return GridSettings.Normalize(null); }
    }

#if DELETE_IMAGE_GRIDS_DEV
    private async Task<DeveloperSettings> LoadDeveloperSettingsAsync(CancellationToken ct)
    {
        if (_store is null) return DeveloperSettings.DiscoverDefaults();
        var json = await _store.GetAsync(DeveloperSettingsKey, ct);
        if (string.IsNullOrWhiteSpace(json)) return DeveloperSettings.DiscoverDefaults();
        try { return DeveloperSettings.Normalize(JsonSerializer.Deserialize<DeveloperSettings>(json, JsonOptions)); }
        catch { return DeveloperSettings.DiscoverDefaults(); }
    }
#endif
}
