using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    private static object ListBeamTypes(UIApplication app)
    {
        var doc = RequireDocument(app);
        return new
        {
            beamTypes = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(x => x.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFraming)
                .Select(x => new { id = x.Id.Value, family = x.FamilyName, name = x.Name }).ToArray(),
            levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Select(x => new { id = x.Id.Value, name = x.Name }).ToArray()
        };
    }

    private static object PreviewCreateBeamSystems(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "createBeamSystems", "Прямоугольные балочные системы", CreateBeamSystems);

    private static object ApplyCreateBeamSystems(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "createBeamSystems", "Создать балочные системы?", CreateBeamSystems, cancelled);

    private static object CreateBeamSystems(Document doc, JsonElement args)
    {
        var requests = args.GetProperty("systems");
        if (requests.GetArrayLength() is < 1 or > 10) throw new ArgumentException("Provide 1 to 10 beam systems.");
        var created = new List<object>();
        foreach (var request in requests.EnumerateArray())
        {
            var level = doc.GetElement(new ElementId(HostedReadId(request, "levelId"))) as Level
                ?? throw new ArgumentException("levelId must identify a level.");
            var beamType = doc.GetElement(new ElementId(HostedReadId(request, "beamTypeId"))) as FamilySymbol;
            if (beamType?.Category?.Id.Value != (long)BuiltInCategory.OST_StructuralFraming)
                throw new ArgumentException("beamTypeId must identify a structural framing FamilySymbol.");
            var x0 = BuildingNumber(request, "minX", -100000, 100000);
            var x1 = BuildingNumber(request, "maxX", -100000, 100000);
            var y0 = BuildingNumber(request, "minY", -100000, 100000);
            var y1 = BuildingNumber(request, "maxY", -100000, 100000);
            if (x1 - x0 < 0.5 || x1 - x0 > 100 || y1 - y0 < 0.5 || y1 - y0 > 100)
                throw new ArgumentException("Beam system rectangle sides must be between 0.5 and 100 m.");
            var spacing = BuildingNumber(request, "spacingMeters", 0.1, 20);
            var axis = JsonTools.GetString(request, "beamDirection");
            if (axis is not ("x" or "y")) throw new ArgumentException("beamDirection must be x or y.");
            var z = level.Elevation;
            var corners = new[] { new XYZ(BuildingFeet(x0), BuildingFeet(y0), z),
                new XYZ(BuildingFeet(x1), BuildingFeet(y0), z), new XYZ(BuildingFeet(x1), BuildingFeet(y1), z),
                new XYZ(BuildingFeet(x0), BuildingFeet(y1), z) };
            var boundary = new List<Curve>();
            for (var i = 0; i < 4; i++) boundary.Add(Line.CreateBound(corners[i], corners[(i + 1) % 4]));
            if (!beamType.IsActive) beamType.Activate();
            var system = BeamSystem.Create(doc, boundary, level, axis == "x" ? XYZ.BasisX : XYZ.BasisY, false);
            system.BeamType = beamType;
            system.LayoutRule = new LayoutRuleFixedDistance(BuildingFeet(spacing), BeamSystemJustifyType.Beginning);
            doc.Regenerate();
            created.Add(new { id = system.Id.Value, levelId = level.Id.Value, beamTypeId = beamType.Id.Value,
                beamDirection = axis, spacingMeters = spacing, beamIds = system.GetBeamIds().Select(id => id.Value).ToArray() });
        }
        return new { systems = created };
    }
}
