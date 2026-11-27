namespace Seein.Pet;

public readonly record struct PetMenuBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Overlaps(PetMenuBox other) => X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
}

public static class PetMenuLayout
{
    // Behavioral port of dsh-pet shared/menu/position.ts (MIT), in logical screen pixels.
    public static PetMenuBox Place(double width, double height, double itemTop, PetMenuBox parent,
        IReadOnlyList<PetMenuBox> openPanels, PetMenuBox workArea)
    {
        var minX = workArea.X + 4;
        var minY = workArea.Y + 4;
        var maxX = workArea.Right - 4 - width;
        var maxY = workArea.Bottom - 4 - height;
        double ClampX(double x) => Math.Max(minX, Math.Min(x, Math.Max(minX, maxX)));
        double ClampY(double y) => Math.Max(minY, Math.Min(y, Math.Max(minY, maxY)));
        PetMenuBox[] candidates = [new(parent.Right + 4, ClampY(itemTop), width, height),
            new(parent.X - 4 - width, ClampY(itemTop), width, height),
            new(parent.Right + 4, ClampY(parent.Y), width, height), new(parent.X - 4 - width, ClampY(parent.Y), width, height)];
        bool Clear(PetMenuBox box) => !openPanels.Any(box.Overlaps);
        foreach (var box in candidates)
            if (box.X >= minX - .5 && box.X <= maxX + .5 && box.Y >= minY - .5 && box.Y <= maxY + .5 && Clear(box)) return box;
        foreach (var box in candidates)
        {
            var fit = box with { X = ClampX(box.X), Y = ClampY(box.Y) };
            if (Clear(fit)) return fit;
        }
        return new(ClampX(parent.Right + 4), ClampY(itemTop), width, height);
    }
}
