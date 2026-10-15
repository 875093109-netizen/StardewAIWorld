namespace AIWorld.Demo;

public sealed class ModConfig
{
    public AIWorld.Core.Chat.TalkOptions Talk { get; set; } = new();
    public StardewModdingAPI.SButton TalkConsoleKey { get; set; } = StardewModdingAPI.SButton.F9;
    public bool EnableWorldPerception { get; set; } = true;
    public bool EnableDialogueBridge { get; set; } = true;
    public bool RewriteDailyDialogue { get; set; } = true;
    public StardewModdingAPI.SButton DialogueSwitchKey { get; set; } = StardewModdingAPI.SButton.F8;
    /// <summary>Player-initiated production chat. QA still uses its explicit routing policy.</summary>
    public bool AllowProductionLlm { get; set; } = true;
    public int TravelTimeoutSeconds { get; set; } = 180;
    /// <summary>AIWorld's conservative estimate policy, not a claim about game mechanics.</summary>
    public double EstimatedGameMinutesPerTile { get; set; } = 0.6;
}
