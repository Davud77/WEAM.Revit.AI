using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    private static object ReadDocumentation(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var offset = JsonTools.GetInt(args, "offset", 0);
        var limit = JsonTools.GetInt(args, "limit", 5);
        if (offset < 0 || limit is < 1 or > 10) throw new ArgumentException("offset >= 0 and limit 1..10 required.");
        var allSheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .OrderBy(s => s.Id.Value).ToArray();
        var errors = new List<object>();
        var typeIds = new HashSet<long>();
        var views = new Dictionary<long, object>();
        var owned = new FilteredElementCollector(doc).WhereElementIsNotElementType()
            .Where(e => e.ViewSpecific).ToLookup(e => e.OwnerViewId.Value);
        var batch = new List<object>();
        foreach (var sheet in allSheets.Skip(offset).Take(limit))
        {
            var placements = new List<object>();
            foreach (var id in sheet.GetAllViewports())
            {
                var vp = (Viewport)doc.GetElement(id);
                var view = (View)doc.GetElement(vp.ViewId);
                AddView(view);
                typeIds.Add(vp.GetTypeId().Value);
                placements.Add(new { id = id.Value, viewId = view.Id.Value, typeId = vp.GetTypeId().Value,
                    centerMm = DocPoint(vp.GetBoxCenter(), UnitTypeId.Millimeters),
                    boundsMm = DocOutline(vp.GetBoxOutline()), labelBoundsMm = Safe(id.Value, "labelBounds", () => DocOutline(vp.GetLabelOutline())),
                    rotation = vp.Rotation.ToString(), labelOffsetMm = DocPoint(vp.LabelOffset, UnitTypeId.Millimeters),
                    labelLineLengthMm = SheetMm(vp.LabelLineLength), parameters = DocParameters(vp) });
            }
            var schedules = new List<object>();
            foreach (var instance in owned[sheet.Id.Value].OfType<ScheduleSheetInstance>())
            {
                if (doc.GetElement(instance.ScheduleId) is ViewSchedule schedule)
                {
                    AddView(schedule);
                    schedules.Add(new { id = instance.Id.Value, scheduleId = schedule.Id.Value,
                        pointMm = DocPoint(instance.Point, UnitTypeId.Millimeters), segmentIndex = instance.SegmentIndex,
                        bounds = DocBounds(instance, sheet) });
                }
            }
            var blocks = owned[sheet.Id.Value].OfType<FamilyInstance>()
                .Where(e => e.Category?.Id.Value == (long)BuiltInCategory.OST_TitleBlocks).ToArray();
            foreach (var b in blocks) typeIds.Add(b.GetTypeId().Value);
            batch.Add(new { id = sheet.Id.Value, sheet.SheetNumber, name = sheet.Name,
                placeholder = sheet.IsPlaceholder, printable = sheet.CanBePrinted,
                outlineMm = Safe(sheet.Id.Value, "outline", () => new { minX = SheetMm(sheet.Outline.Min.U), minY = SheetMm(sheet.Outline.Min.V),
                    maxX = SheetMm(sheet.Outline.Max.U), maxY = SheetMm(sheet.Outline.Max.V) }),
                parameters = DocParameters(sheet), titleBlocks = blocks.Select(b => new { id = b.Id.Value,
                    typeId = b.GetTypeId().Value, family = b.Symbol.FamilyName, type = b.Symbol.Name,
                    parameters = DocParameters(b), bounds = DocBounds(b, sheet) }).ToArray(),
                viewports = placements, schedules,
                annotations = owned[sheet.Id.Value].Where(e => e is not Viewport && e is not ScheduleSheetInstance && !blocks.Contains(e))
                    .Select(e => Annotation(e, sheet)).ToArray() });
        }
        return new { project = doc.Title, path = doc.PathName, documentKey = doc.ProjectInformation.UniqueId,
            documentRevision = DocumentRevision(doc), modelModified = doc.IsModified, modelChanged = false,
            totalSheets = allSheets.Length, offset, returnedSheets = batch.Count, nextOffset = offset + batch.Count,
            sheets = batch, views, types = typeIds.Where(id => id > 0).Select(id => doc.GetElement(new ElementId(id)))
                .Where(e => e is not null).Select(e => new { id = e!.Id.Value, name = e.Name,
                    className = e.GetType().Name, category = e.Category?.Name, family = (e as ElementType)?.FamilyName,
                    parameters = DocParameters(e) }).ToArray(), errors };

        object? Safe(long id, string field, Func<object?> action)
        {
            try { return action(); }
            catch (Exception e) { errors.Add(new { elementId = id, field, error = e.Message }); return null; }
        }

        void AddView(View view)
        {
            if (views.ContainsKey(view.Id.Value)) return;
            typeIds.Add(view.GetTypeId().Value);
            var record = new Dictionary<string, object?> { ["id"] = view.Id.Value, ["name"] = view.Name,
                ["kind"] = view.ViewType.ToString(), ["templateId"] = view.ViewTemplateId.Value,
                ["isTemplate"] = view.IsTemplate, ["scale"] = view.Scale, ["typeId"] = view.GetTypeId().Value,
                ["parameters"] = DocParameters(view), ["dependentViewIds"] = view.GetDependentViewIds().Select(i => i.Value).ToArray(),
                ["primaryViewId"] = view.GetPrimaryViewId().Value };
            views[view.Id.Value] = record;
            if (view is ViewSchedule schedule) record["schedule"] = Safe(view.Id.Value, "schedule", () => DocSchedule(doc, schedule));
            else
            {
                record["detailLevel"] = Safe(view.Id.Value, "detailLevel", () => view.DetailLevel.ToString());
                record["displayStyle"] = Safe(view.Id.Value, "displayStyle", () => view.DisplayStyle.ToString());
                record["crop"] = Safe(view.Id.Value, "crop", () => new { active = view.CropBoxActive,
                    visible = view.CropBoxVisible, box = DocBox(view.CropBox) });
                record["axes"] = Safe(view.Id.Value, "axes", () => new { originMeters = DocPoint(view.Origin),
                    right = DocVector(view.RightDirection), up = DocVector(view.UpDirection), direction = DocVector(view.ViewDirection) });
                record["filters"] = Safe(view.Id.Value, "filters", () => view.GetOrderedFilters().Select(id => new {
                    id = id.Value, name = doc.GetElement(id).Name, visible = view.GetFilterVisibility(id), enabled = view.GetIsFilterEnabled(id),
                    categories = (doc.GetElement(id) as ParameterFilterElement)?.GetCategories().Select(i => i.Value).ToArray() }).ToArray());
                if (view is ViewPlan plan) record["planRange"] = Safe(view.Id.Value, "planRange", () => {
                    using var range = plan.GetViewRange();
                    return new[] { PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane, PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane }
                        .Select(p => new { plane = p.ToString(), levelId = range.GetLevelId(p).Value,
                            offsetMeters = UnitUtils.ConvertFromInternalUnits(range.GetOffset(p), UnitTypeId.Meters) }).ToArray(); });
                if (view is View3D three) record["threeD"] = Safe(view.Id.Value, "threeD", () => {
                    using var orientation = three.GetOrientation();
                    return new { three.IsPerspective, three.IsLocked, three.IsSectionBoxActive, sectionBox = DocBox(three.GetSectionBox()),
                        eyeMeters = DocPoint(orientation.EyePosition), up = DocVector(orientation.UpDirection), forward = DocVector(orientation.ForwardDirection) }; });
            }
            record["annotations"] = owned[view.Id.Value].Select(e => Annotation(e, view)).ToArray();
            if (view.IsTemplate) record["controlledParameterIds"] = view.GetTemplateParameterIds().Select(i => i.Value).ToArray();
            else if (view.ViewTemplateId != ElementId.InvalidElementId && doc.GetElement(view.ViewTemplateId) is View template) AddView(template);
        }

        object Annotation(Element e, View view)
        {
            typeIds.Add(e.GetTypeId().Value);
            var row = new Dictionary<string, object?> { ["id"] = e.Id.Value, ["typeId"] = e.GetTypeId().Value,
                ["name"] = e.Name, ["category"] = e.Category?.Name, ["categoryId"] = e.Category?.Id.Value,
                ["className"] = e.GetType().Name, ["ownerViewId"] = e.OwnerViewId.Value,
                ["bounds"] = Safe(e.Id.Value, "bounds", () => DocBounds(e, view)) };
            if (e is TextNote text)
            {
                row["text"] = text.Text;
                row["pointMeters"] = DocPoint(text.Coord);
                row["widthMeters"] = UnitUtils.ConvertFromInternalUnits(text.Width, UnitTypeId.Meters);
                row["leaderCount"] = text.LeaderCount;
                row["baseDirection"] = DocVector(text.BaseDirection);
            }
            if (e is IndependentTag tag)
            {
                row["tagText"] = Safe(e.Id.Value, "tagText", () => tag.TagText);
                row["pointMeters"] = Safe(e.Id.Value, "tagHead", () => DocPoint(tag.TagHeadPosition));
                row["hasLeader"] = tag.HasLeader;
                row["orientation"] = tag.TagOrientation.ToString();
                row["taggedLocalElementIds"] = Safe(e.Id.Value, "taggedElements", () => tag.GetTaggedLocalElementIds().Select(i => i.Value).ToArray());
                row["leaderEndCondition"] = Safe(e.Id.Value, "leaderEndCondition", () => tag.LeaderEndCondition.ToString());
                row["leaders"] = Safe(e.Id.Value, "leaders", () => tag.GetTaggedReferences().Select(r => new {
                    stableReference = r.ConvertToStableRepresentation(doc), elementId = r.ElementId.Value,
                    linkedElementId = r.LinkedElementId.Value,
                    elbowMeters = tag.HasLeader && tag.HasLeaderElbow(r) ? DocPoint(tag.GetLeaderElbow(r)) : null,
                    endMeters = tag.HasLeader && tag.LeaderEndCondition == LeaderEndCondition.Free ? DocPoint(tag.GetLeaderEnd(r)) : null }).ToArray());
            }
            if (e is Dimension dimension)
            {
                row["curve"] = Safe(e.Id.Value, "curve", () => dimension.Curve is Line line && line.IsBound
                    ? new { startMeters = DocPoint(line.GetEndPoint(0)), endMeters = DocPoint(line.GetEndPoint(1)) } : null);
                row["valueInternal"] = Safe(e.Id.Value, "value", () => dimension.Value);
                row["valueText"] = Safe(e.Id.Value, "valueText", () => dimension.ValueString);
                row["above"] = Safe(e.Id.Value, "above", () => dimension.Above);
                row["below"] = Safe(e.Id.Value, "below", () => dimension.Below);
                row["references"] = Safe(e.Id.Value, "references", () => dimension.References.Cast<Reference>()
                    .Select(r => new { elementId = r.ElementId.Value, stable = r.ConvertToStableRepresentation(doc) }).ToArray());
                if (dimension.NumberOfSegments == 0)
                    row["originMeters"] = Safe(e.Id.Value, "origin", () => DocPoint(dimension.Origin));
                else row["segments"] = Safe(e.Id.Value, "segments", () => dimension.Segments.Cast<DimensionSegment>()
                    .Select(s => new { valueInternal = s.Value, valueText = s.ValueString, s.Above, s.Below,
                        s.Prefix, s.Suffix, s.ValueOverride, originMeters = DocPoint(s.Origin) }).ToArray());
                row["hasLeader"] = Safe(e.Id.Value, "hasLeader", () => dimension.HasLeader);
                if (e is SpotDimension spot)
                {
                    row["parameters"] = DocParameters(spot);
                    row["leaderEndMeters"] = Safe(e.Id.Value, "leaderEnd", () => spot.HasLeader ? DocPoint(spot.LeaderEndPosition) : null);
                    row["leaderShoulderMeters"] = Safe(e.Id.Value, "leaderShoulder", () => spot.HasLeader && spot.LeaderHasShoulder ? DocPoint(spot.LeaderShoulderPosition) : null);
                }
            }
            if (e is FamilyInstance family)
            {
                row["family"] = family.Symbol.FamilyName;
                row["parameters"] = DocParameters(family);
                if (family.Location is LocationPoint point) row["pointMeters"] = DocPoint(point.Point);
            }
            if (e is ImportInstance import)
            {
                row["linked"] = import.IsLinked;
                row["transform"] = new { originMeters = DocPoint(import.GetTotalTransform().Origin),
                    xAxis = DocVector(import.GetTotalTransform().BasisX), yAxis = DocVector(import.GetTotalTransform().BasisY),
                    zAxis = DocVector(import.GetTotalTransform().BasisZ) };
                row["parameters"] = DocParameters(import);
            }
            if (e is CurveElement curve) row["curve"] = Safe(e.Id.Value, "curve", () => curve.GeometryCurve is Line line && line.IsBound
                ? new { startMeters = DocPoint(line.GetEndPoint(0)), endMeters = DocPoint(line.GetEndPoint(1)) } : null);
            return row;
        }
    }

    private static object DocParameters(Element element) => element.Parameters.Cast<Parameter>().Select(p => {
        string? display = null, raw = null;
        try { display = p.AsValueString(); raw = p.StorageType switch { StorageType.String => p.AsString(),
            StorageType.Double => p.AsDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            StorageType.Integer => p.AsInteger().ToString(), StorageType.ElementId => p.AsElementId().Value.ToString(), _ => null }; }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
        return new { id = p.Id.Value, name = p.Definition.Name, storage = p.StorageType.ToString(), value = display ?? raw, raw,
            readOnly = p.IsReadOnly, shared = p.IsShared, guid = p.IsShared ? p.GUID.ToString() : null };
    }).ToArray();

    private static object DocVector(XYZ p) => new { x = p.X, y = p.Y, z = p.Z };
    private static object DocPoint(XYZ p, ForgeTypeId? unit = null) => new {
        x = UnitUtils.ConvertFromInternalUnits(p.X, unit ?? UnitTypeId.Meters),
        y = UnitUtils.ConvertFromInternalUnits(p.Y, unit ?? UnitTypeId.Meters),
        z = UnitUtils.ConvertFromInternalUnits(p.Z, unit ?? UnitTypeId.Meters) };
    private static object DocOutline(Outline box) => new { min = DocPoint(box.MinimumPoint, UnitTypeId.Millimeters), max = DocPoint(box.MaximumPoint, UnitTypeId.Millimeters) };
    private static object? DocBox(BoundingBoxXYZ? box) => box is null ? null : new {
        minLocalMeters = DocPoint(box.Min), maxLocalMeters = DocPoint(box.Max), originMeters = DocPoint(box.Transform.Origin),
        xAxis = DocVector(box.Transform.BasisX), yAxis = DocVector(box.Transform.BasisY), zAxis = DocVector(box.Transform.BasisZ) };
    private static object? DocBounds(Element element, View view) => DocBox(element.get_BoundingBox(view));

    private static object DocSchedule(Document doc, ViewSchedule schedule)
    {
        var d = schedule.Definition;
        var fields = d.GetFieldOrder().Select(id => {
            var f = d.GetField(id);
            return new { id = id.IntegerValue, name = f.GetName(), f.ColumnHeading, f.IsHidden,
                fieldType = f.FieldType.ToString(), parameterId = f.ParameterId.Value,
                widthMm = SheetMm(f.SheetColumnWidth), displayType = f.DisplayType.ToString() };
        }).ToArray();
        using var table = schedule.GetTableData();
        using var body = table.GetSectionData(SectionType.Body);
        var cells = new List<string[]>();
        var truncated = (long)body.NumberOfRows * body.NumberOfColumns > 50000;
        if (!truncated) for (var r = body.FirstRowNumber; r <= body.LastRowNumber; r++)
            cells.Add(Enumerable.Range(body.FirstColumnNumber, body.NumberOfColumns).Select(c => schedule.GetCellText(SectionType.Body, r, c)).ToArray());
        return new { categoryId = d.CategoryId.Value, d.IsItemized, d.ShowGrandTotal, d.ShowHeaders, d.ShowTitle,
            fields, sortGroups = d.GetSortGroupFields().Select(s => new { fieldId = s.FieldId.IntegerValue,
                order = s.SortOrder.ToString(), s.ShowHeader, s.ShowFooter, s.ShowBlankLine }).ToArray(),
            filters = d.GetFilters().Select(f => new { fieldId = f.FieldId.IntegerValue, kind = f.FilterType.ToString(),
                value = f.IsStringValue ? (object)f.GetStringValue() : f.IsDoubleValue ? f.GetDoubleValue()
                    : f.IsIntegerValue ? f.GetIntegerValue() : f.IsElementIdValue ? f.GetElementIdValue().Value : null }).ToArray(),
            rows = body.NumberOfRows, columns = body.NumberOfColumns, truncated, cells };
    }
}
