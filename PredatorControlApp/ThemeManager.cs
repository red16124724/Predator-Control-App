using System;

using System.Drawing;

using System.Runtime.InteropServices;

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

        private const int WM_SETREDRAW = 0x000B;



        [DllImport("user32.dll")]

        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);



        static ThemeManager()

        {

            try

            {

                SystemEvents.UserPreferenceChanged += (s, e) =>

                {

                    if (e.Category == UserPreferenceCategory.General && CurrentTheme == AppTheme.System)

                    {

                        ThemeChanged?.Invoke();

                    }

                };

            }

            catch { }

        }



        public static readonly object ThemeSync = new();

        public static AppTheme CurrentTheme { get; private set; } = AppTheme.System;



        public static event Action? ThemeChanged;



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



        // Modern High-Contrast Theme Palette (Dark & Light)

        public static Color FormBg => IsDarkThemeActive ? Color.FromArgb(0x12, 0x18, 0x2B) : Color.FromArgb(0xF4, 0xEF, 0xE6);

        public static Color SidebarBg => IsDarkThemeActive ? Color.FromArgb(0x1A, 0x21, 0x38) : Color.FromArgb(0xED, 0xE7, 0xDA);

        public static Color TitleBarBg => IsDarkThemeActive ? Color.FromArgb(0x0E, 0x14, 0x22) : Color.FromArgb(0xE6, 0xDF, 0xD1);

        public static Color CardBg => IsDarkThemeActive ? Color.FromArgb(0x1E, 0x26, 0x40) : Color.FromArgb(0xFA, 0xF6, 0xEE);

        public static Color CardBorder => IsDarkThemeActive ? Color.FromArgb(0x2E, 0x3A, 0x5C) : Color.FromArgb(0xCF, 0xC8, 0xB4);

        public static Color ControlBg => IsDarkThemeActive ? Color.FromArgb(0x25, 0x2D, 0x47) : Color.FromArgb(0xED, 0xE6, 0xD8);

        public static Color ControlHover => IsDarkThemeActive ? Color.FromArgb(0x2F, 0x3A, 0x58) : Color.FromArgb(0xE4, 0xD9, 0xC4);

        public static Color ControlActive => IsDarkThemeActive ? Color.FromArgb(0x0B, 0x3D, 0x34) : Color.FromArgb(0x00, 0x7A, 0x60);

        public static Color ControlDisabled => IsDarkThemeActive ? Color.FromArgb(0x18, 0x1E, 0x32) : Color.FromArgb(0xE8, 0xE2, 0xD4);

        public static Color ControlBorder => IsDarkThemeActive ? Color.FromArgb(0x3B, 0x4A, 0x72) : Color.FromArgb(0xC0, 0xB4, 0x9C);

        public static Color BorderHover => IsDarkThemeActive ? Color.FromArgb(0x54, 0x70, 0xA0) : Color.FromArgb(0xA0, 0x90, 0x80);

        public static Color BorderDisabled => IsDarkThemeActive ? Color.FromArgb(0x21, 0x2B, 0x45) : Color.FromArgb(0xCF, 0xC8, 0xB4);

        

        // High contrast text colors

        public static Color TextPrimary => IsDarkThemeActive ? Color.FromArgb(0xE8, 0xEE, 0xF8) : Color.FromArgb(0x2A, 0x1F, 0x0F);

        public static Color TextSecondary => IsDarkThemeActive ? Color.FromArgb(0x8E, 0x9D, 0xC0) : Color.FromArgb(0x5C, 0x4F, 0x38);

        public static Color TextMuted => IsDarkThemeActive ? Color.FromArgb(0x5E, 0x6E, 0x90) : Color.FromArgb(0x7A, 0x6A, 0x52);

        public static Color HeaderText => IsDarkThemeActive ? Color.FromArgb(0x8E, 0x9D, 0xC0) : Color.FromArgb(0x5C, 0x4F, 0x38);

        public static Color Separator => IsDarkThemeActive ? Color.FromArgb(0x2E, 0x3A, 0x5C) : Color.FromArgb(0xCF, 0xC8, 0xB4);

        

        // Brand accents

        public static Color Accent => IsDarkThemeActive ? Color.FromArgb(0x00, 0xE5, 0xB8) : Color.FromArgb(0x00, 0x7A, 0x60);

        public static Color AccentHover => IsDarkThemeActive ? Color.FromArgb(0x26, 0xEE, 0xC0) : Color.FromArgb(0x00, 0x66, 0x50);

        public static Color StatusD3Cold => IsDarkThemeActive ? Color.FromArgb(94, 180, 255) : Color.FromArgb(0, 102, 204);

        public static Color FocusRing => IsDarkThemeActive ? Color.FromArgb(0x00, 0xE5, 0xB8) : Color.FromArgb(0x00, 0x7A, 0x60);

        /// <summary>
        /// Gets the power mode indicator color.
        /// Quiet (0x00): White (#FFFFFF)
        /// Balanced (0x01): Blue (#0096FF)
        /// Performance (0x04): Purple (#A855F7)
        /// Turbo (0x05): Pink (#FF2D87)
        /// Eco (0x06): Green (#00FF78)
        /// Fallback: ThemeManager.Accent
        /// </summary>
        public static Color GetPowerModeColor(byte mode) => mode switch
        {
            0x00 => Color.FromArgb(255, 255, 255),
            0x01 => Color.FromArgb(0, 150, 255),
            0x04 => Color.FromArgb(168, 85, 247),
            0x05 => Color.FromArgb(255, 45, 135),
            0x06 => Color.FromArgb(0, 255, 120),
            _ => Accent
        };

        /// <summary>
        /// Explicit power mode pill color helper mapping:
        /// Quiet (0x00)->#FFFFFF, Balanced (0x01)->#0096FF, Performance (0x04)->#A855F7, Turbo (0x05)->#FF2D87, Eco (0x06)->#00FF78.
        /// </summary>
        public static Color GetPowerModePillColor(byte mode) => GetPowerModeColor(mode);

        /// <summary>
        /// Returns the hex code string for the given power mode pill color.
        /// </summary>
        public static string GetPowerModePillColorHex(byte mode) => mode switch
        {
            0x00 => "#FFFFFF",
            0x01 => "#0096FF",
            0x04 => "#A855F7",
            0x05 => "#FF2D87",
            0x06 => "#00FF78",
            _ => IsDarkThemeActive ? "#00E5B8" : "#007A60"
        };



        public static void LoadThemeFromRegistry()

        {

            lock (ThemeSync)

            {

                // 1. Try registry for explicit user selection

                try

                {

                    using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\PredatorControl");

                    if (key != null)

                    {

                        if (key.GetValue("AppTheme") is int val && Enum.IsDefined(typeof(AppTheme), val) && (AppTheme)val != AppTheme.System)

                        {

                            CurrentTheme = (AppTheme)val;

                            return;

                        }

                        if (key.GetValue("Theme") is string str && Enum.TryParse<AppTheme>(str, out var theme) && theme != AppTheme.System)

                        {

                            CurrentTheme = theme;

                            return;

                        }

                    }

                }

                catch { }



                // 2. Try settings file

                try

                {

                    var settings = AppSettings.Load();

                    if (Enum.TryParse<AppTheme>(settings.Theme, out var st) && st != AppTheme.System)

                    {

                        CurrentTheme = st;

                        return;

                    }

                }

                catch { }



                // 3. Fallback: Default to Dark theme for gaming laptops, never revert to system/default

                CurrentTheme = AppTheme.Dark;

            }

        }



        public static void ToggleTheme()

        {

            SetTheme(IsDarkThemeActive ? AppTheme.Light : AppTheme.Dark);

        }



        public static void SetTheme(AppTheme theme)

        {

            Action? handler = null;

            lock (ThemeSync)

            {

                CurrentTheme = theme;

                try

                {

                    var settings = AppSettings.Load();

                    settings.Theme = theme.ToString();

                    settings.Save();

                }

                catch { }

                try

                {

                    using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\PredatorControl");

                    key?.SetValue("AppTheme", (int)theme);

                    key?.SetValue("Theme", theme.ToString());

                }

                catch { }

                handler = ThemeChanged;

            }

            handler?.Invoke();

        }



        public static void ApplyTheme(Form form)

        {

            if (form.IsDisposed) return;



            bool suspended = false;

            if (form.IsHandleCreated)

            {

                SendMessage(form.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);

                suspended = true;

            }



            try

            {

                form.BackColor = FormBg;

                ApplyToControls(form.Controls);

            }

            finally

            {

                if (suspended)

                {

                    SendMessage(form.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);

                    form.Refresh();

                }

                else

                {

                    form.Invalidate(true);

                }

            }

        }



        public static void ApplyToControls(Control.ControlCollection controls)

        {

            foreach (Control c in controls)

            {

                if (c is Panel pnl)

                {

                    if (pnl.Name == "pnlTitle" || (string?)pnl.Tag == "title_bar")

                        pnl.BackColor = TitleBarBg;

                    else if (pnl.Name == "_sidebarPanel" || (string?)pnl.Tag == "sidebar")

                        pnl.BackColor = SidebarBg;

                    else if (pnl.Name == "_pageContainer" || pnl.Name.StartsWith("page_") || (string?)pnl.Tag == "page_container")

                        pnl.BackColor = FormBg;

                    else if (pnl.Name.StartsWith("card_") || (string?)pnl.Tag == "card")

                        pnl.BackColor = CardBg;

                    else if (pnl.Height <= 2)

                        pnl.BackColor = Separator;

                    else if (pnl is DarkScrollPanel)

                        pnl.BackColor = FormBg;

                    else

                        pnl.BackColor = FormBg;

                }

                else if (c is Label lbl && lbl.Name != "_lblTitle")

                {

                    string? tag = lbl.Tag as string;

                    if (tag == "custom_color")

                    {

                        // Preserve custom forecolor (e.g. power mode pill color)

                    }

                    else if (tag == "accent")

                        lbl.ForeColor = Accent;

                    else if (tag == "d3cold")

                        lbl.ForeColor = StatusD3Cold;

                    else if (tag == "muted")

                        lbl.ForeColor = TextMuted;

                    else if (tag == "header")

                        lbl.ForeColor = HeaderText;

                    else if (tag == "title")

                        lbl.ForeColor = TextPrimary;

                    else if (lbl.Font.Bold)

                        lbl.ForeColor = TextPrimary;

                    else

                        lbl.ForeColor = TextSecondary;

                }



                if (c.HasChildren)

                    ApplyToControls(c.Controls);

            }

        }

    }

}

