namespace WEAM.Revit.AI.Setup;

sealed class SetupWindow : Form
{
    readonly CheckBox codex = new() { Text = "Подключить Codex", AutoSize = true };
    readonly CheckBox claude = new() { Text = "Подключить Claude Desktop", AutoSize = true };
    readonly CheckBox auto = new() { Text = "Включить автоподтверждение команд Revit", AutoSize = true };
    readonly TextBox revit = new() { Dock = DockStyle.Fill };
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    readonly Button action = new() { Text = "Установить", AutoSize = true };

    public SetupWindow(bool uninstall)
    {
        Text = "WEAM.Revit.AI — " + (uninstall ? "удаление" : "установка");
        ClientSize = new Size(610, 460); FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Font = new Font("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 10 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "WEAM.Revit.AI 0.9.8", Font = new Font(Font.FontFamily, 19, FontStyle.Bold), AutoSize = true });
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(560, 0), Text = uninstall
            ? "Удаление надстройки и её MCP-подключения. Ваши модели, снимки и настройки сохранятся. Закройте Revit и AI-клиент."
            : "Управляйте Revit через MCP. Установка для текущего пользователя; Node.js и зависимости включены. Требуется установленный Autodesk Revit 2026 x64." });
        layout.Controls.Add(new Label { Text = "Каталог Revit 2026:", AutoSize = true });
        revit.Text = Deployment.FindRevit() ?? @"C:\Program Files\Autodesk\Revit 2026";
        var pathRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        var browse = new Button { Text = "Обзор…", Dock = DockStyle.Fill };
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { InitialDirectory = revit.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) revit.Text = dialog.SelectedPath; };
        pathRow.Controls.Add(revit, 0, 0); pathRow.Controls.Add(browse, 1, 0); layout.Controls.Add(pathRow);
        codex.Checked = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        claude.Checked = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude"));
        layout.Controls.Add(codex); layout.Controls.Add(claude); layout.Controls.Add(auto);
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(560, 0), Text = "Лицензия AGPL v3. Подключение другого MCP-клиента: готовые файлы конфигурации будут доступны в папке приложения." });
        layout.Controls.Add(status);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "Закрыть", AutoSize = true }; close.Click += (_, _) => Close();
        var license = new Button { Text = "Лицензия", AutoSize = true };
        license.Click += (_, _) => { using var reader = new Form { Text = "GNU AGPL v3", Size = new Size(700, 600) }; reader.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = Deployment.ReadLicense() }); reader.ShowDialog(this); };
        buttons.Controls.Add(close); buttons.Controls.Add(action); buttons.Controls.Add(license); layout.Controls.Add(buttons);
        if (uninstall) { revit.Enabled = browse.Enabled = codex.Enabled = claude.Enabled = auto.Enabled = false; action.Text = "Удалить"; }
        action.Click += (_, _) =>
        {
            action.Enabled = false; status.Text = "Выполняется…"; Refresh();
            try
            {
                if (uninstall) Deployment.Uninstall(); else Deployment.Install(codex.Checked, claude.Checked, auto.Checked, revit.Text);
                status.ForeColor = Color.DarkGreen;
                status.Text = uninstall ? "Надстройка удалена. Данные сохранены." : "Готово. Запустите Revit 2026 и перезапустите AI-клиент для загрузки MCP.";
            }
            catch (Exception ex) { status.ForeColor = Color.DarkRed; status.Text = ex.Message; action.Enabled = true; Deployment.Log("ERROR: " + ex.Message); }
        };
        Controls.Add(layout);
    }
}
