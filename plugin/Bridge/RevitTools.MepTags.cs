using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    private static readonly (BuiltInCategory Element, BuiltInCategory Tag)[] MepTagCategories =
    [
        (BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctTags),
        (BuiltInCategory.OST_FlexDuctCurves, BuiltInCategory.OST_FlexDuctTags),
        (BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_DuctFittingTags),
        (BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_DuctAccessoryTags),
        (BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_DuctTerminalTags),
        (BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_MechanicalEquipmentTags),
        (BuiltInCategory.OST_DuctInsulations, BuiltInCategory.OST_DuctInsulationsTags),
        (BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeTags),
        (BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_FlexPipeTags),
        (BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeFittingTags),
        (BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_PipeAccessoryTags),
        (BuiltInCategory.OST_PipeInsulations, BuiltInCategory.OST_PipeInsulationsTags)
    ];

    private static object PreviewTagMep(UIApplication app, JsonElement args) =>
        PreviewOperation(app, BindAnnotationView(app, args), "tagMep", "Марки инженерных элементов", TagMepElements);

    private static object ApplyTagMep(UIApplication app, JsonElement args, Func<bool>? isCancelled) =>
        ApplyOperation(app, args, "tagMep", "Разместить инженерные марки", TagMepElements, isCancelled);

    private static object TagMepElements(Document document, JsonElement args)
    {
        var viewId = AnnotationId(args, "viewId");
        if (document.GetElement(new ElementId(viewId)) is not View view || view.IsTemplate)
            throw new ArgumentException("viewId must identify a non-template model view.");
        if (view is View3D threeDimensional)
        {
            if (threeDimensional.IsPerspective || !threeDimensional.IsLocked)
                throw new ArgumentException("MEP tags require a locked orthographic 3D view. Lock its orientation first.");
        }
        else if (view.ViewType is not (ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan
            or ViewType.Section or ViewType.Elevation or ViewType.Detail))
            throw new ArgumentException("MEP tags support model plans, sections, elevations, details and locked orthographic 3D views.");
        if (!args.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array || tags.GetArrayLength() is < 1 or > 50)
            throw new ArgumentException("tags must contain between 1 and 50 items.");

        var mappings = new Dictionary<long, long>();
        if (args.TryGetProperty("categoryTypes", out var categoryTypes))
        {
            if (categoryTypes.ValueKind != JsonValueKind.Array || categoryTypes.GetArrayLength() is < 1 or > 12)
                throw new ArgumentException("categoryTypes must contain between 1 and 12 mappings.");
            foreach (var mapping in categoryTypes.EnumerateArray())
            {
                var categoryName = JsonTools.GetString(mapping, "category");
                if (!Enum.TryParse<BuiltInCategory>(categoryName, out var category)
                    || !MepTagCategories.Any(pair => pair.Element == category)
                    || !mappings.TryAdd((long)category, AnnotationId(mapping, "tagTypeId")))
                    throw new ArgumentException("categoryTypes must contain unique supported MEP categories.");
            }
        }
        var visible = new FilteredElementCollector(document, view.Id).WhereElementIsNotElementType()
            .ToElementIds().Select(id => id.Value).ToHashSet();
        var existing = new HashSet<(long Element, long Type)>();
        foreach (var tag in new FilteredElementCollector(document, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            foreach (var target in tag.GetTaggedLocalElementIds()) existing.Add((target.Value, tag.GetTypeId().Value));
        var created = new List<object>();
        var skipped = new List<object>();
        var requested = new HashSet<long>();
        foreach (var item in tags.EnumerateArray())
        {
            var elementId = AnnotationId(item, "elementId");
            if (!requested.Add(elementId)) throw new ArgumentException("Each element may appear only once in a tag batch.");
            var element = document.GetElement(new ElementId(elementId)) ?? throw new ArgumentException($"Element {elementId} does not exist.");
            var categoryId = element.Category?.Id.Value ?? 0;
            var pair = MepTagCategories.FirstOrDefault(pair => (long)pair.Element == categoryId);
            if (pair == default || element is ElementType || element is RevitLinkInstance)
                throw new ArgumentException($"Element {elementId} must belong to a supported local MEP category.");
            var typeId = item.TryGetProperty("tagTypeId", out _) ? AnnotationId(item, "tagTypeId")
                : mappings.TryGetValue(categoryId, out var mapped) ? mapped
                : throw new ArgumentException($"No tag type supplied for element {elementId}; provide tagTypeId or categoryTypes.");
            if (document.GetElement(new ElementId(typeId)) is not FamilySymbol type || type.Category?.Id.Value != (long)pair.Tag)
                throw new ArgumentException($"Type {typeId} must be an existing {pair.Tag} family type for element {elementId}.");
            if (!visible.Contains(elementId) || element.IsHidden(view))
            { skipped.Add(new { elementId, reason = "notVisibleInView" }); continue; }
            if (existing.Contains((elementId, typeId)))
            { skipped.Add(new { elementId, reason = "alreadyTaggedWithType" }); continue; }
            var leader = !item.TryGetProperty("leader", out _) || AnnotationBoolean(item, "leader");
            if (!leader && (item.TryGetProperty("leaderEndPosition", out _) || item.TryGetProperty("elbowPosition", out _)))
                throw new ArgumentException("Leader points require leader=true.");
            var bounds = element.get_BoundingBox(view) ?? element.get_BoundingBox(null)
                ?? throw new ArgumentException($"Element {elementId} has no placement geometry.");
            var anchor = element.Location switch
            {
                LocationPoint position => position.Point,
                LocationCurve curve => curve.Curve.Evaluate(0.5, true),
                _ => bounds.Transform.OfPoint((bounds.Min + bounds.Max) / 2)
            };
            var head = item.TryGetProperty("headPosition", out _) ? ToXYZ(ReadPoint3D(item, "headPosition"))
                : anchor + AnnotationTagOffset(item, view, 5);
            if (!type.IsActive) { type.Activate(); document.Regenerate(); }
            var reference = new Reference(element);
            var tag = IndependentTag.Create(document, type.Id, view.Id, reference, leader,
                JsonTools.GetString(item, "orientation", "horizontal") switch
                {
                    "horizontal" => TagOrientation.Horizontal,
                    "vertical" => TagOrientation.Vertical,
                    _ => throw new ArgumentException("orientation must be horizontal or vertical.")
                }, leader ? anchor : head);
            tag.TagHeadPosition = head;
            if (leader && item.TryGetProperty("leaderEndPosition", out _))
            {
                tag.LeaderEndCondition = LeaderEndCondition.Free;
                tag.SetLeaderEnd(reference, ToXYZ(ReadPoint3D(item, "leaderEndPosition")));
            }
            if (leader && item.TryGetProperty("elbowPosition", out _))
                tag.SetLeaderElbow(reference, ToXYZ(ReadPoint3D(item, "elbowPosition")));
            document.Regenerate();
            var text = tag.TagText.Trim();
            if (tag.IsOrphaned || !tag.GetTaggedLocalElementIds().Any(id => id.Value == elementId)
                || string.IsNullOrWhiteSpace(text) || text.Contains('?'))
                throw new InvalidOperationException($"Tag for element {elementId} has no valid label. Check family label bindings and element parameters.");
            created.Add(new { id = tag.Id.Value, elementId, tagTypeId = typeId, tagText = text,
                headPosition = AnnotationPointMeters(tag.TagHeadPosition), hasLeader = tag.HasLeader,
                leaderEndCondition = tag.LeaderEndCondition.ToString(),
                elbowPosition = tag.HasLeader && tag.HasLeaderElbow(reference) ? AnnotationPointMeters(tag.GetLeaderElbow(reference)) : null,
                leaderEndPosition = tag.HasLeader && tag.LeaderEndCondition == LeaderEndCondition.Free ? AnnotationPointMeters(tag.GetLeaderEnd(reference)) : null });
        }
        return new { viewId, count = created.Count, skippedCount = skipped.Count, created, skipped,
            note = "Native dependent tags; types are chosen by actual category and explicit mapping. Layout collisions require visual review; no automatic collision solving." };
    }
}
