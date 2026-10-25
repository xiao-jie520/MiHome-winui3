using System.Text.Json;
using System.Text.RegularExpressions;
using MiHome.Core.Api;
using MiHome.Core.Crypto;
using MiHome.Core.Models;
using MiHome.Core.Spec;

namespace MiHome.Core;

/// <summary>
/// 业务服务层：设备列表/详情、开关状态批量读写、温湿度读数、场景、
/// 耗材、图标。移植自原项目 app/core/service.py（MijiaService）。
/// </summary>
public sealed partial class MiHomeService
{
    private const int BatchSize = 20;
    private const int SpecWorkers = 8;

    [GeneratedRegex(@"\.s(\d+)$")]
    private static partial Regex SubDeviceSuffix();

    private readonly MijiaApiClient _api;
    private readonly SpecService _specService;

    // did -> (model, spec_type, name) 索引（UI 与轮询线程并发访问，必须用并发字典）
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Model, string SpecType, string Name)> _deviceIndex = new();
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    // spec_type -> spec 内存缓存（并发字典：首屏加载与轮询会同时读）
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SpecInfo?> _specCache = new();
    // spec_type -> 进行中的拉取任务（并发去重，不同 spec 并行拉取）
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<SpecInfo?>> _specTasks = new();
    private readonly object _specLock = new();
    // did -> 详情缓存（含 props/actions）
    private readonly Dictionary<string, DeviceDetail> _detailCache = new();
    // model -> 图标 URL 缓存
    private readonly Dictionary<string, string?> _iconCache = new();
    private readonly Dictionary<string, string> _iconFiles = new();
    // 家庭名 -> 家庭 ID
    private Dictionary<string, string>? _homeIds;

    public MiHomeService(MijiaApiClient api)
    {
        _api = api;
        _specService = new SpecService(MijiaApiClient.SpecCacheDir);
    }

    public bool LoginStatus()
    {
        // 只看本地凭据是否存在：不做网络探测，避免网络抖动导致界面误判为“未登录”
        return _api.HasAuth;
    }

    /// <summary>获取扫码登录数据；返回 null 表示本地凭据已刷新、无需扫码。</summary>
    public async Task<Dictionary<string, object?>?> QrLoginBeginAsync()
    {
        try
        {
            return await _api.GetQrLoginDataAsync();
        }
        catch (Exception exc)
        {
            throw WrapError(exc, "获取登录二维码失败");
        }
    }

    public async Task QrLoginWaitAsync(Dictionary<string, object?> loginData)
    {
        try
        {
            await _api.CompleteQrLoginAsync(loginData);
        }
        catch (Exception exc)
        {
            throw WrapError(exc, "扫码登录未完成");
        }
    }

    public void Logout() => _api.Logout();

    // ---------- 设备列表 ----------

    /// <summary>拉取全部家庭和共享设备并补齐房间归属。</summary>
    public async Task<List<DeviceInfo>> ListDevicesAsync()
    {
        List<Dictionary<string, JsonElement>> homes;
        List<Dictionary<string, JsonElement>> devices;
        try
        {
            homes = await _api.GetHomesListAsync();
            devices = await _api.GetDevicesListAsync();
            devices.AddRange(await _api.GetSharedDevicesListAsync());
        }
        catch (Exception exc)
        {
            throw WrapError(exc, "获取设备列表失败");
        }

        var location = new Dictionary<string, (string Home, string Room)>();
        foreach (var home in homes)
        {
            var homeName = Str(home, "name");
            if (home.TryGetValue("roomlist", out var rooms) && rooms.ValueKind == JsonValueKind.Array)
            {
                foreach (var room in rooms.EnumerateArray())
                {
                    var roomName = room.TryGetProperty("name", out var rn) && rn.ValueKind == JsonValueKind.String ? rn.GetString() ?? "" : "";
                    if (room.TryGetProperty("dids", out var dids) && dids.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var did in dids.EnumerateArray())
                        {
                            location[did.ToString()] = (homeName, roomName);
                        }
                    }
                }
            }
        }

        var result = devices.Select(d =>
        {
            var did = d["did"].ToString();
            var (homeName, roomName) = location.GetValueOrDefault(did, ("未知", "未知"));
            return new DeviceInfo(
                Did: did,
                Name: Str(d, "name") is { Length: > 0 } dn ? dn : did,
                Model: Str(d, "model"),
                HomeName: homeName,
                RoomName: roomName,
                Online: d.TryGetValue("isOnline", out var o) && o.ValueKind == JsonValueKind.True,
                SpecType: d.TryGetValue("spec_type", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "" : "");
        }).ToList();
        return result.OrderBy(x => x.HomeName).ThenBy(x => x.RoomName).ThenBy(x => x.Name).ToList();
    }

    // ---------- 设备控制 ----------

    public async Task<DeviceDetail> DeviceDetailAsync(string did)
    {
        if (_detailCache.TryGetValue(did, out var cached))
        {
            return cached;
        }

        var entry = await EnsureIndexedAsync(did);
        if (entry == default || string.IsNullOrEmpty(entry.SpecType))
        {
            throw new ServiceError("该设备无公开的功能规格（常见于仅蓝牙连接的产品），无法提供控制面板");
        }

        SpecInfo? spec;
        try
        {
            spec = await GetSpecAsync(entry.SpecType);
            if (spec == null)
            {
                throw new ServiceError("该设备无公开的功能规格（常见于仅蓝牙连接的产品），无法提供控制面板");
            }
        }
        catch (ServiceError) { throw; }
        catch (Exception exc)
        {
            throw WrapError(exc, "加载设备信息失败");
        }

        var props = spec.Properties.Select(p => new PropInfo(
            Name: p.Name,
            Desc: string.IsNullOrEmpty(p.Description) ? p.Name : p.Description,
            Type: p.Type,
            Readable: p.Rw.Contains('r'),
            Writable: p.Rw.Contains('w'),
            Range: p.Range,
            ValueList: p.ValueList)
        {
            Siid = p.Siid,
            Piid = p.Piid,
        }).ToList();

        var actions = spec.Actions.Select(a => new ActionInfo(
            Name: a.Name,
            Desc: string.IsNullOrEmpty(a.Description) ? a.Name : a.Description,
            Siid: a.Siid,
            Aiid: a.Aiid,
            InParams: null)).ToList();

        var detail = new DeviceDetail(did, entry.Name ?? spec.Name, entry.Model, props, actions);
        _detailCache[did] = detail;
        return detail;
    }

    /// <summary>批量读取同一台设备的多个属性（一次请求替代逐个轮询）。</summary>
    public async Task<Dictionary<string, object?>> ReadPropsAsync(string did, List<PropInfo> names)
    {
        var queries = new List<object>();
        var keyToName = new Dictionary<(int Siid, int Piid), string>();
        foreach (var prop in names)
        {
            if (!prop.Readable)
            {
                continue;
            }
            queries.Add(new Dictionary<string, object>
            {
                ["did"] = did, ["siid"] = prop.Siid, ["piid"] = prop.Piid,
            });
            keyToName[(prop.Siid, prop.Piid)] = prop.Name;
        }

        var result = keyToName.Values.ToDictionary(n => n, _ => (object?)null);
        for (int start = 0; start < queries.Count; start += BatchSize)
        {
            var batch = queries.Skip(start).Take(BatchSize).ToList();
            JsonElement rets;
            try
            {
                rets = await _api.GetDevicesPropAsync(batch);
            }
            catch (Exception exc)
            {
                throw WrapError(exc, "批量读取属性失败");
            }
            if (rets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var item in rets.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                int siid = item.TryGetProperty("siid", out var s) ? s.GetInt32() : -1;
                int piid = item.TryGetProperty("piid", out var p) ? p.GetInt32() : -1;
                if (keyToName.TryGetValue((siid, piid), out var name) &&
                    item.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() == 0 &&
                    item.TryGetProperty("value", out var value))
                {
                    result[name] = JsonElementToObject(value);
                }
            }
        }
        return result;
    }

    /// <summary>写入单个属性。</summary>
    public async Task WritePropAsync(string did, PropInfo prop, object value)
    {
        try
        {
            var rets = await _api.SetDevicesPropAsync(new List<object>
            {
                new Dictionary<string, object> { ["did"] = did, ["siid"] = prop.Siid, ["piid"] = prop.Piid, ["value"] = value },
            });
            VerifyWriteResult(rets, $"设置属性 {prop.Name} 失败");
        }
        catch (ServiceError) { throw; }
        catch (Exception exc)
        {
            throw WrapError(exc, $"设置属性 {prop.Name} 失败");
        }
    }

    /// <summary>执行动作。params 支持文本指令（execute-text-directive 等 _in 通道）。</summary>
    public async Task RunActionAsync(string did, ActionInfo action, object? parameters = null)
    {
        object param;
        if (parameters != null)
        {
            if (action.Name is "execute-text-directive" or "play-text" or "play-music" or "play-radio")
            {
                // 文本类指令统一按 _in 通道传递，execute-text-directive 缺省补非静默 0
                var inVal = parameters is System.Collections.IEnumerable list and not string
                    ? list.Cast<object>().ToList()
                    : new List<object> { parameters };
                if (action.Name == "execute-text-directive" && inVal.Count == 1)
                {
                    inVal.Add(0);
                }
                param = new Dictionary<string, object>
                {
                    ["did"] = did, ["siid"] = action.Siid, ["aiid"] = action.Aiid, ["in"] = inVal,
                };
            }
            else
            {
                param = new Dictionary<string, object>
                {
                    ["did"] = did, ["siid"] = action.Siid, ["aiid"] = action.Aiid,
                };
            }
        }
        else
        {
            if (action.Name is "execute-text-directive" or "play-text")
            {
                throw new ServiceError($"动作 {action.Name} 需要文本参数");
            }
            param = new Dictionary<string, object>
            {
                ["did"] = did, ["siid"] = action.Siid, ["aiid"] = action.Aiid,
            };
        }

        try
        {
            var ret = await _api.RunActionAsync(param);
            int code = ret.ValueKind == JsonValueKind.Object && ret.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
            if (code is not (0 or 1))
            {
                throw new ServiceError($"执行动作 {action.Name} 失败（错误码 {code}）");
            }
        }
        catch (ServiceError) { throw; }
        catch (Exception exc)
        {
            throw WrapError(exc, $"执行动作 {action.Name} 失败");
        }
    }

    // ---------- 开关状态（卡片快速控制） ----------

    public async Task<bool?> PowerStateAsync(string did)
        => (await PowerStatesAsync(new List<string> { did })).GetValueOrDefault(did);

    /// <summary>写入开关状态（spec method 直写批量接口，一次请求完成）。</summary>
    public async Task SetPowerStateAsync(string did, bool state)
    {
        var info = await SpecsForAsync(new List<string> { did });
        var method = info.TryGetValue(did, out var spec) && spec != null ? FindOnMethod(spec, did) : null;
        if (method == null)
        {
            throw new ServiceError("设备不支持开关控制");
        }
        await WritePowerAsync(SwitchTarget(did), method, state);
    }

    private async Task WritePowerAsync(string did, SpecProp method, bool state)
    {
        try
        {
            var rets = await _api.SetDevicesPropAsync(new List<object>
            {
                new Dictionary<string, object>
                {
                    ["did"] = did, ["siid"] = method.Siid, ["piid"] = method.Piid, ["value"] = state,
                },
            });
            VerifyWriteResult(rets, "设置开关状态失败");
        }
        catch (ServiceError) { throw; }
        catch (Exception exc)
        {
            throw WrapError(exc, "设置开关状态失败");
        }
    }

    private static void VerifyWriteResult(JsonElement rets, string context)
    {
        var items = rets.ValueKind == JsonValueKind.Array
            ? rets.EnumerateArray().ToList()
            : new List<JsonElement> { rets };
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            int code = item.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
            if (code is 0 or 1)
            {
                continue;
            }
            string msg = "未知错误";
            if (item.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                msg = m.GetString()!;
            }
            throw new ServiceError($"{context}（{msg}，错误码 {code}）");
        }
    }

    /// <summary>批量读取多台设备的开关状态。</summary>
    public async Task<Dictionary<string, bool?>> PowerStatesAsync(List<string> dids)
    {
        var specMap = await SpecsForAsync(dids);

        var queries = new List<object>();
        var owners = new List<string>();
        var methodOf = new Dictionary<string, SpecProp>();
        var targetOf = new Dictionary<string, string>();
        var result = new Dictionary<string, bool?>();

        foreach (var did in dids)
        {
            var info = specMap.GetValueOrDefault(did);
            var method = info != null ? FindOnMethod(info, did) : null;
            if (method == null)
            {
                result[did] = null;
                continue;
            }
            methodOf[did] = method;
            var target = SwitchTarget(did);
            targetOf[did] = target;
            queries.Add(new Dictionary<string, object>
            {
                ["did"] = target, ["siid"] = method.Siid, ["piid"] = method.Piid,
            });
            owners.Add(did);
        }

        for (int start = 0; start < queries.Count; start += BatchSize)
        {
            var batch = queries.Skip(start).Take(BatchSize).ToList();
            var batchOwners = owners.Skip(start).Take(BatchSize).ToList();
            JsonElement rets;
            try
            {
                rets = await _api.GetDevicesPropAsync(batch);
            }
            catch (Exception exc)
            {
                throw WrapError(exc, "批量读取设备状态失败");
            }
            if (rets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            // 响应按 (did, siid) 回填：同一父设备的多路共用一个 did
            var byKey = new Dictionary<(string Did, int Siid), JsonElement>();
            var list = rets.EnumerateArray().ToList();
            foreach (var item in list)
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    byKey[(item.TryGetProperty("did", out var d) ? d.GetString() ?? "" : "",
                           item.TryGetProperty("siid", out var s) ? s.GetInt32() : -1)] = item;
                }
            }
            for (int i = 0; i < batchOwners.Count; i++)
            {
                var owner = batchOwners[i];
                var method = methodOf[owner];
                if (!byKey.TryGetValue((targetOf[owner], method.Siid), out var item) &&
                    i < list.Count)
                {
                    item = list[i]; // 响应缺 siid 时按顺序对齐
                }
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() == 0 &&
                    item.TryGetProperty("value", out var value))
                {
                    result[owner] = value.ValueKind == JsonValueKind.True;
                }
                else
                {
                    result[owner] = null; // 读失败不能记成"无开关能力"，交由调用方处理
                }
            }
        }
        return result;
    }

    // ---------- 虚拟子设备 ----------

    /// <summary>虚拟子设备（&lt;父did&gt;.sN）的请求一律发给父设备。</summary>
    private static string SwitchTarget(string did)
    {
        var m = SubDeviceSuffix().Match(did);
        return m.Success ? did[..m.Index] : did;
    }

    private static int? GangFromDid(string did)
    {
        var m = SubDeviceSuffix().Match(did);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>挑出该设备可写的 on 属性；多键面板按 did 后缀选对应那一路。</summary>
    private static SpecProp? FindOnMethod(SpecInfo? info, string? did = null)
    {
        if (info == null)
        {
            return null;
        }
        var props = info.Properties
            .Where(p => p.Type == "bool" && p.Rw.Contains('w') &&
                        (p.Name == "on" || p.Name.Replace('_', '-').StartsWith("on-")))
            .ToList();
        if (props.Count == 0)
        {
            return null;
        }
        var gang = did != null ? GangFromDid(did) : null;
        if (gang != null)
        {
            var hit = props.FirstOrDefault(p => p.Siid == gang.Value);
            if (hit != null)
            {
                return hit;
            }
            // 后缀编号与 siid 对不上时按顺序兜底：s2→第 1 路、s3→第 2 路…
            int index = gang.Value - 2;
            if (index >= 0 && index < props.Count)
            {
                return props[index];
            }
        }
        return props[0];
    }

    // ---------- 环境读数 ----------

    /// <summary>批量读取温湿度并拼成副标题文案（如 "28.3°C 60%"）。</summary>
    public async Task<Dictionary<string, string?>> ReadMetricsAsync(List<string> dids)
    {
        var specMap = await SpecsForAsync(dids);
        var queries = new List<object>();
        var keyToMetric = new Dictionary<(string Did, int Siid, int Piid), (string Did, string Metric)>();
        var result = new Dictionary<string, string?>();

        foreach (var did in dids)
        {
            var info = specMap.GetValueOrDefault(did);
            var found = new Dictionary<string, SpecProp>();
            if (info != null)
            {
                foreach (var prop in info.Properties)
                {
                    string? metric = prop.Name == "temperature" ? "temperature"
                        : prop.Name is "relative-humidity" or "humidity" ? "humidity"
                        : null;
                    if (metric != null && prop.Rw.Contains('r') && !found.ContainsKey(metric))
                    {
                        found[metric] = prop;
                    }
                }
            }
            if (found.Count == 0)
            {
                result[did] = null;
                continue;
            }
            foreach (var (metric, prop) in found)
            {
                queries.Add(new Dictionary<string, object>
                {
                    ["did"] = did, ["siid"] = prop.Siid, ["piid"] = prop.Piid,
                });
                keyToMetric[(did, prop.Siid, prop.Piid)] = (did, metric);
            }
        }

        var temps = new Dictionary<string, double>();
        var hums = new Dictionary<string, double>();
        for (int start = 0; start < queries.Count; start += BatchSize)
        {
            var batch = queries.Skip(start).Take(BatchSize).ToList();
            JsonElement rets;
            try
            {
                rets = await _api.GetDevicesPropAsync(batch);
            }
            catch
            {
                return dids.ToDictionary(d => d, _ => (string?)null);
            }
            if (rets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var item in rets.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var key = (item.TryGetProperty("did", out var d) ? d.GetString() ?? "" : "",
                           item.TryGetProperty("siid", out var s) ? s.GetInt32() : -1,
                           item.TryGetProperty("piid", out var p) ? p.GetInt32() : -1);
                if (!keyToMetric.TryGetValue(key, out var entry))
                {
                    continue;
                }
                if (item.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() != 0)
                {
                    continue;
                }
                if (!item.TryGetProperty("value", out var value) || !value.TryGetDouble(out var num))
                {
                    continue;
                }
                if (entry.Metric == "temperature")
                {
                    temps[entry.Did] = num;
                }
                else
                {
                    hums[entry.Did] = num;
                }
            }
        }

        foreach (var did in dids)
        {
            result[did] = FormatMetricsText(
                temps.GetValueOrDefault(did, double.NaN),
                hums.GetValueOrDefault(did, double.NaN),
                temps.ContainsKey(did), hums.ContainsKey(did));
        }
        return result;
    }

    public static string? FormatMetricsText(double temp, double hum, bool hasTemp, bool hasHum)
    {
        var parts = new List<string>();
        if (hasTemp)
        {
            // SI 规范里 temperature 常以 0.1°C 步进整数存储（283 = 28.3）
            if (Math.Abs(temp) > 60)
            {
                temp /= 10;
            }
            parts.Add($"{temp:F1}°C");
        }
        if (hasHum)
        {
            if (hum > 100)
            {
                hum /= 10;
            }
            parts.Add($"{hum:F0}%");
        }
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    // ---------- 耗材 ----------

    public async Task<List<ConsumableItem>> ConsumableItemsAsync()
    {
        List<Dictionary<string, JsonElement>> rawItems;
        try
        {
            rawItems = await _api.GetConsumableItemsAsync();
        }
        catch
        {
            return new List<ConsumableItem>();
        }
        var result = new List<ConsumableItem>();
        foreach (var item in rawItems)
        {
            if (item.TryGetValue("is_ignore", out var ig) && ig.ValueKind == JsonValueKind.True)
            {
                continue;
            }
            if (!item.TryGetValue("details", out var detailEl) || detailEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var detail = detailEl;
            if (!detail.TryGetProperty("value", out var valueEl) || !valueEl.TryGetDouble(out var value))
            {
                continue;
            }
            double? threshold = null;
            if (detail.TryGetProperty("inadeq", out var inadeq) && inadeq.ValueKind == JsonValueKind.String)
            {
                try
                {
                    using var doc = JsonDocument.Parse(inadeq.GetString() ?? "{}");
                    if (doc.RootElement.TryGetProperty("val", out var val))
                    {
                        threshold = val.GetDouble();
                    }
                }
                catch { /* ignore */ }
            }
            result.Add(new ConsumableItem(
                Did: item.TryGetValue("did", out var did) ? did.ToString() : "",
                Name: item.TryGetValue("name", out var n) ? n.ToString() ?? "" : "",
                Model: item.TryGetValue("model", out var m) ? m.ToString() ?? "" : "",
                Description: detail.TryGetProperty("description", out var desc) ? desc.ToString() ?? "" : "",
                Type: detail.TryGetProperty("type_name", out var tn) ? tn.ToString() ?? "" : "",
                Value: value,
                Threshold: threshold,
                Low: threshold.HasValue && value < threshold.Value,
                Updated: detail.TryGetProperty("update_time", out var ut) && ut.TryGetInt64(out var l) ? l : 0));
        }
        return result;
    }

    // ---------- 场景 ----------

    private async Task<Dictionary<string, string>> HomeIdsMapAsync()
    {
        if (_homeIds == null)
        {
            List<Dictionary<string, JsonElement>> homes;
            try
            {
                homes = await _api.GetHomesListAsync();
            }
            catch (Exception exc)
            {
                throw WrapError(exc, "获取家庭列表失败");
            }
            _homeIds = homes.ToDictionary(
                h => Str(h, "name"),
                h => h.TryGetValue("id", out var i) ? i.ToString() ?? "" : "");
        }
        return _homeIds;
    }

    public async Task<List<SceneInfo>> SceneListAsync(string? homeName = null)
    {
        string? homeId = null;
        var homeMap = await HomeIdsMapAsync();
        if (homeName != null)
        {
            if (!homeMap.TryGetValue(homeName, out homeId))
            {
                return new List<SceneInfo>();
            }
        }
        List<Dictionary<string, JsonElement>> scenes;
        try
        {
            scenes = await _api.GetScenesListAsync(homeId);
        }
        catch (Exception exc)
        {
            throw WrapError(exc, "获取场景列表失败");
        }
        var idToName = homeMap.ToDictionary(kv => kv.Value, kv => kv.Key);
        return scenes.Select(s => new SceneInfo(
            SceneId: s.TryGetValue("scene_id", out var sid) ? sid.ToString() ?? "" : "",
            Name: Str(s, "name") is { Length: > 0 } sn ? sn : "未命名场景",
            HomeId: s.TryGetValue("home_id", out var hid) ? hid.ToString() ?? homeId ?? "" : homeId ?? "",
            HomeName: idToName.TryGetValue(s.TryGetValue("home_id", out var h2) ? h2.ToString() ?? "" : "", out var hn) ? hn : "")).ToList();
    }

    public async Task RunSceneAsync(SceneInfo scene)
    {
        try
        {
            var ok = await _api.RunSceneAsync(scene.SceneId, scene.HomeId);
            if (!ok)
            {
                throw new ServiceError($"执行场景「{scene.Name}」失败");
            }
        }
        catch (ServiceError) { throw; }
        catch (Exception exc)
        {
            throw WrapError(exc, $"执行场景「{scene.Name}」失败");
        }
    }

    // ---------- 设备图标 ----------

    /// <summary>model -> 图标 URL，增量拉取。</summary>
    public async Task<Dictionary<string, string>> IconUrlsAsync(List<string> models)
    {
        if (models.Count == 0)
        {
            return new Dictionary<string, string>();
        }
        var missing = models.Where(m => !_iconCache.ContainsKey(m)).Distinct().ToList();
        foreach (var model in missing)
        {
            _iconCache[model] = await _api.GetIconUrlAsync(model);
        }
        return models.Where(m => _iconCache.GetValueOrDefault(m) != null)
            .ToDictionary(m => m, m => _iconCache[m]!);
    }

    /// <summary>下载图标到本地缓存并返回文件路径；失败返回 null。</summary>
    public async Task<string?> IconFileAsync(string model, string url)
    {
        if (_iconFiles.TryGetValue(model, out var cached) && File.Exists(cached))
        {
            return cached;
        }
        var path = Path.Combine(MijiaApiClient.SpecCacheDir, "icons", $"{model.Replace(':', '_')}.png");
        if (File.Exists(path))
        {
            _iconFiles[model] = path;
            return path;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var bytes = await http.GetByteArrayAsync(url);
            if (bytes.Length == 0)
            {
                return null;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            _iconFiles[model] = path;
            return path;
        }
        catch
        {
            return null;
        }
    }

    // ---------- 内部工具 ----------

    private async Task<(string Model, string SpecType, string Name)> EnsureIndexedAsync(string did)
    {
        if (!_deviceIndex.TryGetValue(did, out var entry) || entry == default)
        {
            await RefreshDeviceIndexAsync();
            _deviceIndex.TryGetValue(did, out entry);
        }
        return entry;
    }

    private async Task RefreshDeviceIndexAsync()
    {
        await _indexLock.WaitAsync();
        try
        {
            await RefreshDeviceIndexCoreAsync();
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task RefreshDeviceIndexCoreAsync()
    {
        List<Dictionary<string, JsonElement>> all;
        try
        {
            all = await _api.GetDevicesListAsync();
            all.AddRange(await _api.GetSharedDevicesListAsync());
        }
        catch (Exception exc)
        {
            throw WrapError(exc, "获取设备列表失败");
        }
        foreach (var d in all)
        {
            var did = d["did"].ToString();
            _deviceIndex[did] = (
                Str(d, "model"),
                Str(d, "spec_type"),
                Str(d, "name"));
        }
    }

    /// <summary>
    /// 取规格：同一 spec_type 的并发请求合并为一个任务，不同 spec 并行拉取。
    /// 原实现用信号量包住整个网络调用，会把并行的 spec 抓取串行化（首屏慢数倍）。
    /// </summary>
    private Task<SpecInfo?> GetSpecAsync(string specType)
    {
        if (string.IsNullOrEmpty(specType))
        {
            return Task.FromResult<SpecInfo?>(null);
        }
        lock (_specLock)
        {
            if (_specCache.TryGetValue(specType, out var cached))
            {
                return Task.FromResult(cached);
            }
            if (_specTasks.TryGetValue(specType, out var running))
            {
                return running;
            }
            var task = LoadSpecAsync(specType);
            _specTasks[specType] = task;
            return task;
        }
    }

    private async Task<SpecInfo?> LoadSpecAsync(string specType)
    {
        SpecInfo? info = null;
        try
        {
            info = await _specService.GetDeviceInfoAsync(specType);
        }
        catch
        {
            info = null; // 个别规格在 miot-spec.org 不存在，按无能力处理
        }
        lock (_specLock)
        {
            _specCache[specType] = info;
            _specTasks.TryRemove(specType, out _);
        }
        return info;
    }

    private async Task<Dictionary<string, SpecInfo?>> SpecsForAsync(List<string> dids)
    {
        if (dids.Any(d => !_deviceIndex.ContainsKey(d)))
        {
            try
            {
                await RefreshDeviceIndexAsync();
            }
            catch (ServiceError)
            {
                // 列表拉取失败时已有索引仍然可用
            }
        }

        var pending = new Dictionary<string, Task<SpecInfo?>>();
        foreach (var did in dids)
        {
            var specType = _deviceIndex.GetValueOrDefault(did).SpecType;
            if (!string.IsNullOrEmpty(specType) && !pending.ContainsKey(specType))
            {
                pending[specType] = GetSpecAsync(specType);
            }
        }
        if (pending.Count > 0)
        {
            await Task.WhenAll(pending.Values);
        }

        var result = new Dictionary<string, SpecInfo?>();
        foreach (var did in dids)
        {
            var specType = _deviceIndex.GetValueOrDefault(did).SpecType;
            result[did] = string.IsNullOrEmpty(specType)
                ? null
                : pending.TryGetValue(specType, out var task) ? task.Result : _specCache.GetValueOrDefault(specType);
        }
        return result;
    }

    /// <summary>安全取字符串字段：JSON null / 缺失 / 非字符串一律返回空串。</summary>
    private static string Str(Dictionary<string, JsonElement> dict, string key)
        => dict.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
        JsonValueKind.String => el.GetString(),
        _ => el.ToString(),
    };

    internal static ServiceError WrapError(Exception exc, string context)
        => new($"{context}: {exc.Message}");

    /// <summary>判断是否小爱音箱（音频控制栏、语音入口共用标准）。</summary>
    public static bool IsSpeaker(DeviceInfo device) => device.Model.Contains("wifispeaker");
}
