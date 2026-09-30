using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    private static double BuildingNumber(JsonElement a,string key,double min,double max)
    {
        var v=a.GetProperty(key).GetDouble();
        if(!double.IsFinite(v)||v<min||v>max) throw new ArgumentException($"{key} must be between {min} and {max}.");
        return v;
    }
    private static double BuildingFeet(double m)=>UnitUtils.ConvertToInternalUnits(m,UnitTypeId.Meters);
    private static object ListRoofTypes(UIApplication app)=>new {
        types=new FilteredElementCollector(RequireDocument(app)).OfClass(typeof(RoofType)).Cast<RoofType>()
            .Select(t=>new {id=t.Id.Value,name=t.Name,thicknessMeters=UnitUtils.ConvertFromInternalUnits(t.GetCompoundStructure()?.GetWidth()??0,UnitTypeId.Meters)}).ToArray()
    };
    private static object ActivateView(UIApplication app,JsonElement args)
    {
        var doc=RequireDocument(app);
        var view=doc.GetElement(new ElementId(HostedReadId(args,"viewId"))) as View;
        if(view is null || view.IsTemplate) throw new ArgumentException("viewId must be an existing non-template view.");
        app.ActiveUIDocument!.ActiveView=view;
        return new {viewId=view.Id.Value,name=view.Name};
    }
    private static object PreviewCreateRoof(UIApplication app,JsonElement args)=>PreviewOperation(app,args,"createRoof","Прямоугольная двускатная кровля",CreateRoof);
    private static object ApplyCreateRoof(UIApplication app,JsonElement args,Func<bool>? cancelled)=>ApplyOperation(app,args,"createRoof","Создать кровлю и присоединить выбранные стены?",CreateRoof,cancelled);
    private static object CreateRoof(Document doc,JsonElement args)
    {
        var type=doc.GetElement(new ElementId(HostedReadId(args,"roofTypeId"))) as RoofType ?? throw new ArgumentException("roofTypeId must identify a roof type.");
        var level=doc.GetElement(new ElementId(HostedReadId(args,"levelId"))) as Level ?? throw new ArgumentException("levelId must identify a level.");
        var x0=BuildingNumber(args,"minX",-100000,100000);var x1=BuildingNumber(args,"maxX",-100000,100000);
        var y0=BuildingNumber(args,"minY",-100000,100000);var y1=BuildingNumber(args,"maxY",-100000,100000);
        if(x1-x0<0.5 || y1-y0<0.5 || x1-x0>100 || y1-y0>100) throw new ArgumentException("Roof rectangle must have sides from 0.5 to 100 m.");
        var slope=BuildingNumber(args,"slopeDegrees",1,70)*Math.PI/180;
        var offset=BuildingNumber(args,"offsetMeters",-100,100);
        var axis=JsonTools.GetString(args,"ridgeAxis");
        if(axis is not ("x" or "y")) throw new ArgumentException("ridgeAxis must be x or y.");
        var vertices=new[]{new XYZ(BuildingFeet(x0),BuildingFeet(y0),level.Elevation),new XYZ(BuildingFeet(x1),BuildingFeet(y0),level.Elevation),new XYZ(BuildingFeet(x1),BuildingFeet(y1),level.Elevation),new XYZ(BuildingFeet(x0),BuildingFeet(y1),level.Elevation)};
        var profile=new CurveArray();for(var i=0;i<4;i++) profile.Append(Line.CreateBound(vertices[i],vertices[(i+1)%4]));
        // Revit's native implementation expects an allocated mapping even though the API marks it out.
        var edges=new ModelCurveArray();
        FootPrintRoof roof;
        try { roof=doc.Create.NewFootPrintRoof(profile,level,type,out edges); }
        catch(Exception e) { throw new InvalidOperationException("Creating roof footprint: "+e.Message,e); }
        if(edges.Size!=4) throw new InvalidOperationException("Revit did not produce four roof boundary edges.");
        foreach(ModelCurve edge in edges)
        {
            var direction=((Line)edge.GeometryCurve).Direction;
            var defines=axis=="x"?Math.Abs(direction.X)>0.99:Math.Abs(direction.Y)>0.99;
            roof.set_DefinesSlope(edge,defines);
            if(defines) roof.set_SlopeAngle(edge,slope);
        }
        var heightParameter=roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)??throw new InvalidOperationException("Roof offset parameter missing.");
        if(!heightParameter.Set(BuildingFeet(offset))) throw new InvalidOperationException("Roof offset could not be set.");
        doc.Regenerate();
        var attached=new List<long>();
        if(args.TryGetProperty("attachWallIds",out var walls))
        {
            if(walls.GetArrayLength()>50)throw new ArgumentException("Maximum 50 walls.");
            foreach(var item in walls.EnumerateArray())
            {
                var id=item.GetInt64();
                if(attached.Contains(id))throw new ArgumentException("Duplicate wall ID.");
                if(doc.GetElement(new ElementId(id)) is not Wall wall || wall.Pinned || wall.GroupId!=ElementId.InvalidElementId || wall.WallType.Kind!=WallKind.Basic)
                    throw new ArgumentException($"Wall {id} must be an unpinned, ungrouped basic wall.");
                wall.AddAttachment(roof.Id,AttachmentLocation.Top);attached.Add(id);
            }
        }
        doc.Regenerate();
        var box=roof.get_BoundingBox(null)??throw new InvalidOperationException("Roof geometry missing.");
        return new {roofId=roof.Id.Value,typeId=type.Id.Value,levelId=level.Id.Value,slopeDegrees=slope*180/Math.PI,ridgeAxis=axis,attachedWallIds=attached,
            minHeightMeters=UnitUtils.ConvertFromInternalUnits(box.Min.Z,UnitTypeId.Meters),maxHeightMeters=UnitUtils.ConvertFromInternalUnits(box.Max.Z,UnitTypeId.Meters)};
    }
    private static object PreviewCreateSections(UIApplication app,JsonElement args)=>PreviewOperation(app,args,"createSections","Разрезы и фасадные виды",CreateSections);
    private static object ApplyCreateSections(UIApplication app,JsonElement args,Func<bool>? cancelled)=>ApplyOperation(app,args,"createSections","Создать разрезы?",CreateSections,cancelled);
    private static object CreateSections(Document doc,JsonElement args)
    {
        var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t=>t.ViewFamily==ViewFamily.Section)??throw new InvalidOperationException("Section view type missing.");
        var requests=args.GetProperty("views");if(requests.GetArrayLength() is <1 or >10)throw new ArgumentException("Provide 1 to 10 sections.");
        var result=new List<object>();
        foreach(var r in requests.EnumerateArray())
        {
            var x=BuildingNumber(r,"xMeters",-100000,100000);var y=BuildingNumber(r,"yMeters",-100000,100000);
            var bottom=BuildingNumber(r,"bottomMeters",-1000,10000);var top=BuildingNumber(r,"topMeters",-1000,10000);
            if(top-bottom<0.1)throw new ArgumentException("Section top must be above bottom.");
            var width=BuildingNumber(r,"widthMeters",0.5,200);var depth=BuildingNumber(r,"depthMeters",0.1,200);
            var dx=BuildingNumber(r,"directionX",-1,1);var dy=BuildingNumber(r,"directionY",-1,1);
            if(Math.Abs(dx*dx+dy*dy-1)>1e-6)throw new ArgumentException("Section direction must be a unit vector.");
            var dir=new XYZ(dx,dy,0);var transform=Transform.Identity;
            transform.Origin=new XYZ(BuildingFeet(x),BuildingFeet(y),BuildingFeet(bottom));transform.BasisX=XYZ.BasisZ.CrossProduct(dir);transform.BasisY=XYZ.BasisZ;transform.BasisZ=dir;
            var box=new BoundingBoxXYZ{Transform=transform,Min=new XYZ(-BuildingFeet(width)/2,0,0),Max=new XYZ(BuildingFeet(width)/2,BuildingFeet(top-bottom),BuildingFeet(depth))};
            var view=ViewSection.CreateSection(doc,type.Id,box);view.Name=JsonTools.GetString(r,"name");view.Scale=100;view.CropBoxVisible=false;view.DetailLevel=ViewDetailLevel.Fine;
            // Newly created sections can default to an early phase and hide later model elements.
            var phaseParameter=view.get_Parameter(BuiltInParameter.VIEW_PHASE);
            if(doc.Phases.Size>0 && phaseParameter is { IsReadOnly:false })
                phaseParameter.Set(doc.Phases.get_Item(doc.Phases.Size-1).Id);
            result.Add(new {viewId=view.Id.Value,name=view.Name});
        }
        return new {views=result};
    }
}
