using Autodesk.Revit.UI;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    private static bool ConfirmOperation(TaskDialog dialog)
    {
        if (ApprovalSettings.AutoConfirm) return true;
        return dialog.Show() == TaskDialogResult.Yes;
    }
}
