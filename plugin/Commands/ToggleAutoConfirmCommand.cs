using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

namespace WEAM.Revit.AI.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class ToggleAutoConfirmCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
    {
        try
        {
            var enabled = !ApprovalSettings.AutoConfirm;
            ApprovalSettings.SetAutoConfirm(enabled);
            TaskDialog.Show("WEAM.Revit.AI", enabled
                ? "Автоподтверждение включено. Команды apply выполняются без окна Yes/No. Предпросмотры, проверки и транзакции сохраняются."
                : "Автоподтверждение выключено. Команды apply требуют подтверждения Yes/No.");
            return Result.Succeeded;
        }
        catch (Exception exception) { message = exception.Message; return Result.Failed; }
    }
}
