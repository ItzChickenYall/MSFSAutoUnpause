using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace MSFSAutoUnpause;

public class AppSettings
{
    private const string RegKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RegName = "MSFSAutoUnpause";

    private static readonly string _settingsPath;

    static AppSettings()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MSFSAutoUnpause");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "settings.json");
    }

    // ── Settings properties ───────────────────────────────────────────────────

    public bool   Enabled                  { get; set; } = true;
    public int    PauseThresholdSeconds    { get; set; } = 30;
    public bool   NetworkWatchdogEnabled   { get; set; } = true;
    public int    NetworkCheckIntervalSecs { get; set; } = 60;
    public bool   StartWithWindows         { get; set; } = false;

    // ── Last unpause event (persisted so it survives restarts) ────────────────
    public DateTime? LastUnpauseUtc        { get; set; }
    public string?   LastUnpauseAircraft   { get; set; }
    public double    LastUnpauseAltitudeFt { get; set; }
    public double    LastUnpausePausedSecs { get; set; }

    // ── Persistence ───────────────────────────────────────────────────────────

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex) { Logger.Error($"Settings load failed: {ex.Message}"); }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(this, opts));
        }
        catch (Exception ex) { Logger.Error($"Settings save failed: {ex.Message}"); }

        ApplyStartWithWindows();
    }

    public void ApplyStartWithWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey, writable: true);
            if (key == null) return;

            if (StartWithWindows)
                key.SetValue(RegName, $"\"{System.Windows.Forms.Application.ExecutablePath}\"");
            else
                key.DeleteValue(RegName, throwOnMissingValue: false);
        }
        catch (Exception ex) { Logger.Error($"Registry update failed: {ex.Message}"); }
    }

    public static bool GetStartWithWindowsFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey);
            return key?.GetValue(RegName) != null;
        }
        catch { return false; }
    }
}
