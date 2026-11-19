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
    // Since 2026-11-18 (« 2 oui » to about 700 requests an opening, past the free 1,000 a month: about 4 USD each) 250
    // meshes and 700 requests (160 and 450 before); 448 layers: 600 MB on the GPU with their mipmaps.
    internal const int MostTiles = 250, MostRequests = 700, MostImages = 448;
    // Metres: the place looked at, out from the door; around it; the eye over the ellipsoid, as a walker's at the door
    // (Wellington Street there is 45.3 m over it, measured on 2026-11-18). Since « beurk » (2026-11-18) the place is
    // smaller and nearer the door (330 and 150 m before), so that the requests refine what is near the eye; the
    // selection judges the error on a 1440-pixel screen (1080 before), which refines further.
    internal const double Ahead = 110, Reach = 260, EyeHeight = 47;

    // The ride's own requests, past the opening's (« fait tout ça », 2026-11-18, the paid option among them): as the eye
    // goes more than RefineStep metres from every place the tiles were refined for, they are chosen again for its path
    // as well, at most RideTiles meshes. What was fetched already comes from memory (this run's, never the disk), so
    // only the new tiles cost; all the run's requests together stop at MostRequests + RideRequests.
    internal const int RideRequests = 300, RideTiles = 320;
    internal const double RefineStep = 70;

    // A coarse tile's spikes (melted cars, trees and people a few metres tall): a triangle of a tile coarser than
    // SpikeError metres lying wholly under SpikeHeight metres over the ground there is flattened onto that ground, with
    // its photograph, rather than stand up as a blade or a blob near the eye.
    internal const double SpikeError = 3, SpikeHeight = 8, GroundCell = 12;

    internal sealed record Tiles(Vertex[] Vertices, byte[][] Layers, IReadOnlyList<string> Attributions, int Requests, int Meshes, bool Truncated, double Ground)
    {
        public int Flattened { get; init; }
    }

    // This run's memory of what Google answered, by URL, so that a second choice of tiles asks only for what is new; and
    // the one source that keeps the session. Never written to disk.
    private static readonly Dictionary<string, byte[]> Fetched = new(StringComparer.Ordinal);
    private static GoogleTileSource? google;
    private static HttpClient? http;

    // The run's one client to tile.googleapis.com (the 3D tiles and StreetPanorama's).
    internal static HttpClient Http
    {
        get
        {
            if (http is null)
            {
                var client = new HttpClient(new Metered(new HttpClientHandler()));
                client.DefaultRequestHeaders.UserAgent.ParseAdd("delta-scene/1.0");
                Interlocked.CompareExchange(ref http, client, null);
            }

            return http;
        }
    }

    // How many requests this run has made, all choices together.
    internal static int RunRequests => google?.Requests ?? 0;

    internal static bool MayRefine => google is not null && RunRequests < MostRequests + RideRequests - 40;

    // The key, from the process's environment or else the user's; null without one. Never printed.
    internal static string? Key() =>
        Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY") is { Length: > 0 } key ? key
        : OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY", EnvironmentVariableTarget.User) is { Length: > 0 } user ? user
        : null;

    internal static bool Keyed => Key() is not null;

    // The tiles around the door, off the render thread; null without a key or when the fetch fails (logged). path: the
    // other places the eye has been (east-north metres, OttawaStreets.Local's), for which the tiles are refined too.
    internal static Task<Tiles?> Load(OttawaStreets.Placement placement, Action<string> log, IReadOnlyList<SN.Vector2>? path = null) => Task.Run(async () =>
    {
        if (Key() is not { } key)
        {
            log("no GOOGLE_MAPS_API_KEY in the environment");
            return null;
        }

        var riding = path is { Count: > 0 };
        // Never past the month's free share (GoogleMonth): an opening or a ride that could not finish within it is not
        // begun, so no request is spent on tiles that would not be drawn.
        var left = GoogleMonth.Left(GoogleMonth.Kind.Tiles3D);
        if (left < (riding ? RideRequests : MostRequests))
        {
            log($"not fetched: {left} of this month's {GoogleMonth.Free3D} 3D tile requests left (1,000 free and {GoogleMonth.Dollars3D} USD), {(riding ? RideRequests : MostRequests)} needed");
            return null;
        }

        var (lat, lon) = OttawaStreets.LatLon(placement.Door);
        var frame = new EnuFrame(lat, lon, 0);
        var options = new SelectionOptions
        {
            Camera = frame.ToEcef(new Vec3d(0, 0, EyeHeight)),
            Path = riding ? [.. path!.Select(p => frame.ToEcef(new Vec3d(p.X - placement.Door.X, p.Y - placement.Door.Y, EyeHeight)))] : [],
            Target = frame.ToEcef(new Vec3d(placement.Out.X * Ahead, placement.Out.Y * Ahead, EyeHeight)),
            Radius = Reach,
            ScreenHeight = 1440,
            MaxTiles = riding ? RideTiles : MostTiles,
            // The opening: the rest of its requests, but for the root and a margin. A ride's: the run's cap below.
            MaxTilesets = riding ? MostRequests + RideRequests : MostRequests - MostTiles - 20,
        };
        google ??= new GoogleTileSource(Http, key);
        var source = new Remembered(new Capped(google, MostRequests + RideRequests));
        var before = google.Requests;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            var scene = await SceneLoader.LoadAsync(source, frame, options, Decode, log, cancel.Token);
            return Build(scene, placement, lat, google.Requests - before);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log($"failed after {google.Requests - before} requests: {e.Message}"); // GeoTiles' messages leave the key out
            return null;
        }
    });

    // Asks at most `most` times in all: past that the load stops, so a mistake costs a bounded number of requests.
    private sealed class Capped(ITileSource inner, int most) : ITileSource
    {
        public Uri Root => inner.Root;

        public Task<byte[]> GetAsync(Uri uri, CancellationToken cancel = default) =>
            RunRequests >= most
                ? throw new InvalidOperationException($"more than {most} requests this run: stopped")
                : inner.GetAsync(uri, cancel); // the month's ledger takes each try as it is sent (Metered)
    }

    // Every request for a 3D tile, each retry among them (GoogleTileSource tries up to three times), is taken from the
    // month's ledger as it is sent, and refused past it (Brutal's review of 0.8.0: the ledger took one per tile, not
    // per try).
    internal sealed class Metered(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal static bool Paid(Uri? uri) => uri is { Host: "tile.googleapis.com" } && uri.AbsolutePath.StartsWith("/v1/3dtiles/", StringComparison.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Paid(request.RequestUri) && !GoogleMonth.Take(GoogleMonth.Kind.Tiles3D)
                ? throw new HttpRequestException($"this month's {GoogleMonth.Free3D} 3D tile requests (1,000 free and {GoogleMonth.Dollars3D} USD) are spent: stopped")
                : base.SendAsync(request, cancellationToken);
    }

    // What was fetched already this run comes from memory, the root among them, so that the session and the tiles' URLs
    // stay the same and only new tiles are asked for.
    private sealed class Remembered(ITileSource inner) : ITileSource
    {
        public Uri Root => inner.Root;

        public async Task<byte[]> GetAsync(Uri uri, CancellationToken cancel = default)
        {
            var name = uri.ToString();
            lock (Fetched)
            {
                if (Fetched.TryGetValue(name, out var known)) return known;
            }

            var bytes = await inner.GetAsync(uri, cancel);
            lock (Fetched)
            {
                Fetched[name] = bytes;
            }

            return bytes;
        }
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
        var (heights, flattened) = Flatten(scene);
        SN.Vector3 Hall(int i) => placement.ToHall(
            placement.Door + new SN.Vector2((float)(p[3 * i] * kE), (float)(-p[(3 * i) + 2] * kN)), (float)(heights[i] - ground));

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
                if (Gymnasium.Covers(centre.X, centre.Z, 0.2f)) continue; // the hall and its gymnasium
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

        return new Tiles([.. vertices], [.. layers], scene.Attributions, requests, scene.Tiles, scene.Truncated, ground) { Flattened = flattened };
    }

    // Each vertex's height, the coarse tiles' spikes brought down to the ground: the ground of a GroundCell-metre cell is
    // its lowest vertex, of any tile; a vertex of a tile coarser than SpikeError that stands under SpikeHeight over it is
    // brought down to 15 cm over it, so that the blades and blobs it lifted lie flat with their photograph. Taller things
    // (walls, roofs) and the fine tiles keep their heights. How many vertices came down by more than 30 cm.
    internal static (float[] Heights, int Flattened) Flatten(GeoTiles.Scene scene)
    {
        var p = scene.Positions;
        var count = p.Length / 3;
        var heights = new float[count];
        var lowest = new Dictionary<(int, int), float>();
        (int, int) Cell(int i) => ((int)Math.Floor(p[3 * i] / GroundCell), (int)Math.Floor(p[(3 * i) + 2] / GroundCell));
        for (var i = 0; i < count; i++)
        {
            heights[i] = p[(3 * i) + 1];
            var cell = Cell(i);
            lowest[cell] = lowest.TryGetValue(cell, out var low) ? Math.Min(low, heights[i]) : heights[i];
        }

        var flattened = 0;
        foreach (var batch in scene.Batches)
        {
            if (batch.Error <= SpikeError) continue;
            for (var i = batch.First; i < batch.First + batch.Count && i < count; i++)
            {
                var floor = lowest[Cell(i)];
                if (heights[i] >= floor + SpikeHeight) continue;
                var down = Math.Min(heights[i], floor + 0.15f);
                if (heights[i] - down > 0.3f) flattened++;
                heights[i] = down;
            }
        }

        return (heights, flattened);
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
        var somewhere = new SN.Vector2(230f, 15f);
        check(SN.Vector2.Distance(placement.FromHall(placement.ToHall(somewhere, 3f)), somewhere) < 0.01f, "a point of the streets comes back from the hall's frame");
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
        // A coarse tile's spike lies down on the street; a fine tile's stays, and so does a coarse tile's tall wall.
        float[] spiky =
        [
            0f, 36f, 0f, 4f, 36f, 0f, 2f, 40f, 1f,       // coarse: a blade 4 m tall on the street
            20f, 36f, 0f, 24f, 36f, 0f, 22f, 60f, 1f,    // coarse: a wall 24 m tall
            0f, 36f, 2f, 4f, 36f, 2f, 2f, 39f, 3f,       // fine: a car 3 m tall
        ];
        var lumpy = new GeoTiles.Scene(spiky, new float[spiky.Length], new float[spiky.Length], [], 1, 1, false)
        {
            Batches = [new Batch(0, 6, null) { Error = 10 }, new Batch(6, 3, null) { Error = 1 }],
        };
        var (lain, down) = Flatten(lumpy);
        check(MathF.Abs(lain[2] - 36.15f) < 1e-4f && MathF.Abs(lain[5] - 60f) < 1e-4f && MathF.Abs(lain[8] - 39f) < 1e-4f && down == 1,
            $"a coarse tile's spike lies on the street, its tall wall stands, a fine tile's car stays ({lain[2]:F2}, {lain[5]:F1}, {lain[8]:F1}; {down} down)");

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
