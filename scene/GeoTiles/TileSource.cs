namespace GeoTiles;

using System.Web;

/// <summary>Where tiles come from. Everything is returned as bytes in memory: nothing here writes a tile to disk,
/// which Google's terms forbid.</summary>
public interface ITileSource
{
    /// <summary>The URL of the root tileset.</summary>
    Uri Root { get; }

    Task<byte[]> GetAsync(Uri uri, CancellationToken cancel = default);
}

/// <summary>Google's Photorealistic 3D Tiles (Map Tiles API). The root request carries the API key; the tilesets
/// it returns carry a <c>session</c> in their URIs, and every later request must send both.</summary>
public sealed class GoogleTileSource(HttpClient http, string apiKey) : ITileSource
{
    private string? _session;

    public Uri Root { get; } = new("https://tile.googleapis.com/v1/3dtiles/root.json");

    private int _requests;
    private int _retried;

    /// <summary>The requests made so far, each attempt counted: the API bills by root request and caps the tile
    /// requests per day.</summary>
    public int Requests => _requests;

    /// <summary>The requests asked again after a timeout or a passing failure.</summary>
    public int Retried => _retried;

    /// <summary>How long one attempt may take, its transfer included: on a slow link a tile of a few megabytes
    /// takes tens of seconds.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How many times a tile or a nested tileset is asked again.</summary>
    public int Retries { get; init; } = 2;

    /// <summary>The wait before the first retry; the second waits twice as long.</summary>
    public TimeSpan Backoff { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<byte[]> GetAsync(Uri uri, CancellationToken cancel = default)
    {
        var request = Sign(uri);
        // The root opens a session, and sessions are billed: it is asked once. Everything after it belongs to that
        // session and is asked again after a timeout or a passing failure, rather than losing the run.
        var attempts = IsRoot(uri) ? 1 : 1 + Retries;
        for (var attempt = 1; ; attempt++)
        {
            Interlocked.Increment(ref _requests);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(AttemptTimeout);
            try
            {
                using var response = await http.GetAsync(request, limit.Token);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsByteArrayAsync(limit.Token);
                }

                // Never the request URL: it carries the key.
                var error = new HttpRequestException(
                    $"{TilesetParser.Redact(uri)}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null,
                    response.StatusCode);
                if (attempt >= attempts || !Passing(response.StatusCode))
                {
                    throw error;
                }
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                if (attempt >= attempts)
                {
                    throw new HttpRequestException(
                        $"{TilesetParser.Redact(uri)}: no answer in {AttemptTimeout.TotalSeconds:F0} s, {attempts} attempts");
                }
            }
            catch (Exception e) when (e is HttpRequestException { StatusCode: null } or IOException && attempt < attempts)
            {
                // The connection dropped or was refused: asked again.
            }

            Interlocked.Increment(ref _retried);
            await Task.Delay(Backoff * attempt, cancel);
        }
    }

    private bool IsRoot(Uri uri) => Uri.Compare(uri, Root, UriComponents.SchemeAndServer | UriComponents.Path,
        UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>A timeout, too many requests, or the server's own trouble: worth asking again.</summary>
    private static bool Passing(System.Net.HttpStatusCode code) =>
        code is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests || (int)code >= 500;

    /// <summary>The URL to request for <paramref name="uri"/>: with the key, and with the session once a tileset
    /// has given one.</summary>
    public Uri Sign(Uri uri)
    {
        if (!string.Equals(uri.Host, Root.Host, StringComparison.OrdinalIgnoreCase) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"{TilesetParser.Redact(uri)}: not on {Root.Host}; the key is not sent elsewhere");
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        if (query["session"] is { Length: > 0 } session)
        {
            _session = session;
        }
        else if (_session is not null)
        {
            query["session"] = _session;
        }

        query["key"] = apiKey;
        return new UriBuilder(uri) { Query = query.ToString() }.Uri;
    }
}
