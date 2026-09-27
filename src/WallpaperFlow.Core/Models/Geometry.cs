namespace WallpaperFlow.Core.Models;

/// <summary>Integer rectangle in virtual-screen physical pixels.</summary>
public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);

    public static RectI FromLTRB(int l, int t, int r, int b) => new(l, t, r - l, b - t);

    public RectI Intersect(RectI o)
    {
        int l = Math.Max(X, o.X), t = Math.Max(Y, o.Y);
        int r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r > l && b > t ? FromLTRB(l, t, r, b) : default;
    }

    public RectI Union(RectI o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        return FromLTRB(Math.Min(X, o.X), Math.Min(Y, o.Y), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));
    }

    public bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;

    /// <summary>True when this rectangle covers at least <paramref name="fraction"/> of <paramref name="target"/>.</summary>
    public bool Covers(RectI target, double fraction = 0.98) =>
        !target.IsEmpty && Intersect(target).Area >= target.Area * fraction;

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}
