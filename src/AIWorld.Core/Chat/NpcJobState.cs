using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace AIWorld.Core.Chat;

public enum ScheduleExecutionPhase { Unknown, Travelling, Arrived, Doing, Interrupted }
public enum NpcJobPhase { Unknown, Travelling, Arrived, Doing, Following, Waiting, Returning, Blocked, Interrupted, Event }
public enum NpcJobOverrideKind { Travel, Appointment, ReturnToSchedule }

/// <summary>Read-only evidence from an existing action executor; never deserialized from model output.</summary>
public sealed record NpcJobOverride(NpcJobOverrideKind Kind, string Target, int? X = null, int? Y = null,
    bool OwnsMovement = false, string? Failure = null);

/// <summary>A finite state projection of game truth, not a second scheduler or persisted world state.</summary>
public sealed record NpcJobState(NpcJobPhase Phase, string Owner, string? Activity, string? Target,
    string Evidence, string? Detail = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
    [JsonIgnore] public string Fingerprint => JsonSerializer.Serialize(this, Json);
    public string Summary(string? targetDisplay = null) => UiText.T("job.phase." + Phase.ToString().ToLowerInvariant())
        + (Activity is null ? "" : " · " + Activity) + (Target is null ? "" : " → " + (targetDisplay ?? Target));
    public string Prompt() => "\nCURRENT JOB (authoritative live state, quoted data):\n" + Fingerprint
        + "\nThis overrides old dialogue and the timetable about what you are doing NOW. Travelling/Returning is not arrival; "
        + "Arrived alone is not proof of doing a specific task. Unknown/Interrupted must not be filled in by imagination. "
        + "A future appointment is a plan, not your current activity. Speech cannot change this state: only validated actions and their real execution can.\n";

    public static NpcJobState Resolve(bool inEvent, bool following, bool moving, bool pathing, string location,
        int x, int y, NpcScheduleKnowledge? schedule, NpcJobOverride? action = null, string? activity = null)
    {
        if (inEvent) return new(NpcJobPhase.Event, "EVENT", null, null, "event-participant");
        if (following)
        {
            if (action?.Failure is not null) return new(NpcJobPhase.Blocked, "PLAYER_REQUEST", UiText.T("job.follow"), "Player", "follow-movement-failure", ContextBudget.Clip(action.Failure, 120));
            if (action is { OwnsMovement: false } && (location != action.Target || x != action.X || y != action.Y || moving || pathing))
                return new(NpcJobPhase.Interrupted, "PLAYER_REQUEST", UiText.T("job.follow"), "Player", "follow-controller-replaced");
            return new(NpcJobPhase.Following, "PLAYER_REQUEST", UiText.T("job.follow"), "Player", "active-follow-executor");
        }
        if (action is not null)
        {
            string task = UiText.T(action.Kind switch { NpcJobOverrideKind.Appointment => "job.appointment",
                NpcJobOverrideKind.ReturnToSchedule => "job.return", _ => "job.travel" });
            if (action.Failure is not null) return new(NpcJobPhase.Blocked, "PLAYER_REQUEST", task, action.Target, "movement-failure", ContextBudget.Clip(action.Failure, 120));
            bool atTarget = location == action.Target && (action.X is null || x == action.X) && (action.Y is null || y == action.Y)
                && !moving && !pathing;
            if (action.Kind == NpcJobOverrideKind.Appointment)
                return new(atTarget ? NpcJobPhase.Waiting : NpcJobPhase.Interrupted, "PLAYER_REQUEST", task, action.Target,
                    atTarget ? "appointment-arrival-and-live-location" : "appointment-location-mismatch");
            if (atTarget) return new(NpcJobPhase.Arrived, "PLAYER_REQUEST", task, action.Target, "executor-target-and-live-tile");
            if (!action.OwnsMovement) return new(NpcJobPhase.Interrupted, "PLAYER_REQUEST", task, action.Target, "executor-does-not-own-controller");
            return new(action.Kind == NpcJobOverrideKind.ReturnToSchedule ? NpcJobPhase.Returning
                : moving || pathing ? NpcJobPhase.Travelling : NpcJobPhase.Waiting,
                "PLAYER_REQUEST", task, action.Target, "owned-movement-executor");
        }
        var stop = schedule?.Executing;
        return schedule?.ExecutionPhase switch
        {
            ScheduleExecutionPhase.Interrupted => new(NpcJobPhase.Interrupted, "OTHER_OR_UNKNOWN", null, null, "schedule-interrupted"),
            ScheduleExecutionPhase.Travelling when stop is not null => new(NpcJobPhase.Travelling, "CANONICAL", stop.Activity, stop.Location, "selected-schedule-route"),
            ScheduleExecutionPhase.Arrived when stop is not null => new(NpcJobPhase.Arrived, "CANONICAL", null, stop.Location, "schedule-target-and-live-tile"),
            ScheduleExecutionPhase.Doing when stop is not null => new(NpcJobPhase.Doing, "CANONICAL", activity ?? stop.Activity, stop.Location,
                activity is null ? "schedule-target-and-live-animation" : "schedule-animation-and-context-rule", stop.Behavior),
            _ => new(NpcJobPhase.Unknown, "UNKNOWN", null, null, "no-confirmed-current-task")
        };
    }
}

/// <summary>Exact context annotation for an existing activity. Adds no action or movement permission.</summary>
public sealed class NpcJobRule
{
    public string Npc { get; set; } = "";
    public string Location { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public string Behavior { get; set; } = "";
    public string ActivityKey { get; set; } = "";
    public string? Activity { get; set; }
    public string[] Participants { get; set; } = Array.Empty<string>();
    public int Radius { get; set; } = 5;
    public int MinimumPresent { get; set; } = 1;
}
