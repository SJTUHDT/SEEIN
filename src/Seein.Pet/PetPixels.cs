namespace Seein.Pet;

public static class PetPixels
{
    public const byte InvisibleAlpha = 16;

    public static void PrepareForLayeredWindow(Span<byte> bgra)
    {
        for (var index = 0; index < bgra.Length; index += 4)
        {
            var alpha = bgra[index + 3];
            if (alpha <= InvisibleAlpha) { bgra.Slice(index, 4).Clear(); continue; }
            if (alpha == 255) continue;
            bgra[index] = (byte)((bgra[index] * alpha + 127) / 255);
            bgra[index + 1] = (byte)((bgra[index + 1] * alpha + 127) / 255);
            bgra[index + 2] = (byte)((bgra[index + 2] * alpha + 127) / 255);
        }
    }
}
