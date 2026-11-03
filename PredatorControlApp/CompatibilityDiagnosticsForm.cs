using System.Text;
using System.Windows.Forms;

namespace PredatorControlApp;

internal static class CompatibilityReport
{
    internal static string Create(string model, string bios, DeviceCapabilities capabilities,
        KeyboardLightingCapabilities? lighting, string nativeStatus)
    {
        static string Value(string? value) => string.IsNullOrWhiteSpace(value)
            ? "Unknown" : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        static string YesNo(bool value) => value ? "Yes" : "No";

        var report = new StringBuilder();
        report.AppendLine("Predator Control compatibility report");
        report.AppendLine($"App version: {Updater.CurrentText}");
        report.AppendLine($"Windows: {Environment.OSVersion.Version}");
        report.AppendLine($"Model: {Value(model)}");
        report.AppendLine($"BIOS: {Value(bios)}");
        report.AppendLine($"Native core: {Value(nativeStatus)}");
        report.AppendLine("Native firmware parsing and lighting rules use managed fallbacks when unavailable.");
        report.AppendLine();
        report.AppendLine("Detected capabilities (not a guarantee of hardware compatibility)");
        report.AppendLine($"Operating modes: {string.Join(", ", capabilities.OperatingModes)}");
        report.AppendLine($"Fan tables: {YesNo(capabilities.FanTable)}");
        report.AppendLine($"GPU mode switch: {YesNo(capabilities.GpuModeSwitch)}");
        report.AppendLine($"Battery health control: {YesNo(capabilities.BatteryHealth)}");
        report.AppendLine($"Power-off USB charging: {YesNo(capabilities.UsbCharging)}");
        report.AppendLine($"Direct EC HID: {(capabilities.HasEcHid.HasValue ? YesNo(capabilities.HasEcHid.Value) : "Unknown")}");
        report.AppendLine($"PawnIO installed: {YesNo(capabilities.PawnIoInstalled)}; available: {YesNo(capabilities.PawnIoAvailable)}");
        report.AppendLine();
        report.AppendLine("Keyboard lighting");
        if (lighting == null)
        {
            report.AppendLine("Detection has not completed.");
        }
        else
        {
            report.AppendLine($"Detected tier: {lighting.TierLabel}; zones: {lighting.ZoneCount}");
            report.AppendLine($"Per-key supported: {YesNo(lighting.PerKeySupported)}; verified on this hardware: {YesNo(lighting.VerifiedOnHardware)}");
            report.AppendLine($"Acer lighting service reachable: {YesNo(lighting.LightingServiceReachable)}");
            report.AppendLine($"WMI zone path available: {YesNo(lighting.WmiZonePathAvailable)}");
        }
        report.AppendLine();
        report.AppendLine("Serial numbers, SNID, firmware fingerprints, raw hardware data and file paths are excluded.");
        report.AppendLine("Review this report before sharing; model and BIOS details are included.");
        return report.ToString();
    }
}

internal sealed class CompatibilityDiagnosticsForm : Form
{
    public CompatibilityDiagnosticsForm(string report)
    {
        Text = "Compatibility and diagnostics";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new System.Drawing.Size(560, 420);
        Size = new System.Drawing.Size(720, 620);
        var text = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Both, WordWrap = false, Text = report
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(5),
            FlowDirection = FlowDirection.LeftToRight
        };
        var copy = new Button { Text = "Copy report", AutoSize = true };
        copy.Click += (_, _) =>
        {
            if (DiagnosticsDumper.CopyToClipboard(report))
                MessageBox.Show(this, "Report copied. Review it before sharing.", Text);
            else
                MessageBox.Show(this, "Could not copy the report to the clipboard.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        var save = new Button { Text = "Save report", AutoSize = true };
        save.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog { Filter = "Text files (*.txt)|*.txt", FileName = "Predator-compatibility-report.txt" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { System.IO.File.WriteAllText(dialog.FileName, report); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                MessageBox.Show(this, "Could not save the report: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
        buttons.Controls.AddRange(new Control[] { copy, save, close });
        Controls.Add(text);
        Controls.Add(buttons);
        AcceptButton = close;
    }
}
