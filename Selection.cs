using System;

namespace AIHub;

public sealed class Selection
{
    private readonly int count;
    private int remainder;
    public int Index { get; private set; }

    public Selection(int count, int index = 0)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        this.count = count;
        Index = Mod(index);
    }

    private int Mod(int value) => (value % count + count) % count;
    public void Select(int index) { Index = Mod(index); remainder = 0; }
    public void Step(int delta) => Select(Index + delta);

    public bool Scroll(int delta)
    {
        // Windows sends 120 units per wheel detent; preserve precision-wheel deltas.
        if (Math.Sign(delta) != Math.Sign(remainder)) remainder = 0;
        remainder += delta;
        int steps = remainder / 120;
        remainder %= 120;
        if (steps == 0) return false;
        Index = Mod(Index - steps);
        return true;
    }
}
