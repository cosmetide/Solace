using SkiaSharp;

namespace Solace.TileRenderer.Wkb;

internal interface IWKBObject
{
    bool ByteOrder { get; }

    uint WkbType { get; }

    static virtual IWKBObject Load(BinaryReader reader)
        => throw new NotImplementedException();

    void Render(SKCanvas canvas, Tile tile, SKColor color, float strokeWidth);
}
