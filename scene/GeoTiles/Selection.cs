namespace GeoTiles;

/// <summary>What the selection is asked: where the eye is, what part of the world matters, and how much error in
/// pixels is tolerated.</summary>
public sealed record SelectionOptions
{
    public required Vec3d Camera { get; init; }

    /// <summary>The other places the camera will pass through, such as a drone's ride: each tile is judged from the
    /// nearest of them and <see cref="Camera"/>, so the tiles it flies over are fine when it gets there, though
    /// they are fetched once, at the start.</summary>
    public IReadOnlyList<Vec3d> Path { get; init; } = [];

    /// <summary>Only tiles that touch the sphere (<see cref="Target"/>, <see cref="Radius"/>) are kept: the demo
    /// shows one place, not the whole view frustum.</summary>
    public required Vec3d Target { get; init; }

    public required double Radius { get; init; }
    public double FovYRadians { get; init; } = Math.PI / 3;
    public int ScreenHeight { get; init; } = 720;

    /// <summary>The tolerated screen-space error in pixels; 16 is the usual default of 3D Tiles viewers.</summary>
    public double MaxScreenSpaceError { get; init; } = 16;

    /// <summary>A cap on the meshes to fetch, so a mistake costs a bounded number of requests.</summary>
    public int MaxTiles { get; init; } = 200;

    /// <summary>A cap on the nested tilesets to fetch, which are requests too.</summary>
    public int MaxTilesets { get; init; } = 250;
}

public sealed record SelectedTile(Uri Content, Mat4d Transform, double GeometricError, int Depth);

public sealed record SelectionResult(IReadOnlyList<SelectedTile> Tiles, bool Truncated, int TilesetsLoaded,
    IReadOnlyList<string> Copyrights);

public static class TileSelector
{
    /// <summary>The error of a tile on screen, in pixels: its geometric error in metres, seen from
    /// <paramref name="distance"/> metres, on a screen <paramref name="screenHeight"/> pixels high.</summary>
    public static double ScreenSpaceError(double geometricError, double distance, int screenHeight, double fovY) =>
        geometricError * screenHeight / (2 * Math.Max(distance, 1e-3) * Math.Tan(fovY / 2));

    /// <summary>Walks the tree from <paramref name="root"/>, loading the external tilesets it meets through
    /// <paramref name="loadTileset"/>, and returns the meshes to draw.
    ///
    /// Delta's change to GeoTiles' walk (brickyard, scene/GeoTiles): the tree is refined best first, the open tile with
    /// the largest error on screen first, as long as the caps allow. GeoTiles walks depth first and stops at the cap,
    /// which leaves whole parts of the place unfetched (seen on 2026-11-18 at Parliament Hill: no tile at the door,
    /// 160 elsewhere); this walk covers the whole place coarsely before it refines anywhere, and spends the cap where it
    /// shows most. A tile that draws nothing is opened whatever its error.</summary>
    public static async Task<SelectionResult> SelectAsync(Tile root, SelectionOptions options,
        Func<Uri, Tile, Task<TilesetDocument>> loadTileset)
    {
        var copyrights = new List<string>();
        var truncated = false;
        var tilesets = 0;

        bool Near(Tile tile) => (tile.Center - options.Target).Length <= tile.Radius + options.Radius;
        static int Meshes(Tile tile) => tile.Contents.Count(c => !Tile.IsTileset(c));
        double Error(Tile tile)
        {
            var nearest = (tile.Center - options.Camera).Length;
            foreach (var eye in options.Path)
            {
                nearest = Math.Min(nearest, (tile.Center - eye).Length);
            }

            return ScreenSpaceError(tile.GeometricError, nearest - tile.Radius, options.ScreenHeight, options.FovYRadians);
        }

        // The open tiles, each with its depth: what is drawn now.
        var open = new List<(Tile Tile, int Depth)> { (root, 0) };
        var settled = new HashSet<Tile>(ReferenceEqualityComparer.Instance); // refined already (ADD), or past a cap
        var drawn = Meshes(root);
        while (true)
        {
            var best = -1;
            var bestError = options.MaxScreenSpaceError;
            for (var i = 0; i < open.Count; i++)
            {
                var tile = open[i].Tile;
                if (settled.Contains(tile) || (tile.Children.Count == 0 && !tile.Contents.Any(Tile.IsTileset)))
                {
                    continue;
                }

                var error = Meshes(tile) == 0 ? double.PositiveInfinity : Error(tile);
                if (error > bestError)
                {
                    (best, bestError) = (i, error);
                }
            }

            if (best < 0)
            {
                break;
            }

            var (parent, depth) = open[best];
            // The tiles that refine it, each down to one that draws: Google's children are external tilesets, whose
            // roots hold the meshes. All or nothing: a refinement the tilesets' cap cuts short keeps the parent as it
            // is, rather than leave a hole where a child could not be fetched.
            var children = new List<Tile>();
            async Task<bool> Gather(Tile tile, bool self, int levels)
            {
                if (self && (!Near(tile) || Meshes(tile) > 0 || levels == 0))
                {
                    if (Near(tile)) children.Add(tile);
                    return true;
                }

                foreach (var child in tile.Children)
                {
                    if (!await Gather(child, true, levels - 1)) return false;
                }

                foreach (var content in tile.Contents.Where(Tile.IsTileset))
                {
                    if (tilesets >= options.MaxTilesets) return false;
                    // An external tileset is this tile's children, written elsewhere.
                    var doc = await loadTileset(content, tile);
                    tilesets++;
                    if (!string.IsNullOrWhiteSpace(doc.Copyright) && !copyrights.Contains(doc.Copyright))
                    {
                        copyrights.Add(doc.Copyright);
                    }

                    if (!await Gather(doc.Root, true, levels - 1)) return false;
                }

                return true;
            }

            if (!await Gather(parent, false, 8))
            {
                settled.Add(parent);
                truncated = true;
                continue;
            }

            var replaces = parent.Replace || Meshes(parent) == 0;
            var after = drawn - (replaces ? Meshes(parent) : 0) + children.Sum(Meshes);
            if (after > options.MaxTiles)
            {
                settled.Add(parent);
                truncated = true;
                continue;
            }

            drawn = after;
            if (replaces)
            {
                open.RemoveAt(best);
            }
            else
            {
                settled.Add(parent);
            }

            open.AddRange(children.Select(c => (c, depth + 1)));
        }

        var selected = open.SelectMany(o => o.Tile.Contents.Where(c => !Tile.IsTileset(c))
            .Select(mesh => new SelectedTile(mesh, o.Tile.Transform, o.Tile.GeometricError, o.Depth))).ToList();
        return new SelectionResult(selected, truncated, tilesets, copyrights);
    }
}
