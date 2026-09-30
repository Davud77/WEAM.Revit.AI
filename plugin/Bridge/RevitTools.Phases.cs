using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    private static object ListPhases(UIApplication app)
    {
        var doc = RequireDocument(app);
        return new { phases = doc.Phases.Cast<Phase>()
            .Select(x => new { id = x.Id.Value, name = x.Name }).ToArray() };
    }

    private static object PreviewSetViewPhase(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "setViewPhase", "Стадии видов", SetViewPhase);

    private static object ApplySetViewPhase(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "setViewPhase", "Изменить стадии видов?", SetViewPhase, cancelled);

    private static object SetViewPhase(Document doc, JsonElement args)
    {
        var phase = doc.GetElement(new ElementId(HostedReadId(args, "phaseId"))) as Phase
            ?? throw new ArgumentException("phaseId must identify a project phase.");
        var ids = args.GetProperty("viewIds");
        if (ids.GetArrayLength() is < 1 or > 50) throw new ArgumentException("Provide 1 to 50 views.");
        var changed = new List<object>();
        var seen = new HashSet<long>();
        foreach (var item in ids.EnumerateArray())
        {
            var id = item.GetInt64();
            if (!seen.Add(id)) throw new ArgumentException("Duplicate viewId.");
            var view = doc.GetElement(new ElementId(id)) as View;
            if (view is null || view.IsTemplate || view is ViewSheet || view is ViewSchedule)
                throw new ArgumentException($"View {id} cannot have a phase.");
            var parameter = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
            if (parameter is null || parameter.IsReadOnly || parameter.StorageType != StorageType.ElementId)
                throw new InvalidOperationException($"View '{view.Name}' has no writable phase parameter.");
            if (!parameter.Set(phase.Id)) throw new InvalidOperationException($"Cannot set phase for view '{view.Name}'.");
            changed.Add(new { viewId = id, name = view.Name });
        }
        doc.Regenerate();
        return new { phaseId = phase.Id.Value, phaseName = phase.Name, views = changed };
    }
}
