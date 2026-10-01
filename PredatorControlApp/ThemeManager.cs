using System;
using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PredatorControlApp
{
    public enum AppTheme
    {
        System,
        Dark,
        Light
    }

    [SupportedOSPlatform("windows")]
    public static class ThemeManager
    {
        public static AppTheme CurrentTheme { get; private set; } = AppTheme.System;

        public static bool IsDarkThemeActive
        {
            get
            {
                if (CurrentTheme == AppTheme.Dark) return true;
                if (CurrentTheme == AppTheme.Light) return false;

                // System theme: check Windows registry
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                    if (key?.GetValue("AppsUseLightTheme") is int val)
                    {
                        return val == 0;
                    }
                }
                catch { }
                return true; // Default to dark for gaming laptops
            }
        }

        public static Color FormBg => IsDarkThemeActive ? Color.FromArgb(22, 22, 26) : Color.FromArgb(245, 245, 248);
        public static Color TitleBarBg => IsDarkThemeActive ? Color.FromArgb(18, 18, 21) : Color.FromArgb(235, 235, 240);
        public static Color TextPrimary => IsDarkThemeActive ? Color.White : Color.FromArgb(20, 20, 24);
        public static Color TextSecondary => IsDarkThemeActive ? Color.FromArgb(100, 100, 110) : Color.FromArgb(120, 120, 130);
        public static Color HeaderText => IsDarkThemeActive ? Color.FromArgb(120, 120, 135) : Color.FromArgb(90, 90, 105);
        public static Color Separator => IsDarkThemeActive ? Color.FromArgb(40, 40, 44) : Color.FromArgb(220, 220, 225);
        public static Color CardBg => IsDarkThemeActive ? Color.FromArgb(30, 32, 40) : Color.FromArgb(255, 255, 255);
        public static Color Accent => Color.FromArgb(0, 200, 160);

        public static void LoadThemeFromRegistry()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\PredatorControl");
                if (key?.GetValue("AppTheme") is int val && Enum.IsDefined(typeof(AppTheme), val))
                {
                    CurrentTheme = (AppTheme)val;
                }
            }
            catch { }
        }

        public static void SetTheme(AppTheme theme)
        {
            CurrentTheme = theme;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\PredatorControl");
                key?.SetValue("AppTheme", (int)theme);
            }
            catch { }
        }

        public static void ApplyTheme(Form form)
        {
            form.BackColor = FormBg;
            ApplyToControls(form.Controls);
            form.Invalidate(true);
        }

        private static void ApplyToControls(Control.ControlCollection controls)
        {
            foreach (Control c in controls)
            {
                if (c is Panel pnl && pnl.Name != "pnlTitle")
                {
                    if (pnl.Height == 1) // Separator
                        pnl.BackColor = Separator;
                    else if (pnl is not DarkScrollPanel)
                        pnl.BackColor = FormBg;
                }
                else if (c is Label lbl && lbl.Name != "_lblTitle")
                {
                    if (lbl.Font.Bold && lbl.ForeColor != Accent)
                        lbl.ForeColor = TextPrimary;
                    else if (!lbl.Font.Bold)
                        lbl.ForeColor = TextSecondary;
                }

                if (c.HasChildren)
                    ApplyToControls(c.Controls);
            }
        }
    }
}
