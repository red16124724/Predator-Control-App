using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace PredatorControlApp
{
    public sealed class CurvePointData
    {
        public int X { get; set; }
        public int Y { get; set; }

        public CurvePointData() { }
        public CurvePointData(int x, int y) { X = x; Y = y; }
        public Point ToPoint() => new(X, Y);
        public static CurvePointData FromPoint(Point p) => new(p.X, p.Y);
    }

        public sealed class ThreadSafeCurveListConverter : JsonConverter<List<CurvePointData>>
    {
        public override List<CurvePointData>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new();
            var list = new List<CurvePointData>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                int x = el.TryGetProperty("X", out var px) ? px.GetInt32() : 0;
                int y = el.TryGetProperty("Y", out var py) ? py.GetInt32() : 0;
                list.Add(new CurvePointData(x, y));
            }
            return list;
        }

        public override void Write(Utf8JsonWriter writer, List<CurvePointData> value, JsonSerializerOptions options)
        {
            CurvePointData[]? snapshot = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    lock (value)
                    {
                        snapshot = value.ToArray();
                    }
                    break;
                }
                catch (InvalidOperationException)
                {
                    Thread.SpinWait(10);
                }
                catch
                {
                    break;
                }
            }
            snapshot ??= Array.Empty<CurvePointData>();
            writer.WriteStartArray();
            foreach (var p in snapshot)
            {
                if (p != null)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("X", p.X);
                    writer.WriteNumber("Y", p.Y);
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();
        }
    }

    [SupportedOSPlatform("windows")]
    public sealed class AppSettings
    {
        public string Theme { get; set; } = "System";

        private byte _powerMode = 0x01;
        public byte PowerMode { get => _powerMode; set => _powerMode = Math.Clamp(value, (byte)0x00, (byte)0x06); }
        public byte? PowerModeAC { get; set; }
        public byte? PowerModeBattery { get; set; }
        public int AutoPowerAC { get; set; } = 0;
        public int AutoPowerBattery { get; set; } = 0;

        private byte _fanMode = 0x01;
        public byte FanMode { get => _fanMode; set => _fanMode = Math.Clamp(value, (byte)0x00, (byte)0x03); }
        public byte? FanModeAC { get; set; }
        public byte? FanModeBattery { get; set; }
        public int AutoFanAC { get; set; } = 0;
        public int AutoFanBattery { get; set; } = 0;
        public bool FanCurveEnabled { get; set; } = false;

        private int _fanSpeedCpu = 50;
        public int FanSpeedCpu { get => _fanSpeedCpu; set => _fanSpeedCpu = Math.Clamp(value, 0, 100); }

        private int _fanSpeedGpu = 50;
        public int FanSpeedGpu { get => _fanSpeedGpu; set => _fanSpeedGpu = Math.Clamp(value, 0, 100); }

        private int _fanSpeedSys = 50;
        public int FanSpeedSys { get => _fanSpeedSys; set => _fanSpeedSys = Math.Clamp(value, 0, 100); }

        private int _fanSpeedCpuAC = 50;
        public int FanSpeedCpuAC { get => _fanSpeedCpuAC; set => _fanSpeedCpuAC = Math.Clamp(value, 0, 100); }

        private int _fanSpeedCpuBattery = 50;
        public int FanSpeedCpuBattery { get => _fanSpeedCpuBattery; set => _fanSpeedCpuBattery = Math.Clamp(value, 0, 100); }

        private int _fanSpeedGpuAC = 50;
        public int FanSpeedGpuAC { get => _fanSpeedGpuAC; set => _fanSpeedGpuAC = Math.Clamp(value, 0, 100); }

        private int _fanSpeedGpuBattery = 50;
        public int FanSpeedGpuBattery { get => _fanSpeedGpuBattery; set => _fanSpeedGpuBattery = Math.Clamp(value, 0, 100); }

        private int _fanSpeedSysAC = 50;
        public int FanSpeedSysAC { get => _fanSpeedSysAC; set => _fanSpeedSysAC = Math.Clamp(value, 0, 100); }

        private int _fanSpeedSysBattery = 50;
        public int FanSpeedSysBattery { get => _fanSpeedSysBattery; set => _fanSpeedSysBattery = Math.Clamp(value, 0, 100); }

        [JsonConverter(typeof(ThreadSafeCurveListConverter))]
        public List<CurvePointData> CpuCurve { get; set; } = new();
        [JsonConverter(typeof(ThreadSafeCurveListConverter))]
        public List<CurvePointData> GpuCurve { get; set; } = new();

        public int RefreshRate { get; set; } = 0;
        public bool LcdOverdrive { get; set; } = true;
        public bool BatteryLimit { get; set; } = false;
        public bool CoolBoost { get; set; } = false;

        public int RgbMode { get; set; } = 3;
        public int RgbBrightness { get; set; } = 100;
        public int RgbSpeed { get; set; } = 50;
        public int RgbR { get; set; } = 0;
        public int RgbG { get; set; } = 150;
        public int RgbB { get; set; } = 255;

        public int GpuMode { get; set; } = 0;

        public int ModeKeyAction { get; set; } = 0; // 0 = Cycle, 1 = TurboToggle
        public byte TurboReturnMode { get; set; } = 0x01;

        [JsonIgnore]
        public static string? CustomFilePath { get; set; }

        [JsonIgnore]
        public static string SettingsFilePath =>
            CustomFilePath ?? 
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PredatorControl", "settings.json");

        private static readonly object _fileLock = new();

        public static List<CurvePointData> SafeCopyCurve(List<CurvePointData>? src)
        {
            if (src == null) return new();
            for (int retry = 0; retry < 10; retry++)
            {
                try
                {
                    lock (src)
                    {
                        return src.Where(p => p != null).Select(p => new CurvePointData(p.X, p.Y)).ToList();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
                {
                    Thread.SpinWait(20);
                }
            }
            try
            {
                lock (src)
                {
                    var arr = new CurvePointData[src.Count];
                    src.CopyTo(arr, 0);
                    return arr.Where(p => p != null).Select(p => new CurvePointData(p.X, p.Y)).ToList();
                }
            }
            catch
            {
                return new();
            }
        }

        public AppSettings CreateSnapshot()
        {
            lock (this)
            {
                return new AppSettings
                {
                    Theme = this.Theme,
                    PowerMode = this.PowerMode,
                    PowerModeAC = this.PowerModeAC,
                    PowerModeBattery = this.PowerModeBattery,
                    AutoPowerAC = this.AutoPowerAC,
                    AutoPowerBattery = this.AutoPowerBattery,
                    FanMode = this.FanMode,
                    FanModeAC = this.FanModeAC,
                    FanModeBattery = this.FanModeBattery,
                    AutoFanAC = this.AutoFanAC,
                    AutoFanBattery = this.AutoFanBattery,
                    FanCurveEnabled = this.FanCurveEnabled,
                    FanSpeedCpu = this.FanSpeedCpu,
                    FanSpeedGpu = this.FanSpeedGpu,
                    FanSpeedSys = this.FanSpeedSys,
                    FanSpeedCpuAC = this.FanSpeedCpuAC,
                    FanSpeedCpuBattery = this.FanSpeedCpuBattery,
                    FanSpeedGpuAC = this.FanSpeedGpuAC,
                    FanSpeedGpuBattery = this.FanSpeedGpuBattery,
                    FanSpeedSysAC = this.FanSpeedSysAC,
                    FanSpeedSysBattery = this.FanSpeedSysBattery,
                    CpuCurve = SafeCopyCurve(this.CpuCurve),
                    GpuCurve = SafeCopyCurve(this.GpuCurve),
                    RefreshRate = this.RefreshRate,
                    LcdOverdrive = this.LcdOverdrive,
                    BatteryLimit = this.BatteryLimit,
                    CoolBoost = this.CoolBoost,
                    RgbMode = this.RgbMode,
                    RgbBrightness = this.RgbBrightness,
                    RgbSpeed = this.RgbSpeed,
                    RgbR = this.RgbR,
                    RgbG = this.RgbG,
                    RgbB = this.RgbB,
                    GpuMode = this.GpuMode,
                    ModeKeyAction = this.ModeKeyAction,
                    TurboReturnMode = this.TurboReturnMode
                };
            }
        }

        public void Sanitize()
        {
            if (string.IsNullOrWhiteSpace(Theme) || !Enum.TryParse<AppTheme>(Theme, true, out _))
                Theme = "System";

            PowerMode = Math.Clamp(PowerMode, (byte)0x00, (byte)0x06);
            if (PowerModeAC.HasValue) PowerModeAC = Math.Clamp(PowerModeAC.Value, (byte)0x00, (byte)0x06);
            if (PowerModeBattery.HasValue) PowerModeBattery = Math.Clamp(PowerModeBattery.Value, (byte)0x00, (byte)0x06);

            AutoPowerAC = Math.Clamp(AutoPowerAC, 0, 10);
            AutoPowerBattery = Math.Clamp(AutoPowerBattery, 0, 10);

            FanMode = Math.Clamp(FanMode, (byte)0x00, (byte)0x03);
            if (FanModeAC.HasValue) FanModeAC = Math.Clamp(FanModeAC.Value, (byte)0x00, (byte)0x03);
            if (FanModeBattery.HasValue) FanModeBattery = Math.Clamp(FanModeBattery.Value, (byte)0x00, (byte)0x03);

            AutoFanAC = Math.Clamp(AutoFanAC, 0, 10);
            AutoFanBattery = Math.Clamp(AutoFanBattery, 0, 10);

            FanSpeedCpu = Math.Clamp(FanSpeedCpu, 0, 100);
            FanSpeedGpu = Math.Clamp(FanSpeedGpu, 0, 100);
            FanSpeedSys = Math.Clamp(FanSpeedSys, 0, 100);

            FanSpeedCpuAC = Math.Clamp(FanSpeedCpuAC, 0, 100);
            FanSpeedCpuBattery = Math.Clamp(FanSpeedCpuBattery, 0, 100);
            FanSpeedGpuAC = Math.Clamp(FanSpeedGpuAC, 0, 100);
            FanSpeedGpuBattery = Math.Clamp(FanSpeedGpuBattery, 0, 100);
            FanSpeedSysAC = Math.Clamp(FanSpeedSysAC, 0, 100);
            FanSpeedSysBattery = Math.Clamp(FanSpeedSysBattery, 0, 100);

            RefreshRate = Math.Clamp(RefreshRate, 0, 1000);

            RgbMode = Math.Clamp(RgbMode, 0, 10);
            RgbBrightness = Math.Clamp(RgbBrightness, 0, 100);
            RgbSpeed = Math.Clamp(RgbSpeed, 0, 100);
            RgbR = Math.Clamp(RgbR, 0, 255);
            RgbG = Math.Clamp(RgbG, 0, 255);
            RgbB = Math.Clamp(RgbB, 0, 255);

            GpuMode = Math.Clamp(GpuMode, 0, 5);
            ModeKeyAction = Math.Clamp(ModeKeyAction, 0, 1);
            TurboReturnMode = Math.Clamp(TurboReturnMode, (byte)0x00, (byte)0x06);

            CpuCurve ??= new();
            lock (CpuCurve)
            {
                CpuCurve.RemoveAll(p => p == null!);
                if (CpuCurve.Count > 16) CpuCurve.RemoveRange(16, CpuCurve.Count - 16);
                var cpuPts = CpuCurve.ToArray();
                foreach (var pt in cpuPts)
                {
                    if (pt != null)
                    {
                        pt.X = Math.Clamp(pt.X, 0, 100);
                        pt.Y = Math.Clamp(pt.Y, 0, 100);
                    }
                }
            }

            GpuCurve ??= new();
            lock (GpuCurve)
            {
                GpuCurve.RemoveAll(p => p == null!);
                if (GpuCurve.Count > 16) GpuCurve.RemoveRange(16, GpuCurve.Count - 16);
                var gpuPts = GpuCurve.ToArray();
                foreach (var pt in gpuPts)
                {
                    if (pt != null)
                    {
                        pt.X = Math.Clamp(pt.X, 0, 100);
                        pt.Y = Math.Clamp(pt.Y, 0, 100);
                    }
                }
            }
        }

        public static AppSettings Load()
        {
            AppSettings settings = new();
            bool loadedFromFile = false;

            string path = SettingsFilePath;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var fi = new FileInfo(path);
                        // Prevent unbounded read / DoS on oversized settings files (> 1MB)
                        // Also verify not a symlink / reparse point
                        if (fi.Length <= 1024 * 1024 && (fi.Attributes & FileAttributes.ReparsePoint) == 0)
                        {
                            lock (_fileLock)
                            {
                                if (File.Exists(path))
                                {
                                    string json = File.ReadAllText(path);
                                    if (!string.IsNullOrWhiteSpace(json))
                                    {
                                        var parsed = JsonSerializer.Deserialize<AppSettings>(json);
                                        if (parsed != null)
                                        {
                                            parsed.Sanitize();
                                            settings = parsed;
                                            loadedFromFile = true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    System.Threading.Thread.Sleep(20);
                }
                catch
                {
                    break;
                }
            }

            // If not loaded from file or partial, fall back / hydrate from Registry
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\PredatorControl");
                if (key != null)
                {
                    if (!loadedFromFile || string.IsNullOrWhiteSpace(settings.Theme))
                    {
                        if (key.GetValue("AppTheme") is int th && Enum.IsDefined(typeof(AppTheme), th) && (AppTheme)th != AppTheme.System)
                            settings.Theme = ((AppTheme)th).ToString();
                        else if (key.GetValue("Theme") is string thStr && Enum.TryParse<AppTheme>(thStr, out var thVal) && thVal != AppTheme.System)
                            settings.Theme = thVal.ToString();
                    }
                    if (!loadedFromFile)
                    {
                        if (key.GetValue("Power") is int p) settings.PowerMode = (byte)Math.Clamp(p, 0, 255);
                        if (key.GetValue("Power_AC") is int pac) settings.PowerModeAC = (byte)Math.Clamp(pac, 0, 255);
                        if (key.GetValue("Power_Battery") is int pbat) settings.PowerModeBattery = (byte)Math.Clamp(pbat, 0, 255);
                        if (key.GetValue("AutoPowerAC") is int apac) settings.AutoPowerAC = apac;
                        if (key.GetValue("AutoPowerBattery") is int apbat) settings.AutoPowerBattery = apbat;

                        if (key.GetValue("Fan") is int f) settings.FanMode = (byte)Math.Clamp(f, 0, 255);
                        if (key.GetValue("Fan_AC") is int fac) settings.FanModeAC = (byte)Math.Clamp(fac, 0, 255);
                        if (key.GetValue("Fan_Battery") is int fbat) settings.FanModeBattery = (byte)Math.Clamp(fbat, 0, 255);
                        if (key.GetValue("AutoFanAC") is int afac) settings.AutoFanAC = afac;
                        if (key.GetValue("AutoFanBattery") is int afbat) settings.AutoFanBattery = afbat;
                        if (key.GetValue("FanCurveEnabled") is int fce) settings.FanCurveEnabled = fce == 1;

                        if (key.GetValue("FanSpeedCpu") is int fsc) settings.FanSpeedCpu = fsc;
                        if (key.GetValue("FanSpeedGpu") is int fsg) settings.FanSpeedGpu = fsg;
                        if (key.GetValue("FanSpeedSys") is int fss) settings.FanSpeedSys = fss;
                        if (key.GetValue("FanSpeedCpuAC") is int fscac) settings.FanSpeedCpuAC = fscac;
                        if (key.GetValue("FanSpeedCpuBattery") is int fscbat) settings.FanSpeedCpuBattery = fscbat;
                        if (key.GetValue("FanSpeedGpuAC") is int fsgac) settings.FanSpeedGpuAC = fsgac;
                        if (key.GetValue("FanSpeedGpuBattery") is int fsgbat) settings.FanSpeedGpuBattery = fsgbat;
                        if (key.GetValue("FanSpeedSysAC") is int fssac) settings.FanSpeedSysAC = fssac;
                        if (key.GetValue("FanSpeedSysBattery") is int fssbat) settings.FanSpeedSysBattery = fssbat;

                        if (key.GetValue("RefreshRate") is int rr) settings.RefreshRate = rr;
                        if (key.GetValue("LcdOverdrive") is int od) settings.LcdOverdrive = od == 1;
                        if (key.GetValue("BatteryLimit") is int bl) settings.BatteryLimit = bl == 1;
                        if (key.GetValue("CoolBoost") is int cb) settings.CoolBoost = cb == 1;

                        if (key.GetValue("RGB_Mode") is int rm) settings.RgbMode = rm;
                        if (key.GetValue("Brightness") is int br) settings.RgbBrightness = br;
                        if (key.GetValue("RGB_Speed") is int sp) settings.RgbSpeed = sp;
                        if (key.GetValue("RGB_R") is int r) settings.RgbR = r;
                        if (key.GetValue("RGB_G") is int g) settings.RgbG = g;
                        if (key.GetValue("RGB_B") is int b) settings.RgbB = b;
                        if (key.GetValue("ModeKeyAction") is int mka) settings.ModeKeyAction = mka;
                        if (key.GetValue("TurboReturnMode") is int trm) settings.TurboReturnMode = (byte)Math.Clamp(trm, 0, 255);
                    }
                }
            }
            catch { }

            settings.Sanitize();
            return settings;
        }

        public void Save()
        {
            try
            {
                Sanitize();

                string path = SettingsFilePath;
                string dir = Path.GetDirectoryName(path)!;

                // Reparse point / directory junction defense:
                var dirInfo = new DirectoryInfo(dir);
                if (dirInfo.Exists && (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                lock (_fileLock)
                {
                    string json;
                    try
                    {
                        lock (this)
                        {
                            json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                        }
                    }
                    catch
                    {
                        var snapshot = CreateSnapshot();
                        json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                    }

                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
                        try
                        {
                            File.WriteAllText(tmp, json);
                            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                            {
                                try { File.Delete(tmp); } catch { }
                                return;
                            }
                            File.Move(tmp, path, true);
                            break;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 4)
                        {
                            System.Threading.Thread.Sleep(30);
                        }
                        finally
                        {
                            if (File.Exists(tmp))
                            {
                                try { File.Delete(tmp); } catch { }
                            }
                        }
                    }
                }
            }
            catch { }

            if (CustomFilePath != null) return;

            // Sync to Registry
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\PredatorControl");
                if (key != null)
                {
                    var savedTheme = ThemeManager.CurrentTheme != AppTheme.System ? ThemeManager.CurrentTheme.ToString() : Theme;
                    if (Enum.TryParse<AppTheme>(savedTheme, out var th))
                    {
                        key.SetValue("AppTheme", (int)th);
                        key.SetValue("Theme", savedTheme);
                    }

                    key.SetValue("Power", PowerMode);
                    if (PowerModeAC.HasValue) key.SetValue("Power_AC", PowerModeAC.Value);
                    if (PowerModeBattery.HasValue) key.SetValue("Power_Battery", PowerModeBattery.Value);
                    key.SetValue("AutoPowerAC", AutoPowerAC);
                    key.SetValue("AutoPowerBattery", AutoPowerBattery);

                    key.SetValue("ModeKeyAction", ModeKeyAction);
                    key.SetValue("TurboReturnMode", TurboReturnMode);

                    key.SetValue("Fan", FanMode);
                    if (FanModeAC.HasValue) key.SetValue("Fan_AC", FanModeAC.Value);
                    if (FanModeBattery.HasValue) key.SetValue("Fan_Battery", FanModeBattery.Value);
                    key.SetValue("AutoFanAC", AutoFanAC);
                    key.SetValue("AutoFanBattery", AutoFanBattery);
                    key.SetValue("FanCurveEnabled", FanCurveEnabled ? 1 : 0);

                    key.SetValue("FanSpeedCpu", FanSpeedCpu);
                    key.SetValue("FanSpeedGpu", FanSpeedGpu);
                    key.SetValue("FanSpeedSys", FanSpeedSys);
                    key.SetValue("FanSpeedCpuAC", FanSpeedCpuAC);
                    key.SetValue("FanSpeedCpuBattery", FanSpeedCpuBattery);
                    key.SetValue("FanSpeedGpuAC", FanSpeedGpuAC);
                    key.SetValue("FanSpeedGpuBattery", FanSpeedGpuBattery);
                    key.SetValue("FanSpeedSysAC", FanSpeedSysAC);
                    key.SetValue("FanSpeedSysBattery", FanSpeedSysBattery);

                    if (RefreshRate > 0) key.SetValue("RefreshRate", RefreshRate);
                    key.SetValue("LcdOverdrive", LcdOverdrive ? 1 : 0);
                    key.SetValue("BatteryLimit", BatteryLimit ? 1 : 0);
                    key.SetValue("CoolBoost", CoolBoost ? 1 : 0);

                    key.SetValue("RGB_Mode", RgbMode);
                    key.SetValue("Brightness", RgbBrightness);
                    key.SetValue("RGB_Speed", RgbSpeed);
                    key.SetValue("RGB_R", RgbR);
                    key.SetValue("RGB_G", RgbG);
                    key.SetValue("RGB_B", RgbB);
                }
            }
            catch { }

            try
            {
                using var hklmKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\PredatorControl");
                if (hklmKey != null)
                {
                    hklmKey.SetValue("Power", PowerMode);
                    hklmKey.SetValue("Fan", FanMode);
                    hklmKey.SetValue("LcdOverdrive", LcdOverdrive ? 1 : 0);
                }
            }
            catch { }
        }
    }
}
