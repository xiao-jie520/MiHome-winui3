using System.Text;
using System.Text.Json;
using MiHome.Core.Api;
using MiHome.Core.Models;

namespace MiHome.Core.Spec;

/// <summary>
/// 从 miot-spec.org 拉取设备规格（按 spec_type URN），本地文件缓存。
/// 移植自 mijiaAPI devices.py 的 get_device_info：services[].properties/actions
/// 归一化为 name/description/type/rw/range/value-list/method{siid,piid|aiid}，
/// 并做重名去重（同名追加 siid/piid 后缀）。
/// </summary>
public sealed class SpecService
{
    private const string DeviceUrl = "https://miot-spec.org/miot-spec-v2/instance";
    private const int Version = 3;

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public SpecService(string cacheDir)
    {
        _cacheDir = cacheDir;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MiHome-Windows-WinUI3/1.0");
    }

    private string CacheFile(string specType) =>
        Path.Combine(_cacheDir, specType.Replace(':', '_') + ".json");

    /// <summary>获取规格：缓存命中（版本匹配）直接返回，否则拉取并落盘。</summary>
    public async Task<SpecInfo> GetDeviceInfoAsync(string specType)
    {
        if (string.IsNullOrEmpty(specType))
        {
            throw new ArgumentException("必须提供 spec_type 参数（设备规格类型 URN）");
        }

        await _fileLock.WaitAsync();
        try
        {
            var cacheFile = CacheFile(specType);
            if (File.Exists(cacheFile))
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<SpecInfo>(File.ReadAllText(cacheFile), Json.Options);
                    if (cached?.Version == Version)
                    {
                        return cached;
                    }
                }
                catch
                {
                    // 缓存损坏：删掉重拉
                    try { File.Delete(cacheFile); } catch { /* ignore */ }
                }
            }

            var info = await FetchAsync(specType);
            SpecZh.ApplyFallback(info);
            Directory.CreateDirectory(_cacheDir);
            var tmp = cacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(info, Json.Options));
            File.Move(tmp, cacheFile, overwrite: true);
            return info;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<SpecInfo> FetchAsync(string specType)
    {
        using var resp = await _http.GetAsync($"{DeviceUrl}?type={Uri.EscapeDataString(specType)}");
        if (resp.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new ApiException(-1, $"无法获取设备规格：{specType}");
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("description", out _) ||
            !root.TryGetProperty("services", out _))
        {
            throw new ApiException(-1, $"设备规格格式异常：{specType}");
        }

        var info = new SpecInfo
        {
            Version = Version,
            Name = root.GetProperty("description").GetString() ?? "",
            SpecType = specType,
        };

        foreach (var svc in root.GetProperty("services").EnumerateArray())
        {
            var svcType = svc.GetProperty("type").GetString() ?? "";
            var parts = svcType.Split(':');
            if (parts.Length > 3 && parts[3] == "device-information")
            {
                continue;
            }
            int siid = svc.GetProperty("iid").GetInt32();

            if (svc.TryGetProperty("properties", out var props))
            {
                foreach (var prop in props.EnumerateArray())
                {
                    int piid = prop.GetProperty("iid").GetInt32();
                    string format = prop.GetProperty("format").GetString() ?? "string";
                    string propType = format.StartsWith("int") ? "int"
                        : format.StartsWith("uint") ? "uint"
                        : format;
                    var access = new List<string>();
                    if (prop.TryGetProperty("access", out var accArr))
                    {
                        foreach (var a in accArr.EnumerateArray())
                        {
                            access.Add(a.GetString() ?? "");
                        }
                    }
                    string rw = (access.Contains("read") ? "r" : "") + (access.Contains("write") ? "w" : "");
                    string name = TypeShortName(prop.GetProperty("type").GetString() ?? "");
                    string desc = prop.GetProperty("description").GetString() ?? "";
                    if (prop.TryGetProperty("comment", out var comment) && comment.ValueKind == JsonValueKind.String)
                    {
                        var c = comment.GetString();
                        if (!string.IsNullOrEmpty(c))
                        {
                            desc = $"{desc} / {c}";
                        }
                    }

                    var item = new SpecProp
                    {
                        Name = name,
                        Description = desc,
                        Type = propType,
                        Rw = rw,
                        Siid = siid,
                        Piid = piid,
                    };
                    if (prop.TryGetProperty("value-range", out var range) && range.ValueKind == JsonValueKind.Array)
                    {
                        item.Range = range.EnumerateArray().Select(v => v.TryGetDouble(out var d) ? d : 0).ToArray();
                    }
                    if (prop.TryGetProperty("value-list", out var vlist) && vlist.ValueKind == JsonValueKind.Array)
                    {
                        item.ValueList = vlist.EnumerateArray().Select(v =>
                        {
                            long value = v.TryGetProperty("value", out var val) ? val.GetInt64() : 0;
                            string vd = v.TryGetProperty("description", out var de) ? de.GetString() ?? "" : "";
                            string? zh = v.TryGetProperty("comment", out var cm) && cm.ValueKind == JsonValueKind.String
                                ? cm.GetString() : null;
                            return new ValueOption(value, vd, zh);
                        }).ToList();
                    }
                    info.Properties.Add(item);
                }
            }

            if (svc.TryGetProperty("actions", out var acts))
            {
                foreach (var act in acts.EnumerateArray())
                {
                    int aiid = act.GetProperty("iid").GetInt32();
                    string name = TypeShortName(act.GetProperty("type").GetString() ?? "");
                    string desc = act.GetProperty("description").GetString() ?? "";
                    if (act.TryGetProperty("comment", out var comment) && comment.ValueKind == JsonValueKind.String)
                    {
                        var c = comment.GetString();
                        if (!string.IsNullOrEmpty(c))
                        {
                            desc = $"{desc} / {c}";
                        }
                    }
                    info.Actions.Add(new SpecAction { Name = name, Description = desc, Siid = siid, Aiid = aiid });
                }
            }
        }

        DeduplicateNames(info.Properties.Select(p => (object)p).ToList(), "Piid", p => ((SpecProp)p).Name, (p, n) => ((SpecProp)p).Name = n);
        DeduplicateNames(info.Actions.Select(a => (object)a).ToList(), "Aiid", a => ((SpecAction)a).Name, (a, n) => ((SpecAction)a).Name = n);
        return info;
    }


    /// <summary>
    /// 从 spec URN 取短名：urn:miot-spec-v2:property:on:00000006:model:1 → "on"。
    /// 注意是第 4 段（索引 3），不是最后一段（最后一段是版本号）。
    /// </summary>
    private static string TypeShortName(string urn)
    {
        var parts = urn.Split(':');
        return parts.Length > 3 ? parts[3] : urn;
    }

    private static void DeduplicateNames(List<object> items, string iidKey,
        Func<object, string> getName, Action<object, string> setName)
    {
        void Dedup(string suffixKey)
        {
            var counts = items.GroupBy(getName).ToDictionary(g => g.Key, g => g.Count());
            foreach (var item in items.Where(i => counts[getName(i)] > 1))
            {
                int iid = suffixKey == "Piid"
                    ? ((SpecProp)item).Piid
                    : ((SpecAction)item).Aiid;
                setName(item, $"{getName(item)}-{iid}");
            }
        }
        // 与上游一致：先按 name 去重（加 siid），仍有重名再加 iid
        void DedupBySiid()
        {
            var counts = items.GroupBy(getName).ToDictionary(g => g.Key, g => g.Count());
            foreach (var item in items.Where(i => counts[getName(i)] > 1).ToList())
            {
                int siid = item is SpecProp p ? p.Siid : ((SpecAction)item).Siid;
                setName(item, $"{getName(item)}-{siid}");
            }
        }
        DedupBySiid();
        Dedup(iidKey);
    }
}
