namespace LunaShoppingList.Core;

/// <summary>Converts game world positions to the coordinates shown on the in-game map.</summary>
public static class MapCoordinates
{
    /// <summary>
    /// Map coordinate for one world axis (world X with the map's X offset, or world Z with its Y offset):
    /// 0.02 x (world + offset) + 2048 / sizeFactor + 1, the same conversion Dalamud uses for map links.
    /// </summary>
    /// <param name="world">World position on that axis.</param>
    /// <param name="sizeFactor">The map's size factor (100 for most field zones); 0 gives 0.</param>
    /// <param name="offset">The map's offset on that axis.</param>
    public static float ToMap(float world, ushort sizeFactor, short offset)
    {
        if (sizeFactor == 0)
            return 0;
        return 0.02f * offset + 2048f / sizeFactor + 0.02f * world + 1f;
    }
}
