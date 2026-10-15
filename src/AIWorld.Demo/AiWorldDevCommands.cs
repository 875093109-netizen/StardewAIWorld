using StardewModdingAPI;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>
/// DEV-ONLY iteration convenience commands (PM: DEV ITERATION path — debug convenience allowed;
/// NEVER part of the PLAYER UX FINAL ACCEPTANCE path).
///
/// These commands ONLY move the test operator to the target NPC ("deliver the tester to the
/// door"). They do NOT participate in the被测 feature chain:
///   SocialValley UI → AIWorld → DeepSeek → StructuredResponse → BehaviorArbitrator → Squad
/// They never open the conversation, never inject intents, never trigger follow/stop.
///
/// Commands:
///   aiworld dev goto &lt;npc&gt;     — warp the player next to the named NPC (her tile), log her
///                                 location/state/time. If NPC is in an unloadable location or
///                                 sleeping, log guidance instead of forcing.
///   aiworld dev morning         — advance game time to 09:10 (interactive morning slot) if it
///                                 is earlier; pure convenience for iteration, no state mutation
///                                 beyond time.
/// </summary>
public sealed class AiWorldDevCommands
{
    private readonly IMonitor _monitor;
    private bool _autoBootstrapRequested;
    private bool _autoBootstrapStarted;
    private bool _autoBootstrapDone;
    private int _bootstrapWaitTicks;

    public AiWorldDevCommands(IMonitor monitor)
    {
        _monitor = monitor;
    }

    public void Register(IModHelper helper)
    {
        helper.ConsoleCommands.Add(
            "aiworld_dev_goto",
            "AIWorld DEV: warp the player next to an NPC for manual testing.\n\n"
            + "Usage: aiworld_dev_goto <npcName>\n"
            + "Example: aiworld_dev_goto Abigail\n"
            + "DEV-ONLY convenience. Does NOT open chat or trigger any AIWorld behavior.",
            OnGoto);

        helper.ConsoleCommands.Add(
            "aiworld_dev_morning",
            "AIWorld DEV: advance game time to 09:10 (interactive morning) if earlier.\n\n"
            + "Usage: aiworld_dev_morning\n"
            + "DEV-ONLY convenience. Only changes timeOfDay.",
            OnMorning);

        helper.ConsoleCommands.Add(
            "aiworld_dev_time",
            "AIWorld DEV: advance game time to <HHMM> (e.g. 2100).\n\n"
            + "Usage: aiworld_dev_time <HHMM>\n"
            + "Example: aiworld_dev_time 2100\n"
            + "DEV-ONLY convenience (A1 runtime time compression). ONLY changes Game1.timeOfDay;\n"
            + "never calls AppointmentManager or any scheduler directly.",
            OnTime);

        helper.ConsoleCommands.Add(
            "aiworld_dev_abigail",
            "AIWorld DEV: one-shot Abigail test setup — combine morning + goto Abigail.\n\n"
            + "Usage: aiworld_dev_abigail\n"
            + "Thin wrapper: sets convenient test time (~09:10) then places the player near an\n"
            + "interactable Abigail. NO intent injection, NO forced Follow/Stop, NO fake state —\n"
            + "the player still chats and plays through the normal game UI.\n"
            + "Equivalent conceptual syntax: aiworld dev abigail.",
            OnAbigail);

        helper.ConsoleCommands.Add(
            "aiworld_dev_zh",
            "AIWorld DEV: force game locale to Chinese (zh) and reload localized content.\n\n"
            + "Usage: aiworld_dev_zh\n"
            + "Diagnostic/repair for CJK glyph rendering: SocialValley's font manager only loads\n"
            + "the Chinese font when the game locale is zh. If the game is running as en, Chinese\n"
            + "text in the chat UI renders as asterisks. This command switches the locale so the\n"
            + "game uses its bundled Chinese font (Content/Fonts/Chinese).\n"
            + "DEV-ONLY convenience; not part of the player-facing acceptance path.",
            OnForceZh);

        helper.ConsoleCommands.Add(
            "aiworld_dev_hearts",
            "AIWorld DEV: set all villager friendships to 2 hearts (500 points).\n\n"
            + "Usage: aiworld_dev_hearts [npcName]\n"
            + "With no args: sets EVERY known villager to 500 friendship points (2 hearts) so\n"
            + "the player can enter all homes. With an NPC name: sets only that NPC.\n"
            + "DEV-ONLY convenience for QA/testing; does not touch AIWorld state.",
            OnHearts);

        TryRegisterAutoBootstrap(helper);
    }

    private void OnHearts(string command, string[] args)
    {
        const int twoHearts = 500;
        var target = args.Length > 0 ? args[0] : null;
        var set = 0;
        try
        {
            foreach (var npc in Utility.getAllCharacters())
            {
                if (npc is null || !npc.isVillager()) continue;
                if (target is not null && !string.Equals(npc.Name, target, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Game1.player.friendshipData.ContainsKey(npc.Name))
                    Game1.player.friendshipData[npc.Name] = new Friendship();
                var f = Game1.player.friendshipData[npc.Name];
                f.Points = twoHearts;
                set++;
            }
            _monitor.Log($"[DEV] HEARTS_SET count={set} points={twoHearts} (2 hearts) target={target ?? "(all villagers)"} — re-enter a location or advance time to refresh.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[DEV] HEARTS_SET FAILED {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
        }
    }

    private void OnForceZh(string command, string[] args)
    {
        _monitor.Log($"[DEV] ForceZh — current locale={StardewValley.LocalizedContentManager.CurrentLanguageCode}", LogLevel.Info);
        try
        {
            StardewValley.LocalizedContentManager.CurrentLanguageCode = StardewValley.LocalizedContentManager.LanguageCode.zh;
            _monitor.Log($"[DEV] ForceZh — set to zh, now={StardewValley.LocalizedContentManager.CurrentLanguageCode}", LogLevel.Info);
            _monitor.Log("[DEV] ForceZh — note: SMAPI OnLanguageChange hook may already have refreshed content; if glyphs are still broken, a game restart in zh is the reliable path.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[DEV] ForceZh FAILED {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>ONE-CLICK HUMAN EXPLORATORY START (PM): if runtest-command.json has
    /// "devBootstrapAbigail":true, wait for the world to be ready after autoload, then run the
    /// SAME setup as aiworld_dev_abigail (morning + goto) automatically. No PTY/keyboard
    /// automation, no new semantics — reuses the existing morning/goto implementation.
    /// Automation stops before the player starts testing.</summary>
    private void TryRegisterAutoBootstrap(IModHelper helper)
    {
        var cmdFile = Path.Combine(helper.DirectoryPath, "runtest-command.json");
        if (!File.Exists(cmdFile)) return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cmdFile));
            if (!doc.RootElement.TryGetProperty("devBootstrapAbigail", out var b) || !b.GetBoolean()) return;
            _autoBootstrapRequested = true;
            _monitor.Log("[DEV] devBootstrapAbigail requested — will auto-run morning + goto Abigail after world ready.", LogLevel.Info);
            helper.Events.GameLoop.UpdateTicked += OnUpdateTickedForAutoBootstrap;
        }
        catch { }
    }

    private void OnUpdateTickedForAutoBootstrap(object? sender, StardewModdingAPI.Events.UpdateTickedEventArgs e)
    {
        // POSTCONDITION-GATED bootstrap (PM DEV GOTO POSTCONDITION): ABIGAIL_BOOTSTRAP_DONE is
        // only emitted once ALL postconditions hold — TIME≈0910 AND PLAYER_LOCATION == ABIGAIL_
        // LOCATION AND PLAYER_NEAR_ABIGAIL. warpFarmer is async (applies on a later tick), so the
        // marker must NOT be printed in the same tick as the warp; we poll until the player is
        // actually standing next to Abigail.
        if (_autoBootstrapDone) return;
        // Wait for WORLD_READY: save loaded and not on the title menu.
        if (!Game1.hasLoadedGame || Game1.activeClickableMenu is StardewValley.Menus.TitleMenu) return;
        if (Game1.player is null) return;

        if (!_autoBootstrapStarted)
        {
            _autoBootstrapStarted = true;
            _monitor.Log("[DEV] ABIGAIL_BOOTSTRAP_START (world ready)", LogLevel.Info);
            // NOTE: do NOT force the locale here (autoload/locale ordering bug is fixed in the
            // RuntimeTestHost by waiting for the persisted locale; masking is forbidden).
            OnMorning("aiworld_dev_abigail", Array.Empty<string>());
            OnGoto("aiworld_dev_abigail", new[] { "Abigail" });
            _monitor.Log("[DEV] ABIGAIL_BOOTSTRAP_WAIT — warp issued, awaiting postconditions", LogLevel.Info);
            return;
        }

        // POSTCONDITION CHECK (every tick until satisfied or timeout).
        var npc = Game1.getCharacterFromName("Abigail");
        var player = Game1.player;
        var npcLoc = npc?.currentLocation?.NameOrUniqueName;
        var playerLoc = player.currentLocation?.NameOrUniqueName;
        bool timeOk = Game1.timeOfDay >= 905 && Game1.timeOfDay <= 915; // ≈0910
        bool locOk = npcLoc is not null && npcLoc == playerLoc;
        bool nearOk = locOk && npc is not null &&
                      Math.Abs(npc.Tile.X - player.Tile.X) <= 2 && Math.Abs(npc.Tile.Y - player.Tile.Y) <= 2;

        if (timeOk && locOk && nearOk)
        {
            _autoBootstrapDone = true;
            _monitor.Log($"[DEV] ABIGAIL_BOOTSTRAP_DONE time={Game1.timeOfDay:D4} playerAt={playerLoc} "
                         + $"abigailAt={npcLoc} near=true — hand control to user.", LogLevel.Info);
            return;
        }

        _bootstrapWaitTicks++;
        if (_bootstrapWaitTicks % 30 == 0) // log every ~0.5s
        {
            _monitor.Log($"[DEV] ABIGAIL_BOOTSTRAP_WAIT tick={_bootstrapWaitTicks} time={Game1.timeOfDay:D4} "
                         + $"playerAt={playerLoc ?? "unknown"} abigailAt={npcLoc ?? "unknown"} "
                         + $"timeOk={timeOk} locOk={locOk} nearOk={nearOk}", LogLevel.Info);
        }
        if (_bootstrapWaitTicks > 600) // ~10s timeout
        {
            _autoBootstrapDone = true;
            _monitor.Log($"[DEV] ABIGAIL_BOOTSTRAP_TIMEOUT time={Game1.timeOfDay:D4} playerAt={playerLoc ?? "unknown"} "
                         + $"abigailAt={npcLoc ?? "unknown"} near=false — hand control to user (may need manual goto).", LogLevel.Warn);
        }
    }

    private void OnAbigail(string command, string[] args)
    {
        // Thin wrapper: morning first (so Abigail is not sleeping), then goto Abigail.
        OnMorning(command, args);
        OnGoto(command, new[] { "Abigail" });
    }

    private void OnGoto(string command, string[] args)
    {
        if (args.Length < 1)
        {
            _monitor.Log("Usage: aiworld_dev_goto <npcName>", LogLevel.Warn);
            return;
        }

        var npcName = args[0];
        var npc = Game1.getCharacterFromName(npcName);
        if (npc is null)
        {
            _monitor.Log($"DEV goto: NPC '{npcName}' not found in the current save.", LogLevel.Warn);
            return;
        }

        var npcLoc = npc.currentLocation;
        if (npcLoc is null)
        {
            _monitor.Log($"DEV goto: NPC '{npcName}' has no current location (not loaded).", LogLevel.Warn);
            return;
        }

        // Log current state for the tester.
        var state = npc.isSleeping.Value ? "sleeping" : (npc.isMoving() ? "moving" : "idle");
        _monitor.Log(
            $"DEV goto: {npc.Name} at {npcLoc.NameOrUniqueName} ({npc.Tile.X},{npc.Tile.Y}) state={state} "
            + $"time={Game1.timeOfDay:D4} playerLoc={Game1.player.currentLocation?.NameOrUniqueName ?? "unknown"}",
            LogLevel.Info);

        // If the player is already in the same location, just move adjacent (her tile is within
        // interaction range; warp to her tile - 1 on Y keeps us on a valid floor tile).
        var targetX = (int)npc.Tile.X;
        var targetY = (int)npc.Tile.Y - 1;
        if (targetY < 0) targetY = (int)npc.Tile.Y + 1; // fallback below her if above is out of bounds

        if (npc.isSleeping.Value)
        {
            _monitor.Log(
                $"DEV goto: {npc.Name} is sleeping — use 'aiworld_dev_morning' first, then retry. "
                + $"Conversation during sleep is not meaningful.",
                LogLevel.Warn);
            return;
        }

        if (Game1.player.currentLocation?.NameOrUniqueName == npcLoc.NameOrUniqueName)
        {
            Game1.player.Position = new Microsoft.Xna.Framework.Vector2(targetX * 64f, targetY * 64f);
            _monitor.Log($"DEV goto: player moved to ({targetX},{targetY}) in {npcLoc.NameOrUniqueName} next to {npc.Name}.", LogLevel.Info);
        }
        else
        {
            var ok = TryWarpPlayer(npcLoc.NameOrUniqueName, targetX, targetY, out var failure);
            _monitor.Log(
                ok
                    ? $"DEV goto: player warped to {npcLoc.NameOrUniqueName} ({targetX},{targetY}) next to {npc.Name}."
                    : $"DEV goto: warp failed ({failure}). {npc.Name} is at {npcLoc.NameOrUniqueName} — move there manually.",
                ok ? LogLevel.Info : LogLevel.Warn);
        }
    }

    private void OnMorning(string command, string[] args)
    {
        if (Game1.timeOfDay < 910)
        {
            Game1.timeOfDay = 910;
            _monitor.Log("DEV morning: game time set to 09:10 (interactive morning slot).", LogLevel.Info);
        }
        else
        {
            _monitor.Log($"DEV morning: time is already {Game1.timeOfDay:D4} (>= 09:10), no change.", LogLevel.Info);
        }
    }

    private void OnTime(string command, string[] args)
    {
        if (args.Length < 1 || !int.TryParse(args[0], out var hhmm))
        {
            _monitor.Log("Usage: aiworld_dev_time <HHMM> (e.g. 2100)", LogLevel.Warn);
            return;
        }
        _monitor.Log(DevGameTime.TrySet(hhmm, out var msg) ? $"DEV time: {msg}." : $"DEV time: {msg}.", LogLevel.Info);
    }

    /// <summary>Warp the player to a location by name with a safe fallback (the location may not
    /// be loaded yet in a fresh save). Uses the public warpFarmer(string,...) overload.</summary>
    private static bool TryWarpPlayer(string locationName, int x, int y, out string failure)
    {
        try
        {
            if (Game1.getLocationFromName(locationName) is null)
            {
                failure = $"location '{locationName}' not loaded";
                return false;
            }
            Game1.warpFarmer(locationName, x, y, false);
            failure = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}
