using Autodesk.Revit.UI;
using System.Reflection;

namespace WEAM.Revit.AI;

public sealed class PluginApplication : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            application.ControlledApplication.DocumentChanged += RevitTools.OnDocumentChanged;

            const string tabName = "WEAM AI";
            try { application.CreateRibbonTab(tabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { }

            var panel = application.CreateRibbonPanel(tabName, "Модель");
            var button = new PushButtonData(
                "WeamRevitAiConnection",
                "WEAM\nAI",
                Assembly.GetExecutingAssembly().Location,
                typeof(Commands.ShowConnectionCommand).FullName!);
            button.Image = RibbonAssets.Icon(16);
            button.LargeImage = RibbonAssets.Icon(32);
            button.ToolTip = "Подключение и восстановление локального MCP-моста WEAM AI";
            panel.AddItem(button);
            var autoConfirmButton = new PushButtonData("WeamAutoConfirm", "Авто\nподтверждение",
                Assembly.GetExecutingAssembly().Location,
                typeof(Commands.ToggleAutoConfirmCommand).FullName!);
            autoConfirmButton.Image = RibbonAssets.AutoConfirmIcon(16);
            autoConfirmButton.LargeImage = RibbonAssets.AutoConfirmIcon(32);
            autoConfirmButton.ToolTip = "Включить или выключить автоматическое подтверждение локальных MCP-команд";
            panel.AddItem(autoConfirmButton);
            // Keep the ribbon available if another Revit process currently owns
            // the port. The connection button can retry once the port is free.
            try { BridgeService.Start(); }
            catch (System.Net.Sockets.SocketException) { }
            return Result.Succeeded;
        }
        catch (Exception exception)
        {
            application.ControlledApplication.DocumentChanged -= RevitTools.OnDocumentChanged;
            TaskDialog.Show("WEAM.Revit.AI", $"Не удалось запустить локальный мост:\n{exception.Message}");
            BridgeService.Stop();
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.ControlledApplication.DocumentChanged -= RevitTools.OnDocumentChanged;
        BridgeService.Stop();
        return Result.Succeeded;
    }
}
