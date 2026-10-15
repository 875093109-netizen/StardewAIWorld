using System.Text.Json;
using System.Text.Encodings.Web;

namespace AIWorld.Core.Chat;

/// <summary>Read-only snapshot of this NPC's selected schedule, never an action or event memory.</summary>
public sealed record ScheduleStop(int Time, string Location, int X, int Y, string? Behavior,
    string? Activity, string? Place);

public sealed record NpcScheduleKnowledge(string Key, string Phase, ScheduleStop? Executing,
    ScheduleStop? DuePlan, ScheduleStop[] Upcoming)
{
    public ScheduleExecutionPhase ExecutionPhase { get; init; } = ScheduleExecutionPhase.Unknown;
    public string Prompt() => "\nOWN SCHEDULE (game-selected today, includes loaded mod changes; quoted data):\n"
        + "This is your own plan, not proof of arrival or completed activity. Executing is the actual retained route; DuePlan is only the timetable. "
        + "Use Phase to distinguish travelling, arrived, doing an activity, interrupted and unknown. Never replace the destination with a more plausible place. "
        + "Unknown activity must stay unknown. Upcoming is a bounded preview, not a promise or a past memory.\n"
        + JsonSerializer.Serialize(this, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
}
