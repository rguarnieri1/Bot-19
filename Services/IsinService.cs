using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BotCripto.Services;

// Codice ISIN delle azioni italiane, preso dall'API ID Mapping di EODHD (1 chiamata API per simbolo):
//   https://eodhd.com/api/id-mapping?filter[symbol]=AAPL.US  ->  data[0].isin = "US0378331005"
// La chiave sta in appsettings.local.json (Eodhd:ApiKey, escluso da git).
// Gli ISIN trovati vengono salvati in una cache su disco condivisa dai bot 11-14
// (Eodhd:CacheFile, predefinita %LOCALAPPDATA%\BotIsin\isin_cache.json): ogni simbolo viene chiesto a EODHD una sola volta.
// Se EODHD risponde che il limite giornaliero di chiamate è esaurito (402/429), le richieste si fermano fino al giorno dopo
// e le righe vengono salvate senza ISIN.
public static class IsinService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Regex IsinFormat = new("^[A-Z]{2}[A-Z0-9]{9}[0-9]$", RegexOptions.Compiled);
    private static readonly object Sync = new();

    // simbolo -> ISIN ("" = EODHD ha risposto ma non ha l'ISIN del simbolo)
    private static Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;
    private static string? _apiKey;
    private static string _cacheFile = "";
    private static DateTime _blockedUntilUtc = DateTime.MinValue;

    // ISIN già in cache (null se non ancora scaricato o non disponibile). Usato da BotDbContext prima di ogni salvataggio.
    public static string? GetCached(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        EnsureLoaded();
        lock (Sync)
            return _cache.TryGetValue(symbol, out var isin) && isin.Length > 0 ? isin : null;
    }

    // Scarica da EODHD gli ISIN dei simboli che non sono ancora in cache.
    public static async Task PrefetchAsync(IEnumerable<string?> symbols)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(_apiKey)) return;

        MergeFromDisk(); // un altro bot potrebbe averli già scaricati
        List<string> missing;
        lock (Sync)
            missing = symbols.Where(s => !string.IsNullOrWhiteSpace(s))
                             .Select(s => s!.Trim().ToUpperInvariant())
                             .Distinct()
                             .Where(s => !_cache.ContainsKey(s))
                             .ToList();
        if (missing.Count == 0) return;

        int added = 0;
        foreach (var symbol in missing)
        {
            if (DateTime.UtcNow < _blockedUntilUtc) break;
            var isin = await FetchAsync(symbol);
            if (isin == null) continue; // errore: si riprova al prossimo ciclo
            lock (Sync) _cache[symbol] = isin;
            added++;
        }

        if (added > 0)
        {
            SaveToDisk();
            Console.WriteLine($"🔖 ISIN EODHD: {added} simboli aggiornati in cache");
        }
    }

    // "" se EODHD non conosce l'ISIN, null in caso di errore (rete, limite giornaliero, chiave non valida)
    private static async Task<string?> FetchAsync(string symbol)
    {
        var url = "https://eodhd.com/api/id-mapping?filter%5Bsymbol%5D=" + Uri.EscapeDataString(symbol + ".US") +
                  "&page%5Blimit%5D=1&fmt=json&api_token=" + Uri.EscapeDataString(_apiKey!);
        try
        {
            using var response = await Http.GetAsync(url);
            if (response.StatusCode is HttpStatusCode.PaymentRequired or HttpStatusCode.TooManyRequests)
            {
                _blockedUntilUtc = DateTime.UtcNow.Date.AddDays(1);
                Console.WriteLine("⚠️  ISIN EODHD: limite giornaliero di chiamate raggiunto, riprovo domani");
                return null;
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _blockedUntilUtc = DateTime.MaxValue;
                Console.WriteLine("❌ ISIN EODHD: chiave API non valida (Eodhd:ApiKey in appsettings.local.json)");
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"⚠️  ISIN EODHD {symbol}: HTTP {(int)response.StatusCode}");
                return null;
            }

            var json = JObject.Parse(await response.Content.ReadAsStringAsync());
            var isin = json["data"]?.FirstOrDefault()?.Value<string>("isin")?.Trim().ToUpperInvariant();
            return isin != null && IsinFormat.IsMatch(isin) ? isin : "";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  ISIN EODHD {symbol}: {ex.GetBaseException().Message}");
            return null;
        }
    }

    private static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_loaded) return;
            _loaded = true;

            JToken? settings = null;
            foreach (var file in new[] { "appsettings.json", "appsettings.local.json" })
            {
                var path = Path.Combine(AppContext.BaseDirectory, file);
                if (!File.Exists(path)) continue;
                try
                {
                    var section = JObject.Parse(File.ReadAllText(path))["Eodhd"];
                    if (section != null) settings = settings == null ? section : Merge(settings, section);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️  {file}: {ex.Message}");
                }
            }

            _apiKey = settings?.Value<string>("ApiKey");
            var cacheFile = settings?.Value<string>("CacheFile");
            _cacheFile = string.IsNullOrWhiteSpace(cacheFile)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotIsin", "isin_cache.json")
                : cacheFile;

            if (string.IsNullOrWhiteSpace(_apiKey))
                Console.WriteLine("⚠️  ISIN: chiave EODHD mancante (Eodhd:ApiKey in appsettings.local.json), colonna Isin vuota");
        }
        MergeFromDisk();
    }

    private static JToken Merge(JToken a, JToken b)
    {
        var merged = (JObject)a.DeepClone();
        merged.Merge(b);
        return merged;
    }

    private static Dictionary<string, string> ReadFile()
    {
        try
        {
            if (File.Exists(_cacheFile))
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(_cacheFile)) ?? new();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Cache ISIN {_cacheFile}: {ex.Message}");
        }
        return new();
    }

    private static void MergeFromDisk()
    {
        using var mutex = new Mutex(false, "BotIsinCache");
        if (!Wait(mutex)) return;
        try
        {
            var fromDisk = ReadFile();
            lock (Sync)
                foreach (var (symbol, isin) in fromDisk)
                    if (!_cache.TryGetValue(symbol, out var current) || current.Length == 0)
                        _cache[symbol] = isin;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static void SaveToDisk()
    {
        using var mutex = new Mutex(false, "BotIsinCache");
        if (!Wait(mutex)) return;
        try
        {
            var merged = ReadFile();
            lock (Sync)
            {
                foreach (var (symbol, isin) in _cache)
                    if (isin.Length > 0 || !merged.ContainsKey(symbol))
                        merged[symbol] = isin;
                _cache = new Dictionary<string, string>(merged, StringComparer.OrdinalIgnoreCase);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var tmp = _cacheFile + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(new SortedDictionary<string, string>(merged), Formatting.Indented));
            File.Move(tmp, _cacheFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Cache ISIN {_cacheFile}: {ex.Message}");
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static bool Wait(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { return true; }
    }
}
