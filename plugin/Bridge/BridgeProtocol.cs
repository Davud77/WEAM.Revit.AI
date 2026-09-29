using System.Text.Json;
using System.Text.Json.Serialization;

namespace WEAM.Revit.AI;

internal sealed class BridgeRequest
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("token")] public string Token { get; init; } = "";
    [JsonPropertyName("tool")] public string Tool { get; init; } = "";
    [JsonPropertyName("arguments")] public JsonElement Arguments { get; init; }
}

internal sealed class BridgeResponse
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("result")] public object? Result { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }

    public static BridgeResponse Success(string id, object result) => new() { Id = id, Ok = true, Result = result };
    public static BridgeResponse Failure(string id, string error) => new() { Id = id, Ok = false, Error = error };
}

internal sealed class PendingRequest(BridgeRequest request)
{
    public BridgeRequest Request { get; } = request;
    public ManualResetEventSlim Completed { get; } = new(false);
    public BridgeResponse? Response { get; set; }
    public volatile bool Cancelled;
}

internal sealed class ParameterPlan
{
    public string Id { get; init; } = "";
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public string Kind { get; init; } = "";
    public List<long> ElementIds { get; init; } = [];
    public string ParameterName { get; init; } = "";
    public string Value { get; init; } = "";
    public Dictionary<long, string> OriginalValues { get; init; } = [];
    public List<LevelPlanItem> Levels { get; init; } = [];
    public List<WallPlanItem> Walls { get; init; } = [];
    public List<FamilyInstancePlanItem> FamilyInstances { get; init; } = [];
    public List<FloorPlanItem> Floors { get; init; } = [];
    public List<GridPlanItem> Grids { get; init; } = [];
    public List<BeamPlanItem> Beams { get; init; } = [];
    public List<CeilingPlanItem> Ceilings { get; init; } = [];
    public List<RoomPlanItem> Rooms { get; init; } = [];
    public List<PlanViewPlanItem> PlanViews { get; init; } = [];
    public ViewVisibilityPlanItem? Visibility { get; init; }
    public Autodesk.Revit.DB.Document? OwnerDocument { get; init; }
    public long DocumentRevision { get; init; }
}

internal sealed record LevelPlanItem(string Name, double ElevationMeters);
internal sealed record WallPlanItem(
    double StartXMeters,
    double StartYMeters,
    double EndXMeters,
    double EndYMeters,
    string BaseLevelName,
    long BaseLevelId,
    string WallTypeName,
    long WallTypeId,
    double HeightMeters,
    double BaseOffsetMeters,
    bool Flip,
    bool Structural);
internal sealed record FamilyInstancePlanItem(
    long FamilySymbolId,
    string FamilyName,
    string TypeName,
    long LevelId,
    string LevelName,
    double XMeters,
    double YMeters,
    double OffsetMeters,
    double RotationDegrees,
    Autodesk.Revit.DB.Structure.StructuralType StructuralType);
internal sealed record FloorPlanItem(
    long FloorTypeId,
    string FloorTypeName,
    long LevelId,
    string LevelName,
    List<PlanPoint2D> Vertices,
    double AreaSquareMeters,
    double HeightOffsetMeters,
    bool Structural);
internal sealed record PlanPoint2D(double X, double Y);
internal sealed record GridPlanItem(string Name, double StartXMeters, double StartYMeters, double EndXMeters, double EndYMeters, double ElevationMeters);
internal sealed record PlanPoint3D(double X, double Y, double Z);
internal sealed record BeamPlanItem(long FamilySymbolId, string FamilyName, string TypeName, long LevelId, string LevelName, PlanPoint3D Start, PlanPoint3D End);
internal sealed record CeilingPlanItem(long CeilingTypeId, string CeilingTypeName, long LevelId, string LevelName, List<PlanPoint2D> Vertices, double AreaSquareMeters, double HeightOffsetMeters);
internal sealed record RoomPlanItem(long LevelId, string LevelName, double XMeters, double YMeters, string Number, string Name);
internal sealed record PlanViewPlanItem(long ViewFamilyTypeId, string ViewTypeName, string ViewFamily, long LevelId, string LevelName, string ViewName);
internal sealed record ViewVisibilityPlanItem(long ViewId, string ViewName, long CategoryId, string CategoryName, bool WasHidden, bool Hidden);

internal static class JsonTools
{
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);

    public static string GetString(JsonElement args, string name, string? fallback = null)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? fallback ?? "";
        return fallback ?? "";
    }

    public static int GetInt(JsonElement args, string name, int fallback)
    {
        return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number : fallback;
    }

    public static List<long> GetIds(JsonElement args, string name = "elementIds")
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray().Where(item => item.TryGetInt64(out _)).Select(item => item.GetInt64()).Distinct().ToList();
    }
}
