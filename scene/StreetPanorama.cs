using System.Text;
using System.Text.Json;
using StbImageSharp;
using SN = System.Numerics;

// Google Street View at street level (the user, 2026-11-18: « toujours pas terrible » of the 3D tiles near the eye, then
// « y'a pas une API google street view ? » and « Oui » to a prototype). The 3D tiles are made from the air and melt at
// the height of a walker; Street View's panoramas are photographs taken in the street. The panorama nearest the eye is
// drawn on a sphere of Radius metres round the place it was taken, its lower half laid on the street, so that the ground
// keeps its parallax as the eye moves near it; the 3D tiles stay for the distance and the air.
//
// Google's Map Tiles API, Street View tiles (developers.google.com/maps/documentation/tile/streetview and policies, read
// on 2026-11-18): a session (createSession, mapType streetview), the metadata of the panorama nearest a place (not
// billed), then its tiles at zoom Zoom (billed: 100,000 free a month, then 2 USD a thousand). They live in memory for the
// run, never on disk; the attribution card shows "Google Maps", the panorama's copyright and date, and a click on it
// opens the panorama's report-a-problem link, as the policies ask. The key goes to tile.googleapis.com over HTTPS only and
// is never written or printed.
internal static class StreetPanorama
{
    internal const int Zoom = 3, MostTilesEach = 40, MostPanoramas = 60;
    // Sink: the panorama's street lies this far under the hall's floor, which is seen through the open door over it.
    internal const double Radius = 30, CameraHeight = 2.5, Near = 12, Again = 8, Sink = 0.05;

    // A panorama: where it was taken, which way its image's centre looks, what must be shown with it, its image as
    // Cols × Rows layers of GoogleStreets.Side pixels, and how much of the last column and row the image fills.
    internal sealed record Pano(string Id, double Lat, double Lon, double Heading, string Copyright, string? Date, string? Report,
        int Cols, int Rows, double Wide, double High, int Tile, byte[][] Layers);

    private static string? session;
    private static int panoramas, tiles;

    internal static int Tiles => tiles;

    // A panorama's metadata, as Google answers it.
    internal sealed record Meta(string Id, double Lat, double Lon, double Heading, string Copyright, string? Date, string? Report,
        string? Kind, double Width, double Height, int Tile)
    {
        internal static Meta? Read(JsonElement r)
        {
            string? Text(string name) => r.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            double Number(string name, double fallback) => r.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : fallback;
            return Text("panoId") is not { Length: > 0 } id || !r.TryGetProperty("lat", out _) ? null
                : new Meta(id, Number("lat", 0), Number("lng", 0), Number("heading", 0), Text("copyright") ?? "", Text("date"), Text("reportProblemLink"),
                    Text("imageryType"), Number("imageWidth", 0), Number("imageHeight", 0), (int)Number("tileWidth", 512));
        }

        // Taken by Google's cars: its copyright names Google as the photographer, outdoors, and whole (13312 × 6656 for
        // the older cameras, 16384 × 8192 since; the public's photospheres are often far smaller).
        internal bool Taken() => Copyright.Contains("Google", StringComparison.OrdinalIgnoreCase) && Kind != "indoor" && Width >= 13312 && Height >= 6656;

        internal string Why() => $"{(Copyright.Length > 0 ? Copyright : "no copyright")}, {Kind ?? "?"}, {Width:F0} × {Height:F0}";
    }

    internal const double Within = 25, Probe = 15;

    // The nearest panorama of Google's own within Within metres of (lat, lon), with its tiles; null when there is none,
    // when it is `shown` already, after MostPanoramas this run, without a key, or when Google refuses (logged).
    internal static Task<Pano?> Nearest(double lat, double lon, Action<string> log, string? shown = null) => Task.Run(async () =>
    {
        if (GoogleStreets.Key() is not { } key) return null;
        if (panoramas >= MostPanoramas)
        {
            log($"no more panoramas this run ({MostPanoramas})");
            return null;
        }

        var http = GoogleStreets.Http;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            if (session is null)
            {
                using var body = new StringContent("""{"mapType":"streetview","language":"en-US","region":"CA"}""", Encoding.UTF8, "application/json");
                using var made = await http.PostAsync($"https://tile.googleapis.com/v1/createSession?key={Uri.EscapeDataString(key)}", body, cancel.Token);
                if (!made.IsSuccessStatusCode) throw new HttpRequestException($"createSession: HTTP {(int)made.StatusCode}");
                using var doc = JsonDocument.Parse(await made.Content.ReadAsStringAsync(cancel.Token));
                session = doc.RootElement.GetProperty("session").GetString();
            }

            var signed = $"session={Uri.EscapeDataString(session!)}&key={Uri.EscapeDataString(key)}";
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            // The metadata (not billed) at the eye and at eight places Probe metres round it: the nearest panorama of
            // Google's own cars within Within metres, outdoors and whole (Taken). The public's photospheres are left
            // out (the user, 2026-11-18: one of them, small and turned, was all the prototype showed); the API has no
            // filter for them (« there is no way to use only one source of Street View imagery over the other »).
            async Task<Meta?> Ask(double atLat, double atLon, int radius)
            {
                using var meta = await http.GetAsync($"https://tile.googleapis.com/v1/streetview/metadata?{signed}&lat={atLat.ToString(inv)}&lng={atLon.ToString(inv)}&radius={radius}", cancel.Token);
                if (meta.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
                if (!meta.IsSuccessStatusCode) throw new HttpRequestException($"metadata: HTTP {(int)meta.StatusCode}");
                using var m = JsonDocument.Parse(await meta.Content.ReadAsStringAsync(cancel.Token));
                return Meta.Read(m.RootElement);
            }

            var here = OttawaStreets.Local(lat, lon);
            var places = new List<(double Lat, double Lon, int Radius)> { (lat, lon, (int)Within) };
            for (var k = 0; k < 8; k++)
            {
                var (at, an) = OttawaStreets.LatLon(here + (new SN.Vector2(MathF.Sin(k * MathF.PI / 4), MathF.Cos(k * MathF.PI / 4)) * (float)Probe));
                places.Add((at, an, (int)Probe));
            }

            var found = await Task.WhenAll(places.Select(p => Ask(p.Lat, p.Lon, p.Radius)));
            var seen = found.OfType<Meta>().DistinctBy(f => f.Id).Select(f => (Meta: f, Away: SN.Vector2.Distance(here, OttawaStreets.Local(f.Lat, f.Lon)))).OrderBy(f => f.Away).ToList();
            var best = seen.FirstOrDefault(f => f.Away <= Within && f.Meta.Taken());
            if (best.Meta is not { } chosen)
            {
                log(seen.Count == 0 ? $"no panorama within {Within + Probe:F0} m"
                    : $"no Google panorama within {Within:F0} m; nearest {seen[0].Away:F0} m: {seen[0].Meta.Why()}");
                return null;
            }

            if (chosen.Id == shown) return null; // the one drawn already: nothing to fetch
            var id = chosen.Id;
            var (cols, rows, wide, high) = Grid(chosen.Width, chosen.Height, chosen.Tile);
            if (cols * rows > MostTilesEach) throw new InvalidDataException($"{cols} × {rows} tiles, more than {MostTilesEach}");
            if (!GoogleMonth.Take(GoogleMonth.Kind.StreetView, cols * rows))
            {
                log($"not fetched: this month's free {GoogleMonth.FreeStreetView} Street View tiles are spent");
                return null;
            }

            var layers = new byte[cols * rows][];
            await Parallel.ForEachAsync(Enumerable.Range(0, cols * rows), new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = cancel.Token }, async (n, token) =>
            {
                var (x, y) = (n % cols, n / cols);
                using var got = await http.GetAsync($"https://tile.googleapis.com/v1/streetview/tiles/{Zoom}/{x}/{y}?{signed}&panoId={Uri.EscapeDataString(id)}", token);
                Interlocked.Increment(ref tiles);
                var bytes = got.IsSuccessStatusCode ? await got.Content.ReadAsByteArrayAsync(token) : null;
                layers[n] = Square(bytes);
            });
            panoramas++;
            return new Pano(id, chosen.Lat, chosen.Lon, chosen.Heading, chosen.Copyright, chosen.Date, chosen.Report,
                cols, rows, wide, high, chosen.Tile, layers);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidDataException or OperationCanceledException or KeyNotFoundException)
        {
            log($"no panorama: {e.Message}"); // the messages carry no URL, so no key
            return null;
        }
    });

    // The panorama's report-a-problem page in the browser, when Google gave one on https at a google.com host.
    internal static void Report(Pano p, Action<string> log)
    {
        if (!Uri.TryCreate(p.Report, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || !(url.Host == "google.com" || url.Host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase)))
        {
            log("no report link for this panorama");
            return;
        }

        log("opens the report-a-problem page");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log($"cannot open the report page: {e.Message}");
        }
    }

    // The tiles at Zoom of a panorama width × height at its full size (zoom 5): each zoom halves it. How many columns and
    // rows, and the image's width and height in tiles (the last column and row may be part empty).
    internal static (int Cols, int Rows, double Wide, double High) Grid(double width, double height, int tile)
    {
        var shrink = Math.Pow(2, 5 - Zoom);
        var (w, h) = (width / shrink / tile, height / shrink / tile);
        return ((int)Math.Ceiling(w - 1e-6), (int)Math.Ceiling(h - 1e-6), w, h);
    }

    // A tile's JPEG as a GoogleStreets.Side square RGBA layer; grey when it did not come.
    private static byte[] Square(byte[]? bytes)
    {
        var side = GoogleStreets.Side;
        if (bytes is not null)
        {
            try
            {
                var image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
                return GoogleStreets.Square(new GeoTiles.Pixels(image.Width, image.Height, 4, image.Data), side);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
            {
            }
        }

        var grey = new byte[side * side * 4];
        for (var i = 0; i < grey.Length; i += 4) (grey[i], grey[i + 1], grey[i + 2], grey[i + 3]) = (110, 110, 110, 255);
        return grey;
    }

    // The panorama in the hall's frame: a sphere of Radius metres round the camera, CameraHeight over the street, its
    // directions below the horizon cut by the street; each of its tiles a patch painted with its layer, from layer first
    // on (Material.Aerial, unlit). Longitude is the compass, the image's centre at the panorama's heading; latitude from
    // the zenith at the top to the nadir at the bottom.
    internal static Vertex[] Build(Pano p, OttawaStreets.Placement placement, int first)
    {
        const int steps = 6;
        var centre = placement.ToHall(OttawaStreets.Local(p.Lat, p.Lon), (float)CameraHeight);
        var o = new List<Vertex>(p.Cols * p.Rows * steps * steps * 6);
        SN.Vector3 Point(double u, double v)
        {
            var yaw = (p.Heading + ((u - 0.5) * 360)) * Math.PI / 180;
            var pitch = (0.5 - v) * Math.PI;
            var en = new SN.Vector2((float)(Math.Sin(yaw) * Math.Cos(pitch)), (float)(Math.Cos(yaw) * Math.Cos(pitch)));
            var dir = new SN.Vector3(SN.Vector2.Dot(en, placement.Left), (float)Math.Sin(pitch), SN.Vector2.Dot(en, placement.Out));
            var t = dir.Y < -1e-3f ? Math.Min(Radius, (CameraHeight + Sink) / -dir.Y) : Radius;
            return centre + (dir * (float)(t * FreeCamera.Scale));
        }

        for (var ty = 0; ty < p.Rows; ty++)
        {
            for (var tx = 0; tx < p.Cols; tx++)
            {
                // What of the image this tile holds, in the tile's own coordinates and in the whole image's.
                var (uEnd, vEnd) = (Math.Min(1.0, p.Wide - tx), Math.Min(1.0, p.High - ty));
                var layer = first + (ty * p.Cols) + tx;
                for (var j = 0; j < steps; j++)
                {
                    for (var i = 0; i < steps; i++)
                    {
                        var corners = new (double U, double V)[] { (i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1) }
                            .Select(c => (U: c.Item1 / steps * uEnd, V: c.Item2 / steps * vEnd)).ToArray();
                        Vertex At((double U, double V) c)
                        {
                            var pos = Point((tx + c.U) / p.Wide, (ty + c.V) / p.High);
                            var n = SN.Vector3.Normalize(centre - pos);
                            return new Vertex(pos, n, SN.Vector3.One, Material.Aerial, SN.Vector3.UnitX, new((float)c.U, (float)c.V, layer, 0f));
                        }

                        var q = corners.Select(At).ToArray();
                        o.AddRange([q[0], q[1], q[2], q[0], q[2], q[3]]);
                    }
                }
            }
        }

        return [.. o];
    }

    internal static void SelfTest(Action<bool, string> check)
    {
        Meta? Sample(string json) => Meta.Read(JsonDocument.Parse(json).RootElement);
        var car = Sample("""{"panoId":"a","lat":45.42,"lng":-75.70,"imageWidth":16384,"imageHeight":8192,"tileWidth":512,"heading":59,"imageryType":"outdoor","copyright":"From the Owner, Photo by: Google"}""");
        var sphere = Sample("""{"panoId":"b","lat":45.42,"lng":-75.70,"imageWidth":6144,"imageHeight":3072,"tileWidth":512,"imageryType":"outdoor","copyright":"From the Owner, Photo by: David Carriere"}""");
        var inside = Sample("""{"panoId":"c","lat":45.42,"lng":-75.70,"imageWidth":16384,"imageHeight":8192,"tileWidth":512,"imageryType":"indoor","copyright":"© 2023 Google"}""");
        check(car is { } c && c.Taken() && c.Heading == 59 && sphere is { } s && !s.Taken() && s.Heading == 0 && inside is { } i && !i.Taken(),
            "Google's own outdoor panoramas are taken, the public's photospheres and indoor ones left out");

        var (cols, rows, wide, high) = Grid(13312, 6656, 512);
        check(cols == 7 && rows == 4 && Math.Abs(wide - 6.5) < 1e-9 && Math.Abs(high - 3.25) < 1e-9, $"a 13312 × 6656 panorama is 7 × 4 tiles at zoom 3, the last part empty ({cols} × {rows}, {wide} × {high})");

        var placement = new OttawaStreets.Placement(new SN.Vector2(0f, 0f), new SN.Vector2(0f, 1f));
        var (lat, lon) = OttawaStreets.LatLon(new SN.Vector2(0f, 0f));
        var grey = Enumerable.Range(0, 8 * 4).Select(_ => Array.Empty<byte>()).ToArray();
        var pano = new Pano("p", lat, lon, 0, "© Google", null, null, 8, 4, 8, 4, 512, grey);
        var mesh = Build(pano, placement, 10);
        var centre = placement.ToHall(new SN.Vector2(0f, 0f), (float)CameraHeight);
        var lowest = mesh.Min(v => v.Pos.Y);
        var farthest = mesh.Max(v => SN.Vector3.Distance(v.Pos, centre));
        check(Math.Abs(lowest + (Sink * FreeCamera.Scale)) < 1e-3f && Math.Abs(farthest - (Radius * FreeCamera.Scale)) < 1e-2, $"the panorama's sphere stands on the street and reaches {Radius} m ({lowest:F3}, {farthest / FreeCamera.Scale:F1} m)");
        check(mesh.Min(v => v.Edge.Z) == 10 && mesh.Max(v => v.Edge.Z) == 41 && mesh.All(v => v.Material == Material.Aerial), "its tiles are painted with their layers, from the first given");

        // The image's centre (u 0.5, the heading north here) lies straight out along Out, on the horizon.
        var ahead = mesh.Where(v => v.Edge.Z == 10 + (1 * 8) + 4 && v.Edge.X == 0f && v.Edge.Y == 1f).Select(v => v.Pos - centre).FirstOrDefault();
        check(ahead.Z > 0.9f * Radius * FreeCamera.Scale && Math.Abs(ahead.X) < 0.05f && Math.Abs(ahead.Y) < 0.05f, $"the image's centre looks the panorama's heading ({ahead.X:F2}, {ahead.Y:F2}, {ahead.Z:F2})");
    }
}
