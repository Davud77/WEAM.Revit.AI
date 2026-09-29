using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    private const string HostedPlacementKind = "hostedFamilies";
    private const double HostedToleranceMeters = 0.001;

    private static object PreviewPlaceHostedFamilies(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, HostedPlacementKind, "Размещение дверей и окон на стенах", PlaceHostedFamilies);

    private static object ApplyPlaceHostedFamilies(UIApplication app, JsonElement args, Func<bool>? isCancelled) =>
        ApplyOperation(app, args, HostedPlacementKind, "Разместить двери и окна на стенах?", PlaceHostedFamilies, isCancelled);

    // The shared operation runner owns the transaction and the preview rollback.
    private static object PlaceHostedFamilies(Document document, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("instances", out var items)
            || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > 50)
            throw new ArgumentException("Provide between 1 and 50 hosted family instances.");

        var results = new List<object>();
        var tolerance = UnitUtils.ConvertToInternalUnits(HostedToleranceMeters, UnitTypeId.Meters);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Each hosted instance must be an object.");
            var wallId = HostedReadId(item, "hostWallId");
            var wall = document.GetElement(new ElementId(wallId)) as Wall
                ?? throw new ArgumentException($"hostWallId {wallId} is not a wall in the current document.");
            var (line, wallBottom, wallTop) = HostedReadWall(document, wall);
            var symbolId = HostedReadId(item, "familySymbolId");
            var symbol = document.GetElement(new ElementId(symbolId)) as FamilySymbol
                ?? throw new ArgumentException($"familySymbolId {symbolId} is not a loaded family type.");
            var categoryId = symbol.Category?.Id.Value;
            if (categoryId != (long)BuiltInCategory.OST_Doors && categoryId != (long)BuiltInCategory.OST_Windows)
                throw new ArgumentException("Only loaded door and window families are supported by this tool.");
            if (symbol.Family.IsInPlace || symbol.Family.FamilyPlacementType != FamilyPlacementType.OneLevelBasedHosted)
                throw new ArgumentException($"'{symbol.FamilyName}: {symbol.Name}' must be a loadable OneLevelBasedHosted door or window family.");

            var level = HostedReadLevel(document, item);
            var (xMeters, yMeters) = ReadWallPoint(item, "position");
            var hasSill = item.TryGetProperty("sillHeightMeters", out _);
            var hasOffset = item.TryGetProperty("offsetMeters", out _);
            if (hasSill && hasOffset)
                throw new ArgumentException("Use either sillHeightMeters or offsetMeters, not both; both mean height above the selected level.");
            var sillMeters = hasSill ? HostedReadNumber(item, "sillHeightMeters", -100, 100)
                : hasOffset ? HostedReadNumber(item, "offsetMeters", -100, 100) : 0;
            var sill = UnitUtils.ConvertToInternalUnits(sillMeters, UnitTypeId.Meters);
            var position = new XYZ(UnitUtils.ConvertToInternalUnits(xMeters, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(yMeters, UnitTypeId.Meters), level.ProjectElevation + sill);
            var start = line.GetEndPoint(0);
            var direction = (line.GetEndPoint(1) - start).Normalize();
            var horizontalPoint = new XYZ(position.X, position.Y, start.Z);
            var distanceAlongWall = (horizontalPoint - start).DotProduct(direction);
            var projected = start + direction * distanceAlongWall;
            if (horizontalPoint.DistanceTo(projected) > tolerance)
                throw new ArgumentException($"Insertion point must be on wall {wallId}'s location line within 1 mm. Use its location-curve coordinates, not a side face.");
            if (distanceAlongWall <= tolerance || distanceAlongWall >= line.Length - tolerance)
                throw new ArgumentException($"Insertion point must lie inside the finite segment of wall {wallId}.");
            if (position.Z < wallBottom - tolerance || position.Z >= wallTop - tolerance)
                throw new ArgumentException($"The requested sill elevation is outside the height of wall {wallId}.");

            if (!symbol.IsActive)
            {
                symbol.Activate();
                document.Regenerate();
            }
            // Snap the submillimetre lateral difference explicitly; report the actual position below.
            var insertion = new XYZ(projected.X, projected.Y, position.Z);
            var instance = document.Create.NewFamilyInstance(insertion, symbol, wall, level, StructuralType.NonStructural)
                ?? throw new InvalidOperationException($"Revit could not place '{symbol.FamilyName}: {symbol.Name}' on wall {wallId}.");
            document.Regenerate();
            var sillParameter = instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            if (sillParameter is null || sillParameter.StorageType != StorageType.Double)
                throw new InvalidOperationException("This family has no standard sill-height parameter; its vertical placement is unsupported.");
            if (Math.Abs(sillParameter.AsDouble() - sill) > tolerance)
            {
                if (sillParameter.IsReadOnly || !sillParameter.Set(sill))
                    throw new InvalidOperationException("The family's sill height cannot be set to the requested value.");
            }
            var flipFacing = HostedReadBoolean(item, "flipFacing");
            var flipHand = HostedReadBoolean(item, "flipHand");
            if (flipFacing && (!instance.CanFlipFacing || !instance.flipFacing()))
                throw new InvalidOperationException("The selected family does not support the requested facing flip.");
            if (flipHand && (!instance.CanFlipHand || !instance.flipHand()))
                throw new InvalidOperationException("The selected family does not support the requested hand flip.");
            document.Regenerate();

            if (instance.Host?.Id != wall.Id || instance.LevelId != level.Id)
                throw new InvalidOperationException("Revit placed the family on a different host or level; the batch has been rejected.");
            if (instance.Location is not LocationPoint actualLocation)
                throw new InvalidOperationException("This family does not expose a point location and is unsupported.");
            var actualPosition = actualLocation.Point;
            if (actualPosition.DistanceTo(insertion) > tolerance || Math.Abs(sillParameter.AsDouble() - sill) > tolerance)
                throw new InvalidOperationException("The family's actual insertion point or sill differs from the request by more than 1 mm; the batch has been rejected.");
            var width = HostedReadDimension(instance, symbol, BuiltInParameter.FAMILY_WIDTH_PARAM, "width");
            var height = HostedReadDimension(instance, symbol, BuiltInParameter.FAMILY_HEIGHT_PARAM, "height");
            if (distanceAlongWall - width / 2 < -tolerance || distanceAlongWall + width / 2 > line.Length + tolerance)
                throw new ArgumentException($"The nominal family width extends beyond wall {wallId}'s endpoints.");
            if (actualPosition.Z + height > wallTop + tolerance)
                throw new ArgumentException($"The nominal family height extends above wall {wallId}.");

            results.Add(new
            {
                id = instance.Id.Value,
                hostWallId = wall.Id.Value,
                familySymbolId = symbol.Id.Value,
                family = symbol.FamilyName,
                type = symbol.Name,
                category = symbol.Category?.Name,
                levelId = level.Id.Value,
                levelName = level.Name,
                position = new
                {
                    xMeters = UnitUtils.ConvertFromInternalUnits(actualPosition.X, UnitTypeId.Meters),
                    yMeters = UnitUtils.ConvertFromInternalUnits(actualPosition.Y, UnitTypeId.Meters),
                    zMeters = UnitUtils.ConvertFromInternalUnits(actualPosition.Z, UnitTypeId.Meters)
                },
                sillHeightMeters = UnitUtils.ConvertFromInternalUnits(sillParameter.AsDouble(), UnitTypeId.Meters),
                widthMeters = UnitUtils.ConvertFromInternalUnits(width, UnitTypeId.Meters),
                heightMeters = UnitUtils.ConvertFromInternalUnits(height, UnitTypeId.Meters),
                facingFlipped = instance.FacingFlipped,
                handFlipped = instance.HandFlipped,
                facingDirection = new { x = instance.FacingOrientation.X, y = instance.FacingOrientation.Y, z = instance.FacingOrientation.Z },
                handDirection = new { x = instance.HandOrientation.X, y = instance.HandOrientation.Y, z = instance.HandOrientation.Z }
            });
        }
        return new
        {
            createdCount = results.Count,
            units = "meters",
            coordinateSystem = "internal origin; sill height relative to selected level",
            geometryValidation = "straight vertical wall, point, standard nominal width and height; no clash detection or custom opening-shape validation",
            instances = results
        };
    }

    private static (Line Line, double Bottom, double Top) HostedReadWall(Document document, Wall wall)
    {
        if (wall.WallType.Kind != WallKind.Basic || wall.IsStackedWallMember || wall.IsStackedWall
            || wall.CrossSection != WallCrossSection.Vertical || wall.SketchId != ElementId.InvalidElementId)
            throw new ArgumentException($"Wall {wall.Id.Value} must be a basic vertical wall without an edited profile; stacked, curtain and slanted walls are unsupported.");
        if (wall.Location is not LocationCurve location || location.Curve is not Line line
            || !line.IsBound || Math.Abs(line.GetEndPoint(0).Z - line.GetEndPoint(1).Z) > 1e-6)
            throw new ArgumentException($"Wall {wall.Id.Value} must have a straight horizontal location curve.");
        if (wall.get_Parameter(BuiltInParameter.WALL_BOTTOM_IS_ATTACHED)?.AsInteger() == 1
            || wall.get_Parameter(BuiltInParameter.WALL_TOP_IS_ATTACHED)?.AsInteger() == 1)
            throw new ArgumentException($"Wall {wall.Id.Value} has an attached top or base; its height is unsupported.");
        if (wall.GroupId != ElementId.InvalidElementId)
            throw new ArgumentException($"Wall {wall.Id.Value} belongs to a group; place hosted families outside this tool.");

        var baseLevel = document.GetElement(wall.LevelId) as Level
            ?? throw new ArgumentException($"Wall {wall.Id.Value} has no valid base level.");
        var bottom = baseLevel.ProjectElevation + (wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0);
        var topLevelId = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId();
        double top;
        if (topLevelId is not null && document.GetElement(topLevelId) is Level topLevel)
            top = topLevel.ProjectElevation + (wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.AsDouble() ?? 0);
        else
        {
            var height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0;
            top = bottom + height;
        }
        if (!double.IsFinite(bottom) || !double.IsFinite(top) || top <= bottom)
            throw new ArgumentException($"Wall {wall.Id.Value} has an invalid or unsupported vertical extent.");
        return (line, bottom, top);
    }

    private static Level HostedReadLevel(Document document, JsonElement item)
    {
        var hasId = item.TryGetProperty("levelId", out _);
        var hasName = item.TryGetProperty("levelName", out _);
        if (hasId == hasName)
            throw new ArgumentException("Each instance needs exactly one of levelId or levelName.");
        if (hasId)
            return document.GetElement(new ElementId(HostedReadId(item, "levelId"))) as Level
                ?? throw new ArgumentException("levelId does not identify a level in the current document.");
        var name = JsonTools.GetString(item, "levelName").Trim();
        if (name.Length is < 1 or > 100)
            throw new ArgumentException("levelName must contain between 1 and 100 characters.");
        var matches = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>()
            .Where(level => string.Equals(level.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0]
            : throw new ArgumentException($"Level '{name}' was not found or is ambiguous.");
    }

    private static double HostedReadDimension(FamilyInstance instance, FamilySymbol symbol, BuiltInParameter parameterId, string name)
    {
        // Revit may expose an empty built-in parameter on the instance even when
        // the family stores its dimensions on the symbol. Null-coalescing alone
        // incorrectly masks that valid type parameter.
        foreach (var parameter in new[] { instance.get_Parameter(parameterId), symbol.get_Parameter(parameterId) })
            if (parameter is not null && parameter.StorageType == StorageType.Double && parameter.HasValue
                && double.IsFinite(parameter.AsDouble()) && parameter.AsDouble() > 0)
                return parameter.AsDouble();
        throw new ArgumentException($"Family '{symbol.FamilyName}: {symbol.Name}' needs a positive standard {name} parameter on its instance or type; custom-size families are unsupported.");
    }

    private static long HostedReadId(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var id) || id <= 0 || id > 9007199254740991)
            throw new ArgumentException($"{name} must be a positive safe integer element id.");
        return id;
    }

    private static double HostedReadNumber(JsonElement args, string name, double min, double max)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number < min || number > max)
            throw new ArgumentException($"{name} must be a finite number between {min} and {max}.");
        return number;
    }

    private static bool HostedReadBoolean(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException($"{name} must be a boolean.");
        return value.GetBoolean();
    }
}
