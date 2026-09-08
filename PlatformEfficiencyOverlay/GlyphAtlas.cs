using System;
using UnityEngine;

/// <summary>
/// Quads UV-mapped onto the character atlas the game already ships for the super chunk
/// coordinate labels, so throughput numbers can be drawn straight into the world with
/// the instanced renderer - no fonts, no canvas, no per-label GameObjects.
///
/// Atlas layout is 7x7: 0-9 are digits, 10-35 are A-Z, 36 is a slash, 37 is a minus.
/// </summary>
public class GlyphAtlas : IDisposable
{
    public const int GlyphSlash = 36;
    public const int GlyphMinus = 37;

    /// Letter index of 'M', used to render the "/M" suffix on a rate.
    public const int GlyphM = 10 + ('M' - 'A');

    private const int AtlasDimensions = 7;
    private const int GlyphCount = AtlasDimensions * AtlasDimensions;

    private readonly TemporaryMeshReference[] Meshes = new TemporaryMeshReference[GlyphCount];

    public GlyphAtlas()
    {
        Vector2 cell = Vector2.one / AtlasDimensions;

        for (int i = 0; i < GlyphCount; i++)
        {
            int column = i % AtlasDimensions;
            int row = i / AtlasDimensions;

            Vector2 min = new Vector2(column * cell.x, (1 - row) * cell.y - 2f * cell.y);
            Vector2 max = min + cell;

            Mesh mesh = new Mesh
            {
                name = "PlatformEfficiencyOverlay.Glyph[" + i + "]",
                vertices = GeometryHelpers.PlaneVertices,
                triangles = GeometryHelpers.PlaneTriangles,
                normals = GeometryHelpers.PlaneNormals,
                uv = new Vector2[4]
                {
                    new Vector2(max.x, min.y),
                    new Vector2(max.x, max.y),
                    new Vector2(min.x, max.y),
                    new Vector2(min.x, min.y)
                }
            };

            Meshes[i] = new TemporaryMeshReference(mesh);
        }
    }

    public IMeshReference Get(int glyphIndex)
    {
        return Meshes[glyphIndex];
    }

    /// <summary>Number of glyphs <see cref="DigitAt"/> will produce for this value.</summary>
    public static int DigitCount(int value)
    {
        int digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }

        return digits;
    }

    /// <summary>Digit at <paramref name="position"/>, counted from the left.</summary>
    public static int DigitAt(int value, int position, int digitCount)
    {
        for (int i = digitCount - 1 - position; i > 0; i--)
        {
            value /= 10;
        }

        return value % 10;
    }

    public void Dispose()
    {
        for (int i = 0; i < Meshes.Length; i++)
        {
            Meshes[i]?.Dispose();
            Meshes[i] = null;
        }
    }
}
