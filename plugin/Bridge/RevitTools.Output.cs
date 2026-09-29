using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    private static object ExportViewImage(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var viewId = HostedReadId(args, "viewId");
        if (doc.GetElement(new ElementId(viewId)) is not View view || view.IsTemplate || !view.CanBePrinted)
            throw new ArgumentException("viewId must identify a printable non-template view.");
        var directory = JsonTools.GetString(args, "directory");
        if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal) || directory.Length > 170)
            throw new ArgumentException("Use a fully qualified local directory, at most 170 characters.");
        var pixelSize = args.TryGetProperty("pixelSize", out var pixels) ? pixels.GetInt32() : 2000;
        if (pixelSize is < 500 or > 4000) throw new ArgumentException("pixelSize must be between 500 and 4000.");
        var outputDirectory = Path.Combine(Path.GetFullPath(directory), "view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        using var options = new ImageExportOptions
        {
            FilePath = Path.Combine(outputDirectory, "revit"),
            ExportRange = ExportRange.SetOfViews,
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
            ImageResolution = ImageResolution.DPI_150,
            ZoomType = ZoomFitType.FitToPage,
            PixelSize = pixelSize,
            ShouldCreateWebSite = false
        };
        options.SetViewsAndSheets(new List<ElementId> { view.Id });
        doc.ExportImage(options);
        var files = Directory.GetFiles(outputDirectory, "*.png");
        if (files.Length == 0) throw new InvalidOperationException("Revit returned without generating a PNG image.");
        return new { viewId, viewName = view.Name, files, modelChanged = false };
    }

    private static object PreviewSaveProject(UIApplication app)
    {
        var doc = RequireDocument(app);
        RequireWritableProject(doc);
        if (doc.IsWorkshared || string.IsNullOrEmpty(doc.PathName))
            throw new ArgumentException("Save requires a standalone project with an existing file path.");
        PrunePlans();
        var id = Guid.NewGuid().ToString("N");
        Plans[id] = new ParameterPlan { Id = id, Kind = "saveCurrentProject", OwnerDocument = doc,
            DocumentRevision = DocumentRevision(doc), Value = doc.PathName };
        return new { planCreated = true, planId = id, expiresInMinutes = 10, destination = doc.PathName, isModified = doc.IsModified };
    }

    private static object ApplySaveProject(UIApplication app, JsonElement args, Func<bool>? cancelled)
    {
        var doc = RequireDocument(app);
        RequireWritableProject(doc);
        var plan = GetPlan(JsonTools.GetString(args, "planId"), "saveCurrentProject");
        ValidateLegacyPlan(plan, doc);
        if (doc.IsWorkshared || !string.Equals(doc.PathName, plan.Value, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The project path or worksharing status changed after preview.");
        var dialog = new TaskDialog("WEAM.Revit.AI") { MainInstruction = "Сохранить текущий проект?", MainContent = doc.PathName,
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No };
        if (cancelled?.Invoke() == true || !ConfirmOperation(dialog) || cancelled?.Invoke() == true)
            return new { applied = false, cancelled = true };
        ValidateLegacyPlan(plan, doc);
        doc.Save();
        Plans.TryRemove(plan.Id, out _);
        return new { applied = true, filePath = doc.PathName, saved = !doc.IsModified };
    }
}
