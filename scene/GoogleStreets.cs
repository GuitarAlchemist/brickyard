using GeoTiles;
using StbImageSharp;
using SN = System.Numerics;

// Google's Photorealistic 3D Tiles past the roll-up door, in place of OpenStreetMap's buildings when G chooses them
// (the author: « Les deux, au choix »; « essayons avec l'api Google »). The tiles are fetched, decoded and brought into
// the hall's frame by ga's GeoTiles (GeoTiles/, a copy of Experiments/Brutal/GeoTiles/GeoTiles.Core at fcef8b5).
//
// Google's terms (developers.google.com/maps/documentation/tile/policies): no tile is written to disk, they live in
// memory for one run (a disk cache would never be read back anyway: the tiles' names change with every session); the
// attribution, "Google" and the providers each tile names, shows along the bottom of the picture while they are drawn.
// The API is paid past 1,000 tiles a month (6 USD a thousand after), so one opening fetches at most MostRequests
// requests, once a run. The key stays in the environment (GOOGLE_MAPS_API_KEY, the process's or the user's): it is sent
// to tile.googleapis.com over HTTPS only and never written or printed (GoogleTileSource).
internal static class GoogleStreets
{
    internal const int Side = 512; // each tile's image, stretched to a square layer of the array
    internal const int MostTiles = 160, MostRequests = 450, MostImages = 320; // 320 layers: 430 MB on the GPU with their mipmaps
    // Metres: the place looked at, out from the door; around it; the eye over the ellipsoid, as a walker's at the door
    // (Wellington Street there is 45.3 m over it, measured on 2026-11-18).
    internal const double Ahead = 150, Reach = 330, EyeHeight = 47;

    internal sealed record Tiles(Vertex[] Vertices, byte[][] Layers, IReadOnlyList<string> Attributions, int Requests, int Meshes, bool Truncated, double Ground);

    // The key, from the process's environment or else the user's; null without one. Never printed.
    private static string? Key() =>
        Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY") is { Length: > 0 } key ? key
        : OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY", EnvironmentVariableTarget.User) is { Length: > 0 } user ? user
        : null;

    internal static bool Keyed => Key() is not null;

    // The tiles around the door, off the render thread; null without a key or when the fetch fails (logged).
    internal static Task<Tiles?> Load(OttawaStreets.Placement placement, Action<string> log) => Task.Run(async () =>
    {
        if (Key() is not { } key)
        {
            log("no GOOGLE_MAPS_API_KEY in the environment");
            return null;
        }

        var (lat, lon) = OttawaStreets.LatLon(placement.Door);
        var frame = new EnuFrame(lat, lon, 0);
        var options = new SelectionOptions
        {
            Camera = frame.ToEcef(new Vec3d(0, 0, EyeHeight)),
            Target = frame.ToEcef(new Vec3d(placement.Out.X * Ahead, placement.Out.Y * Ahead, EyeHeight)),
            Radius = Reach,
            ScreenHeight = 1080,
            MaxTiles = MostTiles,
            MaxTilesets = MostRequests - MostTiles - 20, // the rest of the requests, but for the root and a margin
        };
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("delta-scene/1.0");
        var google = new GoogleTileSource(http, key);
        var source = new Capped(google, MostRequests);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            var scene = await SceneLoader.LoadAsync(source, frame, options, Decode, log, cancel.Token);
            return Build(scene, placement, lat, google.Requests);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log($"failed after {google.Requests} requests: {e.Message}"); // GeoTiles' messages leave the key out
            return null;
        }
    });

    // Asks at most `most` times: past that the load stops, so a mistake costs a bounded number of requests.
    private sealed class Capped(ITileSource inner, int most) : ITileSource
    {
        private int asked;

        public Uri Root => inner.Root;

        public Task<byte[]> GetAsync(Uri uri, CancellationToken cancel = default) =>
            Interlocked.Increment(ref asked) > most
                ? throw new InvalidOperationException($"more than {most} requests asked: stopped")
                : inner.GetAsync(uri, cancel);
    }

    private static GeoTiles.Pixels? Decode(byte[] bytes)
    {
        try
        {
            var image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
            return new GeoTiles.Pixels(image.Width, image.Height, 4, image.Data);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            return null; // KTX2 or WebP: the tile keeps its colours
        }
    }

    // GeoTiles' scene (x east, y up, z south, metres from the door's point on the ellipsoid) in the hall's frame: the
    // ground at the door becomes the floor's level, metres the hall's (OttawaStreets.Placement), each image a layer.
    // What stands where the hall does is left out, so the street's photograph does not come up through its floor.
    internal static Tiles Build(GeoTiles.Scene scene, OttawaStreets.Placement placement, double lat, int requests)
    {
        // True metres east and north to OttawaStreets.Local's, whose degrees are a little off the ellipsoid's.
        const double a = 6_378_137.0, e2 = 0.00669437999014;
        var phi = lat * Math.PI / 180;
        var w = 1 - (e2 * Math.Sin(phi) * Math.Sin(phi));
        var kE = 111_320.0 * Math.Cos(OttawaStreets.Lat * Math.PI / 180) / (a / Math.Sqrt(w) * Math.Cos(phi) * Math.PI / 180);
        var kN = 110_574.0 / (a * (1 - e2) / Math.Pow(w, 1.5) * Math.PI / 180);
        var p = scene.Positions;
        var ground = Ground(p);
        SN.Vector3 Hall(int i) => placement.ToHall(
            placement.Door + new SN.Vector2((float)(p[3 * i] * kE), (float)(-p[(3 * i) + 2] * kN)), (float)(p[(3 * i) + 1] - ground));

        // The map turns the frame's handedness or not: a triangle's corners are swapped when it does, so that its front
        // stays its front.
        var o = placement.ToHall(placement.Door, 0f);
        var ex = placement.ToHall(placement.Door + SN.Vector2.UnitX, 0f) - o;
        var ey = placement.ToHall(placement.Door, 1f) - o;
        var ez = placement.ToHall(placement.Door - SN.Vector2.UnitY, 0f) - o;
        var mirrored = SN.Vector3.Dot(ex, SN.Vector3.Cross(ey, ez)) < 0f;

        var layers = new List<byte[]>();
        var held = new Dictionary<GeoTiles.Pixels, int>(ReferenceEqualityComparer.Instance);
        var vertices = new List<Vertex>(p.Length / 3);
        foreach (var batch in scene.Batches.Count > 0 ? scene.Batches : [new Batch(0, scene.Vertices, null)])
        {
            var layer = -1;
            if (batch.Image is { } image)
            {
                if (held.TryGetValue(image, out var known)) layer = known;
                else if (layers.Count < MostImages)
                {
                    layer = held[image] = layers.Count;
                    layers.Add(Square(image, Side));
                }
            }

            for (var t = batch.First; t + 2 < batch.First + batch.Count; t += 3)
            {
                int ia = t, ib = mirrored ? t + 2 : t + 1, ic = mirrored ? t + 1 : t + 2;
                var (pa, pb, pc) = (Hall(ia), Hall(ib), Hall(ic));
                var centre = (pa + pb + pc) / 3f;
                if (centre.X > HallShell.Left - 0.2f && centre.X < HallShell.Right + 0.2f && centre.Z > HallShell.Back - 0.2f && centre.Z < HallShell.Front + 0.2f) continue;
                var cross = SN.Vector3.Cross(pb - pa, pc - pa);
                if (cross.LengthSquared() < 1e-14f) continue;
                var n = SN.Vector3.Normalize(cross);
                foreach (var (i, at) in (ReadOnlySpan<(int, SN.Vector3)>)[(ia, pa), (ib, pb), (ic, pc)])
                {
                    var colour = new SN.Vector3(scene.Colors[3 * i], scene.Colors[(3 * i) + 1], scene.Colors[(3 * i) + 2]);
                    var uv = scene.Uvs.Length > 0 ? new SN.Vector2(scene.Uvs[2 * i], scene.Uvs[(2 * i) + 1]) : SN.Vector2.Zero;
                    vertices.Add(layer >= 0
                        ? new Vertex(at, n, colour, Material.Aerial, SN.Vector3.Zero, new(uv.X, uv.Y, layer, 0f))
                        : new Vertex(at, n, colour, Material.Matte, SN.Vector3.Zero, new(0f, 0f, 1f, 1f)));
                }
            }
        }

        return new Tiles([.. vertices], [.. layers], scene.Attributions, requests, scene.Tiles, scene.Truncated, ground);
    }

    // The ground's height at the door: the lowest tenth of what lies within 10 m of it (the street, under its cars and
    // people), or 36 m when nothing does (Wellington Street, about 70 m above the sea, the geoid 34 m under it).
    internal static double Ground(float[] positions)
    {
        var near = new List<float>();
        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            if ((positions[i] * positions[i]) + (positions[i + 2] * positions[i + 2]) < 100f) near.Add(positions[i + 1]);
        }

        if (near.Count == 0) return 36;
        near.Sort();
        return near[near.Count / 10];
    }

    // An image stretched to a side × side RGBA square, bilinearly (its texture coordinates span it whatever its shape).
    internal static byte[] Square(GeoTiles.Pixels image, int side)
    {
        var o = new byte[side * side * 4];
        var c = image.Channels;
        for (var y = 0; y < side; y++)
        {
            var fy = Math.Clamp((((y + 0.5f) * image.Height) / side) - 0.5f, 0f, image.Height - 1f);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, image.Height - 1);
            var ty = fy - y0;
            for (var x = 0; x < side; x++)
            {
                var fx = Math.Clamp((((x + 0.5f) * image.Width) / side) - 0.5f, 0f, image.Width - 1f);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, image.Width - 1);
                var tx = fx - x0;
                for (var k = 0; k < 4; k++)
                {
                    float At(int px, int py) => k < c ? image.Data[(((py * image.Width) + px) * c) + k] : 255f;
                    var top = At(x0, y0) + ((At(x1, y0) - At(x0, y0)) * tx);
                    var bottom = At(x0, y1) + ((At(x1, y1) - At(x0, y1)) * tx);
                    o[(((y * side) + x) * 4) + k] = (byte)Math.Clamp(MathF.Round(top + ((bottom - top) * ty)), 0f, 255f);
                }
            }
        }

        return o;
    }

    internal static void SelfTest(Action<bool, string> check)
    {
        var placement = new OttawaStreets.Placement(new SN.Vector2(200f, -40f), SN.Vector2.Normalize(new SN.Vector2(-0.5f, 0.85f)));
        var (lat, lon) = OttawaStreets.LatLon(placement.Door);
        var back = OttawaStreets.Local(lat, lon);
        check(SN.Vector2.Distance(back, placement.Door) < 0.01f, $"Google's tiles: a point's latitude and longitude lead back to it ({back.X:F3}, {back.Y:F3})");

        // A made-up scene: the street at the door 36 m up, and one wall 100 m out along the way out of the door, facing it.
        var outE = placement.Out.X * 100f;
        var outN = placement.Out.Y * 100f;
        var side = new SN.Vector2(-placement.Out.Y, placement.Out.X) * 5f;
        float[] positions =
        [
            -3f, 36f, 3f, 3f, 36f, 3f, 0f, 36f, -3f, // the street at the door (x east, y up, z south)
            outE + side.X, 36f, -(outN + side.Y), outE - side.X, 36f, -(outN - side.Y), outE, 56f, -outN, // the wall ahead, wound to face the door
        ];
        var scene = new GeoTiles.Scene(positions, new float[positions.Length], [.. Enumerable.Repeat(0.5f, positions.Length)], ["Google", "Airbus"], 1, 3, false)
        {
            Uvs = new float[positions.Length / 3 * 2],
            Batches = [new Batch(0, 3, null), new Batch(3, 3, new GeoTiles.Pixels(2, 1, 3, [255, 0, 0, 0, 0, 255]))],
        };
        var tiles = Build(scene, placement, lat, 3);
        check(Math.Abs(tiles.Ground - 36) < 1e-6, $"the ground is the street's height at the door ({tiles.Ground:F2})");
        check(tiles.Vertices.Length == 3 && tiles.Layers.Length == 1, $"what stands where the hall does is left out, the wall kept with its image ({tiles.Vertices.Length} vertices, {tiles.Layers.Length} layers)");
        var wall = tiles.Vertices.Length == 3 ? (tiles.Vertices[0].Pos + tiles.Vertices[1].Pos + tiles.Vertices[2].Pos) / 3f : SN.Vector3.Zero;
        check(MathF.Abs(wall.X - HallShell.DoorX) < 0.3f && MathF.Abs(wall.Z - (HallShell.Front + 0.16f + (100f * FreeCamera.Scale))) < 0.6f,
            $"a wall 100 m out of the door is straight ahead at the hall's scale ({wall.X:F2}, {wall.Z:F2})");
        check(tiles.Vertices.Length == 3 && tiles.Vertices[0].Normal.Z < -0.9f, $"the wall faces the door, as it faced it in Google's frame ({(tiles.Vertices.Length == 3 ? tiles.Vertices[0].Normal.Z : 0f):F2})");
        check(tiles.Vertices.All(v => v.Material == Material.Aerial), "the wall is painted with its image (Material.Aerial)");
        var square = Square(new GeoTiles.Pixels(2, 1, 3, [255, 0, 0, 0, 0, 255]), 4);
        check(square.Length == 64 && square[0] == 255 && square[2] == 0 && square[3] == 255 && square[(3 * 4) + 2] == 255, "an image is stretched to a square, opaque, its ends kept");
        check(!Keyed || Key()!.Length > 0, "the key is read from the environment only"); // never printed

        // The walk best first (GeoTiles/Selection.cs): with room for three meshes, the tile near the eye is refined into
        // its two children and the far one is kept, so no part of the place goes unfetched.
        Tile Leaf(double x, double error, params Tile[] children)
        {
            var tile = new Tile { Center = new Vec3d(x, 0, 0), Radius = 10, GeometricError = error, Replace = true, Transform = Mat4d.Identity, Contents = [new Uri($"https://tile.googleapis.com/m{x}.glb")] };
            tile.Children.AddRange(children);
            return tile;
        }

        var root = new Tile { Center = new Vec3d(0, 0, 0), Radius = 1000, GeometricError = 1000, Replace = true, Transform = Mat4d.Identity };
        root.Children.Add(Leaf(20, 50, Leaf(15, 5), Leaf(25, 5)));
        root.Children.Add(Leaf(300, 50, Leaf(295, 5), Leaf(305, 5)));
        var picked = TileSelector.SelectAsync(root, new SelectionOptions { Camera = new Vec3d(0, 0, 0), Target = new Vec3d(0, 0, 0), Radius = 1000, MaxTiles = 3 },
            (_, _) => throw new InvalidOperationException("no nested tileset here")).GetAwaiter().GetResult();
        var names = string.Join(" ", picked.Tiles.Select(t => t.Content.Segments[^1]).Order(StringComparer.Ordinal));
        check(names == "m15.glb m25.glb m300.glb" && picked.Truncated, $"the tiles are refined best first, the whole place kept ({names})");

        // All or nothing: a child behind an external tileset the cap forbids leaves its parent drawn, not a hole.
        var nested = new Tile { Center = new Vec3d(5, 0, 0), Radius = 5, GeometricError = 5, Replace = true, Transform = Mat4d.Identity, Contents = [new Uri("https://tile.googleapis.com/b.json")] };
        var coarse = Leaf(5, 50, nested);
        var holder = new Tile { Center = new Vec3d(0, 0, 0), Radius = 1000, GeometricError = 1000, Replace = true, Transform = Mat4d.Identity };
        holder.Children.Add(coarse);
        Task<TilesetDocument> Fetch(Uri _, Tile __) => Task.FromResult(new TilesetDocument(Leaf(6, 1), null));
        string Picked(int tilesetsAllowed) => string.Join(" ", TileSelector.SelectAsync(holder,
            new SelectionOptions { Camera = new Vec3d(0, 0, 0), Target = new Vec3d(0, 0, 0), Radius = 1000, MaxTilesets = tilesetsAllowed }, Fetch)
            .GetAwaiter().GetResult().Tiles.Select(t => t.Content.Segments[^1]));
        var (without, with) = (Picked(0), Picked(1));
        check(without == "m5.glb" && with == "m6.glb", $"a refinement cut short keeps its parent ({without}); allowed, the child behind the tileset is drawn ({with})");
    }
}
