using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    private static object ListMepTypes(UIApplication app)
    {
        var doc = RequireDocument(app);
        return new
        {
            pipeTypes = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>()
                .Select(x => new { id = x.Id.Value, name = x.Name }).OrderBy(x => x.name).ToArray(),
            pipingSystems = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>()
                .Select(x => new { id = x.Id.Value, name = x.Name }).OrderBy(x => x.name).ToArray(),
            ductTypes = new FilteredElementCollector(doc).OfClass(typeof(DuctType)).Cast<DuctType>()
                .Select(x => new { id = x.Id.Value, name = x.Name }).OrderBy(x => x.name).ToArray(),
            mechanicalSystems = new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>()
                .Select(x => new { id = x.Id.Value, name = x.Name }).OrderBy(x => x.name).ToArray(),
            levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Select(x => new { id = x.Id.Value, name = x.Name, elevationMeters = UnitUtils.ConvertFromInternalUnits(x.Elevation, UnitTypeId.Meters) }).ToArray()
        };
    }

    private static object PreviewCreateMep(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "createMep", "Прямые трубы и воздуховоды", CreateMep);

    private static object ApplyCreateMep(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "createMep", "Создать трубы и воздуховоды?", CreateMep, cancelled);

    private static object CreateMep(Document doc, JsonElement args)
    {
        var requests = args.GetProperty("segments");
        if (requests.GetArrayLength() is < 1 or > 50) throw new ArgumentException("Provide 1 to 50 MEP segments.");
        var created = new List<object>();
        foreach (var request in requests.EnumerateArray())
        {
            var kind = JsonTools.GetString(request, "kind");
            var typeId = new ElementId(HostedReadId(request, "typeId"));
            var systemId = new ElementId(HostedReadId(request, "systemTypeId"));
            var levelId = new ElementId(HostedReadId(request, "levelId"));
            if (doc.GetElement(levelId) is not Level) throw new ArgumentException("levelId must identify a level.");
            var start = MepPoint(request.GetProperty("start"));
            var end = MepPoint(request.GetProperty("end"));
            var length = start.DistanceTo(end);
            if (length < BuildingFeet(0.01) || length > BuildingFeet(300))
                throw new ArgumentException("Segment length must be between 0.01 and 300 m.");

            Element element;
            if (kind == "pipe")
            {
                if (doc.GetElement(typeId) is not PipeType || doc.GetElement(systemId) is not PipingSystemType)
                    throw new ArgumentException("Pipe requires an existing PipeType and PipingSystemType.");
                element = Pipe.Create(doc, systemId, typeId, levelId, start, end);
                SetMepSize(element, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, BuildingNumber(request, "diameterMeters", 0.001, 3));
            }
            else if (kind == "duct")
            {
                if (doc.GetElement(typeId) is not DuctType || doc.GetElement(systemId) is not MechanicalSystemType)
                    throw new ArgumentException("Duct requires an existing DuctType and MechanicalSystemType.");
                element = Duct.Create(doc, systemId, typeId, levelId, start, end);
                if (request.TryGetProperty("diameterMeters", out var diameter))
                {
                    if (request.TryGetProperty("widthMeters", out _) || request.TryGetProperty("heightMeters", out _))
                        throw new ArgumentException("Specify either duct diameter or width and height.");
                    SetMepSize(element, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM,
                        MepDimension(diameter, "diameterMeters"));
                }
                else
                {
                    SetMepSize(element, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, BuildingNumber(request, "widthMeters", 0.001, 3));
                    SetMepSize(element, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, BuildingNumber(request, "heightMeters", 0.001, 3));
                }
            }
            else throw new ArgumentException("kind must be pipe or duct.");
            created.Add(new { id = element.Id.Value, kind, typeId = typeId.Value, systemTypeId = systemId.Value,
                levelId = levelId.Value, lengthMeters = UnitUtils.ConvertFromInternalUnits(length, UnitTypeId.Meters) });
        }
        doc.Regenerate();
        return new { segments = created };
    }

    private static XYZ MepPoint(JsonElement point) => new(
        BuildingFeet(BuildingNumber(point, "xMeters", -100000, 100000)),
        BuildingFeet(BuildingNumber(point, "yMeters", -100000, 100000)),
        BuildingFeet(BuildingNumber(point, "zMeters", -100000, 100000)));

    private static double MepDimension(JsonElement value, string name)
    {
        var meters = value.GetDouble();
        if (!double.IsFinite(meters) || meters < 0.001 || meters > 3)
            throw new ArgumentException($"{name} must be between 0.001 and 3 m.");
        return meters;
    }

    private static void SetMepSize(Element element, BuiltInParameter parameterId, double meters)
    {
        var parameter = element.get_Parameter(parameterId);
        if (parameter is null || parameter.IsReadOnly || parameter.StorageType != StorageType.Double)
            throw new InvalidOperationException($"Type '{element.Name}' does not support the requested MEP size or shape.");
        if (!parameter.Set(BuildingFeet(meters)))
            throw new InvalidOperationException($"Revit rejected MEP size {meters} m. Check available routing sizes for this type.");
    }
}
