using MiHome.Core.Stores;

namespace MiHome.App.Services;

/// <summary>应用级运行状态：调度引擎单例。</summary>
public static class AppState
{
    public static ScheduleEngine Engine { get; } = new();
}
