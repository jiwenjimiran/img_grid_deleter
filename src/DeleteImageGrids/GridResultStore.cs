using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Cove.DeleteImageGrids;

/// <summary>Small, indexed, extension-owned result database. No method materializes an entire run.</summary>
public sealed class GridResultStore : IDisposable
{
    private readonly string _connectionString;

    public GridResultStore(string extensionRoot)
    {
        Directory.CreateDirectory(extensionRoot);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(extensionRoot, "classification-results.sqlite3"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            // Pooled native connections retain Windows file locks after jobs finish.
            // Each operation must release its handle so Cove can replace the package.
            Pooling = false,
        };
        _connectionString = builder.ToString();
        Initialize();
    }

    public async Task<GridRunRecord> CreateRunAsync(GridSettings settings, string modelVersion, CancellationToken ct)
    {
        var run = new GridRunRecord(Guid.NewGuid().ToString("N"), "queued", null, settings,
            modelVersion, 0, 0, 0, 0, 0, 0, null, 0, DateTimeOffset.UtcNow, null);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runs
              (id,status,folder_mode,paths_json,publish_metadata,model_version,created_at)
            VALUES ($id,'queued',$mode,$paths,$publish,$model,$created);
            """;
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$mode", settings.FolderMode);
        command.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(settings.Paths ?? []));
        command.Parameters.AddWithValue("$publish", settings.PublishMetadata ? 1 : 0);
        command.Parameters.AddWithValue("$model", modelVersion);
        command.Parameters.AddWithValue("$created", run.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
        await PruneOldRunsAsync(connection, run.Id, ct);
        return run;
    }

    public async Task SetJobAsync(string runId, string jobId, CancellationToken ct)
        => await ExecuteAsync("UPDATE runs SET job_id=$value WHERE id=$id", runId, jobId, ct);

    public async Task MarkRunningAsync(string runId, CancellationToken ct)
        => await ExecuteAsync("UPDATE runs SET status='running', candidate_count=-1, error=NULL WHERE id=$id", runId, null, ct);

    public async Task SetCandidateCountAsync(string runId, int count, CancellationToken ct)
        => await ExecuteNumberAsync("UPDATE runs SET candidate_count=$value WHERE id=$id", runId, count, ct);

    public async Task AddResultAsync(string runId, GridMatch result, int lastImageId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO results
                  (run_id,image_id,path,grid_confidence,is_grid,protected_file_count,in_archive,metadata_published)
                VALUES ($run,$image,$path,$confidence,$grid,$protected,$archive,0)
                ON CONFLICT(run_id,image_id) DO UPDATE SET
                  path=excluded.path, grid_confidence=excluded.grid_confidence, is_grid=excluded.is_grid,
                  protected_file_count=excluded.protected_file_count, in_archive=excluded.in_archive;
                """;
            command.Parameters.AddWithValue("$run", runId);
            command.Parameters.AddWithValue("$image", result.ImageId);
            command.Parameters.AddWithValue("$path", result.Path);
            command.Parameters.AddWithValue("$confidence", result.GridConfidence);
            command.Parameters.AddWithValue("$grid", result.IsGrid ? 1 : 0);
            command.Parameters.AddWithValue("$protected", result.ProtectedFileCount);
            command.Parameters.AddWithValue("$archive", result.InArchive ? 1 : 0);
            await command.ExecuteNonQueryAsync(ct);
        }
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM result_files WHERE run_id=$run AND image_id=$image";
            delete.Parameters.AddWithValue("$run", runId);
            delete.Parameters.AddWithValue("$image", result.ImageId);
            await delete.ExecuteNonQueryAsync(ct);
        }
        foreach (var file in result.InScopeFiles)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO result_files(run_id,image_id,file_id,path,size,width,height,in_archive)
                VALUES($run,$image,$file,$path,$size,$width,$height,$archive)
                """;
            command.Parameters.AddWithValue("$run", runId);
            command.Parameters.AddWithValue("$image", result.ImageId);
            command.Parameters.AddWithValue("$file", file.Id);
            command.Parameters.AddWithValue("$path", file.Path);
            command.Parameters.AddWithValue("$size", file.Size);
            command.Parameters.AddWithValue("$width", file.Width);
            command.Parameters.AddWithValue("$height", file.Height);
            command.Parameters.AddWithValue("$archive", file.InArchive ? 1 : 0);
            await command.ExecuteNonQueryAsync(ct);
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE runs SET processed_count=processed_count+1, last_image_id=$last,
                  grid_count=grid_count+$grid, non_grid_count=non_grid_count+$nonGrid
                WHERE id=$run
                """;
            update.Parameters.AddWithValue("$run", runId);
            update.Parameters.AddWithValue("$last", lastImageId);
            update.Parameters.AddWithValue("$grid", result.IsGrid ? 1 : 0);
            update.Parameters.AddWithValue("$nonGrid", result.IsGrid ? 0 : 1);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task MarkSkippedAsync(string runId, int lastImageId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE runs SET processed_count=processed_count+1, skipped_count=skipped_count+1, last_image_id=$last WHERE id=$id";
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$last", lastImageId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkMetadataPublishedAsync(string runId, int imageId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE results SET metadata_published=1 WHERE run_id=$run AND image_id=$image AND metadata_published=0;
            UPDATE runs SET metadata_published_count=(SELECT COUNT(*) FROM results WHERE run_id=$run AND metadata_published=1) WHERE id=$run;
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$image", imageId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteAsync(string runId, string status, string? error, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE runs SET status=$status,error=$error,completed_at=$completed, candidate_count=CASE WHEN $status='completed' AND candidate_count<0 THEN processed_count ELSE candidate_count END WHERE id=$id";
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<GridRunRecord?> GetRunAsync(string? runId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = runId is null
            ? "SELECT * FROM runs ORDER BY created_at DESC LIMIT 1"
            : "SELECT * FROM runs WHERE id=$id LIMIT 1";
        if (runId is not null) command.Parameters.AddWithValue("$id", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRun(reader) : null;
    }

    public async Task<List<GridRunSummary>> ListRunsAsync(CancellationToken ct)
    {
        var result = new List<GridRunSummary>(2);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM runs ORDER BY created_at DESC LIMIT 2";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var run = ReadRun(reader);
            result.Add(new(run.Id, run.Status, run.JobId, run.ModelVersion, run.Settings.PublishMetadata,
                run.CandidateCount, run.ProcessedCount, run.GridCount, run.NonGridCount,
                run.MetadataPublishedCount, run.CreatedAt.ToString("O"), run.CompletedAt?.ToString("O"), run.Resumable));
        }
        return result;
    }

    public async Task<GridRunPage> GetPageAsync(
        string? runId, int page, int pageSize, string? view, string activeModelVersion, bool includeItems, CancellationToken ct)
    {
        view = NormalizeView(view);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var run = await GetRunAsync(runId, ct);
        if (run is null)
            return new(null, "idle", null, "all", [], 0, 0, 0, 0, 0, 0, null,
                1, pageSize, 1, view, activeModelVersion, activeModelVersion, true, 0, false, []);

        await using var connection = await OpenAsync(ct);
        var where = view switch { "grids" => " AND is_grid=1", "non-grids" => " AND is_grid=0", _ => "" };
        var count = view switch { "grids" => run.GridCount, "non-grids" => run.NonGridCount, _ => run.GridCount + run.NonGridCount };
        var totalPages = Math.Max(1, (int)Math.Ceiling(count / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);
        var items = new List<GridMatch>(pageSize);
        if (includeItems)
        {
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    SELECT image_id,path,grid_confidence,is_grid,protected_file_count,in_archive
                    FROM results WHERE run_id=$run{where}
                    ORDER BY grid_confidence DESC,image_id ASC LIMIT $limit OFFSET $offset
                    """;
                command.Parameters.AddWithValue("$run", run.Id);
                command.Parameters.AddWithValue("$limit", pageSize);
                command.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    items.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetFloat(2), reader.GetInt32(3) != 0,
                        [], reader.GetInt32(4), reader.GetInt32(5) != 0));
            }
            await LoadFilesAsync(connection, run.Id, items, ct);
        }
        return new(run.Id, run.Status, run.JobId, run.Settings.FolderMode, run.Settings.Paths ?? [],
            run.CandidateCount, run.ProcessedCount, run.GridCount + run.NonGridCount, run.GridCount,
            run.NonGridCount, run.SkippedCount, run.Error, page, pageSize, totalPages, view,
            run.ModelVersion, activeModelVersion, run.Settings.PublishMetadata, run.MetadataPublishedCount, run.Resumable, items);
    }

    public async Task<GridMatch?> GetMatchAsync(string runId, int imageId, CancellationToken ct)
    {
        var page = await GetMatchesAsync(runId, [imageId], ct);
        return page.Count == 1 ? page[0] : null;
    }

    public async Task<List<GridMatch>> GetUnpublishedBatchAsync(string runId, int batchSize, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT image_id,path,grid_confidence,is_grid,protected_file_count,in_archive
            FROM results WHERE run_id=$run AND metadata_published=0 ORDER BY image_id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(batchSize, 1, 250));
        var matches = new List<GridMatch>(batchSize);
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                matches.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetFloat(2), reader.GetInt32(3) != 0,
                    [], reader.GetInt32(4), reader.GetInt32(5) != 0));
        await LoadFilesAsync(connection, runId, matches, ct);
        return matches;
    }

    public async Task<List<GridMatch>> GetMatchesAsync(string runId, IReadOnlyCollection<int> imageIds, CancellationToken ct)
    {
        if (imageIds.Count == 0) return [];
        await using var connection = await OpenAsync(ct);
        var ids = imageIds.Where(id => id > 0).Distinct().ToArray();
        var parameters = string.Join(',', ids.Select((_, index) => $"$id{index}"));
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT image_id,path,grid_confidence,is_grid,protected_file_count,in_archive FROM results WHERE run_id=$run AND image_id IN ({parameters})";
        command.Parameters.AddWithValue("$run", runId);
        for (var index = 0; index < ids.Length; index++) command.Parameters.AddWithValue($"$id{index}", ids[index]);
        var matches = new List<GridMatch>(ids.Length);
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                matches.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetFloat(2), reader.GetInt32(3) != 0,
                    [], reader.GetInt32(4), reader.GetInt32(5) != 0));
        await LoadFilesAsync(connection, runId, matches, ct);
        return matches;
    }

    public async IAsyncEnumerable<List<GridMatch>> ReadSelectionBatchesAsync(
        GridSelectionRequest selection, int batchSize = 100,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var exceptions = (selection.Exceptions ?? []).Where(id => id > 0).Distinct().ToArray();
        var cursor = float.PositiveInfinity;
        var cursorId = 0;
        while (true)
        {
            await using var connection = await OpenAsync(ct);
            var view = NormalizeView(selection.View);
            var viewWhere = view switch { "grids" => " AND is_grid=1", "non-grids" => " AND is_grid=0", _ => "" };
            var exceptionWhere = BuildExceptionWhere(exceptions, selection.AllMatching);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT image_id,path,grid_confidence,is_grid,protected_file_count,in_archive
                FROM results WHERE run_id=$run{viewWhere}{exceptionWhere}
                  AND (grid_confidence < $confidence OR (grid_confidence = $confidence AND image_id > $cursorId))
                ORDER BY grid_confidence DESC,image_id ASC LIMIT $limit
                """;
            command.Parameters.AddWithValue("$run", selection.RunId);
            command.Parameters.AddWithValue("$confidence", cursor);
            command.Parameters.AddWithValue("$cursorId", cursorId);
            command.Parameters.AddWithValue("$limit", Math.Clamp(batchSize, 1, 500));
            for (var index = 0; index < exceptions.Length; index++) command.Parameters.AddWithValue($"$ex{index}", exceptions[index]);
            var batch = new List<GridMatch>(batchSize);
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    batch.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetFloat(2), reader.GetInt32(3) != 0,
                        [], reader.GetInt32(4), reader.GetInt32(5) != 0));
            if (batch.Count == 0) yield break;
            await LoadFilesAsync(connection, selection.RunId, batch, ct);
            yield return batch;
            cursor = batch[^1].GridConfidence;
            cursorId = batch[^1].ImageId;
        }
    }

    public async Task<GridSelectionBatchResponse> GetSelectionBatchAsync(GridSelectionBatchRequest request, CancellationToken ct)
    {
        var selection = request.Selection;
        var exceptions = (selection.Exceptions ?? []).Where(id => id > 0).Distinct().ToArray();
        var cursor = request.CursorConfidence ?? float.PositiveInfinity;
        await using var connection = await OpenAsync(ct);
        var view = NormalizeView(selection.View);
        var viewWhere = view switch { "grids" => " AND is_grid=1", "non-grids" => " AND is_grid=0", _ => "" };
        var exceptionWhere = BuildExceptionWhere(exceptions, selection.AllMatching);
        var limit = Math.Clamp(request.BatchSize, 1, 250);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT image_id,path,grid_confidence,is_grid,protected_file_count,in_archive
            FROM results WHERE run_id=$run{viewWhere}{exceptionWhere}
              AND (grid_confidence < $confidence OR (grid_confidence = $confidence AND image_id > $cursorId))
            ORDER BY grid_confidence DESC,image_id ASC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$run", selection.RunId);
        command.Parameters.AddWithValue("$confidence", cursor);
        command.Parameters.AddWithValue("$cursorId", request.CursorImageId);
        command.Parameters.AddWithValue("$limit", limit + 1);
        for (var index = 0; index < exceptions.Length; index++) command.Parameters.AddWithValue($"$ex{index}", exceptions[index]);
        var matches = new List<GridMatch>(limit + 1);
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                matches.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetFloat(2), reader.GetInt32(3) != 0,
                    [], reader.GetInt32(4), reader.GetInt32(5) != 0));
        var hasMore = matches.Count > limit;
        if (hasMore) matches.RemoveAt(matches.Count - 1);
        await LoadFilesAsync(connection, selection.RunId, matches, ct);
        return matches.Count == 0
            ? new([], null, 0, false)
            : new(matches, matches[^1].GridConfidence, matches[^1].ImageId, hasMore);
    }

    public async Task<int> RemoveMissingFilesAsync(string runId, IReadOnlyCollection<int> existingFileIds, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var existing = existingFileIds.ToHashSet();
        var removed = 0;
        await using var select = connection.CreateCommand();
        select.Transaction = (SqliteTransaction)transaction;
        select.CommandText = "SELECT DISTINCT image_id FROM result_files WHERE run_id=$run";
        select.Parameters.AddWithValue("$run", runId);
        var imageIds = new List<int>();
        await using (var reader = await select.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) imageIds.Add(reader.GetInt32(0));
        foreach (var imageId in imageIds)
        {
            await using var files = connection.CreateCommand();
            files.Transaction = (SqliteTransaction)transaction;
            files.CommandText = "SELECT file_id FROM result_files WHERE run_id=$run AND image_id=$image";
            files.Parameters.AddWithValue("$run", runId);
            files.Parameters.AddWithValue("$image", imageId);
            var any = false;
            await using (var reader = await files.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) any |= existing.Contains(reader.GetInt32(0));
            if (any) continue;
            await using var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM results WHERE run_id=$run AND image_id=$image";
            delete.Parameters.AddWithValue("$run", runId);
            delete.Parameters.AddWithValue("$image", imageId);
            removed += await delete.ExecuteNonQueryAsync(ct);
        }
        await RecountAsync(connection, (SqliteTransaction)transaction, runId, ct);
        await transaction.CommitAsync(ct);
        return removed;
    }

    public async Task<int> RemoveResultsAsync(string runId, IReadOnlyCollection<int> imageIds, bool recount, CancellationToken ct)
    {
        var ids = imageIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return 0;
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var parameters = string.Join(',', ids.Select((_, index) => $"$image{index}"));
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"DELETE FROM results WHERE run_id=$run AND image_id IN ({parameters})";
        command.Parameters.AddWithValue("$run", runId);
        for (var index = 0; index < ids.Length; index++) command.Parameters.AddWithValue($"$image{index}", ids[index]);
        var removed = await command.ExecuteNonQueryAsync(ct);
        if (recount) await RecountAsync(connection, (SqliteTransaction)transaction, runId, ct);
        await transaction.CommitAsync(ct);
        return removed;
    }

    public async Task RecountAsync(string runId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await RecountAsync(connection, (SqliteTransaction)transaction, runId, ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS runs(
              id TEXT PRIMARY KEY,status TEXT NOT NULL,job_id TEXT,folder_mode TEXT NOT NULL,paths_json TEXT NOT NULL,
              publish_metadata INTEGER NOT NULL,model_version TEXT NOT NULL,candidate_count INTEGER NOT NULL DEFAULT 0,
              processed_count INTEGER NOT NULL DEFAULT 0,grid_count INTEGER NOT NULL DEFAULT 0,
              non_grid_count INTEGER NOT NULL DEFAULT 0,skipped_count INTEGER NOT NULL DEFAULT 0,
              metadata_published_count INTEGER NOT NULL DEFAULT 0,error TEXT,last_image_id INTEGER NOT NULL DEFAULT 0,
              created_at TEXT NOT NULL,completed_at TEXT);
            CREATE TABLE IF NOT EXISTS results(
              run_id TEXT NOT NULL,image_id INTEGER NOT NULL,path TEXT NOT NULL,grid_confidence REAL NOT NULL,
              is_grid INTEGER NOT NULL,protected_file_count INTEGER NOT NULL,in_archive INTEGER NOT NULL,
              metadata_published INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(run_id,image_id),
              FOREIGN KEY(run_id) REFERENCES runs(id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS result_files(
              run_id TEXT NOT NULL,image_id INTEGER NOT NULL,file_id INTEGER NOT NULL,path TEXT NOT NULL,size INTEGER NOT NULL,
              width INTEGER NOT NULL,height INTEGER NOT NULL,in_archive INTEGER NOT NULL,
              PRIMARY KEY(run_id,image_id,file_id),FOREIGN KEY(run_id,image_id) REFERENCES results(run_id,image_id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS ix_results_page ON results(run_id,is_grid,grid_confidence DESC,image_id);
            CREATE INDEX IF NOT EXISTS ix_result_files_file ON result_files(run_id,file_id);
            UPDATE runs SET status='interrupted',job_id=NULL WHERE status IN ('queued','running');
            """;
        command.ExecuteNonQuery();
    }

    private async Task ExecuteAsync(string sql, string runId, string? value, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task ExecuteNumberAsync(string sql, string runId, int value, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task LoadFilesAsync(SqliteConnection connection, string runId, List<GridMatch> matches, CancellationToken ct)
    {
        if (matches.Count == 0) return;
        var byId = matches.ToDictionary(item => item.ImageId);
        var ids = matches.Select(item => item.ImageId).ToArray();
        var parameters = string.Join(',', ids.Select((_, index) => $"$image{index}"));
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT image_id,file_id,path,size,width,height,in_archive FROM result_files WHERE run_id=$run AND image_id IN ({parameters}) ORDER BY image_id,file_id";
        command.Parameters.AddWithValue("$run", runId);
        for (var index = 0; index < ids.Length; index++) command.Parameters.AddWithValue($"$image{index}", ids[index]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var target = byId[reader.GetInt32(0)];
            target.InScopeFiles.Add(new(reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6) != 0));
        }
    }

    private static string BuildExceptionWhere(int[] ids, bool allMatching)
    {
        if (ids.Length == 0) return allMatching ? "" : " AND 0=1";
        var list = string.Join(',', ids.Select((_, index) => $"$ex{index}"));
        return allMatching ? $" AND image_id NOT IN ({list})" : $" AND image_id IN ({list})";
    }

    private static string NormalizeView(string? view) => view?.Trim().ToLowerInvariant() switch
    {
        "classified" => "classified", "non-grids" => "non-grids", _ => "grids"
    };

    private static GridRunRecord ReadRun(SqliteDataReader reader)
    {
        var settings = new GridSettings(reader.GetString(reader.GetOrdinal("folder_mode")),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("paths_json"))) ?? [],
            reader.GetInt32(reader.GetOrdinal("publish_metadata")) != 0);
        DateTimeOffset? completed = reader.IsDBNull(reader.GetOrdinal("completed_at")) ? null
            : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("completed_at")), CultureInfo.InvariantCulture);
        return new(reader.GetString(reader.GetOrdinal("id")), reader.GetString(reader.GetOrdinal("status")),
            reader.IsDBNull(reader.GetOrdinal("job_id")) ? null : reader.GetString(reader.GetOrdinal("job_id")), settings,
            reader.GetString(reader.GetOrdinal("model_version")), reader.GetInt32(reader.GetOrdinal("candidate_count")),
            reader.GetInt32(reader.GetOrdinal("processed_count")), reader.GetInt32(reader.GetOrdinal("grid_count")),
            reader.GetInt32(reader.GetOrdinal("non_grid_count")), reader.GetInt32(reader.GetOrdinal("skipped_count")),
            reader.GetInt32(reader.GetOrdinal("metadata_published_count")),
            reader.IsDBNull(reader.GetOrdinal("error")) ? null : reader.GetString(reader.GetOrdinal("error")),
            reader.GetInt32(reader.GetOrdinal("last_image_id")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at")), CultureInfo.InvariantCulture), completed);
    }

    private static async Task RecountAsync(SqliteConnection connection, SqliteTransaction transaction, string runId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE runs SET
              grid_count=(SELECT COUNT(*) FROM results WHERE run_id=$run AND is_grid=1),
              non_grid_count=(SELECT COUNT(*) FROM results WHERE run_id=$run AND is_grid=0),
              metadata_published_count=(SELECT COUNT(*) FROM results WHERE run_id=$run AND metadata_published=1)
            WHERE id=$run
            """;
        command.Parameters.AddWithValue("$run", runId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task PruneOldRunsAsync(SqliteConnection connection, string currentId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM runs WHERE id<>$current AND id NOT IN (
              SELECT id FROM runs WHERE id<>$current AND status='completed' ORDER BY created_at DESC LIMIT 1
            )
            """;
        command.Parameters.AddWithValue("$current", currentId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public void Dispose() { } // Connections are owned and closed by each operation.
}

public sealed record GridRunRecord(
    string Id, string Status, string? JobId, GridSettings Settings, string ModelVersion,
    int CandidateCount, int ProcessedCount, int GridCount, int NonGridCount, int SkippedCount,
    int MetadataPublishedCount, string? Error, int LastImageId, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)
{
    public bool Resumable => Status is "cancelled" or "interrupted" or "failed"
        && (CandidateCount < 0 || ProcessedCount < CandidateCount);
}
