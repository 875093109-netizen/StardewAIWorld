using StardewModdingAPI;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>
/// DEV/QA-only game-time advance — the ONE implementation shared by the
/// aiworld_dev_time console command and the QA drivers (PM A1 checkpoint P1:
/// reuse DEV_QA time advancement).
///
/// Only mutates Game1.timeOfDay. It NEVER calls AppointmentManager or any
/// scheduler directly — the scheduler observes the new time on its next tick
/// and reacts through its own state machine.
/// </summary>
public static class DevGameTime
{
    /// <summary>Valid Stardew timeOfDay range: 0600 (6:00) … 2600 (next-day 2:00).</summary>
    public static bool TrySet(int hhmm, out string message)
    {
        int h = hhmm / 100;
        int m = hhmm % 100;
        if (h < 6 || h > 26 || m < 0 || m > 59)
        {
            message = $"invalid time {hhmm:D4} (expected HHMM with hour 6..26, minute 0..59)";
            return false;
        }
        Game1.timeOfDay = hhmm;
        message = $"game time set to {hhmm:D4}";
        return true;
    }
}
