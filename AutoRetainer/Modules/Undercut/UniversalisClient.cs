using ECommons.ExcelServices;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;

namespace AutoRetainer.Modules.Undercut;

public static class UniversalisClient
{
    private const long CacheLifetimeMs = 5 * 60 * 1000;
    private static readonly HttpClient Client = CreateClient();
    private static readonly ConcurrentDictionary<uint, (long FetchedAt, List<UndercutListing> Listings)> Cache = new();

    //Universalis' per-data-center aggregation intermittently stalls 10-20s on individual requests while the same
    //query usually returns in ~150ms. Cut a stalled attempt short and retry - the retry almost always hits the fast
    //path. Overall HttpClient.Timeout is left effectively off; each attempt is bounded by its own cancellation token.
    private const int RequestAttempts = 3;
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(6);

    private static HttpClient CreateClient()
    {
        //.NET's default HttpClient inspects the Windows/WPAD proxy configuration on its first request, which can
        //stall when auto-detection is enabled but no proxy exists. Disable proxy handling so requests go out
        //directly. UseCookies off avoids per-request container overhead we don't need.
        var handler = new SocketsHttpHandler()
        {
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(6),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AutoRetainer/{P.GetType().Assembly.GetName().Version} (Dalamud plugin)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static async Task<string> GetWithRetryAsync(string url)
    {
        Exception last = null;
        for(var attempt = 1; attempt <= RequestAttempts; attempt++)
        {
            using var cts = new CancellationTokenSource(AttemptTimeout);
            try
            {
                return await Client.GetStringAsync(url, cts.Token).ConfigureAwait(false);
            }
            catch(Exception e)
            {
                last = e;
                PluginLog.Warning($"Universalis request attempt {attempt}/{RequestAttempts} failed ({e.Message}){(attempt < RequestAttempts ? ", retrying" : "")}");
            }
        }
        throw last ?? new Exception("Universalis request failed");
    }
    private static volatile Task FetchTask = null;
    private static string CacheScope = null;

    public static bool IsBusy => FetchTask != null && !FetchTask.IsCompleted;

    /// <summary>
    /// Returns the Universalis query scope (world, data center or region name) for the configured scope. Must be called from the framework thread.
    /// </summary>
    public static string GetQueryScope()
    {
        var world = ExcelWorldHelper.Get(Player.Object.CurrentWorld.RowId);
        if(world != null)
        {
            if(C.UndercutUniversalisScope == UndercutUniversalisScope.Region)
            {
                var region = RegionName(world.Value.GetRegion());
                if(!region.IsNullOrEmpty()) return region;
            }
            else if(C.UndercutUniversalisScope == UndercutUniversalisScope.Data_Center)
            {
                var dc = world.Value.DataCenter.Value.Name.ToString();
                if(!dc.IsNullOrEmpty()) return dc;
            }
        }
        return Player.CurrentWorld;
    }

    /// <summary>
    /// Maps the game region to the name Universalis expects in its query path.
    /// </summary>
    private static string RegionName(ExcelWorldHelper.Region region) => region switch
    {
        ExcelWorldHelper.Region.JP => "Japan",
        ExcelWorldHelper.Region.NA => "North-America",
        ExcelWorldHelper.Region.EU => "Europe",
        ExcelWorldHelper.Region.OC => "Oceania",
        _ => null,
    };

    /// <summary>
    /// Starts a background request for all items that are not freshly cached. Must be called from the framework thread.
    /// </summary>
    public static void BeginFetch(IEnumerable<uint> itemIds, string scope)
    {
        if(IsBusy) return;
        //a change of scope (server/DC/region or a different world) invalidates everything cached for the previous scope
        if(scope != CacheScope)
        {
            Cache.Clear();
            CacheScope = scope;
        }
        var ids = itemIds.Distinct().Where(x => !TryGetListings(x, out _)).ToArray();
        if(ids.Length == 0 || scope.IsNullOrEmpty())
        {
            FetchTask = null;
            return;
        }
        var ownRetainerNames = C.OfflineData.SelectMany(x => x.RetainerData.Select(r => r.Name)).Where(x => !x.IsNullOrEmpty()).ToHashSet();
        var ownWorlds = C.OfflineData.Select(x => x.World).Where(x => !x.IsNullOrEmpty()).ToHashSet();
        FetchTask = Task.Run(async () =>
        {
            try
            {
                var url = $"https://universalis.app/api/v2/{Uri.EscapeDataString(scope)}/{string.Join(",", ids)}?listings=40&entries=0";
                PluginLog.Debug($"Universalis request: {url}");
                var response = await GetWithRetryAsync(url).ConfigureAwait(false);
                var json = JObject.Parse(response);
                var results = new List<JObject>();
                if(json["items"] is JObject multi)
                {
                    foreach(var prop in multi.Properties())
                    {
                        if(prop.Value is JObject o) results.Add(o);
                    }
                }
                else
                {
                    results.Add(json);
                }
                var received = new Dictionary<uint, List<UndercutListing>>();
                foreach(var item in results)
                {
                    var itemId = item["itemID"]?.Value<uint>();
                    if(itemId == null) continue;
                    var listings = new List<UndercutListing>();
                    if(item["listings"] is JArray arr)
                    {
                        foreach(var l in arr)
                        {
                            var price = l["pricePerUnit"]?.Value<long>();
                            if(price == null || price <= 0) continue;
                            var hq = l["hq"]?.Value<bool>() ?? false;
                            var retainerName = l["retainerName"]?.Value<string>();
                            var worldName = l["worldName"]?.Value<string>();
                            //name collisions are possible across worlds, so a listing is only ours when the world matches too (or is unknown, for a single-world query)
                            var isOwn = retainerName != null && ownRetainerNames.Contains(retainerName) && (worldName.IsNullOrEmpty() || ownWorlds.Contains(worldName));
                            listings.Add(new(price.Value, hq, isOwn, worldName));
                        }
                    }
                    received[itemId.Value] = listings;
                }
                var now = Environment.TickCount64;
                foreach(var id in ids)
                {
                    //items absent from the response were never sold on this market: cache them as having no listings
                    Cache[id] = (now, received.TryGetValue(id, out var l) ? l : []);
                }
                PluginLog.Debug($"Universalis response processed, {received.Count} items received for scope {scope}");
            }
            catch(Exception e)
            {
                PluginLog.Warning($"Universalis request failed: {e.Message}");
            }
        });
    }

    public static bool TryGetListings(uint itemId, out List<UndercutListing> listings)
    {
        if(Cache.TryGetValue(itemId, out var e) && Environment.TickCount64 - e.FetchedAt <= CacheLifetimeMs)
        {
            listings = e.Listings;
            return true;
        }
        listings = null;
        return false;
    }
}
