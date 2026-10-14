using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.DeleteImageGrids;

public sealed class GridMetadataDefinitionException(string message) : InvalidOperationException(message);

public static class GridMetadataPublisher
{
    public const string GridTagName = "Image Grid Deleter: Grid";
    public const string NonGridTagName = "Image Grid Deleter: Not grid";
    public const string ProbabilityKey = "image_grid_probability";
    public const string ProbabilityLabel = "Image grid probability (%)";
    public const string SourceKey = "extension:delete-image-grids";

    public static async Task PublishBatchAsync(
        CoveContext db, GridRunRecord run, IReadOnlyCollection<GridMatch> matches, CancellationToken ct)
    {
        if (matches.Count == 0) return;
        var definition = await EnsureDefinitionAsync(db, ct);
        var (gridTag, nonGridTag) = await EnsureTagsAsync(db, ct);
        var imageIds = matches.Select(match => match.ImageId).Distinct().ToArray();
        var classificationTagIds = new[] { gridTag.Id, nonGridTag.Id };

        var existingLinks = await db.Set<ImageTag>()
            .Where(link => imageIds.Contains(link.ImageId) && classificationTagIds.Contains(link.TagId))
            .ToListAsync(ct);
        var existingApplications = await db.TagApplications
            .Where(application => application.HostType == AffinityHostType.Image
                && imageIds.Contains(application.HostId)
                && classificationTagIds.Contains(application.TagId)
                && application.SourceKey == SourceKey)
            .ToListAsync(ct);
        var existingValues = await db.CustomFieldValues
            .Where(value => value.DefinitionId == definition.Id
                && value.EntityType == CustomFieldEntityTypes.Image
                && imageIds.Contains(value.EntityId))
            .ToListAsync(ct);

        db.TagApplications.RemoveRange(existingApplications);
        db.CustomFieldValues.RemoveRange(existingValues);

        foreach (var match in matches)
        {
            var tag = match.IsGrid ? gridTag : nonGridTag;
            var links = existingLinks.Where(link => link.ImageId == match.ImageId).ToList();
            var correctLink = links.FirstOrDefault(link => link.TagId == tag.Id);
            db.Set<ImageTag>().RemoveRange(links.Where(link => link.TagId != tag.Id));
            if (correctLink is null)
                db.Set<ImageTag>().Add(new ImageTag { ImageId = match.ImageId, TagId = tag.Id });
            db.TagApplications.Add(new TagApplication
            {
                HostType = AffinityHostType.Image,
                HostId = match.ImageId,
                TagId = tag.Id,
                SourceKey = SourceKey,
                SourceRunId = run.Id,
                ModelKey = run.ModelVersion,
                Confidence = match.GridConfidence,
            });
            db.CustomFieldValues.Add(new CustomFieldValue
            {
                DefinitionId = definition.Id,
                EntityType = CustomFieldEntityTypes.Image,
                EntityId = match.ImageId,
                Position = 0,
                NumberValue = GridMetadataValues.ToStoredPercent(match.GridConfidence),
            });
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    public static async Task ApplyTrainingLabelAsync(
        CoveContext db, IReadOnlyCollection<int> imageIds, bool isGrid, CancellationToken ct)
    {
        var ids = imageIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return;
        var (gridTag, nonGridTag) = await EnsureTagsAsync(db, ct);
        var targetTag = isGrid ? gridTag : nonGridTag;
        var classificationTagIds = new[] { gridTag.Id, nonGridTag.Id };
        var existingLinks = await db.Set<ImageTag>()
            .Where(link => ids.Contains(link.ImageId) && classificationTagIds.Contains(link.TagId))
            .ToListAsync(ct);
        var existingApplications = await db.TagApplications
            .Where(application => application.HostType == AffinityHostType.Image
                && ids.Contains(application.HostId)
                && classificationTagIds.Contains(application.TagId)
                && application.SourceKey == SourceKey)
            .ToListAsync(ct);

        db.TagApplications.RemoveRange(existingApplications);
        foreach (var imageId in ids)
        {
            var links = existingLinks.Where(link => link.ImageId == imageId).ToList();
            var correctLink = links.FirstOrDefault(link => link.TagId == targetTag.Id);
            db.Set<ImageTag>().RemoveRange(links.Where(link => link.TagId != targetTag.Id));
            if (correctLink is null)
                db.Set<ImageTag>().Add(new ImageTag { ImageId = imageId, TagId = targetTag.Id });
            db.TagApplications.Add(new TagApplication
            {
                HostType = AffinityHostType.Image,
                HostId = imageId,
                TagId = targetTag.Id,
                SourceKey = SourceKey,
                SourceRunId = "training-data-correction",
                ModelKey = "manual-training-label",
                Confidence = isGrid ? 1f : 0f,
            });
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private static async Task<CustomFieldDefinition> EnsureDefinitionAsync(CoveContext db, CancellationToken ct)
    {
        var definition = await db.CustomFieldDefinitions.FirstOrDefaultAsync(item => item.Key == ProbabilityKey, ct);
        if (definition is not null)
        {
            if (CustomFieldTypes.Normalize(definition.Type) != CustomFieldTypes.Number
                || !definition.EntityTypes.Contains(CustomFieldEntityTypes.Image))
            {
                throw new GridMetadataDefinitionException(
                    $"The custom field '{ProbabilityKey}' already exists but is not a numeric Image field. Rename or remove it in Cove Custom Fields settings, then resume this run.");
            }
            if (!definition.Filterable || !definition.Sortable)
            {
                definition.Filterable = true;
                definition.Sortable = true;
                definition.Label = ProbabilityLabel;
                definition.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return definition;
        }

        var nextOrder = (await db.CustomFieldDefinitions.Select(item => (int?)item.DisplayOrder).MaxAsync(ct) ?? -10) + 10;
        definition = new CustomFieldDefinition
        {
            Key = ProbabilityKey,
            Label = ProbabilityLabel,
            Type = CustomFieldTypes.Number,
            EntityTypes = [CustomFieldEntityTypes.Image],
            Filterable = true,
            Sortable = true,
            IsMultiValue = false,
            DisplayOrder = nextOrder,
        };
        db.CustomFieldDefinitions.Add(definition);
        await db.SaveChangesAsync(ct);
        return definition;
    }

    private static async Task<(Tag Grid, Tag NonGrid)> EnsureTagsAsync(CoveContext db, CancellationToken ct)
    {
        var tags = await db.Tags.Where(tag => tag.Name == GridTagName || tag.Name == NonGridTagName).ToListAsync(ct);
        var grid = tags.FirstOrDefault(tag => tag.Name == GridTagName);
        var nonGrid = tags.FirstOrDefault(tag => tag.Name == NonGridTagName);
        if (grid is null)
        {
            grid = new Tag { Name = GridTagName, Description = "Image classified as a grid by Image Grid Deleter.", Organized = true };
            db.Tags.Add(grid);
        }
        if (nonGrid is null)
        {
            nonGrid = new Tag { Name = NonGridTagName, Description = "Image classified as not a grid by Image Grid Deleter.", Organized = true };
            db.Tags.Add(nonGrid);
        }
        if (grid.Id == 0 || nonGrid.Id == 0) await db.SaveChangesAsync(ct);
        return (grid, nonGrid);
    }
}
