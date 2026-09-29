using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    private static readonly ConcurrentDictionary<string, ParameterPlan> Plans = new();
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(10);

    public static BridgeResponse Execute(UIApplication application, BridgeRequest request, Func<bool>? isCancelled = null)
    {
        try
        {
            if (isCancelled?.Invoke() == true)
                return BridgeResponse.Failure(request.Id, "The MCP client cancelled the request before Revit started it.");
            var args = request.Arguments;
            LegacyWarnings.Clear();
            if (request.Tool.StartsWith("apply_", StringComparison.Ordinal)
                && Plans.TryGetValue(JsonTools.GetString(args, "planId"), out var legacyPlan))
            {
                var currentDocument = RequireDocument(application);
                if (!(legacyPlan.OwnerDocument?.IsValidObject == true && legacyPlan.OwnerDocument.Equals(currentDocument)))
                    throw new InvalidOperationException("This plan belongs to another Revit document. Prepare a new preview.");
                if (legacyPlan.DocumentRevision != DocumentRevision(currentDocument))
                    throw new InvalidOperationException("The document changed after preview. Prepare a new preview.");
            }
            object result = request.Tool switch
            {
                "revit_ping" => Ping(application),
                "get_project_info" => GetProjectInfo(application),
                "get_current_view_elements" => GetCurrentViewElements(application, args),
                "get_selected_elements" => GetSelectedElements(application),
                "search_elements" => SearchElements(application, args),
                "get_project_stats" => GetProjectStats(application),
                "list_rooms" => ListRooms(application, args),
                "get_material_quantities" => GetMaterialQuantities(application, args),
                "list_family_types" => ListFamilyTypes(application, args),
                "list_host_types" => ListHostTypes(application, args),
                "list_plan_view_types" => ListPlanViewTypes(application),
                "get_element_properties" => GetElementProperties(application, args),
                "select_elements" => SelectElements(application, args),
                "preview_set_parameter" => PreviewSetParameter(application, args),
                "apply_parameter_plan" => ApplyParameterPlan(application, args, isCancelled),
                "preview_create_levels" => PreviewCreateLevels(application, args),
                "apply_create_levels" => ApplyCreateLevels(application, args, isCancelled),
                "preview_create_walls" => PreviewCreateWalls(application, args),
                "apply_create_walls" => ApplyCreateWalls(application, args, isCancelled),
                "preview_place_family_instances" => PreviewPlaceFamilyInstances(application, args),
                "apply_place_family_instances" => ApplyPlaceFamilyInstances(application, args, isCancelled),
                "preview_create_floors" => PreviewCreateFloors(application, args),
                "apply_create_floors" => ApplyCreateFloors(application, args, isCancelled),
                "preview_create_grids" => PreviewCreateGrids(application, args),
                "apply_create_grids" => ApplyCreateGrids(application, args, isCancelled),
                "preview_set_category_visibility" => PreviewSetCategoryVisibility(application, args),
                "apply_set_category_visibility" => ApplySetCategoryVisibility(application, args, isCancelled),
                "preview_create_beams" => PreviewCreateBeams(application, args),
                "apply_create_beams" => ApplyCreateBeams(application, args, isCancelled),
                "preview_create_ceilings" => PreviewCreateCeilings(application, args),
                "apply_create_ceilings" => ApplyCreateCeilings(application, args, isCancelled),
                "preview_create_rooms" => PreviewCreateRooms(application, args),
                "apply_create_rooms" => ApplyCreateRooms(application, args, isCancelled),
                "preview_create_plan_views" => PreviewCreatePlanViews(application, args),
                "apply_create_plan_views" => ApplyCreatePlanViews(application, args, isCancelled),
                "preview_delete_elements" => PreviewDeleteElements(application, args),
                "apply_delete_plan" => ApplyDeletePlan(application, args, isCancelled),
                "preview_place_hosted_families" => PreviewPlaceHostedFamilies(application, args),
                "apply_place_hosted_families" => ApplyPlaceHostedFamilies(application, args, isCancelled),
                "list_annotation_types" => ListAnnotationTypes(application, args),
                "get_element_references" => GetElementReferences(application, args),
                "preview_create_dimensions" => PreviewCreateDimensions(application, args),
                "apply_create_dimensions" => ApplyCreateDimensions(application, args, isCancelled),
                "preview_tag_walls" => PreviewTagWalls(application, args),
                "apply_tag_walls" => ApplyTagWalls(application, args, isCancelled),
                "preview_tag_rooms" => PreviewTagRooms(application, args),
                "apply_tag_rooms" => ApplyTagRooms(application, args, isCancelled),
                "preview_element_graphics" => PreviewElementGraphics(application, args),
                "apply_element_graphics" => ApplyElementGraphics(application, args, isCancelled),
                "preview_color_by_parameter" => PreviewColorByParameter(application, args),
                "apply_color_by_parameter" => ApplyColorByParameter(application, args, isCancelled),
                "preview_duplicate_family_types" => PreviewDuplicateFamilyTypes(application, args),
                "apply_duplicate_family_types" => ApplyDuplicateFamilyTypes(application, args, isCancelled),
                "preview_load_family" => PreviewLoadFamily(application, args),
                "apply_load_family" => ApplyLoadFamily(application, args, isCancelled),
                "preview_create_room_separators" => PreviewCreateRoomSeparators(application, args),
                "apply_create_room_separators" => ApplyCreateRoomSeparators(application, args, isCancelled),
                "preview_save_project_as" => PreviewSaveProjectAs(application, args),
                "apply_save_project_as" => ApplySaveProjectAs(application, args, isCancelled),
                "preview_save_project" => PreviewSaveProject(application),
                "apply_save_project" => ApplySaveProject(application, args, isCancelled),
                "export_view_image" => ExportViewImage(application, args),
                "list_sheet_resources" => ListSheetResources(application),
                "preview_create_sheets" => PreviewCreateSheets(application, args),
                "apply_create_sheets" => ApplyCreateSheets(application, args, isCancelled),
                "export_sheets_pdf" => ExportSheetsPdf(application, args),
                _ => throw new InvalidOperationException($"Unknown Revit tool '{request.Tool}'.")
            };
            if (LegacyWarnings.Count > 0)
            {
                var resultFields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    JsonSerializer.Serialize(result, JsonTools.SerializerOptions))!;
                resultFields["warnings"] = JsonSerializer.SerializeToElement(LegacyWarnings);
                result = resultFields;
            }
            return BridgeResponse.Success(request.Id, result);
        }
        catch (Exception exception)
        {
            return BridgeResponse.Failure(request.Id, exception.Message);
        }
    }

    private static object Ping(UIApplication app)
    {
        var document = app.ActiveUIDocument?.Document;
        return new
        {
            connected = true,
            pluginVersion = typeof(RevitTools).Assembly.GetName().Version?.ToString(),
            autoConfirm = ApprovalSettings.AutoConfirm,
            revitVersion = app.Application.VersionNumber,
            activeDocumentOpen = document is not null,
            projectName = document?.Title,
            bridgePort = BridgeService.Port
        };
    }

    private static object GetProjectInfo(UIApplication app)
    {
        var document = RequireDocument(app);
        var view = app.ActiveUIDocument!.ActiveView;
        return new
        {
            projectName = document.Title,
            projectNumber = document.ProjectInformation.Number,
            clientName = document.ProjectInformation.ClientName,
            filePath = document.PathName,
            revitVersion = app.Application.VersionNumber,
            activeView = new
            {
                id = view.Id.Value,
                name = view.Name,
                type = view.ViewType.ToString(),
                scale = view.Scale,
                detailLevel = view.DetailLevel.ToString(),
                isTemplate = view.IsTemplate
            }
        };
    }

    private static object GetCurrentViewElements(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        var view = app.ActiveUIDocument!.ActiveView;
        var categoryFilter = JsonTools.GetString(args, "category");
        var limit = Math.Clamp(JsonTools.GetInt(args, "limit", 200), 1, 500);
        var collector = new FilteredElementCollector(document, view.Id).WhereElementIsNotElementType();
        var rows = new List<object>(limit);
        var matching = 0;
        foreach (var element in collector)
        {
            if (!CategoryMatches(element, categoryFilter)) continue;
            matching++;
            if (rows.Count < limit) rows.Add(Summarize(document, element, includeParameters: false));
        }
        return new { view = view.Name, matchingCount = matching, returnedCount = rows.Count, limit, elements = rows };
    }

    private static object GetSelectedElements(UIApplication app)
    {
        var document = RequireDocument(app);
        var ids = app.ActiveUIDocument!.Selection.GetElementIds();
        return new
        {
            selectedCount = ids.Count,
            elements = ids.Select(id => document.GetElement(id)).Where(element => element is not null)
                .Select(element => Summarize(document, element!, includeParameters: false)).ToList()
        };
    }

    private static object SearchElements(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        var query = JsonTools.GetString(args, "query").Trim();
        var categoryFilter = JsonTools.GetString(args, "category");
        var classFilter = JsonTools.GetString(args, "revitClass");
        var familyFilter = JsonTools.GetString(args, "familyName");
        var typeFilter = JsonTools.GetString(args, "typeName");
        var viewScope = JsonTools.GetString(args, "viewScope", "project");
        var includeHiddenInView = args.TryGetProperty("includeHiddenInView", out var includeHiddenValue)
            && includeHiddenValue.ValueKind == JsonValueKind.True;
        var offset = Math.Clamp(JsonTools.GetInt(args, "offset", 0), 0, 100000);
        var limit = Math.Clamp(JsonTools.GetInt(args, "limit", 100), 1, 500);
        var bounds = ReadSearchBounds(args);
        var elementId = args.TryGetProperty("elementId", out var elementIdValue)
            && elementIdValue.TryGetInt64(out var parsedId) && parsedId > 0 ? parsedId : 0;

        if (viewScope is not ("project" or "activeView"))
            throw new ArgumentException("viewScope must be 'project' or 'activeView'.");
        if (includeHiddenInView && viewScope != "activeView")
            throw new ArgumentException("includeHiddenInView requires viewScope='activeView'.");
        if (query.Length == 0 && categoryFilter.Length == 0 && classFilter.Length == 0
            && familyFilter.Length == 0 && typeFilter.Length == 0 && elementId == 0 && bounds is null
            && viewScope == "project")
            throw new ArgumentException("Provide a query or at least one element filter.");

        var activeView = app.ActiveUIDocument!.ActiveView;
        FilteredElementCollector collector = viewScope == "activeView"
            ? new FilteredElementCollector(document, activeView.Id)
            : new FilteredElementCollector(document);
        collector.WhereElementIsNotElementType();
        if (bounds is not null)
            collector.WherePasses(bounds.CreateIntersectsFilter());

        var candidates = collector.ToElements().ToList();
        if (includeHiddenInView)
        {
            candidates.AddRange(new FilteredElementCollector(document)
                .WhereElementIsNotElementType()
                .ToElements()
                .Where(element => element.IsHidden(activeView)));
        }

        var matches = new List<Element>();
        var seenIds = new HashSet<long>();
        foreach (var element in candidates)
        {
            if (elementId != 0 && element.Id.Value != elementId) continue;
            if (!CategoryMatches(element, categoryFilter)) continue;
            if (classFilter.Length > 0 && !ClassMatches(element, classFilter)) continue;

            var type = document.GetElement(element.GetTypeId()) as ElementType;
            if (familyFilter.Length > 0 && !Contains(type?.FamilyName, familyFilter)) continue;
            if (typeFilter.Length > 0 && !Contains(type?.Name, typeFilter)) continue;
            if (bounds is not null && !bounds.Intersects(element.get_BoundingBox(null))) continue;
            if (query.Length > 0 && !MatchesQuery(document, element, query)) continue;
            if (!seenIds.Add(element.Id.Value)) continue;
            matches.Add(element);
        }

        matches.Sort((left, right) => left.Id.Value.CompareTo(right.Id.Value));
        var rows = matches.Skip(offset).Take(limit)
            .Select(element => Summarize(document, element, includeParameters: false)).ToList();
        var nextOffset = offset + rows.Count;
        return new
        {
            query,
            view = viewScope == "activeView" ? activeView.Name : null,
            matchingCount = matches.Count,
            returnedCount = rows.Count,
            offset,
            nextOffset,
            hasMore = nextOffset < matches.Count,
            limit,
            elements = rows
        };
    }

    private static object GetProjectStats(UIApplication app)
    {
        var document = RequireDocument(app);
        var categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        foreach (var element in new FilteredElementCollector(document).WhereElementIsNotElementType())
        {
            total++;
            var category = element.Category?.Name ?? "(без категории)";
            categoryCounts[category] = categoryCounts.GetValueOrDefault(category) + 1;
        }

        var familyTypeCount = new FilteredElementCollector(document).OfClass(typeof(FamilySymbol)).GetElementCount();
        var roomCount = new FilteredElementCollector(document)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .GetElementCount();
        var levelNames = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>()
            .Select(level => new { name = level.Name, elevationFeet = level.Elevation }).OrderBy(level => level.elevationFeet).ToList();
        var viewCount = new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>().Count(view => !view.IsTemplate);
        var sheetCount = new FilteredElementCollector(document).OfClass(typeof(ViewSheet)).GetElementCount();
        return new
        {
            totalInstances = total,
            roomCount,
            familyTypes = familyTypeCount,
            views = viewCount,
            sheets = sheetCount,
            levels = levelNames,
            categories = categoryCounts.OrderByDescending(pair => pair.Value).Take(40)
                .Select(pair => new { category = pair.Key, count = pair.Value }).ToList()
        };
    }

    private static object ListRooms(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        var levelFilter = JsonTools.GetString(args, "levelName");
        var limit = Math.Clamp(JsonTools.GetInt(args, "limit", 200), 1, 500);
        var offset = Math.Max(0, JsonTools.GetInt(args, "offset", 0));
        var rooms = new List<Room>();
        foreach (var room in new FilteredElementCollector(document)
                     .OfCategory(BuiltInCategory.OST_Rooms)
                     .WhereElementIsNotElementType()
                     .Cast<Room>())
        {
            var levelName = document.GetElement(room.LevelId)?.Name;
            if (levelFilter.Length > 0 && !string.Equals(levelName, levelFilter, StringComparison.OrdinalIgnoreCase)) continue;
            rooms.Add(room);
        }

        return new
        {
            matchingCount = rooms.Count,
            offset,
            returnedCount = Math.Min(Math.Max(0, rooms.Count - offset), limit),
            hasMore = offset + limit < rooms.Count,
            rooms = rooms.Skip(offset).Take(limit).Select(room => new
            {
                id = room.Id.Value,
                number = room.Number,
                name = room.Name,
                level = document.GetElement(room.LevelId)?.Name,
                areaSquareMeters = UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters),
                perimeterMillimeters = UnitUtils.ConvertFromInternalUnits(room.Perimeter, UnitTypeId.Millimeters),
                volumeCubicMeters = UnitUtils.ConvertFromInternalUnits(room.Volume, UnitTypeId.CubicMeters)
            }).ToList()
        };
    }

    private static object GetMaterialQuantities(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        var scope = JsonTools.GetString(args, "scope", "project");
        IEnumerable<Element> elements;
        if (scope == "selection")
        {
            var selectedIds = app.ActiveUIDocument!.Selection.GetElementIds();
            if (selectedIds.Count == 0) throw new InvalidOperationException("Select elements in Revit or use scope='project'.");
            elements = selectedIds.Select(document.GetElement).Where(element => element is not null).Cast<Element>();
        }
        else if (scope == "project")
        {
            elements = new FilteredElementCollector(document).WhereElementIsNotElementType();
        }
        else throw new ArgumentException("scope must be 'project' or 'selection'.");

        var totals = new Dictionary<long, MaterialRollup>();
        foreach (var element in elements)
        {
            ICollection<ElementId> materialIds;
            try { materialIds = element.GetMaterialIds(returnPaintMaterials: false); }
            catch { continue; }
            foreach (var materialId in materialIds)
            {
                if (document.GetElement(materialId) is not Material material) continue;
                var key = materialId.Value;
                if (!totals.TryGetValue(key, out var total))
                {
                    total = new MaterialRollup(material.Name, material.MaterialClass);
                    totals[key] = total;
                }
                try { total.AreaSquareMeters += UnitUtils.ConvertFromInternalUnits(element.GetMaterialArea(materialId, false), UnitTypeId.SquareMeters); }
                catch { }
                try { total.VolumeCubicMeters += UnitUtils.ConvertFromInternalUnits(element.GetMaterialVolume(materialId), UnitTypeId.CubicMeters); }
                catch { }
                total.ElementIds.Add(element.Id.Value);
            }
        }

        var rows = totals.OrderByDescending(pair => pair.Value.VolumeCubicMeters + pair.Value.AreaSquareMeters)
            .Select(pair => new
            {
                materialId = pair.Key,
                name = pair.Value.Name,
                materialClass = pair.Value.MaterialClass,
                areaSquareMeters = pair.Value.AreaSquareMeters,
                volumeCubicMeters = pair.Value.VolumeCubicMeters,
                elementCount = pair.Value.ElementIds.Count,
                elementIds = pair.Value.ElementIds.Order().Take(50).ToList()
            }).ToList();
        return new { scope, materialCount = rows.Count, materials = rows, note = "Окрашенные поверхности не включены; итог зависит от геометрии и данных материалов Revit." };
    }

    private sealed class MaterialRollup(string name, string materialClass)
    {
        public string Name { get; } = name;
        public string MaterialClass { get; } = materialClass;
        public double AreaSquareMeters { get; set; }
        public double VolumeCubicMeters { get; set; }
        public HashSet<long> ElementIds { get; } = [];
    }

    private static object ListFamilyTypes(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        var categoryFilter = JsonTools.GetString(args, "category");
        var limit = Math.Clamp(JsonTools.GetInt(args, "limit", 200), 1, 500);
        var matches = new List<FamilySymbol>();
        foreach (var symbol in new FilteredElementCollector(document).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
        {
            if (categoryFilter.Length > 0 && !string.Equals(symbol.Category?.Name, categoryFilter, StringComparison.OrdinalIgnoreCase)) continue;
            matches.Add(symbol);
        }
        return new
        {
            matchingCount = matches.Count,
            returnedCount = Math.Min(matches.Count, limit),
            types = matches.Take(limit).Select(symbol => new
            {
                id = symbol.Id.Value,
                category = symbol.Category?.Name,
                family = symbol.FamilyName,
                type = symbol.Name,
                placementType = symbol.Family.FamilyPlacementType.ToString(),
                active = symbol.IsActive
            }).ToList()
        };
    }

    private static object ListHostTypes(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        var hostCategory = JsonTools.GetString(args, "hostCategory");
        var limit = Math.Clamp(JsonTools.GetInt(args, "limit", 200), 1, 500);
        var collector = new FilteredElementCollector(document);
        IEnumerable<ElementType> types = hostCategory switch
        {
            "walls" => collector.OfClass(typeof(WallType)).Cast<ElementType>(),
            "floors" => collector.OfClass(typeof(FloorType)).Cast<ElementType>(),
            "ceilings" => collector.OfClass(typeof(CeilingType)).Cast<ElementType>(),
            _ => throw new ArgumentException("hostCategory must be walls, floors, or ceilings.")
        };
        var ordered = types.OrderBy(type => type.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new
        {
            hostCategory,
            matchingCount = ordered.Count,
            returnedCount = Math.Min(ordered.Count, limit),
            types = ordered.Take(limit).Select(type => new
            {
                id = type.Id.Value,
                name = type.Name,
                className = type.GetType().Name,
                category = type.Category?.Name
            }).ToList()
        };
    }

    private static object ListPlanViewTypes(UIApplication app)
    {
        var document = RequireDocument(app);
        var types = new FilteredElementCollector(document).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .Where(type => type.ViewFamily is ViewFamily.FloorPlan or ViewFamily.CeilingPlan or ViewFamily.StructuralPlan)
            .Select(type => new { id = type.Id.Value, typeName = type.Name, viewFamily = type.ViewFamily.ToString() })
            .OrderBy(type => type.viewFamily).ThenBy(type => type.typeName, StringComparer.OrdinalIgnoreCase).ToList();
        return new { count = types.Count, types };
    }

    private static object GetElementProperties(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        var ids = JsonTools.GetIds(args);
        if (ids.Count == 0) throw new ArgumentException("Provide at least one element id.");
        if (ids.Count > 100) throw new ArgumentException("A maximum of 100 elements can be requested at once.");
        var found = new List<object>();
        var missing = new List<long>();
        foreach (var id in ids)
        {
            var element = document.GetElement(new ElementId(id));
            if (element is null) missing.Add(id);
            else found.Add(Summarize(document, element, includeParameters: true));
        }
        return new { foundCount = found.Count, missingIds = missing, elements = found };
    }

    private static object SelectElements(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        var ids = JsonTools.GetIds(args);
        if (ids.Count == 0) throw new ArgumentException("Provide at least one element id.");
        if (ids.Count > 100) throw new ArgumentException("A maximum of 100 elements can be selected at once.");
        var validIds = ids.Select(id => new ElementId(id)).Where(id => document.GetElement(id) is not null).ToList();
        app.ActiveUIDocument!.Selection.SetElementIds(validIds);
        return new { selectedCount = validIds.Count, missingCount = ids.Count - validIds.Count, selectedIds = validIds.Select(id => id.Value).ToList() };
    }

    private static object PreviewSetParameter(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        var ids = ReadRequestedIds(args);
        var parameterName = JsonTools.GetString(args, "parameterName").Trim();
        var value = JsonTools.GetString(args, "value");
        if (parameterName.Length == 0) throw new ArgumentException("Parameter name cannot be empty.");

        PrunePlans();
        var originals = new Dictionary<long, string>();
        var rows = new List<object>();
        var skipped = new List<object>();
        foreach (var id in ids)
        {
            var element = document.GetElement(new ElementId(id));
            if (element is null) { skipped.Add(new { id, reason = "element not found" }); continue; }
            var parameter = element.LookupParameter(parameterName);
            if (parameter is null) { skipped.Add(new { id, reason = "parameter not found" }); continue; }
            if (parameter.IsReadOnly) { skipped.Add(new { id, reason = "parameter is read-only" }); continue; }
            if (parameter.StorageType == StorageType.ElementId) { skipped.Add(new { id, reason = "ElementId parameters are not supported by this MVP" }); continue; }
            if (parameter.StorageType == StorageType.Integer && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            { skipped.Add(new { id, reason = "value is not an integer" }); continue; }

            var oldValue = RawParameterValue(parameter);
            originals[id] = oldValue;
            rows.Add(new { id, element = element.Name, category = element.Category?.Name, parameter = parameterName, currentValue = DisplayParameterValue(parameter), proposedValue = value });
        }

        if (originals.Count == 0) return new { planCreated = false, changedCount = 0, skipped };
        var planId = Guid.NewGuid().ToString("N");
        Plans[planId] = new ParameterPlan
        {
            Id = planId,
            Kind = "parameter", OwnerDocument = document, DocumentRevision = DocumentRevision(document),
            ElementIds = originals.Keys.ToList(),
            ParameterName = parameterName,
            Value = value,
            OriginalValues = originals
        };
        return new
        {
            planCreated = true,
            planId,
            expiresInMinutes = (int)PlanLifetime.TotalMinutes,
            changedCount = originals.Count,
            value,
            note = "Строковые и целочисленные значения проверены. Дробное значение Revit разберёт по единицам проекта при подтверждении; ошибка отменит всю транзакцию.",
            changes = rows.Take(30).ToList(),
            omittedFromPreview = Math.Max(0, rows.Count - 30),
            skipped
        };
    }

    private static object ApplyParameterPlan(UIApplication app, JsonElement args, Func<bool>? isCancelled)
    {
        var document = RequireDocument(app);
        var plan = GetPlan(JsonTools.GetString(args, "planId"), "parameter");
        var preview = string.Join("\n", plan.ElementIds.Take(10).Select(id => $"Element {id}: {plan.OriginalValues.GetValueOrDefault(id)} → {plan.Value}"));
        if (plan.ElementIds.Count > 10) preview += $"\n… и ещё {plan.ElementIds.Count - 10} элементов";
        var dialog = new TaskDialog("WEAM.Revit.AI")
        {
            MainInstruction = $"Изменить параметр «{plan.ParameterName}» у {plan.ElementIds.Count} элементов?",
            MainContent = preview,
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No
        };
        if (!ConfirmOperation(dialog)) return new { applied = false, cancelled = true, changedCount = 0 };
        if (isCancelled?.Invoke() == true) return new { applied = false, cancelled = true, changedCount = 0, reason = "MCP client cancelled while confirmation was open." };

        using var transaction = new Transaction(document, "WEAM AI: изменение параметра");
        ValidateLegacyPlan(plan, document);
        BeginCheckedTransaction(transaction);
        try
        {
            foreach (var id in plan.ElementIds)
            {
                var element = document.GetElement(new ElementId(id)) ?? throw new InvalidOperationException($"Element {id} was deleted after the preview.");
                var parameter = element.LookupParameter(plan.ParameterName) ?? throw new InvalidOperationException($"Parameter '{plan.ParameterName}' no longer exists on element {id}.");
                if (parameter.IsReadOnly) throw new InvalidOperationException($"Parameter '{plan.ParameterName}' became read-only on element {id}.");
                if (!string.Equals(RawParameterValue(parameter), plan.OriginalValues[id], StringComparison.Ordinal))
                    throw new InvalidOperationException($"Element {id} changed after the preview. No values were applied; create a new preview.");
                SetParameter(parameter, plan.Value, id);
            }
            CommitCheckedTransaction(transaction, isCancelled);
            Plans.TryRemove(plan.Id, out _);
            return new { applied = true, changedCount = plan.ElementIds.Count, parameter = plan.ParameterName, value = plan.Value, elementIds = plan.ElementIds };
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            throw;
        }
    }

    private static object PreviewCreateLevels(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        if (!args.TryGetProperty("levels", out var levelArray) || levelArray.ValueKind != JsonValueKind.Array || levelArray.GetArrayLength() is < 1 or > 30)
            throw new ArgumentException("Provide between 1 and 30 levels.");

        var proposed = new List<LevelPlanItem>();
        foreach (var item in levelArray.EnumerateArray())
        {
            var name = JsonTools.GetString(item, "name").Trim();
            if (name.Length == 0 || !item.TryGetProperty("elevationMeters", out var elevationElement) || !elevationElement.TryGetDouble(out var elevation) || !double.IsFinite(elevation))
                throw new ArgumentException("Each level needs a name and finite elevationMeters value.");
            proposed.Add(new LevelPlanItem(name, elevation));
        }

        var duplicateNames = proposed.GroupBy(level => level.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (duplicateNames.Count > 0) throw new ArgumentException($"Duplicate names in the request: {string.Join(", ", duplicateNames)}.");
        var existingNames = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>().Select(level => level.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflicts = proposed.Where(level => existingNames.Contains(level.Name)).Select(level => level.Name).ToList();
        if (conflicts.Count > 0) throw new ArgumentException($"These level names already exist: {string.Join(", ", conflicts)}.");

        var planId = Guid.NewGuid().ToString("N");
        Plans[planId] = new ParameterPlan { Id = planId, Kind = "levels", OwnerDocument = document, DocumentRevision = DocumentRevision(document), Levels = proposed };
        return new
        {
            planCreated = true,
            planId,
            expiresInMinutes = (int)PlanLifetime.TotalMinutes,
            levels = proposed.Select(level => new { name = level.Name, elevationMeters = level.ElevationMeters }).ToList()
        };
    }

    private static object ApplyCreateLevels(UIApplication app, JsonElement args, Func<bool>? isCancelled)
    {
        var document = RequireDocument(app);
        var plan = GetPlan(JsonTools.GetString(args, "planId"), "levels");
        var list = string.Join("\n", plan.Levels.Select(level => $"{level.Name}: {level.ElevationMeters.ToString("0.###", CultureInfo.InvariantCulture)} м"));
        var dialog = new TaskDialog("WEAM.Revit.AI")
        {
            MainInstruction = $"Создать {plan.Levels.Count} уровней?",
            MainContent = list,
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No
        };
        if (!ConfirmOperation(dialog)) return new { applied = false, cancelled = true, createdCount = 0 };
        if (isCancelled?.Invoke() == true) return new { applied = false, cancelled = true, createdCount = 0, reason = "MCP client cancelled while confirmation was open." };

        using var transaction = new Transaction(document, "WEAM AI: создание уровней");
        ValidateLegacyPlan(plan, document);
        BeginCheckedTransaction(transaction);
        try
        {
            var created = new List<object>();
            foreach (var item in plan.Levels)
            {
                var elevationFeet = UnitUtils.ConvertToInternalUnits(item.ElevationMeters, UnitTypeId.Meters);
                var level = Level.Create(document, elevationFeet);
                level.Name = item.Name;
                created.Add(new { id = level.Id.Value, name = level.Name, elevationMeters = item.ElevationMeters });
            }
            CommitCheckedTransaction(transaction, isCancelled);
            Plans.TryRemove(plan.Id, out _);
            return new { applied = true, createdCount = created.Count, levels = created };
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            throw;
        }
    }

    private static object PreviewCreateWalls(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        if (!args.TryGetProperty("walls", out var wallArray) || wallArray.ValueKind != JsonValueKind.Array || wallArray.GetArrayLength() is < 1 or > 50)
            throw new ArgumentException("Provide between 1 and 50 walls.");

        var levels = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>().ToList();
        var wallTypes = new FilteredElementCollector(document).OfClass(typeof(WallType)).Cast<WallType>().ToList();
        var proposed = new List<WallPlanItem>();
        foreach (var item in wallArray.EnumerateArray())
        {
            var baseLevelName = JsonTools.GetString(item, "baseLevelName").Trim();
            var wallTypeName = JsonTools.GetString(item, "wallTypeName").Trim();
            if (baseLevelName.Length == 0 || wallTypeName.Length == 0)
                throw new ArgumentException("Each wall needs baseLevelName and wallTypeName.");

            var matchingLevels = levels.Where(level => string.Equals(level.Name, baseLevelName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchingLevels.Count != 1) throw new ArgumentException(matchingLevels.Count == 0
                ? $"Base level '{baseLevelName}' was not found."
                : $"Base level name '{baseLevelName}' is ambiguous.");
            var matchingTypes = wallTypes.Where(type => string.Equals(type.Name, wallTypeName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchingTypes.Count != 1) throw new ArgumentException(matchingTypes.Count == 0
                ? $"Wall type '{wallTypeName}' was not found."
                : $"Wall type name '{wallTypeName}' is ambiguous.");

            var start = ReadWallPoint(item, "start");
            var end = ReadWallPoint(item, "end");
            var length = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
            if (length < 0.01) throw new ArgumentException("Wall endpoints must be at least 0.01 m apart.");
            if (!TryGetFiniteNumber(item, "heightMeters", out var heightMeters) || heightMeters is < 0.1 or > 100)
                throw new ArgumentException("Each wall heightMeters must be between 0.1 and 100.");
            var offsetMeters = TryGetFiniteNumber(item, "baseOffsetMeters", out var parsedOffset) ? parsedOffset : 0;
            if (offsetMeters is < -100 or > 100) throw new ArgumentException("baseOffsetMeters must be between -100 and 100.");

            var flip = item.TryGetProperty("flip", out var flipElement) && flipElement.ValueKind == JsonValueKind.True;
            var structural = item.TryGetProperty("structural", out var structuralElement) && structuralElement.ValueKind == JsonValueKind.True;
            proposed.Add(new WallPlanItem(
                start.X, start.Y, end.X, end.Y,
                matchingLevels[0].Name, matchingLevels[0].Id.Value,
                matchingTypes[0].Name, matchingTypes[0].Id.Value,
                heightMeters, offsetMeters, flip, structural));
        }

        var planId = Guid.NewGuid().ToString("N");
        Plans[planId] = new ParameterPlan
        {
            Id = planId,
            Kind = "walls",
            Walls = proposed,
            OwnerDocument = document, DocumentRevision = DocumentRevision(document)
        };
        return new
        {
            planCreated = true,
            planId,
            expiresInMinutes = (int)PlanLifetime.TotalMinutes,
            wallCount = proposed.Count,
            units = "meters",
            walls = proposed.Select((wall, index) => new
            {
                index = index + 1,
                wall.BaseLevelName,
                wall.WallTypeName,
                start = new { x = wall.StartXMeters, y = wall.StartYMeters },
                end = new { x = wall.EndXMeters, y = wall.EndYMeters },
                wall.HeightMeters,
                wall.BaseOffsetMeters,
                wall.Flip,
                wall.Structural
            }).ToList()
        };
    }

    private static object ApplyCreateWalls(UIApplication app, JsonElement args, Func<bool>? isCancelled)
    {
        var document = RequireDocument(app);
        var plan = GetPlan(JsonTools.GetString(args, "planId"), "walls");
        if (!(plan.OwnerDocument?.IsValidObject == true && plan.OwnerDocument.Equals(document)))
            throw new InvalidOperationException("The wall plan belongs to a different Revit document. Prepare a new preview in the active document.");

        var preview = string.Join("\n", plan.Walls.Take(10).Select((wall, index) =>
            $"{index + 1}. {wall.WallTypeName}, {wall.BaseLevelName}: ({wall.StartXMeters:0.###}, {wall.StartYMeters:0.###}) → ({wall.EndXMeters:0.###}, {wall.EndYMeters:0.###}) м; H={wall.HeightMeters:0.###} м"));
        if (plan.Walls.Count > 10) preview += $"\n… и ещё {plan.Walls.Count - 10} стен";
        var dialog = new TaskDialog("WEAM.Revit.AI")
        {
            MainInstruction = $"Создать {plan.Walls.Count} стен?",
            MainContent = preview,
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No
        };
        if (!ConfirmOperation(dialog)) return new { applied = false, cancelled = true, createdCount = 0 };
        if (isCancelled?.Invoke() == true) return new { applied = false, cancelled = true, createdCount = 0, reason = "MCP client cancelled while confirmation was open." };

        using var transaction = new Transaction(document, "WEAM AI: создание стен");
        ValidateLegacyPlan(plan, document);
        BeginCheckedTransaction(transaction);
        try
        {
            var created = new List<object>();
            foreach (var wallPlan in plan.Walls)
            {
                var level = document.GetElement(new ElementId(wallPlan.BaseLevelId)) as Level
                    ?? throw new InvalidOperationException($"Base level '{wallPlan.BaseLevelName}' was deleted after the preview.");
                var wallType = document.GetElement(new ElementId(wallPlan.WallTypeId)) as WallType
                    ?? throw new InvalidOperationException($"Wall type '{wallPlan.WallTypeName}' was deleted after the preview.");
                var start = new XYZ(
                    UnitUtils.ConvertToInternalUnits(wallPlan.StartXMeters, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(wallPlan.StartYMeters, UnitTypeId.Meters),
                    0);
                var end = new XYZ(
                    UnitUtils.ConvertToInternalUnits(wallPlan.EndXMeters, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(wallPlan.EndYMeters, UnitTypeId.Meters),
                    0);
                var line = Line.CreateBound(start, end);
                var wall = Wall.Create(
                    document,
                    line,
                    wallType.Id,
                    level.Id,
                    UnitUtils.ConvertToInternalUnits(wallPlan.HeightMeters, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(wallPlan.BaseOffsetMeters, UnitTypeId.Meters),
                    wallPlan.Flip,
                    wallPlan.Structural);
                created.Add(new { id = wall.Id.Value, wallType = wallType.Name, baseLevel = level.Name, heightMeters = wallPlan.HeightMeters });
            }
            CommitCheckedTransaction(transaction, isCancelled);
            Plans.TryRemove(plan.Id, out _);
            return new { applied = true, createdCount = created.Count, walls = created };
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            throw;
        }
    }

    private static (double X, double Y) ReadWallPoint(JsonElement wall, string propertyName)
    {
        if (!wall.TryGetProperty(propertyName, out var point) || point.ValueKind != JsonValueKind.Object
            || !TryGetFiniteNumber(point, "xMeters", out var x) || !TryGetFiniteNumber(point, "yMeters", out var y)
            || Math.Abs(x) > 100000 || Math.Abs(y) > 100000)
            throw new ArgumentException($"Each {propertyName} point must contain finite xMeters/yMeters coordinates within ±100000 m.");
        return (x, y);
    }

    private static bool TryGetFiniteNumber(JsonElement element, string propertyName, out double number)
    {
        number = 0;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.TryGetDouble(out number)
            && double.IsFinite(number);
    }

    private static object PreviewPlaceFamilyInstances(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        if (!args.TryGetProperty("instances", out var instanceArray) || instanceArray.ValueKind != JsonValueKind.Array || instanceArray.GetArrayLength() is < 1 or > 100)
            throw new ArgumentException("Provide between 1 and 100 family instances.");

        var levels = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>().ToList();
        var symbols = new FilteredElementCollector(document).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
        var proposed = new List<FamilyInstancePlanItem>();
        foreach (var item in instanceArray.EnumerateArray())
        {
            var familyName = JsonTools.GetString(item, "familyName").Trim();
            var typeName = JsonTools.GetString(item, "typeName").Trim();
            var levelName = JsonTools.GetString(item, "levelName").Trim();
            if (familyName.Length == 0 || typeName.Length == 0 || levelName.Length == 0)
                throw new ArgumentException("Each instance needs familyName, typeName and levelName.");

            var matchingSymbols = symbols.Where(symbol =>
                string.Equals(symbol.FamilyName, familyName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(symbol.Name, typeName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchingSymbols.Count != 1) throw new ArgumentException(matchingSymbols.Count == 0
                ? $"Family type '{familyName}: {typeName}' was not found. Use list_family_types to inspect loaded types."
                : $"Family type '{familyName}: {typeName}' is ambiguous.");
            var symbol = matchingSymbols[0];
            if (symbol.Family.FamilyPlacementType != FamilyPlacementType.OneLevelBased)
                throw new ArgumentException($"Family '{familyName}: {typeName}' is {symbol.Family.FamilyPlacementType}; this operation supports only unhosted one-level-based families.");

            var matchingLevels = levels.Where(level => string.Equals(level.Name, levelName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchingLevels.Count != 1) throw new ArgumentException(matchingLevels.Count == 0
                ? $"Level '{levelName}' was not found."
                : $"Level name '{levelName}' is ambiguous.");

            var position = ReadWallPoint(item, "position");
            var offsetMeters = TryGetFiniteNumber(item, "offsetMeters", out var parsedOffset) ? parsedOffset : 0;
            if (offsetMeters is < -100 or > 100) throw new ArgumentException("offsetMeters must be between -100 and 100.");
            var rotationDegrees = TryGetFiniteNumber(item, "rotationDegrees", out var parsedRotation) ? parsedRotation : 0;
            if (Math.Abs(rotationDegrees) > 36000) throw new ArgumentException("rotationDegrees must be between -36000 and 36000.");
            var structuralTypeName = JsonTools.GetString(item, "structuralType", "nonStructural");
            var structuralType = structuralTypeName.ToLowerInvariant() switch
            {
                "nonstructural" => StructuralType.NonStructural,
                "beam" => StructuralType.Beam,
                "brace" => StructuralType.Brace,
                "column" => StructuralType.Column,
                "footing" => StructuralType.Footing,
                _ => throw new ArgumentException("structuralType must be nonStructural, beam, brace, column or footing.")
            };
            proposed.Add(new FamilyInstancePlanItem(
                symbol.Id.Value, symbol.FamilyName, symbol.Name,
                matchingLevels[0].Id.Value, matchingLevels[0].Name,
                position.X, position.Y, offsetMeters, rotationDegrees, structuralType));
        }

        var planId = Guid.NewGuid().ToString("N");
        Plans[planId] = new ParameterPlan
        {
            Id = planId,
            Kind = "familyInstances",
            FamilyInstances = proposed,
            OwnerDocument = document, DocumentRevision = DocumentRevision(document)
        };
        return new
        {
            planCreated = true,
            planId,
            expiresInMinutes = (int)PlanLifetime.TotalMinutes,
            instanceCount = proposed.Count,
            units = "meters",
            instances = proposed.Select((instance, index) => new
            {
                index = index + 1,
                instance.FamilyName,
                instance.TypeName,
                instance.LevelName,
                position = new { x = instance.XMeters, y = instance.YMeters },
                instance.OffsetMeters,
                instance.RotationDegrees,
                structuralType = instance.StructuralType.ToString()
            }).ToList()
        };
    }

    private static object ApplyPlaceFamilyInstances(UIApplication app, JsonElement args, Func<bool>? isCancelled)
    {
        var document = RequireDocument(app);
        var plan = GetPlan(JsonTools.GetString(args, "planId"), "familyInstances");
        if (!(plan.OwnerDocument?.IsValidObject == true && plan.OwnerDocument.Equals(document)))
            throw new InvalidOperationException("The family placement plan belongs to a different Revit document. Prepare a new preview in the active document.");

        var preview = string.Join("\n", plan.FamilyInstances.Take(10).Select((instance, index) =>
            $"{index + 1}. {instance.FamilyName}: {instance.TypeName}, {instance.LevelName}: ({instance.XMeters:0.###}, {instance.YMeters:0.###}, {instance.OffsetMeters:0.###}) м; поворот {instance.RotationDegrees:0.###}°"));
        if (plan.FamilyInstances.Count > 10) preview += $"\n… и ещё {plan.FamilyInstances.Count - 10} экземпляров";
        var dialog = new TaskDialog("WEAM.Revit.AI")
        {
            MainInstruction = $"Разместить {plan.FamilyInstances.Count} экземпляров семейств?",
            MainContent = preview,
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No
        };
        if (!ConfirmOperation(dialog)) return new { applied = false, cancelled = true, createdCount = 0 };
        if (isCancelled?.Invoke() == true) return new { applied = false, cancelled = true, createdCount = 0, reason = "MCP client cancelled while confirmation was open." };

        using var transaction = new Transaction(document, "WEAM AI: размещение семейств");
        ValidateLegacyPlan(plan, document);
        BeginCheckedTransaction(transaction);
        try
        {
            var created = new List<object>();
            foreach (var instancePlan in plan.FamilyInstances)
            {
                var level = document.GetElement(new ElementId(instancePlan.LevelId)) as Level
                    ?? throw new InvalidOperationException($"Level '{instancePlan.LevelName}' was deleted after the preview.");
                var symbol = document.GetElement(new ElementId(instancePlan.FamilySymbolId)) as FamilySymbol
                    ?? throw new InvalidOperationException($"Family type '{instancePlan.FamilyName}: {instancePlan.TypeName}' was deleted after the preview.");
                if (symbol.Family.FamilyPlacementType != FamilyPlacementType.OneLevelBased)
                    throw new InvalidOperationException($"Family type '{instancePlan.FamilyName}: {instancePlan.TypeName}' changed placement type after the preview.");
                if (!symbol.IsActive) symbol.Activate();
                var location = new XYZ(
                    UnitUtils.ConvertToInternalUnits(instancePlan.XMeters, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(instancePlan.YMeters, UnitTypeId.Meters),
                    level.Elevation + UnitUtils.ConvertToInternalUnits(instancePlan.OffsetMeters, UnitTypeId.Meters));
                var instance = document.Create.NewFamilyInstance(location, symbol, level, instancePlan.StructuralType);
                if (Math.Abs(instancePlan.RotationDegrees) > 1e-9)
                {
                    var axis = Line.CreateBound(location, location + XYZ.BasisZ);
                    ElementTransformUtils.RotateElement(document, instance.Id, axis, instancePlan.RotationDegrees * Math.PI / 180.0);
                }
                created.Add(new { id = instance.Id.Value, family = symbol.FamilyName, type = symbol.Name, level = level.Name });
            }
            CommitCheckedTransaction(transaction, isCancelled);
            Plans.TryRemove(plan.Id, out _);
            return new { applied = true, createdCount = created.Count, instances = created };
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            throw;
        }
    }

    private static object PreviewCreateFloors(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        if (!args.TryGetProperty("floors", out var floorArray) || floorArray.ValueKind != JsonValueKind.Array || floorArray.GetArrayLength() is < 1 or > 10)
            throw new ArgumentException("Provide between 1 and 10 floors.");

        var levels = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>().ToList();
        var floorTypes = new FilteredElementCollector(document).OfClass(typeof(FloorType)).Cast<FloorType>().ToList();
        var proposed = new List<FloorPlanItem>();
        foreach (var item in floorArray.EnumerateArray())
        {
            var levelName = JsonTools.GetString(item, "levelName").Trim();
            var floorTypeName = JsonTools.GetString(item, "floorTypeName").Trim();
            if (levelName.Length == 0 || floorTypeName.Length == 0)
                throw new ArgumentException("Each floor needs levelName and floorTypeName.");

            var matchingLevels = levels.Where(level => string.Equals(level.Name, levelName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchingLevels.Count != 1) throw new ArgumentException(matchingLevels.Count == 0
                ? $"Level '{levelName}' was not found."
                : $"Level name '{levelName}' is ambiguous.");
            var matchingTypes = floorTypes.Where(type => string.Equals(type.Name, floorTypeName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchingTypes.Count != 1) throw new ArgumentException(matchingTypes.Count == 0
                ? $"Floor type '{floorTypeName}' was not found."
                : $"Floor type name '{floorTypeName}' is ambiguous.");

            var (vertices, area) = ReadPolygon(item, "floor");

            var heightOffset = TryGetFiniteNumber(item, "heightOffsetMeters", out var parsedOffset) ? parsedOffset : 0;
            if (heightOffset is < -100 or > 100) throw new ArgumentException("heightOffsetMeters must be between -100 and 100.");
            var structural = item.TryGetProperty("structural", out var structuralElement) && structuralElement.ValueKind == JsonValueKind.True;
            proposed.Add(new FloorPlanItem(
                matchingTypes[0].Id.Value, matchingTypes[0].Name,
                matchingLevels[0].Id.Value, matchingLevels[0].Name,
                vertices, area, heightOffset, structural));
        }

        var planId = Guid.NewGuid().ToString("N");
        Plans[planId] = new ParameterPlan
        {
            Id = planId,
            Kind = "floors",
            Floors = proposed,
            OwnerDocument = document, DocumentRevision = DocumentRevision(document)
        };
        return new
        {
            planCreated = true,
            planId,
            expiresInMinutes = (int)PlanLifetime.TotalMinutes,
            floorCount = proposed.Count,
            units = "meters",
            floors = proposed.Select((floor, index) => new
            {
                index = index + 1,
                floor.FloorTypeName,
                floor.LevelName,
                floor.AreaSquareMeters,
                floor.HeightOffsetMeters,
                floor.Structural,
                vertexCount = floor.Vertices.Count,
                vertices = floor.Vertices.Select(point => new { x = point.X, y = point.Y }).ToList()
            }).ToList()
        };
    }

    private static object ApplyCreateFloors(UIApplication app, JsonElement args, Func<bool>? isCancelled)
    {
        var document = RequireDocument(app);
        var plan = GetPlan(JsonTools.GetString(args, "planId"), "floors");
        if (!(plan.OwnerDocument?.IsValidObject == true && plan.OwnerDocument.Equals(document)))
            throw new InvalidOperationException("The floor plan belongs to a different Revit document. Prepare a new preview in the active document.");

        var preview = string.Join("\n", plan.Floors.Select((floor, index) =>
            $"{index + 1}. {floor.FloorTypeName}, {floor.LevelName}: {floor.AreaSquareMeters:0.###} м², вершин {floor.Vertices.Count}, смещение {floor.HeightOffsetMeters:0.###} м{(floor.Structural ? ", конструктивное" : "")}"));
        var dialog = new TaskDialog("WEAM.Revit.AI")
        {
            MainInstruction = $"Создать {plan.Floors.Count} перекрытий?",
            MainContent = preview,
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No
        };
        if (!ConfirmOperation(dialog)) return new { applied = false, cancelled = true, createdCount = 0 };
        if (isCancelled?.Invoke() == true) return new { applied = false, cancelled = true, createdCount = 0, reason = "MCP client cancelled while confirmation was open." };

        using var transaction = new Transaction(document, "WEAM AI: создание перекрытий");
        ValidateLegacyPlan(plan, document);
        BeginCheckedTransaction(transaction);
        try
        {
            var created = new List<object>();
            foreach (var floorPlan in plan.Floors)
            {
                var level = document.GetElement(new ElementId(floorPlan.LevelId)) as Level
                    ?? throw new InvalidOperationException($"Level '{floorPlan.LevelName}' was deleted after the preview.");
                var floorType = document.GetElement(new ElementId(floorPlan.FloorTypeId)) as FloorType
                    ?? throw new InvalidOperationException($"Floor type '{floorPlan.FloorTypeName}' was deleted after the preview.");
                var loop = new CurveLoop();
                for (var index = 0; index < floorPlan.Vertices.Count; index++)
                {
                    var startPoint = floorPlan.Vertices[index];
                    var endPoint = floorPlan.Vertices[(index + 1) % floorPlan.Vertices.Count];
                    var start = new XYZ(
                        UnitUtils.ConvertToInternalUnits(startPoint.X, UnitTypeId.Meters),
                        UnitUtils.ConvertToInternalUnits(startPoint.Y, UnitTypeId.Meters),
                        level.Elevation);
                    var end = new XYZ(
                        UnitUtils.ConvertToInternalUnits(endPoint.X, UnitTypeId.Meters),
                        UnitUtils.ConvertToInternalUnits(endPoint.Y, UnitTypeId.Meters),
                        level.Elevation);
                    loop.Append(Line.CreateBound(start, end));
                }
                var floor = Floor.Create(document, new List<CurveLoop> { loop }, floorType.Id, level.Id, floorPlan.Structural, null, 0);
                if (Math.Abs(floorPlan.HeightOffsetMeters) > 1e-9)
                {
                    var offset = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)
                        ?? throw new InvalidOperationException($"Could not set the level offset on floor type '{floorType.Name}'.");
                    if (offset.IsReadOnly || !offset.Set(UnitUtils.ConvertToInternalUnits(floorPlan.HeightOffsetMeters, UnitTypeId.Meters)))
                        throw new InvalidOperationException($"Revit rejected the height offset on floor type '{floorType.Name}'. The transaction was rolled back.");
                }
                created.Add(new { id = floor.Id.Value, floorType = floorType.Name, level = level.Name, floorPlan.AreaSquareMeters, floorPlan.HeightOffsetMeters, floorPlan.Structural });
            }
            CommitCheckedTransaction(transaction, isCancelled);
            Plans.TryRemove(plan.Id, out _);
            return new { applied = true, createdCount = created.Count, floors = created };
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            throw;
        }
    }

    private static (List<PlanPoint2D> Vertices, double AreaSquareMeters) ReadPolygon(JsonElement item, string elementName)
    {
        if (!item.TryGetProperty("vertices", out var vertexArray) || vertexArray.ValueKind != JsonValueKind.Array || vertexArray.GetArrayLength() is < 3 or > 100)
            throw new ArgumentException($"Each {elementName} needs between 3 and 100 vertices.");
        var vertices = vertexArray.EnumerateArray().Select(ReadPoint2D).ToList();
        if (vertices.Count > 3 && DistanceSquared(vertices[0], vertices[^1]) < 1e-12) vertices.RemoveAt(vertices.Count - 1);
        if (vertices.Count < 3) throw new ArgumentException($"Each {elementName} profile needs at least 3 distinct vertices.");
        for (var index = 0; index < vertices.Count; index++)
        {
            if (DistanceSquared(vertices[index], vertices[(index + 1) % vertices.Count]) < 1e-8)
                throw new ArgumentException($"{elementName} profile edges must be at least 0.0001 m long and may not contain duplicate adjacent vertices.");
        }
        if (HasOverlappingAdjacentEdges(vertices)) throw new ArgumentException($"{elementName} profile may not double back over an adjacent edge.");
        if (HasSelfIntersection(vertices)) throw new ArgumentException($"{elementName} profile must be a simple polygon without crossing or touching non-adjacent edges.");
        var area = Math.Abs(PolygonSignedArea(vertices));
        if (area < 0.01) throw new ArgumentException($"{elementName} profile area must be at least 0.01 m².");
        return (vertices, area);
    }

    private static PlanPoint2D ReadPoint2D(JsonElement point)
    {
        if (!TryGetFiniteNumber(point, "xMeters", out var x) || !TryGetFiniteNumber(point, "yMeters", out var y)
            || Math.Abs(x) > 100000 || Math.Abs(y) > 100000)
            throw new ArgumentException("Each floor vertex must contain finite xMeters/yMeters coordinates within ±100000 m.");
        return new PlanPoint2D(x, y);
    }

    private static double DistanceSquared(PlanPoint2D first, PlanPoint2D second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return dx * dx + dy * dy;
    }

    private static double PolygonSignedArea(IReadOnlyList<PlanPoint2D> vertices)
    {
        var twiceArea = 0.0;
        for (var index = 0; index < vertices.Count; index++)
        {
            var current = vertices[index];
            var next = vertices[(index + 1) % vertices.Count];
            twiceArea += current.X * next.Y - next.X * current.Y;
        }
        return twiceArea / 2.0;
    }

    private static bool HasSelfIntersection(IReadOnlyList<PlanPoint2D> vertices)
    {
        for (var firstIndex = 0; firstIndex < vertices.Count; firstIndex++)
        {
            var firstNext = (firstIndex + 1) % vertices.Count;
            for (var secondIndex = firstIndex + 1; secondIndex < vertices.Count; secondIndex++)
            {
                var secondNext = (secondIndex + 1) % vertices.Count;
                if (firstIndex == secondIndex || firstNext == secondIndex || secondNext == firstIndex) continue;
                if (SegmentsIntersect(vertices[firstIndex], vertices[firstNext], vertices[secondIndex], vertices[secondNext])) return true;
            }
        }
        return false;
    }

    private static bool HasOverlappingAdjacentEdges(IReadOnlyList<PlanPoint2D> vertices)
    {
        const double epsilon = 1e-9;
        for (var index = 0; index < vertices.Count; index++)
        {
            var previous = vertices[(index + vertices.Count - 1) % vertices.Count];
            var current = vertices[index];
            var next = vertices[(index + 1) % vertices.Count];
            if (Math.Abs(Cross(previous, current, next)) > epsilon) continue;
            var incomingX = previous.X - current.X;
            var incomingY = previous.Y - current.Y;
            var outgoingX = next.X - current.X;
            var outgoingY = next.Y - current.Y;
            if (incomingX * outgoingX + incomingY * outgoingY > epsilon) return true;
        }
        return false;
    }

    private static bool SegmentsIntersect(PlanPoint2D a, PlanPoint2D b, PlanPoint2D c, PlanPoint2D d)
    {
        const double epsilon = 1e-9;
        var abc = Cross(a, b, c);
        var abd = Cross(a, b, d);
        var cda = Cross(c, d, a);
        var cdb = Cross(c, d, b);
        if (((abc > epsilon && abd < -epsilon) || (abc < -epsilon && abd > epsilon))
            && ((cda > epsilon && cdb < -epsilon) || (cda < -epsilon && cdb > epsilon))) return true;
        return Math.Abs(abc) <= epsilon && IsOnSegment(a, b, c, epsilon)
            || Math.Abs(abd) <= epsilon && IsOnSegment(a, b, d, epsilon)
            || Math.Abs(cda) <= epsilon && IsOnSegment(c, d, a, epsilon)
            || Math.Abs(cdb) <= epsilon && IsOnSegment(c, d, b, epsilon);
    }

    private static double Cross(PlanPoint2D a, PlanPoint2D b, PlanPoint2D c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool IsOnSegment(PlanPoint2D a, PlanPoint2D b, PlanPoint2D point, double epsilon) =>
        point.X >= Math.Min(a.X, b.X) - epsilon && point.X <= Math.Max(a.X, b.X) + epsilon
        && point.Y >= Math.Min(a.Y, b.Y) - epsilon && point.Y <= Math.Max(a.Y, b.Y) + epsilon;

    private static object PreviewCreateGrids(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        if (!args.TryGetProperty("grids", out var gridArray) || gridArray.ValueKind != JsonValueKind.Array || gridArray.GetArrayLength() is < 1 or > 50)
            throw new ArgumentException("Provide between 1 and 50 grids.");

        var proposed = new List<GridPlanItem>();
        foreach (var item in gridArray.EnumerateArray())
        {
            var name = JsonTools.GetString(item, "name").Trim();
            if (name.Length == 0) throw new ArgumentException("Each grid needs a name.");
            var start = ReadWallPoint(item, "start");
            var end = ReadWallPoint(item, "end");
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < 0.01) throw new ArgumentException($"Grid '{name}' endpoints must be at least 0.01 m apart.");
            var elevationMeters = TryGetFiniteNumber(item, "elevationMeters", out var parsedElevation) ? parsedElevation : 0;
            if (elevationMeters is < -1000 or > 10000) throw new ArgumentException("elevationMeters must be between -1000 and 10000.");
            proposed.Add(new GridPlanItem(name, start.X, start.Y, end.X, end.Y, elevationMeters));
        }

        var duplicateNames = proposed.GroupBy(grid => grid.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (duplicateNames.Count > 0) throw new ArgumentException($"Duplicate grid names in the request: {string.Join(", ", duplicateNames)}.");
        var existingNames = new FilteredElementCollector(document).OfClass(typeof(Grid)).Cast<Grid>().Select(grid => grid.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflicts = proposed.Where(grid => existingNames.Contains(grid.Name)).Select(grid => grid.Name).ToList();
        if (conflicts.Count > 0) throw new ArgumentException($"These grid names already exist: {string.Join(", ", conflicts)}.");

        var planId = Guid.NewGuid().ToString("N");
        Plans[planId] = new ParameterPlan { Id = planId, Kind = "grids", Grids = proposed, OwnerDocument = document, DocumentRevision = DocumentRevision(document) };
        return new
        {
            planCreated = true,
            planId,
            expiresInMinutes = (int)PlanLifetime.TotalMinutes,
            gridCount = proposed.Count,
            units = "meters",
            grids = proposed.Select(grid => new
            {
                grid.Name,
                start = new { x = grid.StartXMeters, y = grid.StartYMeters },
                end = new { x = grid.EndXMeters, y = grid.EndYMeters },
                grid.ElevationMeters
            }).ToList()
        };
    }

    private static object ApplyCreateGrids(UIApplication app, JsonElement args, Func<bool>? isCancelled)
    {
        var document = RequireDocument(app);
        var plan = GetPlan(JsonTools.GetString(args, "planId"), "grids");
        if (!(plan.OwnerDocument?.IsValidObject == true && plan.OwnerDocument.Equals(document)))
            throw new InvalidOperationException("The grid plan belongs to a different Revit document. Prepare a new preview in the active document.");

        var preview = string.Join("\n", plan.Grids.Select(grid =>
            $"{grid.Name}: ({grid.StartXMeters:0.###}, {grid.StartYMeters:0.###}) → ({grid.EndXMeters:0.###}, {grid.EndYMeters:0.###}) м, отметка {grid.ElevationMeters:0.###} м"));
        var dialog = new TaskDialog("WEAM.Revit.AI")
        {
            MainInstruction = $"Создать {plan.Grids.Count} координационных осей?",
            MainContent = preview,
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No
        };
        if (!ConfirmOperation(dialog)) return new { applied = false, cancelled = true, createdCount = 0 };
        if (isCancelled?.Invoke() == true) return new { applied = false, cancelled = true, createdCount = 0, reason = "MCP client cancelled while confirmation was open." };

        using var transaction = new Transaction(document, "WEAM AI: создание координационных осей");
        ValidateLegacyPlan(plan, document);
        BeginCheckedTransaction(transaction);
        try
        {
            var created = new List<object>();
            foreach (var gridPlan in plan.Grids)
            {
                var start = new XYZ(
                    UnitUtils.ConvertToInternalUnits(gridPlan.StartXMeters, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(gridPlan.StartYMeters, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(gridPlan.ElevationMeters, UnitTypeId.Meters));
                var end = new XYZ(
                    UnitUtils.ConvertToInternalUnits(gridPlan.EndXMeters, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(gridPlan.EndYMeters, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(gridPlan.ElevationMeters, UnitTypeId.Meters));
                var grid = Grid.Create(document, Line.CreateBound(start, end));
                grid.Name = gridPlan.Name;
                created.Add(new { id = grid.Id.Value, name = grid.Name, elevationMeters = gridPlan.ElevationMeters });
            }
            CommitCheckedTransaction(transaction, isCancelled);
            Plans.TryRemove(plan.Id, out _);
            return new { applied = true, createdCount = created.Count, grids = created };
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            throw;
        }
    }

    private static object PreviewSetCategoryVisibility(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        var view = app.ActiveUIDocument!.ActiveView;
        if (view.IsTemplate) throw new InvalidOperationException("The active view is a view template; edit a regular view instead.");
        var categoryName = JsonTools.GetString(args, "categoryName").Trim();
        if (categoryName.Length == 0) throw new ArgumentException("categoryName cannot be empty.");
        if (!args.TryGetProperty("hidden", out var hiddenValue) || hiddenValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("hidden must be true or false.");
        var desiredHidden = hiddenValue.GetBoolean();
        var matches = document.Settings.Categories.Cast<Category>()
            .Where(category => string.Equals(category.Name, categoryName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1) throw new ArgumentException(matches.Count == 0
            ? $"Category '{categoryName}' was not found."
            : $"Category name '{categoryName}' is ambiguous; use the exact Revit category name that appears once in this project.");
        var category = matches[0];
        if (!view.CanCategoryBeHidden(category.Id)) throw new InvalidOperationException($"Category '{category.Name}' cannot be hidden or shown in view '{view.Name}'.");
        var currentHidden = view.GetCategoryHidden(category.Id);
        if (currentHidden == desiredHidden) return new { planCreated = false, changed = false, viewName = view.Name, categoryName = category.Name, hidden = currentHidden, reason = "Category already has the requested visibility." };

        var planId = Guid.NewGuid().ToString("N");
        Plans[planId] = new ParameterPlan
        {
            Id = planId,
            Kind = "visibility",
            Visibility = new ViewVisibilityPlanItem(view.Id.Value, view.Name, category.Id.Value, category.Name, currentHidden, desiredHidden),
            OwnerDocument = document, DocumentRevision = DocumentRevision(document)
        };
        return new
        {
            planCreated = true,
            planId,
            expiresInMinutes = (int)PlanLifetime.TotalMinutes,
            viewName = view.Name,
            categoryName = category.Name,
            wasHidden = currentHidden,
            hidden = desiredHidden
        };
    }

    private static object ApplySetCategoryVisibility(UIApplication app, JsonElement args, Func<bool>? isCancelled)
    {
        var document = RequireDocument(app);
        var plan = GetPlan(JsonTools.GetString(args, "planId"), "visibility");
        var change = plan.Visibility ?? throw new InvalidOperationException("The visibility plan is incomplete. Prepare a new preview.");
        if (!(plan.OwnerDocument?.IsValidObject == true && plan.OwnerDocument.Equals(document)))
            throw new InvalidOperationException("The visibility plan belongs to a different Revit document. Prepare a new preview in the active document.");
        var view = document.GetElement(new ElementId(change.ViewId)) as View
            ?? throw new InvalidOperationException("The target view was deleted after the preview.");
        var category = document.Settings.Categories.Cast<Category>().FirstOrDefault(item => item.Id.Value == change.CategoryId)
            ?? throw new InvalidOperationException("The target category was deleted after the preview.");
        if (view.IsTemplate || !view.CanCategoryBeHidden(category.Id))
            throw new InvalidOperationException("The target view no longer supports changing this category's visibility.");
        if (view.GetCategoryHidden(category.Id) != change.WasHidden)
            throw new InvalidOperationException("The category visibility changed after the preview. Prepare a new preview.");

        var dialog = new TaskDialog("WEAM.Revit.AI")
        {
            MainInstruction = $"{(change.Hidden ? "Скрыть" : "Показать")} категорию «{change.CategoryName}»?",
            MainContent = $"Вид: {change.ViewName}\nТекущее состояние: {(change.WasHidden ? "скрыта" : "видна")}\nНовое состояние: {(change.Hidden ? "скрыта" : "видна")}",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No
        };
        if (!ConfirmOperation(dialog)) return new { applied = false, cancelled = true, changed = false };
        if (isCancelled?.Invoke() == true) return new { applied = false, cancelled = true, changed = false, reason = "MCP client cancelled while confirmation was open." };

        using var transaction = new Transaction(document, "WEAM AI: видимость категории");
        ValidateLegacyPlan(plan, document);
        BeginCheckedTransaction(transaction);
        try
        {
            view.SetCategoryHidden(category.Id, change.Hidden);
            CommitCheckedTransaction(transaction, isCancelled);
            Plans.TryRemove(plan.Id, out _);
            return new { applied = true, viewName = view.Name, categoryName = category.Name, hidden = change.Hidden };
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            throw;
        }
    }

    private static object PreviewCreateBeams(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        if (!args.TryGetProperty("beams", out var beamArray) || beamArray.ValueKind != JsonValueKind.Array || beamArray.GetArrayLength() is < 1 or > 50)
            throw new ArgumentException("Provide between 1 and 50 beams.");

        var symbols = new FilteredElementCollector(document).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
        var levels = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>().ToList();
        var proposed = new List<BeamPlanItem>();
        foreach (var item in beamArray.EnumerateArray())
        {
            var familyName = JsonTools.GetString(item, "familyName").Trim();
            var typeName = JsonTools.GetString(item, "typeName").Trim();
            var levelName = JsonTools.GetString(item, "levelName").Trim();
            if (familyName.Length == 0 || typeName.Length == 0 || levelName.Length == 0)
                throw new ArgumentException("Each beam needs familyName, typeName and levelName.");
            var matchingSymbols = symbols.Where(symbol => string.Equals(symbol.FamilyName, familyName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(symbol.Name, typeName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchingSymbols.Count != 1) throw new ArgumentException(matchingSymbols.Count == 0
                ? $"Beam family type '{familyName}: {typeName}' was not found. Use list_family_types to inspect loaded types."
                : $"Beam family type '{familyName}: {typeName}' is ambiguous.");
            var symbol = matchingSymbols[0];
            if (symbol.Family.FamilyPlacementType != FamilyPlacementType.CurveDrivenStructural)
                throw new ArgumentException($"Family '{familyName}: {typeName}' is {symbol.Family.FamilyPlacementType}; beam creation supports CurveDrivenStructural families only.");
            var matchingLevels = levels.Where(level => string.Equals(level.Name, levelName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchingLevels.Count != 1) throw new ArgumentException(matchingLevels.Count == 0
                ? $"Level '{levelName}' was not found."
                : $"Level name '{levelName}' is ambiguous.");
            var start = ReadPoint3D(item, "start");
            var end = ReadPoint3D(item, "end");
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var dz = end.Z - start.Z;
            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) < 0.01) throw new ArgumentException("Beam endpoints must be at least 0.01 m apart.");
            proposed.Add(new BeamPlanItem(symbol.Id.Value, symbol.FamilyName, symbol.Name,
                matchingLevels[0].Id.Value, matchingLevels[0].Name, start, end));
        }

        var planId = Guid.NewGuid().ToString("N");
        Plans[planId] = new ParameterPlan { Id = planId, Kind = "beams", Beams = proposed, OwnerDocument = document, DocumentRevision = DocumentRevision(document) };
        return new
        {
            planCreated = true,
            planId,
            expiresInMinutes = (int)PlanLifetime.TotalMinutes,
            beamCount = proposed.Count,
            units = "meters",
            beams = proposed.Select((beam, index) => new
            {
                index = index + 1,
                beam.FamilyName,
                beam.TypeName,
                beam.LevelName,
                start = new { x = beam.Start.X, y = beam.Start.Y, z = beam.Start.Z },
                end = new { x = beam.End.X, y = beam.End.Y, z = beam.End.Z }
            }).ToList()
        };
    }

    private static object ApplyCreateBeams(UIApplication app, JsonElement args, Func<bool>? isCancelled)
    {
        var document = RequireDocument(app);
        var plan = GetPlan(JsonTools.GetString(args, "planId"), "beams");
        if (!(plan.OwnerDocument?.IsValidObject == true && plan.OwnerDocument.Equals(document)))
            throw new InvalidOperationException("The beam plan belongs to a different Revit document. Prepare a new preview in the active document.");
        var preview = string.Join("\n", plan.Beams.Take(10).Select((beam, index) =>
            $"{index + 1}. {beam.FamilyName}: {beam.TypeName}, {beam.LevelName}: ({beam.Start.X:0.###}, {beam.Start.Y:0.###}, {beam.Start.Z:0.###}) → ({beam.End.X:0.###}, {beam.End.Y:0.###}, {beam.End.Z:0.###}) м"));
        if (plan.Beams.Count > 10) preview += $"\n… и ещё {plan.Beams.Count - 10} балок";
        var dialog = new TaskDialog("WEAM.Revit.AI")
        {
            MainInstruction = $"Создать {plan.Beams.Count} балок?",
            MainContent = preview,
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No
        };
        if (!ConfirmOperation(dialog)) return new { applied = false, cancelled = true, createdCount = 0 };
        if (isCancelled?.Invoke() == true) return new { applied = false, cancelled = true, createdCount = 0, reason = "MCP client cancelled while confirmation was open." };

        using var transaction = new Transaction(document, "WEAM AI: создание балок");
        ValidateLegacyPlan(plan, document);
        BeginCheckedTransaction(transaction);
        try
        {
            var created = new List<object>();
            foreach (var beamPlan in plan.Beams)
            {
                var level = document.GetElement(new ElementId(beamPlan.LevelId)) as Level
                    ?? throw new InvalidOperationException($"Level '{beamPlan.LevelName}' was deleted after the preview.");
                var symbol = document.GetElement(new ElementId(beamPlan.FamilySymbolId)) as FamilySymbol
                    ?? throw new InvalidOperationException($"Beam family type '{beamPlan.FamilyName}: {beamPlan.TypeName}' was deleted after the preview.");
                if (symbol.Family.FamilyPlacementType != FamilyPlacementType.CurveDrivenStructural)
                    throw new InvalidOperationException($"Beam type '{beamPlan.FamilyName}: {beamPlan.TypeName}' changed placement type after the preview.");
                if (!symbol.IsActive) { symbol.Activate(); document.Regenerate(); }
                var start = ToXYZ(beamPlan.Start);
                var end = ToXYZ(beamPlan.End);
                var instance = document.Create.NewFamilyInstance(Line.CreateBound(start, end), symbol, level, StructuralType.Beam)
                    ?? throw new InvalidOperationException($"Revit could not create beam '{beamPlan.FamilyName}: {beamPlan.TypeName}'.");
                created.Add(new { id = instance.Id.Value, family = symbol.FamilyName, type = symbol.Name, level = level.Name });
            }
            CommitCheckedTransaction(transaction, isCancelled);
            Plans.TryRemove(plan.Id, out _);
            return new { applied = true, createdCount = created.Count, beams = created };
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            throw;
        }
    }

    private static PlanPoint3D ReadPoint3D(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var point) || !TryGetFiniteNumber(point, "xMeters", out var x)
            || !TryGetFiniteNumber(point, "yMeters", out var y) || !TryGetFiniteNumber(point, "zMeters", out var z)
            || Math.Abs(x) > 100000 || Math.Abs(y) > 100000 || Math.Abs(z) > 100000)
            throw new ArgumentException($"Each {name} point must contain finite xMeters/yMeters/zMeters coordinates within ±100000 m.");
        return new PlanPoint3D(x, y, z);
    }

    private static XYZ ToXYZ(PlanPoint3D point) => new(
        UnitUtils.ConvertToInternalUnits(point.X, UnitTypeId.Meters),
        UnitUtils.ConvertToInternalUnits(point.Y, UnitTypeId.Meters),
        UnitUtils.ConvertToInternalUnits(point.Z, UnitTypeId.Meters));

    private static object PreviewCreateCeilings(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        if (!args.TryGetProperty("ceilings", out var ceilingArray) || ceilingArray.ValueKind != JsonValueKind.Array || ceilingArray.GetArrayLength() is < 1 or > 10)
            throw new ArgumentException("Provide between 1 and 10 ceilings.");
        var levels = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>().ToList();
        var ceilingTypes = new FilteredElementCollector(document).OfClass(typeof(CeilingType)).Cast<CeilingType>().ToList();
        var proposed = new List<CeilingPlanItem>();
        foreach (var item in ceilingArray.EnumerateArray())
        {
            var levelName = JsonTools.GetString(item, "levelName").Trim();
            var ceilingTypeName = JsonTools.GetString(item, "ceilingTypeName").Trim();
            if (levelName.Length == 0 || ceilingTypeName.Length == 0)
                throw new ArgumentException("Each ceiling needs levelName and ceilingTypeName.");
            var matchingLevels = levels.Where(level => string.Equals(level.Name, levelName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchingLevels.Count != 1) throw new ArgumentException(matchingLevels.Count == 0
                ? $"Level '{levelName}' was not found."
                : $"Level name '{levelName}' is ambiguous.");
            var matchingTypes = ceilingTypes.Where(type => string.Equals(type.Name, ceilingTypeName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchingTypes.Count != 1) throw new ArgumentException(matchingTypes.Count == 0
                ? $"Ceiling type '{ceilingTypeName}' was not found."
                : $"Ceiling type name '{ceilingTypeName}' is ambiguous.");
            var (vertices, area) = ReadPolygon(item, "ceiling");
            var heightOffset = TryGetFiniteNumber(item, "heightOffsetMeters", out var parsedOffset) ? parsedOffset : 0;
            if (heightOffset is < -100 or > 100) throw new ArgumentException("heightOffsetMeters must be between -100 and 100.");
            proposed.Add(new CeilingPlanItem(matchingTypes[0].Id.Value, matchingTypes[0].Name,
                matchingLevels[0].Id.Value, matchingLevels[0].Name, vertices, area, heightOffset));
        }

        var planId = Guid.NewGuid().ToString("N");
        Plans[planId] = new ParameterPlan { Id = planId, Kind = "ceilings", Ceilings = proposed, OwnerDocument = document, DocumentRevision = DocumentRevision(document) };
        return new
        {
            planCreated = true,
            planId,
            expiresInMinutes = (int)PlanLifetime.TotalMinutes,
            ceilingCount = proposed.Count,
            units = "meters",
            ceilings = proposed.Select((ceiling, index) => new
            {
                index = index + 1,
                ceiling.CeilingTypeName,
                ceiling.LevelName,
                ceiling.AreaSquareMeters,
                ceiling.HeightOffsetMeters,
                vertexCount = ceiling.Vertices.Count,
                vertices = ceiling.Vertices.Select(point => new { x = point.X, y = point.Y }).ToList()
            }).ToList()
        };
    }

    private static object ApplyCreateCeilings(UIApplication app, JsonElement args, Func<bool>? isCancelled)
    {
        var document = RequireDocument(app);
        var plan = GetPlan(JsonTools.GetString(args, "planId"), "ceilings");
        if (!(plan.OwnerDocument?.IsValidObject == true && plan.OwnerDocument.Equals(document)))
            throw new InvalidOperationException("The ceiling plan belongs to a different Revit document. Prepare a new preview in the active document.");
        var preview = string.Join("\n", plan.Ceilings.Select((ceiling, index) =>
            $"{index + 1}. {ceiling.CeilingTypeName}, {ceiling.LevelName}: {ceiling.AreaSquareMeters:0.###} м², вершин {ceiling.Vertices.Count}, смещение {ceiling.HeightOffsetMeters:0.###} м"));
        var dialog = new TaskDialog("WEAM.Revit.AI")
        {
            MainInstruction = $"Создать {plan.Ceilings.Count} потолков?",
            MainContent = preview,
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No
        };
        if (!ConfirmOperation(dialog)) return new { applied = false, cancelled = true, createdCount = 0 };
        if (isCancelled?.Invoke() == true) return new { applied = false, cancelled = true, createdCount = 0, reason = "MCP client cancelled while confirmation was open." };

        using var transaction = new Transaction(document, "WEAM AI: создание потолков");
        ValidateLegacyPlan(plan, document);
        BeginCheckedTransaction(transaction);
        try
        {
            var created = new List<object>();
            foreach (var ceilingPlan in plan.Ceilings)
            {
                var level = document.GetElement(new ElementId(ceilingPlan.LevelId)) as Level
                    ?? throw new InvalidOperationException($"Level '{ceilingPlan.LevelName}' was deleted after the preview.");
                var ceilingType = document.GetElement(new ElementId(ceilingPlan.CeilingTypeId)) as CeilingType
                    ?? throw new InvalidOperationException($"Ceiling type '{ceilingPlan.CeilingTypeName}' was deleted after the preview.");
                var loop = BuildHorizontalProfile(ceilingPlan.Vertices, level.Elevation);
                var ceiling = Ceiling.Create(document, new List<CurveLoop> { loop }, ceilingType.Id, level.Id);
                if (Math.Abs(ceilingPlan.HeightOffsetMeters) > 1e-9)
                {
                    var offset = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)
                        ?? throw new InvalidOperationException($"Could not set the level offset on ceiling type '{ceilingType.Name}'.");
                    if (offset.IsReadOnly || !offset.Set(UnitUtils.ConvertToInternalUnits(ceilingPlan.HeightOffsetMeters, UnitTypeId.Meters)))
                        throw new InvalidOperationException($"Revit rejected the height offset on ceiling type '{ceilingType.Name}'. The transaction was rolled back.");
                }
                created.Add(new { id = ceiling.Id.Value, ceilingType = ceilingType.Name, level = level.Name, ceilingPlan.AreaSquareMeters, ceilingPlan.HeightOffsetMeters });
            }
            CommitCheckedTransaction(transaction, isCancelled);
            Plans.TryRemove(plan.Id, out _);
            return new { applied = true, createdCount = created.Count, ceilings = created };
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            throw;
        }
    }

    private static CurveLoop BuildHorizontalProfile(IReadOnlyList<PlanPoint2D> vertices, double elevation)
    {
        var loop = new CurveLoop();
        for (var index = 0; index < vertices.Count; index++)
        {
            var first = vertices[index];
            var second = vertices[(index + 1) % vertices.Count];
            var start = new XYZ(UnitUtils.ConvertToInternalUnits(first.X, UnitTypeId.Meters), UnitUtils.ConvertToInternalUnits(first.Y, UnitTypeId.Meters), elevation);
            var end = new XYZ(UnitUtils.ConvertToInternalUnits(second.X, UnitTypeId.Meters), UnitUtils.ConvertToInternalUnits(second.Y, UnitTypeId.Meters), elevation);
            loop.Append(Line.CreateBound(start, end));
        }
        return loop;
    }

    private static object PreviewCreateRooms(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        if (!args.TryGetProperty("rooms", out var roomArray) || roomArray.ValueKind != JsonValueKind.Array || roomArray.GetArrayLength() is < 1 or > 50)
            throw new ArgumentException("Provide between 1 and 50 rooms.");
        var levels = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>().ToList();
        var existingNumbers = new FilteredElementCollector(document)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .Select(room => room.Number).Where(number => !string.IsNullOrWhiteSpace(number)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var proposed = new List<RoomPlanItem>();
        foreach (var item in roomArray.EnumerateArray())
        {
            var levelName = JsonTools.GetString(item, "levelName").Trim();
            var number = JsonTools.GetString(item, "number").Trim();
            var name = JsonTools.GetString(item, "name").Trim();
            if (levelName.Length == 0) throw new ArgumentException("Each room needs levelName.");
            if (number.Length > 50 || name.Length > 200) throw new ArgumentException("Room number is limited to 50 characters and room name to 200.");
            var matchingLevels = levels.Where(level => string.Equals(level.Name, levelName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchingLevels.Count != 1) throw new ArgumentException(matchingLevels.Count == 0
                ? $"Level '{levelName}' was not found."
                : $"Level name '{levelName}' is ambiguous.");
            if (!item.TryGetProperty("location", out var locationElement)) throw new ArgumentException("Each room needs location with xMeters and yMeters.");
            var location = ReadPoint2D(locationElement);
            proposed.Add(new RoomPlanItem(matchingLevels[0].Id.Value, matchingLevels[0].Name, location.X, location.Y, number, name));
        }

        var numbered = proposed.Where(room => room.Number.Length > 0).ToList();
        var duplicateNumbers = numbered.GroupBy(room => room.Number, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (duplicateNumbers.Count > 0) throw new ArgumentException($"Duplicate room numbers in the request: {string.Join(", ", duplicateNumbers)}.");
        var conflicts = numbered.Where(room => existingNumbers.Contains(room.Number)).Select(room => room.Number).ToList();
        if (conflicts.Count > 0) throw new ArgumentException($"These room numbers already exist: {string.Join(", ", conflicts)}.");
        for (var first = 0; first < proposed.Count; first++)
            for (var second = first + 1; second < proposed.Count; second++)
                if (proposed[first].LevelId == proposed[second].LevelId
                    && Math.Pow(proposed[first].XMeters - proposed[second].XMeters, 2) + Math.Pow(proposed[first].YMeters - proposed[second].YMeters, 2) < 0.01)
                    throw new ArgumentException("Two requested rooms are too close to place independently; move their points at least 0.1 m apart.");

        var planId = Guid.NewGuid().ToString("N");
        Plans[planId] = new ParameterPlan { Id = planId, Kind = "rooms", Rooms = proposed, OwnerDocument = document, DocumentRevision = DocumentRevision(document) };
        return new
        {
            planCreated = true,
            planId,
            expiresInMinutes = (int)PlanLifetime.TotalMinutes,
            roomCount = proposed.Count,
            units = "meters",
            rooms = proposed.Select((room, index) => new
            {
                index = index + 1,
                room.Number,
                room.Name,
                room.LevelName,
                location = new { x = room.XMeters, y = room.YMeters }
            }).ToList(),
            note = "Точка должна лежать внутри замкнутой области помещения, ограниченной стенами/линиями границ на выбранном уровне. При создании площадь проверяется; если помещение не ограничено, вся операция откатится."
        };
    }

    private static object ApplyCreateRooms(UIApplication app, JsonElement args, Func<bool>? isCancelled)
    {
        var document = RequireDocument(app);
        var plan = GetPlan(JsonTools.GetString(args, "planId"), "rooms");
        if (!(plan.OwnerDocument?.IsValidObject == true && plan.OwnerDocument.Equals(document)))
            throw new InvalidOperationException("The room plan belongs to a different Revit document. Prepare a new preview in the active document.");
        var existingNumbers = new FilteredElementCollector(document)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .Select(room => room.Number).Where(number => !string.IsNullOrWhiteSpace(number)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflicts = plan.Rooms.Where(room => room.Number.Length > 0 && existingNumbers.Contains(room.Number)).Select(room => room.Number).ToList();
        if (conflicts.Count > 0) throw new InvalidOperationException($"Room numbers were assigned after the preview: {string.Join(", ", conflicts)}. Prepare a new preview.");

        var preview = string.Join("\n", plan.Rooms.Select((room, index) =>
            $"{index + 1}. {room.Number} {room.Name} — {room.LevelName}, точка ({room.XMeters:0.###}, {room.YMeters:0.###}) м"));
        var dialog = new TaskDialog("WEAM.Revit.AI")
        {
            MainInstruction = $"Создать {plan.Rooms.Count} помещений?",
            MainContent = preview + "\n\nТочки должны находиться внутри замкнутых границ помещений.",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No
        };
        if (!ConfirmOperation(dialog)) return new { applied = false, cancelled = true, createdCount = 0 };
        if (isCancelled?.Invoke() == true) return new { applied = false, cancelled = true, createdCount = 0, reason = "MCP client cancelled while confirmation was open." };

        using var transaction = new Transaction(document, "WEAM AI: создание помещений");
        ValidateLegacyPlan(plan, document);
        BeginCheckedTransaction(transaction);
        try
        {
            var created = new List<Room>();
            foreach (var roomPlan in plan.Rooms)
            {
                var level = document.GetElement(new ElementId(roomPlan.LevelId)) as Level
                    ?? throw new InvalidOperationException($"Level '{roomPlan.LevelName}' was deleted after the preview.");
                var point = new UV(
                    UnitUtils.ConvertToInternalUnits(roomPlan.XMeters, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(roomPlan.YMeters, UnitTypeId.Meters));
                var room = document.Create.NewRoom(level, point)
                    ?? throw new InvalidOperationException($"Revit could not place room '{roomPlan.Number} {roomPlan.Name}'.");
                if (roomPlan.Number.Length > 0) room.Number = roomPlan.Number;
                if (roomPlan.Name.Length > 0) room.Name = roomPlan.Name;
                if (room.Area <= 0)
                    throw new InvalidOperationException($"Room '{roomPlan.Number} {roomPlan.Name}' has no enclosed area at the requested point. No rooms were created; verify the boundary and level.");
                created.Add(room);
            }
            CommitCheckedTransaction(transaction, isCancelled);
            Plans.TryRemove(plan.Id, out _);
            return new
            {
                applied = true,
                createdCount = created.Count,
                rooms = created.Select(room => new
                {
                    id = room.Id.Value,
                    number = room.Number,
                    name = room.Name,
                    level = document.GetElement(room.LevelId)?.Name,
                    areaSquareMeters = UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters)
                }).ToList()
            };
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            throw;
        }
    }

    private static object PreviewCreatePlanViews(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        if (!args.TryGetProperty("views", out var viewArray) || viewArray.ValueKind != JsonValueKind.Array || viewArray.GetArrayLength() is < 1 or > 30)
            throw new ArgumentException("Provide between 1 and 30 plan views.");
        var viewFamilyName = JsonTools.GetString(args, "viewFamily").Trim();
        var viewFamily = viewFamilyName switch
        {
            "FloorPlan" => ViewFamily.FloorPlan,
            "CeilingPlan" => ViewFamily.CeilingPlan,
            "StructuralPlan" => ViewFamily.StructuralPlan,
            _ => throw new ArgumentException("viewFamily must be FloorPlan, CeilingPlan or StructuralPlan.")
        };
        var viewTypeName = JsonTools.GetString(args, "viewTypeName").Trim();
        var typeCandidates = new FilteredElementCollector(document).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .Where(type => type.ViewFamily == viewFamily
                && (viewTypeName.Length == 0 || string.Equals(type.Name, viewTypeName, StringComparison.OrdinalIgnoreCase))).ToList();
        if (typeCandidates.Count != 1) throw new ArgumentException(typeCandidates.Count == 0
            ? $"No {viewFamily} view type named '{viewTypeName}' was found. Use list_plan_view_types to inspect available types."
            : $"More than one {viewFamily} view type matches; pass viewTypeName from list_plan_view_types.");
        var viewType = typeCandidates[0];
        var levels = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>().ToList();
        var proposed = new List<PlanViewPlanItem>();
        foreach (var item in viewArray.EnumerateArray())
        {
            var levelName = JsonTools.GetString(item, "levelName").Trim();
            if (levelName.Length == 0) throw new ArgumentException("Each plan view needs levelName.");
            var matchingLevels = levels.Where(level => string.Equals(level.Name, levelName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchingLevels.Count != 1) throw new ArgumentException(matchingLevels.Count == 0
                ? $"Level '{levelName}' was not found."
                : $"Level name '{levelName}' is ambiguous.");
            var viewName = JsonTools.GetString(item, "viewName").Trim();
            if (viewName.Length == 0) viewName = $"{levelName} - {viewFamilyName}";
            if (viewName.Length > 200) throw new ArgumentException("viewName is limited to 200 characters.");
            proposed.Add(new PlanViewPlanItem(viewType.Id.Value, viewType.Name, viewFamilyName, matchingLevels[0].Id.Value, matchingLevels[0].Name, viewName));
        }

        var requestedDuplicateNames = proposed.GroupBy(item => item.ViewName, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (requestedDuplicateNames.Count > 0) throw new ArgumentException($"Duplicate view names in the request: {string.Join(", ", requestedDuplicateNames)}.");
        var existingNames = new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>()
            .Select(view => view.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nameConflicts = proposed.Where(item => existingNames.Contains(item.ViewName)).Select(item => item.ViewName).ToList();
        if (nameConflicts.Count > 0) throw new ArgumentException($"These view names already exist: {string.Join(", ", nameConflicts)}.");

        var planId = Guid.NewGuid().ToString("N");
        Plans[planId] = new ParameterPlan { Id = planId, Kind = "planViews", PlanViews = proposed, OwnerDocument = document, DocumentRevision = DocumentRevision(document) };
        return new
        {
            planCreated = true,
            planId,
            expiresInMinutes = (int)PlanLifetime.TotalMinutes,
            viewFamily = viewFamilyName,
            viewTypeName = viewType.Name,
            viewCount = proposed.Count,
            views = proposed.Select(item => new { item.ViewName, item.LevelName, item.ViewFamily, item.ViewTypeName }).ToList()
        };
    }

    private static object ApplyCreatePlanViews(UIApplication app, JsonElement args, Func<bool>? isCancelled)
    {
        var document = RequireDocument(app);
        var plan = GetPlan(JsonTools.GetString(args, "planId"), "planViews");
        if (!(plan.OwnerDocument?.IsValidObject == true && plan.OwnerDocument.Equals(document)))
            throw new InvalidOperationException("The plan view request belongs to a different Revit document. Prepare a new preview in the active document.");
        var existingNames = new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>()
            .Select(view => view.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflicts = plan.PlanViews.Where(item => existingNames.Contains(item.ViewName)).Select(item => item.ViewName).ToList();
        if (conflicts.Count > 0) throw new InvalidOperationException($"These view names were created after the preview: {string.Join(", ", conflicts)}. Prepare a new preview.");

        var preview = string.Join("\n", plan.PlanViews.Select(item => $"{item.ViewName}: {item.ViewFamily}, уровень {item.LevelName}, тип {item.ViewTypeName}"));
        var dialog = new TaskDialog("WEAM.Revit.AI")
        {
            MainInstruction = $"Создать {plan.PlanViews.Count} планов?",
            MainContent = preview,
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No
        };
        if (!ConfirmOperation(dialog)) return new { applied = false, cancelled = true, createdCount = 0 };
        if (isCancelled?.Invoke() == true) return new { applied = false, cancelled = true, createdCount = 0, reason = "MCP client cancelled while confirmation was open." };

        using var transaction = new Transaction(document, "WEAM AI: создание планов");
        ValidateLegacyPlan(plan, document);
        BeginCheckedTransaction(transaction);
        try
        {
            var created = new List<object>();
            foreach (var item in plan.PlanViews)
            {
                var level = document.GetElement(new ElementId(item.LevelId)) as Level
                    ?? throw new InvalidOperationException($"Level '{item.LevelName}' was deleted after the preview.");
                var viewType = document.GetElement(new ElementId(item.ViewFamilyTypeId)) as ViewFamilyType
                    ?? throw new InvalidOperationException($"View type '{item.ViewTypeName}' was deleted after the preview.");
                var expectedFamily = item.ViewFamily switch
                {
                    "FloorPlan" => ViewFamily.FloorPlan,
                    "CeilingPlan" => ViewFamily.CeilingPlan,
                    "StructuralPlan" => ViewFamily.StructuralPlan,
                    _ => throw new InvalidOperationException("The plan view type in the preview is invalid.")
                };
                if (viewType.ViewFamily != expectedFamily)
                    throw new InvalidOperationException($"View type '{item.ViewTypeName}' changed family after the preview.");
                var view = ViewPlan.Create(document, viewType.Id, level.Id);
                view.Name = item.ViewName;
                created.Add(new { id = view.Id.Value, name = view.Name, viewFamily = item.ViewFamily, level = level.Name });
            }
            CommitCheckedTransaction(transaction, isCancelled);
            Plans.TryRemove(plan.Id, out _);
            return new { applied = true, createdCount = created.Count, views = created };
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            throw;
        }
    }

    private static ParameterPlan GetPlan(string planId, string kind)
    {
        if (string.IsNullOrWhiteSpace(planId) || !Plans.TryGetValue(planId, out var plan) || plan.Kind != kind)
            throw new InvalidOperationException("Plan not found or has already been used. Prepare a new preview.");
        if (DateTime.UtcNow - plan.CreatedUtc > PlanLifetime)
        {
            Plans.TryRemove(planId, out _);
            throw new InvalidOperationException("Plan expired. Prepare a new preview.");
        }
        return plan;
    }

    private static void PrunePlans()
    {
        var cutoff = DateTime.UtcNow - PlanLifetime;
        foreach (var entry in Plans)
            if (entry.Value.CreatedUtc < cutoff) Plans.TryRemove(entry.Key, out _);
    }

    private static List<long> ReadRequestedIds(JsonElement args)
    {
        var ids = JsonTools.GetIds(args);
        if (ids.Count == 0) throw new ArgumentException("Provide at least one element id.");
        if (ids.Count > 100) throw new ArgumentException("A maximum of 100 elements can be processed at once.");
        return ids;
    }

    private static string RawParameterValue(Parameter parameter) => parameter.StorageType switch
    {
        StorageType.String => parameter.AsString() ?? "",
        StorageType.Integer => parameter.AsInteger().ToString(CultureInfo.InvariantCulture),
        StorageType.Double => parameter.AsDouble().ToString("R", CultureInfo.InvariantCulture),
        StorageType.ElementId => parameter.AsElementId().Value.ToString(CultureInfo.InvariantCulture),
        _ => ""
    };

    private static string DisplayParameterValue(Parameter parameter)
    {
        try { return parameter.AsValueString() ?? RawParameterValue(parameter); }
        catch { return RawParameterValue(parameter); }
    }

    private static void SetParameter(Parameter parameter, string value, long elementId)
    {
        var changed = parameter.StorageType switch
        {
            StorageType.String => parameter.Set(value),
            StorageType.Integer when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => parameter.Set(number),
            StorageType.Integer => throw new InvalidOperationException($"Value '{value}' is not an integer for element {elementId}."),
            StorageType.Double => parameter.SetValueString(value),
            _ => throw new InvalidOperationException($"This parameter type cannot be changed by the MVP (element {elementId}).")
        };
        if (!changed) throw new InvalidOperationException($"Revit rejected the value for element {elementId}; the transaction was rolled back.");
    }

    private static Document RequireDocument(UIApplication app) => app.ActiveUIDocument?.Document
        ?? throw new InvalidOperationException("No Revit project is open.");

    private static bool CategoryMatches(Element element, string filter) => filter.Length == 0
        || string.Equals(element.Category?.Name, filter, StringComparison.OrdinalIgnoreCase);

    private static bool ClassMatches(Element element, string filter)
    {
        var type = element.GetType();
        return string.Equals(type.Name, filter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type.FullName, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static SearchBounds? ReadSearchBounds(JsonElement args)
    {
        if (!args.TryGetProperty("bounds", out var boundsValue)) return null;
        if (boundsValue.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("bounds must be an object with minX/minY/minZ and maxX/maxY/maxZ in metres.");

        double ReadCoordinate(string name)
        {
            if (!boundsValue.TryGetProperty(name, out var value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetDouble(out var coordinate)
                || !double.IsFinite(coordinate)
                || coordinate is < -100000 or > 100000)
                throw new ArgumentException($"bounds.{name} must be a finite coordinate between -100000 and 100000 metres.");
            return UnitUtils.ConvertToInternalUnits(coordinate, UnitTypeId.Meters);
        }

        var minX = ReadCoordinate("minX");
        var minY = ReadCoordinate("minY");
        var minZ = ReadCoordinate("minZ");
        var maxX = ReadCoordinate("maxX");
        var maxY = ReadCoordinate("maxY");
        var maxZ = ReadCoordinate("maxZ");
        if (minX > maxX || minY > maxY || minZ > maxZ)
            throw new ArgumentException("Each bounds min coordinate must be less than or equal to its max coordinate.");

        return new SearchBounds(minX, minY, minZ, maxX, maxY, maxZ);
    }

    private sealed record SearchBounds(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
    {
        private const double DegenerateExtentTolerance = 0.001 / 0.3048;

        public BoundingBoxIntersectsFilter CreateIntersectsFilter()
        {
            var maxX = MaxX > MinX ? MaxX : MinX + DegenerateExtentTolerance;
            var maxY = MaxY > MinY ? MaxY : MinY + DegenerateExtentTolerance;
            var maxZ = MaxZ > MinZ ? MaxZ : MinZ + DegenerateExtentTolerance;
            return new BoundingBoxIntersectsFilter(new Outline(new XYZ(MinX, MinY, MinZ), new XYZ(maxX, maxY, maxZ)));
        }

        public bool Intersects(BoundingBoxXYZ? box)
        {
            if (box is null) return false;

            var corners = new[]
            {
                new XYZ(box.Min.X, box.Min.Y, box.Min.Z),
                new XYZ(box.Min.X, box.Min.Y, box.Max.Z),
                new XYZ(box.Min.X, box.Max.Y, box.Min.Z),
                new XYZ(box.Min.X, box.Max.Y, box.Max.Z),
                new XYZ(box.Max.X, box.Min.Y, box.Min.Z),
                new XYZ(box.Max.X, box.Min.Y, box.Max.Z),
                new XYZ(box.Max.X, box.Max.Y, box.Min.Z),
                new XYZ(box.Max.X, box.Max.Y, box.Max.Z)
            }.Select(box.Transform.OfPoint).ToList();

            return corners.Min(point => point.X) <= MaxX && corners.Max(point => point.X) >= MinX
                && corners.Min(point => point.Y) <= MaxY && corners.Max(point => point.Y) >= MinY
                && corners.Min(point => point.Z) <= MaxZ && corners.Max(point => point.Z) >= MinZ;
        }
    }

    private static bool MatchesQuery(Document document, Element element, string query)
    {
        if (Contains(element.Name, query) || Contains(element.Category?.Name, query)) return true;
        var type = document.GetElement(element.GetTypeId()) as ElementType;
        if (type is not null && (Contains(type.Name, query) || Contains(type.FamilyName, query))) return true;
        foreach (Parameter parameter in element.Parameters)
        {
            try
            {
                var value = parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString();
                if (Contains(value, query)) return true;
            }
            catch { }
        }
        return false;
    }

    private static bool Contains(string? text, string query) => text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static object Summarize(Document document, Element element, bool includeParameters)
    {
        var type = document.GetElement(element.GetTypeId()) as ElementType;
        var levelParameter = element.get_Parameter(BuiltInParameter.LEVEL_PARAM);
        var levelId = levelParameter?.StorageType == StorageType.ElementId ? levelParameter.AsElementId() : ElementId.InvalidElementId;
        var parameters = includeParameters
            ? element.Parameters.Cast<Parameter>().Where(parameter => parameter.Definition is not null).Take(40)
                .Select(parameter => new
                {
                    name = parameter.Definition.Name,
                    storageType = parameter.StorageType.ToString(),
                    value = DisplayParameterValue(parameter),
                    rawValue = RawParameterValue(parameter),
                    readOnly = parameter.IsReadOnly
                }).ToList()
            : null;

        return new
        {
            id = element.Id.Value,
            uniqueId = element.UniqueId,
            name = element.Name,
            category = element.Category?.Name,
            typeId = type?.Id.Value,
            type = type?.Name,
            family = type?.FamilyName,
            level = levelId == ElementId.InvalidElementId ? null : document.GetElement(levelId)?.Name,
            geometry = includeParameters ? SummarizeLocation(element) : null,
            parameters
        };
    }

    private static object? SummarizeLocation(Element element)
    {
        if (element.Location is LocationCurve curve && curve.Curve.IsBound)
            return new { kind = curve.Curve.GetType().Name, start = AnnotationPointMeters(curve.Curve.GetEndPoint(0)),
                end = AnnotationPointMeters(curve.Curve.GetEndPoint(1)), lengthMeters = UnitUtils.ConvertFromInternalUnits(curve.Curve.Length, UnitTypeId.Meters),
                widthMeters = element is Wall wall ? (double?)UnitUtils.ConvertFromInternalUnits(wall.Width, UnitTypeId.Meters) : null };
        if (element.Location is LocationPoint point)
            return new { kind = "point", position = AnnotationPointMeters(point.Point), rotationRadians = point.Rotation };
        return null;
    }
}
