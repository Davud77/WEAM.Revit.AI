using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

namespace WEAM.Revit.AI.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class ShowConnectionCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
    {
        try { BridgeService.Start(); }
        catch (Exception exception) { message = "Не удалось подключить MCP-мост: " + exception.Message; return Result.Failed; }
        var document = commandData.Application.ActiveUIDocument?.Document;
        var project = document is null ? "Проект не открыт" : document.Title;
        TaskDialog.Show("WEAM.Revit.AI", $"Локальный MCP-мост запущен.\nПорт: {BridgeService.Port}\nПроект: {project}\nВерсия: {typeof(PluginApplication).Assembly.GetName().Version}\nАвтоподтверждение: {(ApprovalSettings.AutoConfirm ? "включено" : "выключено")}\n\nПодключите MCP-сервер из папки server к своему AI-клиенту.");
        return Result.Succeeded;
    }
}
