using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Pathfinding;

namespace AIWorld.Demo;

/// <summary>Vanilla walking with per-step validation. No implicit door warp, off-screen shortcut or path-failure teleport.</summary>
internal sealed class GuardedNpcPath : PathFindController
{
    private readonly NPC _npc;
    private readonly Action<string> _failed;
    private Point _lastTile;
    private DateTime _lastProgress = DateTime.UtcNow;
    public bool Completed { get; private set; }

    public GuardedNpcPath(NPC npc, GameLocation map, Stack<Point> path, Action<string> failed)
        // The stack constructor has no dialogue side effects. Zero endpoint disables vanilla's off-screen teleport branch.
        : base(path, map, npc, Point.Zero)
    { _npc = npc; _failed = failed; _lastTile = npc.TilePoint; NPCSchedule = false; nonDestructivePathing = false; }

    public override bool update(GameTime time)
    {
        try
        {
            if (_npc.currentLocation != location || new SmapiHardAnchorProvider().IsHardAnchorActive(_npc.Name))
                return Fail("movementInterrupted");
            if (!NpcMovementRules.CanEnter(_npc, location)) return Fail("locationAccessChanged");
            if (_npc.TilePoint != _lastTile) { _lastTile = _npc.TilePoint; _lastProgress = DateTime.UtcNow; }
            if (DateTime.UtcNow - _lastProgress > TimeSpan.FromSeconds(10)) return Fail("pathStalled");
            if (pathToEndPoint?.Count > 0)
            {
                var next = pathToEndPoint.Peek();
                if (!NpcMovementRules.CanStand(_npc, location, next.X, next.Y)) return Fail("pathTileBlocked");
            }
            Completed = base.update(time);
            return Completed;
        }
        catch (Exception) { return Fail("pathValidationFailed"); }
    }
    private bool Fail(string reason) { _npc.Halt(); pathToEndPoint?.Clear(); _failed(reason); return true; }
}
