using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Globalization;
using System.Text.Json;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    private static object PreviewElementGraphics(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "element_graphics", "Изменение видимости и графики элементов", GraphicsExecuteElements);

    private static object ApplyElementGraphics(UIApplication app, JsonElement args, Func<bool>? isCancelled) =>
        ApplyOperation(app, args, "element_graphics", "Изменение видимости и графики элементов", GraphicsExecuteElements, isCancelled);

    private static object PreviewColorByParameter(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "color_by_parameter", "Раскраска элементов по параметру", GraphicsExecuteColorByParameter);

    private static object ApplyColorByParameter(UIApplication app, JsonElement args, Func<bool>? isCancelled) =>
        ApplyOperation(app, args, "color_by_parameter", "Раскраска элементов по параметру", GraphicsExecuteColorByParameter, isCancelled);

    private static object GraphicsExecuteElements(Document document, JsonElement args)
    {
        var view = GraphicsRequireView(document, args);
        var action = JsonTools.GetString(args, "action");
        if (action is not ("set_color" or "set_transparency" or "reset_overrides" or "hide" or "unhide"
            or "temporary_hide" or "temporary_isolate" or "reset_temporary"))
            throw new ArgumentException("Unsupported graphics action.");

        var ids = GraphicsReadIds(args, action == "reset_temporary");
        var elements = ids.Select(id => GraphicsRequireElement(document, view, id)).ToList();
        var temporary = action is "temporary_hide" or "temporary_isolate" or "reset_temporary";
        if (temporary && !view.CanUseTemporaryVisibilityModes())
            throw new ArgumentException("This view does not support temporary visibility modes.");
        if (!temporary && !view.AreGraphicsOverridesAllowed())
            throw new ArgumentException("This view does not support graphics overrides.");
        if (action == "reset_temporary" && ids.Count > 0)
            throw new ArgumentException("reset_temporary acts on the entire view; omit elementIds.");
        if (action != "set_color" && args.TryGetProperty("color", out _))
            throw new ArgumentException("color is valid only for set_color.");
        if (action is not ("set_color" or "set_transparency") && args.TryGetProperty("transparency", out _))
            throw new ArgumentException("transparency is valid only for set_color or set_transparency.");
        var requestedColor = action == "set_color" ? GraphicsReadColor(args.GetProperty("color")) : null;
        var requestedTransparency = GraphicsReadTransparency(args, action == "set_transparency");

        var changed = new List<ElementId>();
        var skipped = new List<ElementId>();
        switch (action)
        {
            case "set_color":
            case "set_transparency":
            case "reset_overrides":
                var fillId = requestedColor is null ? ElementId.InvalidElementId : GraphicsSolidFill(document);
                foreach (var element in elements)
                {
                    var settings = action == "reset_overrides" ? new OverrideGraphicSettings() : view.GetElementOverrides(element.Id);
                    if (requestedColor is not null) GraphicsSetColor(settings, requestedColor, fillId);
                    if (requestedTransparency is not null) settings.SetSurfaceTransparency(requestedTransparency.Value);
                    view.SetElementOverrides(element.Id, settings);
                    changed.Add(element.Id);
                }
                break;
            case "hide":
            case "unhide":
                foreach (var element in elements)
                {
                    var hidden = element.IsHidden(view);
                    if (hidden == (action == "hide")) { skipped.Add(element.Id); continue; }
                    if (action == "hide" && !element.CanBeHidden(view))
                        throw new ArgumentException($"Element {element.Id.Value} cannot be hidden in view {view.Id.Value}.");
                    changed.Add(element.Id);
                }
                if (changed.Count > 0)
                {
                    if (action == "hide") view.HideElements(changed);
                    else view.UnhideElements(changed);
                }
                break;
            case "temporary_hide":
            case "temporary_isolate":
                // Revit does not automatically include group members in temporary visibility.
                // Reject groups rather than claiming the whole group has been hidden or isolated.
                if (elements.Any(element => element is Group))
                    throw new ArgumentException("Temporary visibility requires explicit member IDs instead of group IDs.");
                if (action == "temporary_isolate")
                {
                    view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                    view.IsolateElementsTemporary(ids);
                }
                else view.HideElementsTemporary(ids);
                changed.AddRange(ids);
                break;
            case "reset_temporary":
                view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                break;
        }

        return new
        {
            viewId = view.Id.Value,
            viewName = view.Name,
            action,
            color = requestedColor is null ? null : new { r = (int)requestedColor.Red, g = (int)requestedColor.Green, b = (int)requestedColor.Blue },
            transparency = requestedTransparency,
            requestedCount = ids.Count,
            changedCount = changed.Count,
            elementIds = changed.Select(id => id.Value).ToArray(),
            alreadyInRequestedStateIds = skipped.Select(id => id.Value).ToArray(),
            affectsEntireView = action is "temporary_isolate" or "reset_temporary",
            note = action switch
            {
                "unhide" => "Only explicit element hiding is removed. Category, filter, crop, phase, workset and temporary visibility are unchanged.",
                "reset_overrides" => "All per-element graphics overrides are cleared for the listed elements in this view.",
                "temporary_isolate" => "Replaces existing temporary hide/isolate state. Permanent visibility restrictions still apply.",
                "reset_temporary" => "Resets temporary hide/isolate for the entire view. Permanent hiding is unchanged.",
                "set_color" => "Sets projection/cut line colors and solid foreground surface/cut fills; preserves other overrides.",
                _ => "Operation is limited to this document and the explicitly specified view."
            }
        };
    }

    private static object GraphicsExecuteColorByParameter(Document document, JsonElement args)
    {
        var view = GraphicsRequireView(document, args);
        if (!view.AreGraphicsOverridesAllowed() || !FilteredElementCollector.IsViewValidForElementIteration(document, view.Id))
            throw new ArgumentException("This view cannot be used to collect and color elements.");
        var category = GraphicsResolveCategory(document, JsonTools.GetString(args, "category"));
        var parameterName = JsonTools.GetString(args, "parameterName").Trim();
        if (parameterName.Length is < 1 or > 200) throw new ArgumentException("parameterName must contain 1 to 200 characters.");
        var parameterScope = JsonTools.GetString(args, "parameterScope", "instance");
        if (parameterScope is not ("instance" or "type")) throw new ArgumentException("parameterScope must be instance or type.");
        var mode = JsonTools.GetString(args, "mode", "categorical");
        if (mode is not ("categorical" or "gradient")) throw new ArgumentException("mode must be categorical or gradient.");
        var maxElements = JsonTools.GetInt(args, "maxElements", 1000);
        if (maxElements is < 1 or > 1000) throw new ArgumentException("maxElements must be between 1 and 1000.");
        var includeMissing = !args.TryGetProperty("includeMissing", out var missingOption) || missingOption.ValueKind == JsonValueKind.True;
        if (args.TryGetProperty("includeMissing", out missingOption) && missingOption.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("includeMissing must be a boolean.");
        var transparency = GraphicsReadTransparency(args, false);
        var palette = GraphicsReadPalette(args, mode);
        var elements = new FilteredElementCollector(document, view.Id).WhereElementIsNotElementType()
            .Where(element => element.Category?.Id == category.Id && !element.IsHidden(view))
            .OrderBy(element => element.Id.Value).Take(maxElements + 1).ToList();
        if (elements.Count > maxElements)
            throw new ArgumentException($"Category has more than maxElements={maxElements} elements in the view. Narrow the view/category; no partial recoloring is performed.");
        if (elements.Count == 0) throw new ArgumentException("No eligible elements of this category were found in the specified view.");

        var grouped = new Dictionary<string, GraphicsParameterGroup>(StringComparer.Ordinal);
        var skippedIds = new List<long>();
        foreach (var element in elements)
        {
            GraphicsRequireElement(document, view, element.Id);
            var owner = parameterScope == "type" ? document.GetElement(element.GetTypeId()) : element;
            var parameters = owner?.GetParameters(parameterName);
            if (parameters?.Count > 1)
                throw new ArgumentException($"Parameter name '{parameterName}' is ambiguous on element {element.Id.Value}. Use a unique parameter name.");
            var parameter = parameters?.SingleOrDefault();
            var value = GraphicsParameterValue(parameter, document);
            if (value.Missing && !includeMissing) { skippedIds.Add(element.Id.Value); continue; }
            if (mode == "gradient" && !value.Missing && value.Numeric is null)
                throw new ArgumentException("Gradient mode requires a numeric Integer or Double parameter; use categorical for text and element references.");
            if (!grouped.TryGetValue(value.Key, out var group))
            {
                group = new GraphicsParameterGroup(value.Key, value.Label, value.Numeric, value.Missing);
                grouped.Add(value.Key, group);
            }
            group.ElementIds.Add(element.Id);
        }
        if (grouped.Count == 0) throw new ArgumentException("No elements have an eligible parameter value. Enable includeMissing to include absent/unset values.");
        if (grouped.Count > 100) throw new ArgumentException("More than 100 distinct parameter values. Narrow the category/view before coloring.");
        var groups = mode == "gradient"
            ? grouped.Values.OrderBy(group => group.Missing).ThenBy(group => group.Numeric).ThenBy(group => group.Key, StringComparer.Ordinal).ToList()
            : grouped.Values.OrderBy(group => group.Missing).ThenBy(group => group.Key, StringComparer.Ordinal).ToList();
        var numericValues = groups.Where(group => group.Numeric.HasValue).Select(group => group.Numeric!.Value).ToArray();
        var minimum = numericValues.Length > 0 ? numericValues.Min() : 0;
        var maximum = numericValues.Length > 0 ? numericValues.Max() : 0;
        var solidFill = GraphicsSolidFill(document);
        var rows = new List<object>();
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var color = group.Missing ? new Color(160, 160, 160)
                : mode == "gradient" ? GraphicsInterpolate(palette, maximum > minimum ? (group.Numeric!.Value - minimum) / (maximum - minimum) : 0.5)
                : palette[i % palette.Count];
            foreach (var id in group.ElementIds)
            {
                var settings = view.GetElementOverrides(id);
                GraphicsSetColor(settings, color, solidFill);
                if (transparency is not null) settings.SetSurfaceTransparency(transparency.Value);
                view.SetElementOverrides(id, settings);
            }
            rows.Add(new
            {
                key = group.Key,
                value = group.Label,
                numericValueInternalUnits = group.Numeric,
                missing = group.Missing,
                color = new { r = (int)color.Red, g = (int)color.Green, b = (int)color.Blue },
                count = group.ElementIds.Count,
                elementIds = group.ElementIds.Select(id => id.Value).ToArray()
            });
        }
        return new
        {
            viewId = view.Id.Value,
            viewName = view.Name,
            category = category.Name,
            categoryId = category.Id.Value,
            parameterName,
            parameterScope,
            mode,
            matchedCount = elements.Count,
            changedCount = groups.Sum(group => group.ElementIds.Count),
            skippedMissingParameterIds = skippedIds,
            groupCount = groups.Count,
            groups = rows,
            paletteRepeats = mode == "categorical" && groups.Count(group => !group.Missing) > palette.Count,
            note = "Only instances returned by the specified view collector are colored; explicitly hidden elements are excluded. Revit view collectors may include obscured elements. Numeric grouping/gradient uses exact internal values; displayed labels use project units. Missing/unset values use gray. Existing unrelated overrides are preserved."
        };
    }

    private static View GraphicsRequireView(Document document, JsonElement args)
    {
        if (document.IsFamilyDocument) throw new ArgumentException("Graphics tools require a project document.");
        if (!args.TryGetProperty("viewId", out var value) || !value.TryGetInt64(out var id) || id < 1 || id > 9007199254740991)
            throw new ArgumentException("A positive, explicit viewId is required.");
        var view = document.GetElement(new ElementId(id)) as View ?? throw new ArgumentException($"View {id} does not exist in this document.");
        if (view.IsTemplate) throw new ArgumentException("View templates cannot be targeted by these tools.");
        return view;
    }

    private static List<ElementId> GraphicsReadIds(JsonElement args, bool allowEmpty)
    {
        if (!args.TryGetProperty("elementIds", out var raw))
        {
            if (allowEmpty) return [];
            throw new ArgumentException("elementIds is required.");
        }
        if (raw.ValueKind != JsonValueKind.Array) throw new ArgumentException("elementIds must be an array.");
        var ids = new List<ElementId>();
        foreach (var item in raw.EnumerateArray())
        {
            if (!item.TryGetInt64(out var id) || id < 1 || id > 9007199254740991) throw new ArgumentException("All elementIds must be positive safe integers.");
            ids.Add(new ElementId(id));
        }
        if (ids.Count > 1000 || (!allowEmpty && ids.Count == 0)) throw new ArgumentException("Supply 1 to 1000 elementIds.");
        if (ids.Distinct().Count() != ids.Count) throw new ArgumentException("elementIds must not contain duplicates.");
        return ids;
    }

    private static Element GraphicsRequireElement(Document document, View view, ElementId id)
    {
        var element = document.GetElement(id) ?? throw new ArgumentException($"Element {id.Value} no longer exists.");
        if (element is ElementType or View || element.Category is null
            || element.Category.CategoryType is not (CategoryType.Model or CategoryType.Annotation))
            throw new ArgumentException($"Element {id.Value} is not a supported graphical instance.");
        if (element.ViewSpecific && element.OwnerViewId != view.Id)
            throw new ArgumentException($"Element {id.Value} belongs to a different view.");
        return element;
    }

    private static int? GraphicsReadTransparency(JsonElement args, bool required)
    {
        if (!args.TryGetProperty("transparency", out var value))
        {
            if (required) throw new ArgumentException("transparency is required for set_transparency.");
            return null;
        }
        if (!value.TryGetInt32(out var result) || result is < 0 or > 100)
            throw new ArgumentException("transparency must be an integer between 0 and 100.");
        return result;
    }

    private static Color GraphicsReadColor(JsonElement value)
    {
        byte Channel(string name)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var component)
                || !component.TryGetInt32(out var number) || number is < 0 or > 255)
                throw new ArgumentException("Color must contain integer r, g, b channels between 0 and 255.");
            return (byte)number;
        }
        return new Color(Channel("r"), Channel("g"), Channel("b"));
    }

    private static List<Color> GraphicsReadPalette(JsonElement args, string mode)
    {
        if (!args.TryGetProperty("palette", out var value))
        {
            if (mode == "gradient") return [new(45, 112, 180), new(239, 204, 84), new(196, 72, 100)];
            return [new(45, 112, 180), new(235, 128, 45), new(67, 155, 100), new(196, 72, 100), new(134, 105, 183), new(67, 165, 177), new(180, 148, 57), new(174, 102, 68), new(214, 119, 172), new(93, 118, 122), new(100, 151, 218), new(171, 183, 75)];
        }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 2 or > 100)
            throw new ArgumentException("palette must contain 2 to 100 RGB colors.");
        return value.EnumerateArray().Select(GraphicsReadColor).ToList();
    }

    private static Color GraphicsInterpolate(List<Color> palette, double fraction)
    {
        var scaled = Math.Clamp(fraction, 0, 1) * (palette.Count - 1);
        var index = Math.Min((int)Math.Floor(scaled), palette.Count - 2);
        var t = scaled - index;
        byte Blend(byte a, byte b) => (byte)Math.Clamp((int)Math.Round(a + (b - a) * t), 0, 255);
        return new Color(Blend(palette[index].Red, palette[index + 1].Red), Blend(palette[index].Green, palette[index + 1].Green), Blend(palette[index].Blue, palette[index + 1].Blue));
    }

    private static ElementId GraphicsSolidFill(Document document)
    {
        var fill = new FilteredElementCollector(document).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
            .FirstOrDefault(element => element.GetFillPattern().IsSolidFill && element.GetFillPattern().Target == FillPatternTarget.Drafting);
        return fill?.Id ?? throw new InvalidOperationException("No solid drafting fill pattern is available in this document.");
    }

    private static void GraphicsSetColor(OverrideGraphicSettings settings, Color color, ElementId fillId)
    {
        settings.SetProjectionLineColor(color);
        settings.SetCutLineColor(color);
        settings.SetSurfaceForegroundPatternId(fillId);
        settings.SetSurfaceForegroundPatternColor(color);
        settings.SetSurfaceForegroundPatternVisible(true);
        settings.SetCutForegroundPatternId(fillId);
        settings.SetCutForegroundPatternColor(color);
        settings.SetCutForegroundPatternVisible(true);
    }

    private static Category GraphicsResolveCategory(Document document, string name)
    {
        if (name.Length is < 1 or > 200) throw new ArgumentException("category must be an exact category name or built-in name such as OST_Walls.");
        if (name.StartsWith("OST_", StringComparison.Ordinal) && Enum.TryParse<BuiltInCategory>(name, out var builtIn))
            return Category.GetCategory(document, builtIn) ?? throw new ArgumentException($"Category '{name}' is unavailable.");
        var matches = document.Settings.Categories.Cast<Category>().Where(category => string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : throw new ArgumentException($"Category '{name}' is missing or ambiguous. Use an exact built-in name such as OST_Walls.");
    }

    private static (string Key, string Label, double? Numeric, bool Missing) GraphicsParameterValue(Parameter? parameter, Document document)
    {
        if (parameter is null || !parameter.HasValue || parameter.StorageType == StorageType.None)
            return ("missing", "(parameter missing or unset)", null, true);
        switch (parameter.StorageType)
        {
            case StorageType.String:
                var text = parameter.AsString() ?? "";
                return ("string:" + text, text, null, false);
            case StorageType.Integer:
                var integer = parameter.AsInteger();
                return ("integer:" + integer.ToString(CultureInfo.InvariantCulture), parameter.AsValueString() ?? integer.ToString(CultureInfo.InvariantCulture), integer, false);
            case StorageType.Double:
                var number = parameter.AsDouble();
                if (!double.IsFinite(number)) throw new ArgumentException("Parameter contains a non-finite numeric value.");
                return ("double:" + number.ToString("R", CultureInfo.InvariantCulture), parameter.AsValueString() ?? number.ToString("G17", CultureInfo.InvariantCulture), number, false);
            case StorageType.ElementId:
                var id = parameter.AsElementId();
                return ("elementId:" + id.Value.ToString(CultureInfo.InvariantCulture), document.GetElement(id)?.Name ?? parameter.AsValueString() ?? id.Value.ToString(CultureInfo.InvariantCulture), null, false);
            default:
                throw new ArgumentException("Unsupported parameter storage type.");
        }
    }

    private sealed class GraphicsParameterGroup(string key, string label, double? numeric, bool missing)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        public double? Numeric { get; } = numeric;
        public bool Missing { get; } = missing;
        public List<ElementId> ElementIds { get; } = [];
    }
}
