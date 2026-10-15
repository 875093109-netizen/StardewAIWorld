namespace AIWorld.Core.Perception;

/// <summary>Conservative tile ray. Corner crossings test both side cells; the visible target itself is not an obstruction.</summary>
public static class GridSight
{
    public static bool Clear(TilePosition from, TilePosition to, Func<int, int, bool> blocked)
    {
        if (from.DistanceSquared(to) > PerceptionEngine.Radius * PerceptionEngine.Radius) return false;
        int x = from.X, y = from.Y, dx = Math.Abs(to.X - x), dy = Math.Abs(to.Y - y);
        int sx = Math.Sign(to.X - x), sy = Math.Sign(to.Y - y), ix = 0, iy = 0;
        while (ix < dx || iy < dy)
        {
            int decision = (1 + 2 * ix) * dy - (1 + 2 * iy) * dx;
            if (decision == 0)
            {
                if (blocked(x + sx, y) || blocked(x, y + sy)) return false;
                x += sx; y += sy; ix++; iy++;
            }
            else if (decision < 0) { x += sx; ix++; }
            else { y += sy; iy++; }
            if ((x != to.X || y != to.Y) && blocked(x, y)) return false;
        }
        return true;
    }
}
