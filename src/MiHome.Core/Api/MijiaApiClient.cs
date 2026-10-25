using System.Net;
using System.Text;
using System.Text.Json;
using MiHome.Core.Crypto;

namespace MiHome.Core.Api;

/// <summary>上游 API 错误（code/desc）。</summary>
public class ApiException : Exception
{
    public int Code { get; }
    public ApiException(int code, string message) : base($"[{code}] {message}") => Code = code;
}

public class LoginException : Exception
{
    public LoginException(string message) : base(message) { }
}

/// <summary>
/// 米家云 API 客户端。移植自 mijiaAPI 5.x 的 apis.py：
/// 扫码登录、token 刷新、RC4 加密请求、设备/场景/属性/动作/耗材接口。
/// 认证文件沿用上游默认位置 ~/.config/mijia-api/auth.json，
/// 与 CLI 扫过的码互通。
/// </summary>
public sealed class MijiaApiClient : IDisposable
{
    private const string ApiBaseUrl = "https://api.mijia.tech/app";
    private const string LoginUrl = "https://account.xiaomi.com/longPolling/loginUrl";
    private const string ServiceLoginUrl = "https://account.xiaomi.com/pass/serviceLogin?_json=true&sid=mijia&_locale=zh_CN";

    private const string Locale = "zh_CN";
    private const string UaBase = "Android-15-11.0.701-Xiaomi-23046RP50C-OS2.0.212.0.VMYCNXM";

    private static string ComputeAuthDir()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile))
        {
            profile = AppContext.BaseDirectory;
        }
        return Path.Combine(profile, ".config", "mijia-api");
    }

    private static readonly string AuthDir = ComputeAuthDir();
    private static readonly string AuthPath = Path.Combine(AuthDir, "auth.json");

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private readonly object _lock = new();

    public Dictionary<string, JsonElement> AuthData { get; private set; } = new();

    private long _availableCacheTime;
    private bool? _availableCache;

    public MijiaApiClient()
    {
        LoadAuth();
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false, // get_icon 需要自己读 302 Location
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        if (AuthData.Count > 0)
        {
            InitSessionCookies();
        }
    }

    public static string SpecCacheDir => AuthDir;

    // ---------- 认证数据 ----------

    private void LoadAuth()
    {
        try
        {
            if (File.Exists(AuthPath))
            {
                var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(AuthPath));
                AuthData = doc ?? new Dictionary<string, JsonElement>();
            }
        }
        catch
        {
            // 认证文件损坏：隔离坏文件并降级为未登录
            try
            {
                if (File.Exists(AuthPath))
                {
                    File.Move(AuthPath, AuthPath + ".corrupt", overwrite: true);
                }
            }
            catch { /* ignore */ }
            AuthData = new Dictionary<string, JsonElement>();
        }
    }

    public void SaveAuth()
    {
        AuthData["saveTime"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Directory.CreateDirectory(AuthDir);
        var tmp = AuthPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(AuthData, MiHome.Core.Models.Json.Options));
        File.Move(tmp, AuthPath, overwrite: true);
    }

    private string? AuthStr(string key) =>
        AuthData.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    // ---------- UA / deviceId / pass_o ----------

    private static string RandomHex(int len) =>
        new(Enumerable.Range(0, len).Select(_ => "0123456789abcdef"[Random.Shared.Next(16)]).ToArray());

    private static string RandomUpper(int len) =>
        new(Enumerable.Range(0, len).Select(_ => "0123456789ABCDEF"[Random.Shared.Next(16)]).ToArray());

    private static string RandomChars(int len) =>
        new(Enumerable.Range(0, len).Select(_ => "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_-"[Random.Shared.Next(64)]).ToArray());

    public string PassO
    {
        get
        {
            if (AuthStr("pass_o") is { } v)
            {
                return v;
            }
            var gen = RandomHex(16);
            AuthData["pass_o"] = JsonSerializer.SerializeToElement(gen);
            return gen;
        }
    }

    public string UserAgent
    {
        get
        {
            if (AuthStr("ua") is { } v)
            {
                return v;
            }
            var id1 = RandomUpper(40);
            var ua = $"{UaBase}-{id1}-CN-{RandomUpper(32)}-{RandomUpper(32)}-SmartHome-MI_APP_STORE-{id1}|{RandomUpper(40)}|{PassO}-64";
            AuthData["ua"] = JsonSerializer.SerializeToElement(ua);
            return ua;
        }
    }

    public string DeviceId
    {
        get
        {
            if (AuthStr("deviceId") is { } v)
            {
                return v;
            }
            var gen = RandomChars(16);
            AuthData["deviceId"] = JsonSerializer.SerializeToElement(gen);
            return gen;
        }
    }

    // ---------- 会话 Cookie ----------

    private static string GmtOffset()
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return $"GMT{sign}{abs.Hours:00}:{abs.Minutes:00}";
    }

    private void InitSessionCookies()
    {
        var baseUri = new Uri("https://api.mijia.tech/");
        var accountUri = new Uri("https://account.xiaomi.com/");

        // 用 SetCookies 覆盖写：重新登录后 token 已变，旧 cookie 必须被替换，
        // 否则同名 Cookie 添加会抛异常（被吞掉）而残留的是过期 token。
        void Seed(Uri uri, params (string Name, string Value)[] cookies)
        {
            try
            {
                var text = string.Join("; ", cookies
                    .Where(c => !string.IsNullOrEmpty(c.Value))
                    .Select(c => $"{c.Name}={c.Value}"));
                if (text.Length > 0)
                {
                    _cookies.SetCookies(uri, text);
                }
            }
            catch
            {
                // 非法 cookie 值直接忽略，不阻断登录流程
            }
        }

        var serviceToken = AuthStr("serviceToken") ?? "";
        var cUserId = AuthStr("cUserId") ?? AuthStr("userId") ?? "";
        var isDst = TimeZoneInfo.Local.IsDaylightSavingTime(DateTime.Now) ? 1 : 0;

        Seed(baseUri,
            ("cUserId", cUserId),
            ("yetAnotherServiceToken", serviceToken),
            ("serviceToken", serviceToken),
            ("timezone_id", TimeZoneInfo.Local.Id),
            ("timezone", GmtOffset()),
            ("is_daylight", isDst.ToString()),
            ("dst_offset", (isDst * 60 * 60 * 1000).ToString()),
            ("channel", "MI_APP_STORE"),
            ("countryCode", "CN"),
            ("PassportDeviceId", DeviceId),
            ("locale", Locale));

        Seed(accountUri,
            ("deviceId", DeviceId),
            ("userId", AuthStr("userId") ?? ""),
            ("passToken", AuthStr("passToken") ?? ""));
    }

    private void AddServiceHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("accept-encoding", "identity");
        req.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded");
        req.Headers.TryAddWithoutValidation("miot-accept-encoding", "GZIP");
        req.Headers.TryAddWithoutValidation("miot-encrypt-algorithm", "ENCRYPT-RC4");
        req.Headers.TryAddWithoutValidation("x-xiaomi-protocal-flag-cli", "PROTOCAL-HTTP2");
    }

    // ---------- 登录 ----------

    private static Dictionary<string, object?> ParseServiceRet(string text)
    {
        var cleaned = text.Replace("&&&START&&&", "").Trim();
        using var doc = JsonDocument.Parse(cleaned);
        var dict = new Dictionary<string, object?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = UnwrapJson(prop.Value);
        }
        return dict;
    }

    /// <summary>
    /// 把 JsonElement 展开成原生 CLR 值（string / long / double / bool / null / 字典 / 列表）。
    /// 上游 ParseServiceRet 返回的实际就是 Python 原生对象，若保留 JsonElement
    /// 会让 Convert.ToInt64 / 字符串插值在调用处抛 InvalidCastException。
    /// </summary>
    private static object? UnwrapJson(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Object => el.EnumerateObject()
            .ToDictionary(p => p.Name, p => UnwrapJson(p.Value)),
        JsonValueKind.Array => el.EnumerateArray().Select(UnwrapJson).ToList(),
        _ => el.GetRawText(),
    };

    /// <summary>serviceLogin 取 location；code==0 时跟随 location 收下 cookie 即刷新成功。</summary>
    private async Task<Dictionary<string, object?>> GetLocationAsync()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, ServiceLoginUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        var cookie =
            $"deviceId={DeviceId};pass_o={PassO};passToken={AuthStr("passToken") ?? ""};" +
            $"userId={AuthStr("userId") ?? ""};cUserId={AuthStr("cUserId") ?? ""};uLocale={Locale}";
        req.Headers.TryAddWithoutValidation("Cookie", cookie);

        var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            throw new LoginException($"登录服务返回 HTTP {(int)resp.StatusCode}");
        }
        var data = ParseServiceRet(body);

        if (data.TryGetValue("code", out var codeEl) && Convert.ToInt64(codeEl) == 0 &&
            data.TryGetValue("location", out var loc) && loc?.ToString() is { Length: > 0 } locUrl)
        {
            // 跟随 location 完成服务登录，收下 serviceToken cookie
            var follow = new HttpRequestMessage(HttpMethod.Get, locUrl);
            follow.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            var followResp = await _http.SendAsync(follow);
            var followBody = await followResp.Content.ReadAsStringAsync();
            if (followResp.StatusCode == HttpStatusCode.OK && followBody.Trim() == "ok")
            {
                foreach (var c in _cookies.GetCookies(new Uri("https://account.xiaomi.com/")).Cast<Cookie>())
                {
                    AuthData[c.Name] = JsonSerializer.SerializeToElement(c.Value);
                }
                if (data.TryGetValue("ssecurity", out var ssecObj))
                {
                    var ssec = ssecObj switch
                    {
                        JsonElement je => je.GetString() ?? "",
                        string s => s,
                        _ => ssecObj?.ToString() ?? "",
                    };
                    AuthData["ssecurity"] = JsonSerializer.SerializeToElement(ssec);
                }
                return new Dictionary<string, object?> { ["code"] = 0L, ["message"] = "刷新Token成功" };
            }
        }

        // location 携带 query 时返回其参数（含 code != 0）
        if (data.TryGetValue("location", out var locStr) && locStr?.ToString() is { Length: > 0 } locText &&
            Uri.TryCreate(locText, UriKind.Absolute, out var uri))
        {
            var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var dict = new Dictionary<string, object?>();
            foreach (var key in q.AllKeys)
            {
                if (key != null)
                {
                    dict[key] = q[key];
                }
            }
            return dict;
        }
        return data;
    }

    /// <summary>凭据是否存在（不发起网络请求，界面判断登录态用）。</summary>
    public bool HasAuth
    {
        get
        {
            string[] requiredKeys = { "ssecurity", "serviceToken" };
            return AuthData.Count > 0 && requiredKeys.All(k => AuthData.ContainsKey(k));
        }
    }

    /// <summary>凭据仍有效则跳过刷新。</summary>
    public bool Available()
    {
        string[] requiredKeys = { "ua", "ssecurity", "userId", "cUserId", "serviceToken" };
        if (AuthData.Count == 0 || requiredKeys.Any(k => !AuthData.ContainsKey(k)))
        {
            return false;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - _availableCacheTime < 60)
        {
            return _availableCache == true;
        }
        try
        {
            CheckNewMsgAsync(refreshToken: false).GetAwaiter().GetResult();
        }
        catch
        {
            _availableCache = null;
            _availableCacheTime = 0;
            return false;
        }        _availableCache = true;
        _availableCacheTime = now;
        return true;
    }

    public async Task RefreshTokenAsync()
    {
        if (Available())
        {
            return;
        }
        var locationData = await GetLocationAsync();
        if (Convert.ToInt64(locationData.GetValueOrDefault("code", -1L)) == 0 &&
            locationData.GetValueOrDefault("message")?.ToString() == "刷新Token成功")
        {
            SaveAuth();
            InitSessionCookies();
            return;
        }
        throw new LoginException("刷新Token失败，请重新登录");
    }

    /// <summary>
    /// 获取扫码登录数据。返回 null 表示本地凭据已自动刷新、无需扫码。
    /// </summary>
    public async Task<Dictionary<string, object?>?> GetQrLoginDataAsync()
    {
        var locationData = await GetLocationAsync();
        if (Convert.ToInt64(locationData.GetValueOrDefault("code", -1L)) == 0 &&
            locationData.GetValueOrDefault("message")?.ToString() == "刷新Token成功")
        {
            SaveAuth();
            InitSessionCookies();
            return null;
        }

        var query = new Dictionary<string, string>();
        foreach (var (k, v) in locationData)
        {
            query[k] = v?.ToString() ?? "";
        }
        query["theme"] = "";
        query["bizDeviceType"] = "";
        query["_hasLogo"] = "false";
        query["_qrsize"] = "240";
        query["_dc"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var url = LoginUrl + "?" + string.Join("&",
            query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            throw new LoginException($"获取登录二维码失败：HTTP {(int)resp.StatusCode}");
        }
        var data = ParseServiceRet(body);
        if (data.TryGetValue("code", out var codeEl) && Convert.ToInt64(codeEl) != 0)
        {
            throw new LoginException(data.GetValueOrDefault("desc")?.ToString() ?? "未知错误");
        }
        return data;
    }

    /// <summary>长轮询等待用户扫码并完成登录（可达两分钟，须后台调用）。</summary>
    public async Task CompleteQrLoginAsync(Dictionary<string, object?> loginData)
    {
        if (!loginData.TryGetValue("lp", out var lp) || lp?.ToString() is not { Length: > 0 } lpUrl)
        {
            throw new LoginException("登录数据缺少 lp 字段");
        }
        var sessionHandler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        using var session = new HttpClient(sessionHandler) { Timeout = TimeSpan.FromSeconds(120) };
        var req = new HttpRequestMessage(HttpMethod.Get, lpUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        var lpResp = await session.SendAsync(req);
        var lpBody = await lpResp.Content.ReadAsStringAsync();
        if (lpResp.StatusCode != HttpStatusCode.OK)
        {
            throw new LoginException($"扫码轮询失败：HTTP {(int)lpResp.StatusCode}");
        }
        var lpData = ParseServiceRet(lpBody);
        if (lpData.TryGetValue("code", out var codeEl) && Convert.ToInt64(codeEl) != 0)
        {
            throw new LoginException(lpData.GetValueOrDefault("desc")?.ToString() ?? "未知错误");
        }

        foreach (var key in new[] { "psecurity", "nonce", "ssecurity", "passToken", "userId", "cUserId" })
        {
            if (lpData.TryGetValue(key, out var v))
            {
                AuthData[key] = JsonSerializer.SerializeToElement(v);
            }
        }
        if (lpData.TryGetValue("location", out var callback) && callback?.ToString() is { Length: > 0 } callbackUrl)
        {
            var cbReq = new HttpRequestMessage(HttpMethod.Get, callbackUrl);
            cbReq.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            await session.SendAsync(cbReq);
            // 服务登录成功后 cookie 可能落在 xiaomi.com / sts.api.io.mi.com 等多个域，
            // 全部收下（与 requests Session.cookies.get_dict() 一致）
            foreach (var uri in new[]
            {
                "https://account.xiaomi.com/",
                "https://sts.api.io.mi.com/",
                "https://api.mijia.tech/",
            })
            {
                foreach (var c in sessionHandler.CookieContainer.GetCookies(new Uri(uri)).Cast<Cookie>())
                {
                    AuthData[c.Name] = JsonSerializer.SerializeToElement(c.Value);
                }
            }
        }
        AuthData["expireTime"] = JsonSerializer.SerializeToElement(
            DateTimeOffset.Now.AddDays(30).ToUnixTimeMilliseconds());
        SaveAuth();
        InitSessionCookies();
    }

    public void Logout()
    {
        AuthData = new Dictionary<string, JsonElement>();
        try { File.Delete(AuthPath); } catch { /* ignore */ }
        _availableCache = null;
        _availableCacheTime = 0;
    }

    // ---------- 加密请求 ----------

    /// <summary>执行加密 POST 请求并返回 result 字段。</summary>
    public async Task<JsonElement> RequestAsync(string uri, object data, bool refreshToken = true)
    {
        if (refreshToken)
        {
            await RefreshTokenAsync();
        }
        var ssecurity = AuthStr("ssecurity") ?? throw new LoginException("未登录或凭据缺失，请重新扫码登录");

        var jsonData = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
        });
        // 与 Python json.dumps(separators=(',',':')) 对齐：紧凑序列化
        jsonData = CompactJson(jsonData);

        var nonce = MiCrypto.GenNonce();
        var signedNonce = MiCrypto.GetSignedNonce(ssecurity, nonce);
        var parameters = new Dictionary<string, string> { ["data"] = jsonData };
        parameters = MiCrypto.GenerateEncParams(uri, "POST", signedNonce, nonce, parameters, ssecurity);

        var url = ApiBaseUrl + uri;
        using var content = new FormUrlEncodedContent(parameters);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        AddServiceHeaders(req);
        // FormUrlEncodedContent 会覆盖 Content-Type 头，这里统一改写
        req.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");

        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        JsonElement retData;
        try
        {
            retData = JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch (JsonException)
        {
            var dec = MiCrypto.DecryptResponse(ssecurity, nonce, body.Trim());
            retData = JsonSerializer.Deserialize<JsonElement>(dec);
        }

        int code = retData.ValueKind == JsonValueKind.Object && retData.TryGetProperty("code", out var c)
            ? c.GetInt32() : 0;
        if (code != 0 || retData.ValueKind != JsonValueKind.Object || !retData.TryGetProperty("result", out var result))
        {
            string msg = "未知错误";
            if (retData.ValueKind == JsonValueKind.Object)
            {
                if (retData.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    msg = m.GetString()!;
                }
                else if (retData.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String)
                {
                    msg = d.GetString()!;
                }
            }
            throw new ApiException(code, msg);
        }
        return result;
    }

    /// <summary>把 System.Text.Json 的输出压缩为 Python 风格的紧凑格式（去空格）。</summary>
    private static string CompactJson(string json)
    {
        var sb = new StringBuilder(json.Length);
        bool inString = false;
        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];
            if (inString)
            {
                sb.Append(ch);
                if (ch == '\\' && i + 1 < json.Length)
                {
                    sb.Append(json[++i]);
                }
                else if (ch == '"')
                {
                    inString = false;
                }
            }
            else if (ch == '"')
            {
                inString = true;
                sb.Append(ch);
            }
            else if (!char.IsWhiteSpace(ch))
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    // ---------- 业务接口 ----------

    public Task<JsonElement> CheckNewMsgAsync(bool refreshToken = true)
        => RequestAsync("/v2/message/v2/check_new_msg", new { begin_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600 }, refreshToken);

    public async Task<List<Dictionary<string, JsonElement>>> GetHomesListAsync()
    {
        var ret = await RequestAsync("/v2/homeroom/gethome_merged",
            new { fg = true, fetch_share = true, fetch_share_dev = true, fetch_cariot = true, limit = 300, app_ver = 7, plat_form = 0 });
        return ElementList(ret.GetProperty("homelist"));
    }

    public async Task<List<Dictionary<string, JsonElement>>> GetDevicesListAsync(string? homeId = null)
    {
        var devices = new List<Dictionary<string, JsonElement>>();
        if (homeId == null)
        {
            foreach (var home in await GetHomesListAsync())
            {
                devices.AddRange(await GetDevicesListAsync(home["id"].ToString()!));
            }
            return devices;
        }
        long homeOwner = await GetHomeOwnerAsync(homeId);
        string startDid = "";
        bool hasMore = true;
        while (hasMore)
        {
            var ret = await RequestAsync("/home/home_device_list", new
            {
                home_owner = homeOwner,
                home_id = long.Parse(homeId),
                limit = 200,
                start_did = startDid,
                get_split_device = true,
                support_smart_home = true,
                get_cariot_device = true,
                get_third_device = true,
            });
            if (ret.ValueKind == JsonValueKind.Object && ret.TryGetProperty("device_info", out var info))
            {
                devices.AddRange(ElementList(info));
                startDid = ret.TryGetProperty("max_did", out var max) ? max.GetString() ?? "" : "";
                hasMore = ret.TryGetProperty("has_more", out var more) && more.GetBoolean() && startDid != "";
            }
            else
            {
                hasMore = false;
            }
        }
        return devices;
    }

    public async Task<List<Dictionary<string, JsonElement>>> GetSharedDevicesListAsync()
    {
        var ret = await RequestAsync("/v2/home/device_list_page", new
        {
            ssid = "<unknown ssid>",
            bssid = "02:00:00:00:00:00",
            getVirtualModel = true,
            getHuamiDevices = 1,
            get_split_device = true,
            support_smart_home = true,
            get_cariot_device = true,
            get_third_device = true,
            get_phone_device = true,
            get_miwear_device = true,
        });
        var list = ElementList(ret.GetProperty("list"))
            .Where(d => d.TryGetValue("owner", out var o) && (o.ValueKind != JsonValueKind.False && o.ValueKind != JsonValueKind.Null))
            .ToList();
        return list;
    }

    public async Task<List<Dictionary<string, JsonElement>>> GetScenesListAsync(string? homeId = null)
    {
        if (homeId == null)
        {
            var scenes = new List<Dictionary<string, JsonElement>>();
            foreach (var home in await GetHomesListAsync())
            {
                scenes.AddRange(await GetScenesListAsync(home["id"].ToString()!));
            }
            return scenes;
        }
        long ownerUid = await GetHomeOwnerAsync(homeId);
        var ret = await RequestAsync("/appgateway/miot/appsceneservice/AppSceneService/GetSimpleSceneList",
            new { app_version = 12, get_type = 2, home_id = homeId, owner_uid = ownerUid });
        if (ret.ValueKind == JsonValueKind.Object && ret.TryGetProperty("manual_scene_info_list", out var list))
        {
            return ElementList(list);
        }
        return new List<Dictionary<string, JsonElement>>();
    }

    public async Task<bool> RunSceneAsync(string sceneId, string homeId)
    {
        long ownerUid = await GetHomeOwnerAsync(homeId);
        var ret = await RequestAsync("/appgateway/miot/appsceneservice/AppSceneService/NewRunScene",
            new { scene_id = sceneId, scene_type = 2, phone_id = "null", home_id = homeId, owner_uid = ownerUid });
        return ret.ValueKind != JsonValueKind.False;
    }

    public Task<JsonElement> GetDevicesPropAsync(List<object> batch)
        => RequestAsync("/miotspec/prop/get", new { @params = batch, datasource = 1 });

    public async Task<JsonElement> SetDevicesPropAsync(List<object> batch)
    {
        var ret = await RequestAsync("/miotspec/prop/set", new { @params = batch });
        // 与上游一致：把错误码翻译成可读消息
        if (ret.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ret.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                int code = item.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                // 不修改 JsonElement（不可变），错误判定放在调用方
                _ = code;
            }
        }
        return ret;
    }

    public Task<JsonElement> RunActionAsync(object param)
        => RequestAsync("/miotspec/action", new { @params = param });

    public async Task<List<Dictionary<string, JsonElement>>> GetConsumableItemsAsync()
    {
        var homes = await GetHomesListAsync();
        var items = new List<Dictionary<string, JsonElement>>();
        foreach (var home in homes)
        {
            var homeId = home["id"].ToString()!;
            try
            {
                var ownerUid = await GetHomeOwnerAsync(homeId);
                var ret = await RequestAsync("/v2/home/standard_consumable_items",
                    new { home_id = long.Parse(homeId), owner_id = ownerUid, filter_ignore = true });
                if (ret.ValueKind == JsonValueKind.Object &&
                    ret.TryGetProperty("items", out var arr) && arr.GetArrayLength() > 0 &&
                    arr[0].TryGetProperty("consumes_data", out var data))
                {
                    items.AddRange(ElementList(data));
                }
            }
            catch
            {
                // 单个家庭失败不影响其他家庭
            }
        }
        return items;
    }

    /// <summary>获取设备图标 CDN 地址（接口用 302 重定向返回 Location）。</summary>
    public async Task<string?> GetIconUrlAsync(string model)
    {
        try
        {
            await RefreshTokenAsync();
            var ssecurity = AuthStr("ssecurity");
            if (ssecurity == null)
            {
                return null;
            }
            const string uri = "/v2/productconfig/get_icon";
            var jsonData = CompactJson(JsonSerializer.Serialize(new { icon_name = "icon_real", model }));
            var nonce = MiCrypto.GenNonce();
            var signedNonce = MiCrypto.GetSignedNonce(ssecurity, nonce);
            var parameters = new Dictionary<string, string> { ["data"] = jsonData };
            parameters = MiCrypto.GenerateEncParams(uri, "POST", signedNonce, nonce, parameters, ssecurity);
            using var content = new FormUrlEncodedContent(parameters);
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiBaseUrl + uri) { Content = content };
            AddServiceHeaders(req);
            req.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");
            using var resp = await _http.SendAsync(req);
            if ((int)resp.StatusCode != 302)
            {
                return null;
            }
            return resp.Headers.Location?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private async Task<long> GetHomeOwnerAsync(string homeId)
    {
        var homes = await GetHomesListAsync();
        foreach (var home in homes)
        {
            if (home["id"].ToString() == homeId && home.TryGetValue("uid", out var uid))
            {
                return uid.GetInt64();
            }
        }
        throw new ApiException(-1, $"未找到 home_id={homeId} 的家庭信息");
    }

    internal static List<Dictionary<string, JsonElement>> ElementList(JsonElement element)
    {
        var list = new List<Dictionary<string, JsonElement>>();
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var dict = new Dictionary<string, JsonElement>();
                    foreach (var p in item.EnumerateObject())
                    {
                        dict[p.Name] = p.Value;
                    }
                    list.Add(dict);
                }
            }
        }
        return list;
    }

    public void Dispose() => _http.Dispose();
}
