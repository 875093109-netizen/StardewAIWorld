namespace AIWorld.Core.Behaviors;

/// <summary>
/// A1 — 定时约会出发时间规划（纯函数，无游戏依赖，离线可测）。
/// 出发时间 = 目标时间 − 预估行程分钟 − 安全余量分钟。
/// 预估行程由调用方依据 NPC 出发时的真实位置、实时路线与速度动态计算，不得缓存
/// 早上接受请求时的坐标或路线。游戏时间以 HHMM 表示（如 2100 = 21:00）。
/// </summary>
public static class TravelDeparturePlanner
{
    public const int DefaultSafetyMarginMinutes = 30;

    /// <summary>HHMM → 当日累计分钟。</summary>
    public static int ToMinutes(int hhmm) => (hhmm / 100) * 60 + (hhmm % 100);

    /// <summary>当日累计分钟 → HHMM（负数/跨日由调用方结合 gameDay 处理）。</summary>
    public static int FromMinutes(int minutes)
    {
        minutes = Math.Max(0, minutes);
        return (minutes / 60) * 100 + (minutes % 60);
    }

    /// <summary>计算出发时间（HHMM）。安全余量默认 30 分钟。</summary>
    public static int ComputeDepartureTime(int targetGameTime, int estimatedTravelMinutes, int safetyMarginMinutes = DefaultSafetyMarginMinutes)
        => FromMinutes(ToMinutes(targetGameTime) - estimatedTravelMinutes - safetyMarginMinutes);

    /// <summary>是否已错过约会（gameDay/time 已越过目标 + 等待窗口）。</summary>
    public static bool IsMissed(int gameDay, int gameTime, int targetGameDay, int targetGameTime, int waitWindowMinutes)
    {
        if (gameDay > targetGameDay) return true;
        if (gameDay < targetGameDay) return false;
        return ToMinutes(gameTime) > ToMinutes(targetGameTime) + waitWindowMinutes;
    }

    /// <summary>是否已到等待窗口结束（自然恢复日程，不瞬移离开）。</summary>
    public static bool IsWaitWindowElapsed(int gameTime, int targetGameTime, int waitWindowMinutes)
        => ToMinutes(gameTime) >= ToMinutes(targetGameTime) + waitWindowMinutes;
}
