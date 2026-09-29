using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    private static object PreviewDeleteElements(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "delete_checked", "Удалить проверенный набор элементов и его зависимости?", DeleteOperation);

    private static object ApplyDeletePlan(UIApplication app, JsonElement args, Func<bool>? isCancelled) =>
        ApplyOperation(app, args, "delete_checked", "Удалить проверенный набор элементов и его зависимости?", DeleteOperation, isCancelled);

    private static object DeleteOperation(Document document, JsonElement args)
    {
        var ids = ReadRequestedIds(args);
        var requested = ids.ToHashSet();
        // Capture summaries before Delete invalidates Element wrappers.
        var requestedRows = ids.Select(id =>
        {
            var element = document.GetElement(new ElementId(id))
                ?? throw new ArgumentException($"Element {id} does not exist. Prepare a new preview.");
            if (element.Pinned) throw new InvalidOperationException($"Element {id} is pinned. Unpin it explicitly before deletion.");
            return new { id, name = element.Name, category = element.Category?.Name, uniqueId = element.UniqueId };
        }).ToArray();
        var deleted = document.Delete(ids.Select(id => new ElementId(id)).ToList()).Select(id => id.Value).Order().ToArray();
        if (!requested.IsSubsetOf(deleted)) throw new InvalidOperationException("Revit did not delete every requested element.");
        var dependentIds = deleted.Where(id => !requested.Contains(id)).ToArray();
        return new { requestedCount = ids.Count, deletedCount = deleted.Length, dependentCount = dependentIds.Length,
            requestedElements = requestedRows, requestedIds = ids, deletedIds = deleted, dependentIds,
            requiresDependentDeletionPermission = dependentIds.Length > 0 };
    }
}
