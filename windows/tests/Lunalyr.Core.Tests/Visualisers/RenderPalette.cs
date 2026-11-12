using Lunalyr.Core.Colour;

namespace Lunalyr.Core.Tests.Visualisers;

/// <summary>
/// Album colours for visualiser tests, ported from the Mac's Tests/Swift/Support/VisualiserRenderHarness.swift
/// (<c>RenderPalette</c>): the same HSB stops in the same cells of a 3 × 3 grid. <see cref="None"/> is no artwork.
/// </summary>
internal static class RenderPalette
{
    public static AlbumWallpaperPalette? None => null;

    /// <summary>A dusky cover: deep blue, violet, rose, amber and teal.</summary>
    public static AlbumWallpaperPalette Album => Cover((0.62, 0.70, 0.55), (0.78, 0.55, 0.62), (0.94, 0.58, 0.80),
                                                       (0.08, 0.78, 0.90), (0.50, 0.62, 0.66));
    public static AlbumWallpaperPalette Warm => Cover((0.00, 0.78, 0.70), (0.04, 0.80, 0.92), (0.09, 0.85, 0.95),
                                                      (0.13, 0.70, 0.98), (0.97, 0.65, 0.60));
    public static AlbumWallpaperPalette Cool => Cover((0.55, 0.80, 0.60), (0.50, 0.70, 0.80), (0.62, 0.65, 0.90),
                                                      (0.42, 0.55, 0.75), (0.68, 0.50, 0.70));
    public static AlbumWallpaperPalette Mono => Cover((0.60, 0.05, 0.35), (0.60, 0.05, 0.55), (0.60, 0.05, 0.75),
                                                      (0.60, 0.05, 0.90), (0.60, 0.05, 0.60));
    // Star Trails' sky checks: one blue, warm brown and olive, saturated primaries, pastels, black and white.
    public static AlbumWallpaperPalette Blue => Cover((0.60, 0.85, 0.35), (0.60, 0.70, 0.60), (0.61, 0.55, 0.85),
                                                      (0.59, 0.80, 0.50), (0.60, 0.40, 0.95));
    public static AlbumWallpaperPalette Earth => Cover((0.07, 0.60, 0.40), (0.10, 0.50, 0.55), (0.15, 0.55, 0.45),
                                                       (0.18, 0.45, 0.35), (0.12, 0.35, 0.70));
    public static AlbumWallpaperPalette Vivid => Cover((0.00, 0.95, 0.95), (0.15, 0.95, 0.95), (0.33, 0.90, 0.85),
                                                       (0.60, 0.95, 0.90), (0.83, 0.90, 0.90));
    public static AlbumWallpaperPalette Pastel => Cover((0.95, 0.25, 0.97), (0.40, 0.22, 0.95), (0.75, 0.25, 0.96),
                                                        (0.08, 0.28, 0.98), (0.55, 0.22, 0.97));
    public static AlbumWallpaperPalette Bw => Cover((0.00, 0.00, 0.08), (0.00, 0.00, 0.35), (0.00, 0.00, 0.60),
                                                    (0.00, 0.00, 0.85), (0.00, 0.00, 0.98));

    /// <summary>A 3 × 3 artwork grid (hue, saturation, brightness) whose sampled cells are <paramref name="stops"/>.</summary>
    private static AlbumWallpaperPalette Cover(params (double H, double S, double B)[] stops)
    {
        var grid = new Double3[9];
        Array.Fill(grid, new Double3(0, 0, 0.5));
        for (var i = 0; i < Math.Min(stops.Length, AlbumWallpaperPalette.SampleIndices.Length); i++)
        {
            grid[AlbumWallpaperPalette.SampleIndices[i]] = new Double3(stops[i].H, stops[i].S, stops[i].B);
        }
        return new AlbumWallpaperPalette(grid);
    }
}
