using System.Text.RegularExpressions;
using MiHome.Core.Models;

namespace MiHome.Core.Stores;

/// <summary>应用设置（settings.json）。</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool VoiceFabEnabled { get; set; } = true;
    public string Theme { get; set; } = "system";
    public string ThemeAccent { get; set; } = "";
    public bool HideNoFuncDevices { get; set; }
    public bool ShowSceneTab { get; set; } = true;
    public string DefaultSpeakerDid { get; set; } = "";
    public string TrayAudioSpeakerDid { get; set; } = "";
    public double UiScale { get; set; } = 1.0;
    public int TrayColumns { get; set; } = 2;
    public bool CheckUpdateEnabled { get; set; } = true;
    public bool WidgetsBoardEnabled { get; set; }
    public bool WidgetsBoardTopmost { get; set; }
    public double WidgetsBoardOpacity { get; set; } = 0.9;
    public int WidgetsBoardColumns { get; set; } = 3;
}

/// <summary>设置存储：加载/保存 settings.json（线程安全）。</summary>
public static class SettingsStore
{
    private static readonly object Lock = new();
    private static AppSettings? _cache;

    public static string DataDir { get; set; } = ComputeDataDir();

    private static string ComputeDataDir()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            local = string.IsNullOrEmpty(profile) ? AppContext.BaseDirectory : Path.Combine(profile, "AppData", "Local");
        }
        return Path.Combine(local, "MiHome-Windows");
    }

    public static string DataFile(string name) => Path.Combine(DataDir, name);

    private static string FilePath => DataFile("settings.json");

    public static AppSettings Get()
    {
        lock (Lock)
        {
            if (_cache == null)
            {
                _cache = Json.Load<AppSettings>(FilePath) ?? new AppSettings();
                // 非法值收敛
                if (_cache.Theme is not ("system" or "light" or "dark"))
                {
                    _cache.Theme = "system";
                }
                if (_cache.ThemeAccent.Length != 7 || !_cache.ThemeAccent.StartsWith('#'))
                {
                    _cache.ThemeAccent = "";
                }
                _cache.UiScale = Math.Clamp(_cache.UiScale, 0.5, 2.0);
                if (_cache.TrayColumns is not (1 or 2))
                {
                    _cache.TrayColumns = 2;
                }
                if (_cache.WidgetsBoardColumns is < 1 or > 9)
                {
                    _cache.WidgetsBoardColumns = 3;
                }
                _cache.WidgetsBoardOpacity = Math.Clamp(_cache.WidgetsBoardOpacity, 0.1, 1.0);
            }
            return _cache;
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Lock)
        {
            _cache = settings;
            Json.Save(FilePath, settings);
        }
    }
}

/// <summary>定时开关规则存储（schedules.json）。</summary>
public static class ScheduleStore
{
    private static readonly object Lock = new();

    private static string FilePath => SettingsStore.DataFile("schedules.json");

    public sealed class ScheduleFile
    {
        public int Version { get; set; } = 1;
        public List<ScheduleRule> Rules { get; set; } = new();
    }

    public static string? NormalizeTime(string? value)
    {
        if (value == null || !value.Contains(':'))
        {
            return null;
        }
        var parts = value.Trim().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var hour) || !int.TryParse(parts[1], out var minute))
        {
            return null;
        }
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            return null;
        }
        return $"{hour:00}:{minute:00}";
    }

    private static ScheduleRule? Normalize(ScheduleRule? rule)
    {
        if (rule == null || string.IsNullOrWhiteSpace(rule.Did) || NormalizeTime(rule.Time) is not { } time)
        {
            return null;
        }
        if (rule.Action is not ("on" or "off"))
        {
            return null;
        }
        return new ScheduleRule
        {
            Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N")[..12] : rule.Id,
            Did = rule.Did,
            Time = time,
            Action = rule.Action,
            Enabled = rule.Enabled,
            LastFired = rule.LastFired ?? "",
        };
    }

    public static List<ScheduleRule> Load()
    {
        lock (Lock)
        {
            var raw = Json.Load<ScheduleFile>(FilePath);
            if (raw == null || raw.Version != 1)
            {
                return new List<ScheduleRule>();
            }
            return raw.Rules.Select(Normalize).Where(r => r != null).Cast<ScheduleRule>()
                .OrderBy(r => r.Time).ThenBy(r => r.Did).ToList();
        }
    }

    public static void Save(IEnumerable<ScheduleRule> rules)
    {
        lock (Lock)
        {
            var cleaned = rules.Select(Normalize).Where(r => r != null).Cast<ScheduleRule>()
                .GroupBy(r => r.Id).Select(g => g.First()).ToList();
            Json.Save(FilePath, new ScheduleFile { Rules = cleaned });
        }
    }

    public static ScheduleRule? Add(string did, string time, string action)
    {
        var timeText = NormalizeTime(time);
        if (timeText == null || action is not ("on" or "off") || string.IsNullOrWhiteSpace(did))
        {
            return null;
        }
        var rule = new ScheduleRule
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Did = did,
            Time = timeText,
            Action = action,
            Enabled = true,
            // 今天该时刻已过：last_fired 记为今天，从明天开始生效
            LastFired = string.CompareOrdinal(timeText, DateTime.Now.ToString("HH:mm")) <= 0
                ? DateTime.Now.ToString("yyyy-MM-dd") : "",
        };
        var rules = Load();
        rules.Add(rule);
        Save(rules);
        return rule;
    }

    public static bool Remove(string ruleId)
    {
        var rules = Load();
        var kept = rules.Where(r => r.Id != ruleId).ToList();
        if (kept.Count == rules.Count)
        {
            return false;
        }
        Save(kept);
        return true;
    }

    public static bool SetEnabled(string ruleId, bool enabled)
    {
        var rules = Load();
        var hit = rules.FirstOrDefault(r => r.Id == ruleId);
        if (hit == null)
        {
            return false;
        }
        hit.Enabled = enabled;
        Save(rules);
        return true;
    }

    public static void MarkFired(string ruleId, string dateText)
    {
        var rules = Load();
        var hit = rules.FirstOrDefault(r => r.Id == ruleId);
        if (hit != null)
        {
            hit.LastFired = dateText;
            Save(rules);
        }
    }
}

/// <summary>
/// 定时开关判定逻辑（纯函数）。只认"正点那一分钟"，不补执行；
/// 错过的规则单独报出；同一天只执行一次由 last_fired 保证。
/// </summary>
public sealed class ScheduleEngine
{
    private List<ScheduleRule> _rules = new();
    private readonly HashSet<(string Id, string Date)> _reportedMiss = new();

    public void SetRules(List<ScheduleRule> rules) => _rules = rules ?? new List<ScheduleRule>();

    public List<ScheduleRule> Rules => _rules.ToList();

    public (List<ScheduleRule> Due, List<ScheduleRule> Missed) Tick(DateTime? now = null)
    {
        var moment = now ?? DateTime.Now;
        var today = moment.ToString("yyyy-MM-dd");
        var hhmm = moment.ToString("HH:mm");
        var due = new List<ScheduleRule>();
        var missed = new List<ScheduleRule>();
        foreach (var rule in _rules)
        {
            if (!rule.Enabled || rule.LastFired == today)
            {
                continue;
            }
            if (rule.Time == hhmm)
            {
                due.Add(rule);
            }
            else if (string.CompareOrdinal(rule.Time, hhmm) < 0)
            {
                if (_reportedMiss.Add((rule.Id, today)))
                {
                    missed.Add(rule);
                }
            }
        }
        return (due, missed);
    }

    public void MarkFired(string ruleId, DateTime? now = null)
    {
        var today = (now ?? DateTime.Now).ToString("yyyy-MM-dd");
        foreach (var rule in _rules)
        {
            if (rule.Id == ruleId)
            {
                rule.LastFired = today;
                return;
            }
        }
    }
}

/// <summary>GitHub Releases 新版本检测。</summary>
public static class UpdateChecker
{
    public const string CurrentVersion = "1.0.0";
    public const string RepoUrl = "https://github.com/huanyuejue/MiHome-Windows";

    public sealed record UpdateInfo(string LatestVersion, string Url, string Notes);

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MiHome-Windows-WinUI3");
            using var resp = await http.GetAsync("https://api.github.com/repos/huanyuejue/MiHome-Windows/releases/latest");
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (tag.TrimStart('v') == CurrentVersion)
            {
                return null;
            }
            return new UpdateInfo(
                tag,
                root.TryGetProperty("html_url", out var u) ? u.GetString() ?? RepoUrl : RepoUrl,
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "");
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>开机自启动（注册表 HKCU Run）。</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MiHome-Windows-WinUI3";

    public static bool Get()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            var value = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            var target = ParseTarget(value);
            return target != null && File.Exists(target);
        }
        catch
        {
            return false;
        }
    }

    private static string? ParseTarget(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            return null;
        }
        if (value.StartsWith('"'))
        {
            int end = value.IndexOf('"', 1);
            return end == -1 ? null : value[1..end];
        }
        return value.Split(' ', 2)[0];
    }

    public static void Set(bool value)
    {
        if (value)
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        }
        else
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            catch
            {
                // ignore
            }
        }
    }
}
