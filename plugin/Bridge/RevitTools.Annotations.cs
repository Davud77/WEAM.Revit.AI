using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    private static object ListAnnotationTypes(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        var kind = JsonTools.GetString(args, "kind", "all");
        if (kind is not ("all" or "linearDimensions" or "wallTags" or "roomTags" or "mepTags"))
            throw new ArgumentException("kind must be all, linearDimensions, wallTags, roomTags or mepTags.");
        var types = new List<object>();
        if (kind is "all" or "linearDimensions")
            types.AddRange(new FilteredElementCollector(document).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .Where(type => type.StyleType == DimensionStyleType.Linear).OrderBy(type => type.Name)
                .Select(type => (object)new { id = type.Id.Value, kind = "linearDimensions", name = type.Name, familyName = type.FamilyName }));
        foreach (var entry in new[] { ("wallTags", BuiltInCategory.OST_WallTags), ("roomTags", BuiltInCategory.OST_RoomTags) })
        {
            if (kind != "all" && kind != entry.Item1) continue;
            types.AddRange(new FilteredElementCollector(document).OfCategory(entry.Item2).WhereElementIsElementType().Cast<ElementType>()
                .OrderBy(type => type.FamilyName).ThenBy(type => type.Name)
                .Select(type => (object)new { id = type.Id.Value, kind = entry.Item1, name = type.Name, familyName = type.FamilyName }));
        }
        if (kind is "all" or "mepTags")
            foreach (var entry in MepTagCategories)
                types.AddRange(new FilteredElementCollector(document).OfCategory(entry.Tag).WhereElementIsElementType().Cast<FamilySymbol>()
                    .OrderBy(type => type.FamilyName).ThenBy(type => type.Name)
                    .Select(type => (object)new { id = type.Id.Value, kind = "mepTags", name = type.Name,
                        familyName = type.FamilyName, elementCategory = entry.Element.ToString(), tagCategory = entry.Tag.ToString() }));
        return new { count = types.Count, types };
    }

    private static object GetElementReferences(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        var view = AnnotationView(document, BindAnnotationView(app, args));
        var ids = AnnotationIds(args, "elementIds", 20, required: true);
        var limit = JsonTools.GetInt(args, "maxReferencesPerElement", 100);
        if (limit is < 1 or > 200) throw new ArgumentException("maxReferencesPerElement must be between 1 and 200.");
        var rows = new List<object>();
        foreach (var id in ids)
        {
            var element = document.GetElement(new ElementId(id)) ?? throw new ArgumentException($"Element {id} does not exist in this document.");
            if (element is RevitLinkInstance) throw new ArgumentException("Linked model references are not supported.");
            var references = AnnotationReferences(document, element, view);
            rows.Add(new
            {
                elementId = id,
                name = element.Name,
                count = references.Count,
                returnedCount = Math.Min(limit, references.Count),
                hasMore = references.Count > limit,
                references = references.Take(limit).Select(reference => new
                {
                    reference.StableReference,
                    reference.Kind,
                    reference.Name,
                    reference.ReferenceType,
                    pointMeters = reference.Point is null ? null : AnnotationPointMeters(reference.Point),
                    normal = reference.Normal is null ? null : new { x = reference.Normal.X, y = reference.Normal.Y, z = reference.Normal.Z },
                    reference.AreaSquareMeters
                }).ToList()
            });
        }
        return new
        {
            viewId = view.Id.Value,
            viewName = view.Name,
            dimensionPlaneOriginMeters = AnnotationPointMeters(AnnotationPlaneOrigin(view)),
            viewDirection = new { x = view.ViewDirection.X, y = view.ViewDirection.Y, z = view.ViewDirection.Z },
            elements = rows,
            note = "Planar faces and family reference planes/lines in the current document. Use elementId and stableReference together. A reference is a candidate; preview_create_dimensions checks dimension compatibility in a rolled-back Revit transaction."
        };
    }

    private static object PreviewCreateDimensions(UIApplication app, JsonElement args) =>
        PreviewOperation(app, BindAnnotationView(app, args), "createDimensions", "Создание линейных размеров", CreateAnnotationDimensions);

    private static object ApplyCreateDimensions(UIApplication app, JsonElement args, Func<bool>? isCancelled) =>
        ApplyOperation(app, args, "createDimensions", "Создать линейные размеры", CreateAnnotationDimensions, isCancelled);

    private static object PreviewTagWalls(UIApplication app, JsonElement args) =>
        PreviewOperation(app, BindAnnotationView(app, args), "tagWalls", "Марки стен на плане", TagAnnotationWalls);

    private static object ApplyTagWalls(UIApplication app, JsonElement args, Func<bool>? isCancelled) =>
        ApplyOperation(app, args, "tagWalls", "Разместить марки стен", TagAnnotationWalls, isCancelled);

    private static object PreviewTagRooms(UIApplication app, JsonElement args) =>
        PreviewOperation(app, BindAnnotationView(app, args), "tagRooms", "Марки помещений на плане", TagAnnotationRooms);

    private static object ApplyTagRooms(UIApplication app, JsonElement args, Func<bool>? isCancelled) =>
        ApplyOperation(app, args, "tagRooms", "Разместить марки помещений", TagAnnotationRooms, isCancelled);

    private static object CreateAnnotationDimensions(Document document, JsonElement args)
    {
        var view = AnnotationView(document, args);
        if (!args.TryGetProperty("dimensions", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > 20)
            throw new ArgumentException("dimensions must contain between 1 and 20 items.");
        var created = new List<object>();
        var referenceCache = new Dictionary<long, List<AnnotationReference>>();
        foreach (var item in items.EnumerateArray())
        {
            var typeId = AnnotationId(item, "dimensionTypeId");
            if (document.GetElement(new ElementId(typeId)) is not DimensionType type || type.StyleType != DimensionStyleType.Linear)
                throw new ArgumentException($"Dimension type {typeId} must be an existing linear dimension type.");
            if (!item.TryGetProperty("line", out var lineData)) throw new ArgumentException("Each dimension needs a line.");
            var start = ToXYZ(ReadPoint3D(lineData, "start"));
            var end = ToXYZ(ReadPoint3D(lineData, "end"));
            if (start.DistanceTo(end) <= document.Application.ShortCurveTolerance)
                throw new ArgumentException("Dimension line is shorter than Revit's short curve tolerance.");
            var direction = (end - start).Normalize();
            var viewNormal = view.ViewDirection.Normalize();
            var planeOrigin = AnnotationPlaneOrigin(view);
            if (Math.Abs(direction.DotProduct(viewNormal)) > 1e-7
                || Math.Abs((start - planeOrigin).DotProduct(viewNormal)) > 0.001)
                throw new ArgumentException("Dimension line must lie in the selected view plane. Obtain dimensionPlaneOriginMeters from get_element_references.");
            if (!item.TryGetProperty("references", out var inputs) || inputs.ValueKind != JsonValueKind.Array || inputs.GetArrayLength() is < 2 or > 20)
                throw new ArgumentException("Each dimension needs between 2 and 20 explicit references.");
            var references = new ReferenceArray();
            var stableKeys = new HashSet<string>(StringComparer.Ordinal);
            var owners = new List<long>();
            foreach (var input in inputs.EnumerateArray())
            {
                var ownerId = AnnotationId(input, "elementId");
                var stable = JsonTools.GetString(input, "stableReference");
                if (stable.Length is < 1 or > 4096 || !stableKeys.Add(stable))
                    throw new ArgumentException("Dimension references must be non-empty, unique stable references.");
                var owner = document.GetElement(new ElementId(ownerId)) ?? throw new ArgumentException($"Reference owner {ownerId} no longer exists.");
                if (owner is RevitLinkInstance) throw new ArgumentException("Dimensions to linked models are not supported.");
                var reference = Reference.ParseFromStableRepresentation(document, stable);
                if (reference.ElementId != owner.Id || reference.LinkedElementId != ElementId.InvalidElementId)
                    throw new ArgumentException("A reference must belong to the specified element in the current document.");
                if (!referenceCache.TryGetValue(ownerId, out var candidates))
                    referenceCache[ownerId] = candidates = AnnotationReferences(document, owner, view);
                var candidate = candidates.FirstOrDefault(value => value.StableReference == stable)
                    ?? throw new ArgumentException($"Reference for element {ownerId} is not an available planar face or family reference. Refresh get_element_references.");
                if (candidate.Normal is not null &&
                    (Math.Abs(Math.Abs(candidate.Normal.DotProduct(direction)) - 1.0) > 1e-6
                     || Math.Abs(candidate.Normal.DotProduct(viewNormal)) > 1e-6))
                    throw new ArgumentException("Referenced faces must be parallel to one another, perpendicular to the dimension line, and perpendicular to the view plane.");
                references.Append(reference);
                owners.Add(ownerId);
            }
            var dimension = document.Create.NewDimension(view, Line.CreateBound(start, end), references, type)
                ?? throw new InvalidOperationException("Revit did not create the dimension.");
            document.Regenerate();
            if (!dimension.AreReferencesAvailable) throw new InvalidOperationException("Revit could not resolve the dimension references.");
            var measurements = new List<double>();
            if (dimension.Segments.Size > 0)
            {
                foreach (DimensionSegment segment in dimension.Segments)
                {
                    if (!segment.Value.HasValue) throw new InvalidOperationException("Revit did not calculate a dimension segment.");
                    measurements.Add(UnitUtils.ConvertFromInternalUnits(segment.Value.Value, UnitTypeId.Meters));
                }
            }
            else if (dimension.Value.HasValue)
                measurements.Add(UnitUtils.ConvertFromInternalUnits(dimension.Value.Value, UnitTypeId.Meters));
            if (measurements.Count != references.Size - 1 || measurements.Any(value => !double.IsFinite(value) || value <= 1e-7))
                throw new InvalidOperationException("Dimension references did not produce a non-zero linear measurement for every segment.");
            created.Add(new { id = dimension.Id.Value, viewId = view.Id.Value, dimensionTypeId = typeId, referencedElementIds = owners, measurementsMeters = measurements });
        }
        return new { count = created.Count, viewId = view.Id.Value, created };
    }

    private static object TagAnnotationWalls(Document document, JsonElement args)
    {
        var view = AnnotationView(document, args, planOnly: true);
        var typeId = AnnotationId(args, "tagTypeId");
        if (document.GetElement(new ElementId(typeId)) is not FamilySymbol type || type.Category?.Id.Value != (long)BuiltInCategory.OST_WallTags)
            throw new ArgumentException("tagTypeId must identify an existing wall tag family type.");
        var visible = new FilteredElementCollector(document, view.Id).OfClass(typeof(Wall)).WhereElementIsNotElementType()
            .Cast<Wall>().Where(wall => !wall.IsHidden(view)).Select(wall => wall.Id.Value).ToHashSet();
        var ids = AnnotationTagTargets(args, visible);
        var tagged = new HashSet<long>();
        foreach (var tag in new FilteredElementCollector(document).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
                     .Where(tag => tag.OwnerViewId == view.Id && tag.Category?.Id.Value == (long)BuiltInCategory.OST_WallTags))
            foreach (var id in tag.GetTaggedLocalElementIds()) tagged.Add(id.Value);
        var leader = AnnotationBoolean(args, "leader");
        var offset = AnnotationTagOffset(args, view, 5);
        var created = new List<object>();
        var skipped = new List<object>();
        foreach (var id in ids)
        {
            if (document.GetElement(new ElementId(id)) is not Wall wall) throw new ArgumentException($"Element {id} is not a wall in this document.");
            if (tagged.Contains(id)) { skipped.Add(new { elementId = id, reason = "alreadyTagged" }); continue; }
            if (!visible.Contains(id)) { skipped.Add(new { elementId = id, reason = "notVisibleInView" }); continue; }
            if (wall.Location is not LocationCurve location) { skipped.Add(new { elementId = id, reason = "noLocationCurve" }); continue; }
            if (!type.IsActive) { type.Activate(); document.Regenerate(); }
            var anchor = AnnotationProjectToView(location.Curve.Evaluate(0.5, true), view);
            var head = anchor + offset;
            var tag = IndependentTag.Create(document, type.Id, view.Id, new Reference(wall), leader, TagOrientation.Horizontal, leader ? anchor : head)
                ?? throw new InvalidOperationException($"Revit did not create a tag for wall {id}.");
            if (leader) tag.TagHeadPosition = head;
            created.Add(new { id = tag.Id.Value, wallId = id, positionMeters = AnnotationPointMeters(head) });
        }
        document.Regenerate();
        return new { viewId = view.Id.Value, tagTypeId = typeId, count = created.Count, created, skipped };
    }

    private static object TagAnnotationRooms(Document document, JsonElement args)
    {
        var view = AnnotationView(document, args, planOnly: true);
        var typeId = AnnotationId(args, "tagTypeId");
        if (document.GetElement(new ElementId(typeId)) is not RoomTagType type)
            throw new ArgumentException("tagTypeId must identify an existing room tag type.");
        var visible = new FilteredElementCollector(document, view.Id).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
            .OfType<Room>().Where(room => !room.IsHidden(view)).Select(room => room.Id.Value).ToHashSet();
        var ids = AnnotationTagTargets(args, visible);
        var tagged = new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType().OfType<RoomTag>()
            .Where(tag => tag.OwnerViewId == view.Id && !tag.IsTaggingLink && tag.Room is not null)
            .Select(tag => tag.Room.Id.Value).ToHashSet();
        var leader = AnnotationBoolean(args, "leader");
        var offset = AnnotationTagOffset(args, view, 0);
        var created = new List<object>();
        var skipped = new List<object>();
        foreach (var id in ids)
        {
            if (document.GetElement(new ElementId(id)) is not Room room) throw new ArgumentException($"Element {id} is not a room in this document.");
            if (tagged.Contains(id)) { skipped.Add(new { elementId = id, reason = "alreadyTagged" }); continue; }
            if (room.Location is not LocationPoint location || room.Area <= 0) { skipped.Add(new { elementId = id, reason = "unplacedOrUnenclosed" }); continue; }
            if (!visible.Contains(id)) { skipped.Add(new { elementId = id, reason = "notVisibleInView" }); continue; }
            var point = location.Point + offset;
            var tag = document.Create.NewRoomTag(new LinkElementId(room.Id), new UV(point.X, point.Y), view.Id)
                ?? throw new InvalidOperationException($"Revit did not create a tag for room {id}.");
            tag.RoomTagType = type;
            tag.HasLeader = leader;
            created.Add(new { id = tag.Id.Value, roomId = id, number = room.Number, positionMeters = AnnotationPointMeters(point) });
        }
        document.Regenerate();
        return new { viewId = view.Id.Value, tagTypeId = typeId, count = created.Count, created, skipped };
    }

    private static JsonElement BindAnnotationView(UIApplication app, JsonElement args)
    {
        var data = JsonNode.Parse(args.GetRawText())?.AsObject() ?? throw new ArgumentException("Expected arguments object.");
        if (!data.ContainsKey("viewId")) data["viewId"] = app.ActiveUIDocument?.ActiveView.Id.Value ?? throw new InvalidOperationException("Open a project first.");
        return JsonSerializer.SerializeToElement(data);
    }

    private static View AnnotationView(Document document, JsonElement args, bool planOnly = false)
    {
        var id = AnnotationId(args, "viewId");
        if (document.GetElement(new ElementId(id)) is not View view || view.IsTemplate)
            throw new ArgumentException("viewId must identify an existing non-template view.");
        var isPlan = view.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan;
        if (planOnly ? !isPlan : !(isPlan || view.ViewType is ViewType.EngineeringPlan or ViewType.Section or ViewType.Elevation or ViewType.Detail or ViewType.DraftingView))
            throw new ArgumentException(planOnly ? "Tags support floor and ceiling plans only." : "Dimensions support plan, section, elevation, detail and drafting views; 3D views and sheets are not supported.");
        return view;
    }

    private static XYZ AnnotationPlaneOrigin(View view) => view is ViewPlan plan && plan.GenLevel is not null
        ? new XYZ(0, 0, plan.GenLevel.ProjectElevation) : view.Origin;

    private static XYZ AnnotationProjectToView(XYZ point, View view) => point - view.ViewDirection.Normalize()
        .Multiply((point - AnnotationPlaneOrigin(view)).DotProduct(view.ViewDirection.Normalize()));

    private static object AnnotationPointMeters(XYZ point) => new
    {
        xMeters = UnitUtils.ConvertFromInternalUnits(point.X, UnitTypeId.Meters),
        yMeters = UnitUtils.ConvertFromInternalUnits(point.Y, UnitTypeId.Meters),
        zMeters = UnitUtils.ConvertFromInternalUnits(point.Z, UnitTypeId.Meters)
    };

    private static long AnnotationId(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var id) || id <= 0)
            throw new ArgumentException($"{name} must be a positive element id.");
        return id;
    }

    private static List<long> AnnotationIds(JsonElement args, string name, int maximum, bool required)
    {
        if (!args.TryGetProperty(name, out var array))
        {
            if (required) throw new ArgumentException($"{name} is required.");
            return [];
        }
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 || array.GetArrayLength() > maximum)
            throw new ArgumentException($"{name} must contain between 1 and {maximum} ids.");
        var ids = new List<long>();
        foreach (var value in array.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var id) || id <= 0)
                throw new ArgumentException($"{name} must contain positive integer ids.");
            if (!ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }

    private static List<long> AnnotationTagTargets(JsonElement args, HashSet<long> visible)
    {
        var ids = AnnotationIds(args, "elementIds", 50, required: false);
        if (ids.Count == 0) ids = visible.OrderBy(value => value).ToList();
        if (ids.Count > 50) throw new ArgumentException($"The view contains {ids.Count} candidates. Supply elementIds in batches of at most 50.");
        return ids;
    }

    private static bool AnnotationBoolean(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException($"{name} must be a boolean.");
        return value.GetBoolean();
    }

    private static XYZ AnnotationTagOffset(JsonElement args, View view, double defaultUp)
    {
        double ReadOffset(string name, double fallback)
        {
            if (!args.TryGetProperty(name, out var value)) return fallback;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || Math.Abs(number) > 100)
                throw new ArgumentException($"{name} must be a finite number within ±100 paper millimeters.");
            return number;
        }
        return view.RightDirection.Multiply(UnitUtils.ConvertToInternalUnits(ReadOffset("offsetRightPaperMillimeters", 0) * view.Scale, UnitTypeId.Millimeters))
            + view.UpDirection.Multiply(UnitUtils.ConvertToInternalUnits(ReadOffset("offsetUpPaperMillimeters", defaultUp) * view.Scale, UnitTypeId.Millimeters));
    }

    private sealed record AnnotationReference(string StableReference, string Kind, string Name, string ReferenceType, XYZ? Point, XYZ? Normal, double? AreaSquareMeters);

    private static List<AnnotationReference> AnnotationReferences(Document document, Element element, View view)
    {
        var result = new List<AnnotationReference>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(Reference reference, string kind, string name, XYZ? point = null, XYZ? normal = null, double? area = null)
        {
            if (reference.ElementId != element.Id || reference.LinkedElementId != ElementId.InvalidElementId) return;
            var stable = reference.ConvertToStableRepresentation(document);
            if (seen.Add(stable)) result.Add(new AnnotationReference(stable, kind, name, reference.ElementReferenceType.ToString(), point, normal, area));
        }
        if (element is FamilyInstance instance)
        {
            foreach (var type in Enum.GetValues<FamilyInstanceReferenceType>())
            {
                if (type == FamilyInstanceReferenceType.NotAReference) continue;
                foreach (var reference in instance.GetReferences(type) ?? [])
                    Add(reference, "familyReference", instance.GetReferenceName(reference) is { Length: > 0 } name ? name : type.ToString());
            }
        }
        var options = new Options { ComputeReferences = true, IncludeNonVisibleObjects = false, View = view };
        var geometry = element.get_Geometry(options);
        if (geometry is not null) Walk(geometry, Transform.Identity, 0);
        return result;

        void Walk(GeometryElement geometryElement, Transform transform, int depth)
        {
            if (depth > 12 || result.Count > 2000) throw new InvalidOperationException($"Element {element.Id.Value} has too many nested reference candidates.");
            foreach (var geometryObject in geometryElement)
            {
                if (geometryObject is Solid solid)
                {
                    foreach (Face face in solid.Faces)
                    {
                        if (face is not PlanarFace plane || face.Reference is null) continue;
                        // Keep original references: transformed geometry copies cannot host dimensions.
                        var bounds = plane.GetBoundingBox();
                        var midpoint = new UV((bounds.Min.U + bounds.Max.U) / 2, (bounds.Min.V + bounds.Max.V) / 2);
                        Add(face.Reference, "planarFace", "", transform.OfPoint(plane.Evaluate(midpoint)),
                            transform.OfVector(plane.FaceNormal).Normalize(), UnitUtils.ConvertFromInternalUnits(plane.Area, UnitTypeId.SquareMeters));
                    }
                }
                else if (geometryObject is GeometryInstance nested)
                    Walk(nested.GetSymbolGeometry(), transform.Multiply(nested.Transform), depth + 1);
            }
        }
    }
}
