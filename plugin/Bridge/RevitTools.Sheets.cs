using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    private static double SheetUnits(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    private static double SheetMm(double value) => UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters);

    private static object ListSheetResources(UIApplication app)
    {
        var doc = RequireDocument(app);
        return new {
            titleBlocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType()
                .OfType<FamilySymbol>().Select(t => new { id=t.Id.Value, family=t.FamilyName, name=t.Name,
                    parameters=t.Parameters.Cast<Parameter>().Select(p => new { name=p.Definition.Name, value=p.AsValueString() ?? p.AsString(), storageType=p.StorageType.ToString(), readOnly=p.IsReadOnly }).ToArray() }).ToArray(),
            sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s=>!s.IsPlaceholder)
                .Select(s=>new { id=s.Id.Value, number=s.SheetNumber, name=s.Name, widthMillimeters=SheetMm(s.Outline.Max.U-s.Outline.Min.U), heightMillimeters=SheetMm(s.Outline.Max.V-s.Outline.Min.V),
                    viewports=s.GetAllViewports().Select(id=>{var v=(Viewport)doc.GetElement(id); var box=v.GetBoxOutline(); return new { id=id.Value, viewId=v.ViewId.Value, minXmm=SheetMm(box.MinimumPoint.X), minYmm=SheetMm(box.MinimumPoint.Y), maxXmm=SheetMm(box.MaximumPoint.X),maxYmm=SheetMm(box.MaximumPoint.Y) };}).ToArray() }).ToArray()
        };
    }

    private static object PreviewCreateSheets(UIApplication app, JsonElement args) =>
        PreviewOperation(app,args,"createSheets","Листы А3 и размещение видов",CreateSheets);
    private static object ApplyCreateSheets(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app,args,"createSheets","Создать листы А3?",CreateSheets,cancelled);

    private static object CreateSheets(Document doc, JsonElement args)
    {
        var typeId=HostedReadId(args,"titleBlockTypeId");
        if(doc.GetElement(new ElementId(typeId)) is not FamilySymbol titleType || titleType.Category?.Id.Value!=(long)BuiltInCategory.OST_TitleBlocks)
            throw new ArgumentException("titleBlockTypeId must be a loaded title block family type.");
        var requests=args.GetProperty("sheets");
        if(requests.GetArrayLength() is <1 or >10) throw new ArgumentException("Provide 1 to 10 sheets.");
        var existing=new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s=>s.SheetNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result=new List<object>();
        foreach(var request in requests.EnumerateArray())
        {
            var number=JsonTools.GetString(request,"number").Trim();
            var name=JsonTools.GetString(request,"name").Trim();
            if(string.IsNullOrWhiteSpace(number)||string.IsNullOrWhiteSpace(name)||!existing.Add(number)) throw new ArgumentException("Sheet number/name missing or number already exists.");
            var sheet=ViewSheet.Create(doc,titleType.Id);
            sheet.SheetNumber=number; sheet.Name=name;
            doc.Regenerate();
            var block=new FilteredElementCollector(doc,sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstElement();
            if(request.TryGetProperty("titleBlockParameters",out var parameters))
                foreach(var field in parameters.EnumerateObject())
                {
                    var matches=block.GetParameters(field.Name).Where(p=>!p.IsReadOnly).ToArray();
                    if(matches.Length!=1) throw new ArgumentException($"Title block instance parameter '{field.Name}' is missing, read-only or ambiguous.");
                    var p=matches[0]; var value=field.Value.GetString()!;
                    var success=p.StorageType==StorageType.String ? p.Set(value) : p.SetValueString(value);
                    if(!success) throw new ArgumentException($"Cannot set title block parameter '{field.Name}'.");
                }
            doc.Regenerate();
            var outline=sheet.Outline;
            var width=SheetMm(outline.Max.U-outline.Min.U); var height=SheetMm(outline.Max.V-outline.Min.V);
            if(Math.Abs(width-420)>1 || Math.Abs(height-297)>1)
                throw new ArgumentException($"Selected title block produces {width:F1} x {height:F1} mm; landscape A3 (420 x 297 mm) is required.");
            XYZ Position(double x,double y)=>new(outline.Min.U+SheetUnits(x),outline.Min.V+SheetUnits(y),0);
            var placed=new List<object>();
            foreach(var placement in request.GetProperty("views").EnumerateArray())
            {
                var sourceId=HostedReadId(placement,"sourceViewId");
                if(doc.GetElement(new ElementId(sourceId)) is not View source || source.IsTemplate || source is ViewSheet || source is ViewSchedule)
                    throw new ArgumentException("sourceViewId must identify a plan, elevation, section or 3D view.");
                var option=source is View3D ? ViewDuplicateOption.Duplicate : ViewDuplicateOption.WithDetailing;
                if(!source.CanViewBeDuplicated(option)) throw new ArgumentException($"View '{source.Name}' cannot be duplicated for a sheet.");
                var view=(View)doc.GetElement(source.Duplicate(option));
                view.Name=JsonTools.GetString(placement,"viewName");
                view.ViewTemplateId=ElementId.InvalidElementId;
                var scale=placement.GetProperty("scale").GetInt32();
                if(scale is <1 or >1000) throw new ArgumentException("View scale is out of range.");
                view.Scale=scale;
                view.CropBoxVisible=false;
                if(placement.TryGetProperty("crop",out var crop))
                {
                    var bounds=view is View3D v3 ? v3.GetSectionBox() : view.CropBox;
                    var inverse=bounds.Transform.Inverse;
                    var points=new List<XYZ>();
                    foreach(var x in new[]{crop.GetProperty("minX").GetDouble(),crop.GetProperty("maxX").GetDouble()})
                    foreach(var y in new[]{crop.GetProperty("minY").GetDouble(),crop.GetProperty("maxY").GetDouble()})
                    foreach(var z in new[]{crop.GetProperty("minZ").GetDouble(),crop.GetProperty("maxZ").GetDouble()})
                        points.Add(inverse.OfPoint(new XYZ(UnitUtils.ConvertToInternalUnits(x,UnitTypeId.Meters),UnitUtils.ConvertToInternalUnits(y,UnitTypeId.Meters),UnitUtils.ConvertToInternalUnits(z,UnitTypeId.Meters))));
                    bounds.Min=new XYZ(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z));
                    bounds.Max=new XYZ(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z));
                    if(view is View3D three){three.SetSectionBox(bounds);three.IsSectionBoxActive=true;}
                    else {view.CropBox=bounds;view.CropBoxActive=true;}
                }
                if(!Viewport.CanAddViewToSheet(doc,sheet.Id,view.Id)) throw new InvalidOperationException("The duplicate view cannot be placed on this sheet.");
                var center=Position(placement.GetProperty("centerXmm").GetDouble(),placement.GetProperty("centerYmm").GetDouble());
                var viewport=Viewport.Create(doc,sheet.Id,view.Id,center);
                doc.Regenerate(); viewport.SetBoxCenter(center); doc.Regenerate();
                var box=viewport.GetBoxOutline();
                if(box.MinimumPoint.X < outline.Min.U+SheetUnits(20) || box.MaximumPoint.X > outline.Max.U-SheetUnits(5)
                    || box.MinimumPoint.Y < outline.Min.V+SheetUnits(5) || box.MaximumPoint.Y > outline.Max.V-SheetUnits(5))
                    throw new InvalidOperationException($"View '{view.Name}' exceeds the A3 frame at scale 1:{scale}; adjust placement/scale.");
                placed.Add(new {sourceViewId=sourceId,viewId=view.Id.Value,viewportId=viewport.Id.Value,viewName=view.Name,scale,
                    minXmm=SheetMm(box.MinimumPoint.X-outline.Min.U),minYmm=SheetMm(box.MinimumPoint.Y-outline.Min.V),maxXmm=SheetMm(box.MaximumPoint.X-outline.Min.U),maxYmm=SheetMm(box.MaximumPoint.Y-outline.Min.V)});
            }
            long? scheduleId=null;
            if(request.TryGetProperty("roomSchedule",out var scheduleArgs))
            {
                var schedule=CreateSheetRoomSchedule(doc,scheduleArgs);
                ScheduleSheetInstance.Create(doc,sheet.Id,schedule.Id,Position(scheduleArgs.GetProperty("xMm").GetDouble(),scheduleArgs.GetProperty("yMm").GetDouble()));
                scheduleId=schedule.Id.Value;
            }
            result.Add(new {sheetId=sheet.Id.Value,number,name,titleBlockId=block.Id.Value,widthMillimeters=width,heightMillimeters=height,views=placed,roomScheduleId=scheduleId});
        }
        doc.Regenerate();
        return new {count=result.Count,sheets=result};
    }

    private static ViewSchedule CreateSheetRoomSchedule(Document doc,JsonElement args)
    {
        var schedule=ViewSchedule.CreateSchedule(doc,new ElementId(BuiltInCategory.OST_Rooms));
        schedule.Name=JsonTools.GetString(args,"name");
        var definition=schedule.Definition;
        var available=definition.GetSchedulableFields();
        ScheduleField Field(BuiltInParameter parameter,string heading,double width)
        {
            var candidate=available.FirstOrDefault(f=>f.ParameterId.Value==(long)parameter) ?? throw new ArgumentException($"Room schedule field {parameter} unavailable.");
            var field=definition.AddField(candidate); field.ColumnHeading=heading; field.GridColumnWidth=SheetUnits(width); return field;
        }
        var number=Field(BuiltInParameter.ROOM_NUMBER,"Номер\nпомещения",20);
        Field(BuiltInParameter.ROOM_NAME,"Наименование",65);
        var area=Field(BuiltInParameter.ROOM_AREA,"Площадь, м²",25);
        var format=new FormatOptions(UnitTypeId.SquareMeters){UseDefault=false,Accuracy=0.01};
        area.SetFormatOptions(format); area.DisplayType=ScheduleFieldDisplayType.Totals;
        var level=Field(BuiltInParameter.ROOM_LEVEL_ID,"Уровень",15); level.IsHidden=true;
        definition.AddFilter(new ScheduleFilter(level.FieldId,ScheduleFilterType.Equal,new ElementId(HostedReadId(args,"levelId"))));
        definition.AddFilter(new ScheduleFilter(area.FieldId,ScheduleFilterType.GreaterThan,0.0));
        definition.AddSortGroupField(new ScheduleSortGroupField(number.FieldId));
        definition.IsItemized=true; definition.ShowGrandTotal=true; definition.ShowGrandTotalTitle=true; definition.GrandTotalTitle="Итого";
        return schedule;
    }

    private static object ExportSheetsPdf(UIApplication app,JsonElement args)
    {
        var doc=RequireDocument(app);
        var path=AuthoringFilePath(args,"path",".pdf",false);
        if(File.Exists(path)) throw new ArgumentException("PDF already exists; choose a new filename.");
        var ids=args.GetProperty("sheetIds").EnumerateArray().Select(v=>new ElementId(v.GetInt64())).ToList();
        if(ids.Count is <1 or >20 || ids.Distinct().Count()!=ids.Count || ids.Any(id=>doc.GetElement(id) is not ViewSheet sheet || sheet.IsPlaceholder))
            throw new ArgumentException("Provide 1 to 20 distinct sheet IDs.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var options=new PDFExportOptions {Combine=true,FileName=Path.GetFileNameWithoutExtension(path),PaperFormat=ExportPaperFormat.Default,
            StopOnError=true,HideCropBoundaries=true,HideScopeBoxes=true,HideReferencePlane=true,HideUnreferencedViewTags=true};
        options.SetExportInBackground(false);
        var success=doc.Export(Path.GetDirectoryName(path)!,ids,options);
        if(!success || !File.Exists(path)) throw new InvalidOperationException("Revit did not produce the PDF.");
        return new {exported=true,filePath=path,sheetIds=ids.Select(id=>id.Value).ToArray(),bytes=new FileInfo(path).Length};
    }
}
