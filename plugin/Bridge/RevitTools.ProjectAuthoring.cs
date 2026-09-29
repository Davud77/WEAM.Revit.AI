using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    private static object PreviewDuplicateFamilyTypes(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "duplicateFamilyTypes", "Создание типоразмеров дверей и окон", DuplicateFamilyTypes);
    private static object ApplyDuplicateFamilyTypes(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "duplicateFamilyTypes", "Создать проверенные типоразмеры дверей и окон?", DuplicateFamilyTypes, cancelled);

    private static object DuplicateFamilyTypes(Document document, JsonElement args)
    {
        var items = args.GetProperty("types");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > 20)
            throw new ArgumentException("types must contain 1 to 20 entries.");
        var result = new List<object>();
        foreach (var item in items.EnumerateArray())
        {
            var sourceId = HostedReadId(item, "sourceSymbolId");
            if (document.GetElement(new ElementId(sourceId)) is not FamilySymbol source
                || source.Category?.Id.Value is not ((long)BuiltInCategory.OST_Doors) and not ((long)BuiltInCategory.OST_Windows))
                throw new ArgumentException("sourceSymbolId must be an existing door or window type.");
            var name = JsonTools.GetString(item, "name").Trim();
            if (name.Length is < 1 or > 200) throw new ArgumentException("Type name must contain 1 to 200 characters.");
            if (source.Family.GetFamilySymbolIds().Any(id => string.Equals(document.GetElement(id).Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Type '{name}' already exists in family '{source.FamilyName}'.");
            var width = HostedReadNumber(item, "widthMeters", 0.1, 10);
            var height = HostedReadNumber(item, "heightMeters", 0.1, 10);
            var target = (FamilySymbol)source.Duplicate(name);
            SetFamilyTypeLength(target, BuiltInParameter.FAMILY_WIDTH_PARAM, width);
            SetFamilyTypeLength(target, BuiltInParameter.FAMILY_HEIGHT_PARAM, height);
            document.Regenerate();
            result.Add(new { id = target.Id.Value, sourceSymbolId = sourceId, family = target.FamilyName, name, widthMeters = width, heightMeters = height });
        }
        return new { count = result.Count, types = result };
    }

    private static void SetFamilyTypeLength(FamilySymbol symbol, BuiltInParameter parameterId, double meters)
    {
        var parameter = symbol.get_Parameter(parameterId);
        if (parameter is null || parameter.IsReadOnly || parameter.StorageType != StorageType.Double)
            throw new ArgumentException($"Family '{symbol.FamilyName}' has no writable standard type {parameterId} parameter.");
        var value = UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);
        if (!parameter.Set(value) || Math.Abs(parameter.AsDouble() - value) > 1e-7)
            throw new InvalidOperationException($"Revit rejected {parameterId}={meters} m.");
    }

    private static object PreviewLoadFamily(UIApplication app, JsonElement args)
    {
        var path = AuthoringFilePath(args, "path", ".rfa", true);
        var bound = JsonNode.Parse(args.GetRawText())!.AsObject();
        bound["path"] = path;
        bound["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        return PreviewOperation(app, JsonSerializer.SerializeToElement(bound), "loadFamily", "Загрузка локального семейства", LoadFamilyOperation);
    }
    private static object ApplyLoadFamily(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "loadFamily", "Загрузить проверенное локальное семейство?", LoadFamilyOperation, cancelled);
    private static object LoadFamilyOperation(Document document, JsonElement args)
    {
        var path = AuthoringFilePath(args, "path", ".rfa", true);
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != JsonTools.GetString(args, "sha256"))
            throw new InvalidOperationException("The family file changed after preview.");
        if (!document.LoadFamily(path, out var family) || family is null)
            throw new InvalidOperationException("Revit could not load this family, or it already exists. Use list_family_types to inspect the project.");
        return new { familyId = family.Id.Value, family = family.Name, sourcePath = path,
            types = family.GetFamilySymbolIds().Select(id => new { id = id.Value, name = document.GetElement(id).Name }).ToArray() };
    }

    private static object PreviewCreateRoomSeparators(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "roomSeparators", "Линии разделения помещений", CreateRoomSeparators);
    private static object ApplyCreateRoomSeparators(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "roomSeparators", "Создать линии разделения помещений?", CreateRoomSeparators, cancelled);
    private static object CreateRoomSeparators(Document document, JsonElement args)
    {
        if (document.GetElement(new ElementId(HostedReadId(args, "viewId"))) is not ViewPlan view
            || view.ViewType != ViewType.FloorPlan || view.IsTemplate || view.GenLevel is null)
            throw new ArgumentException("Room separators require a non-template floor plan.");
        var items = args.GetProperty("lines");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > 100)
            throw new ArgumentException("lines must contain 1 to 100 segments.");
        var curves = new CurveArray();
        var z = view.GenLevel.ProjectElevation;
        foreach (var item in items.EnumerateArray())
        {
            var (x1, y1) = ReadWallPoint(item, "start");
            var (x2, y2) = ReadWallPoint(item, "end");
            var a = new XYZ(UnitUtils.ConvertToInternalUnits(x1, UnitTypeId.Meters), UnitUtils.ConvertToInternalUnits(y1, UnitTypeId.Meters), z);
            var b = new XYZ(UnitUtils.ConvertToInternalUnits(x2, UnitTypeId.Meters), UnitUtils.ConvertToInternalUnits(y2, UnitTypeId.Meters), z);
            if (a.DistanceTo(b) <= document.Application.ShortCurveTolerance) throw new ArgumentException("Room separator is too short.");
            curves.Append(Line.CreateBound(a, b));
        }
        var plane = SketchPlane.Create(document, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z)));
        var lines = document.Create.NewRoomBoundaryLines(plane, curves, view).Cast<ModelCurve>().Select(line => line.Id.Value).ToArray();
        return new { viewId = view.Id.Value, level = view.GenLevel.Name, count = lines.Length, elementIds = lines };
    }

    private static object PreviewSaveProjectAs(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        RequireWritableProject(doc);
        if (doc.IsWorkshared) throw new ArgumentException("Save-as supports standalone projects only.");
        var path = AuthoringFilePath(args, "path", ".rvt", false);
        if (File.Exists(path)) throw new ArgumentException("The destination already exists; choose a new file name.");
        PrunePlans();
        var id = Guid.NewGuid().ToString("N");
        Plans[id] = new ParameterPlan { Id = id, Kind = "saveProject", OwnerDocument = doc, DocumentRevision = DocumentRevision(doc), Value = path };
        return new { planCreated = true, planId = id, expiresInMinutes = 10, currentPath = doc.PathName, destination = path, overwritesExistingFile = false };
    }
    private static object ApplySaveProjectAs(UIApplication app, JsonElement args, Func<bool>? cancelled)
    {
        var doc = RequireDocument(app);
        var plan = GetPlan(JsonTools.GetString(args, "planId"), "saveProject");
        ValidateLegacyPlan(plan, doc);
        var dialog = new TaskDialog("WEAM.Revit.AI") { MainInstruction = "Сохранить проект под новым именем?", MainContent = plan.Value,
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No };
        if (cancelled?.Invoke() == true || !ConfirmOperation(dialog) || cancelled?.Invoke() == true) return new { applied = false, cancelled = true };
        ValidateLegacyPlan(plan, doc);
        if (File.Exists(plan.Value)) throw new InvalidOperationException("The destination was created after preview. Choose a new path.");
        Directory.CreateDirectory(Path.GetDirectoryName(plan.Value)!);
        doc.SaveAs(plan.Value, new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 });
        Plans.TryRemove(plan.Id, out _);
        return new { applied = true, filePath = doc.PathName, saved = !doc.IsModified };
    }

    private static string AuthoringFilePath(JsonElement args, string property, string extension, bool requireExists)
    {
        var value = JsonTools.GetString(args, property);
        if (!Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal) || value.Length > 240)
            throw new ArgumentException("Use a fully qualified local file path, at most 240 characters.");
        var path = Path.GetFullPath(value);
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"Expected a {extension} file.");
        if (requireExists && !File.Exists(path)) throw new ArgumentException("The file does not exist.");
        return path;
    }
}
