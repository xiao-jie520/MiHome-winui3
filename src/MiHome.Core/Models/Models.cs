using System.Text.Json;

namespace MiHome.Core.Models;

/// <summary>设备列表条目（界面展示用最小字段集）。</summary>
public sealed record DeviceInfo(
    string Did,
    string Name,
    string Model,
    string HomeName,
    string RoomName,
    bool Online,
    string SpecType);

/// <summary>枚举属性的一个选项。</summary>
public sealed record ValueOption(long Value, string Description, string? DescZhCn);

/// <summary>属性元数据，控件工厂据此决定生成哪种控件。</summary>
public sealed record PropInfo(
    string Name,
    string Desc,
    string Type,
    bool Readable,
    bool Writable,
    double[]? Range,
    List<ValueOption>? ValueList)
{
    /// <summary>spec 调用参数：siid/piid。</summary>
    public required int Siid { get; init; }
    public required int Piid { get; init; }
}

/// <summary>动作元数据。</summary>
public sealed record ActionInfo(string Name, string Desc, int Siid, int Aiid, List<object?>? InParams);

/// <summary>设备详情（控制面板全部信息）。</summary>
public sealed record DeviceDetail(
    string Did,
    string Name,
    string Model,
    IReadOnlyList<PropInfo> Props,
    IReadOnlyList<ActionInfo> Actions);

/// <summary>米家手动场景。</summary>
public sealed record SceneInfo(string SceneId, string Name, string HomeId, string HomeName);

/// <summary>耗材条目。</summary>
public sealed record ConsumableItem(
    string Did, string Name, string Model, string Description,
    string Type, double Value, double? Threshold, bool Low, long Updated);

/// <summary>定时开关规则。</summary>
public sealed class ScheduleRule
{
    public string Id { get; set; } = "";
    public string Did { get; set; } = "";
    public string Time { get; set; } = "";
    public string Action { get; set; } = "on";
    public bool Enabled { get; set; } = true;
    public string LastFired { get; set; } = "";
}

/// <summary>规格定义里的属性（spec dict 形式）。</summary>
public sealed class SpecProp
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "";
    public string Rw { get; set; } = "";
    public double[]? Range { get; set; }
    public List<ValueOption>? ValueList { get; set; }
    public int Siid { get; set; }
    public int Piid { get; set; }
}

/// <summary>规格定义里的动作。</summary>
public sealed class SpecAction
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Siid { get; set; }
    public int Aiid { get; set; }
}

/// <summary>从 miot-spec.org 拉取并归一化后的设备规格。</summary>
public sealed class SpecInfo
{
    public int Version { get; set; } = 2;
    public string Name { get; set; } = "";
    public string SpecType { get; set; } = "";
    public List<SpecProp> Properties { get; set; } = new();
    public List<SpecAction> Actions { get; set; } = new();
}

/// <summary>带用户可读中文消息的服务层异常。</summary>
public class ServiceError : Exception
{
    public ServiceError(string message) : base(message) { }
}

/// <summary>JSON 序列化助手：统一驼峰/蛇形处理。</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch
        {
            return null;
        }
    }

    public static void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
