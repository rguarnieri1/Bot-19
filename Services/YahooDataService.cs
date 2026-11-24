using System.Globalization;
using System.IO.Compression;
using BotCripto.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YFSharp;
using YFSharp.Models;

namespace BotCripto.Services;

// Dati di mercato delle azioni italiane da Yahoo Finance tramite la libreria YFSharp, solo candele giornaliere.
//
// Yahoo non ha una richiesta "tutte le azioni di una seduta" come Polygon: lo storico si scarica titolo per titolo.
// Per questo l'intera finestra (circa 450 giorni) viene scaricata UNA volta per seduta, dopo la chiusura del mercato,
// e salvata in Data/yahoo/prices.csv.gz; finché non chiude un'altra seduta il bot usa la cache su disco.
// Riscaricare tutto a ogni seduta tiene coerenti i prezzi passati con gli split: Yahoo li restituisce già rettificati
// per gli split (AutoAdjust = false: niente rettifica per i dividendi, i prezzi sono quelli quotati).
//
// L'elenco delle azioni analizzate viene dallo screener di Yahoo (azioni italiane ordinate per capitalizzazione).
public class YahooDataService : IDisposable
{
    // Indice FTSE MIB usato come indice di mercato per il filtro di regime
    public const string MarketSymbol = "FTSEMIB.MI";

    private const int ScreenerRefreshDays = 7;                    // l'elenco delle azioni si aggiorna una volta a settimana
    private const int ChunkSize = 100;                            // simboli per richiesta di download
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);
    private static readonly string[] Exchanges = { "MIL" }; // Borsa Italiana (Euronext Milan)

    private readonly string _cacheDir;
    private readonly string _pricesFile;
    private readonly string _metaFile;
    private readonly string _universeFile;
    private readonly int _maxConcurrency;
    private readonly int _maxStocks;
    private readonly decimal _minPrice;
    private readonly long _minAvgVolume;
    private readonly YahooFinanceClient _client;

    private DateTime _retryAfter = DateTime.MinValue;
    private Dictionary<string, List<Candle>> _candles = new();
    private HashSet<string> _loadedSymbols = new();
    private List<string> _universe = new();
    private string _loadedKey = "";

    // Azioni analizzate: le più grandi per capitalizzazione (o l'elenco passato a LoadAsync)
    public IReadOnlyList<string> Universe => _universe;

    public YahooDataService()
    {
        _maxConcurrency = int.TryParse(Setting("Yahoo.MaxConcurrency"), out var conc) && conc > 0 ? conc : 6;
        _maxStocks = int.TryParse(Setting("MarketMonitoring.MaxStocksToAnalyze"), out var max) ? max : 500;
        _minPrice = decimal.TryParse(Setting("MarketMonitoring.MinPriceEur"), NumberStyles.Float, CultureInfo.InvariantCulture, out var minPrice) ? minPrice : 5m;
        _minAvgVolume = long.TryParse(Setting("MarketMonitoring.MinAvgDailyVolume"), out var minVol) ? minVol : 500_000;

        var cacheDir = Setting("Yahoo.CacheDirectory");
        _cacheDir = string.IsNullOrWhiteSpace(cacheDir) ? Path.Combine(AppContext.BaseDirectory, "Data", "yahoo") : cacheDir;
        Directory.CreateDirectory(_cacheDir);
        _pricesFile = Path.Combine(_cacheDir, "prices.csv.gz");
        _metaFile = Path.Combine(_cacheDir, "prices.meta.json");
        _universeFile = Path.Combine(_cacheDir, "universe.txt");

        // Cookie e crumb di Yahoo restano su disco, così non vanno richiesti a ogni avvio
        _client = new YahooFinanceClient(new YahooFinanceClientOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(int.TryParse(Setting("Yahoo.RequestTimeoutSeconds"), out var timeout) ? timeout : 60),
            MaxRetries = int.TryParse(Setting("Yahoo.MaxRetries"), out var retries) ? retries : 4,
            AuthStore = new FileYahooFinanceAuthStore(Path.Combine(_cacheDir, "auth.json"))
        });
    }

    public void Dispose() => _client.Dispose();

    // Valore di configurazione: appsettings.local.json ha la precedenza su appsettings.json
    private static string? Setting(string path)
    {
        string? result = null;
        foreach (var file in new[] { "appsettings.json", "appsettings.local.json" })
        {
            var full = Path.Combine(AppContext.BaseDirectory, file);
            if (!File.Exists(full)) continue;
            var value = JObject.Parse(File.ReadAllText(full)).SelectToken(path)?.Value<string>();
            if (!string.IsNullOrWhiteSpace(value)) result = value;
        }
        return result;
    }

    private class CacheMeta
    {
        public DateTime FetchedAt { get; set; }      // UTC: quando è stato fatto il download completo
        public DateTime From { get; set; }           // prima data richiesta
        public List<string> Attempted { get; set; } = new(); // simboli richiesti (anche quelli che Yahoo non ha restituito)
    }

    // Scarica (se serve) lo storico di [from, to] e carica in memoria le candele giornaliere.
    // symbols: elenco fisso di azioni (backtest con --symbols); se null si usano le più grandi per capitalizzazione.
    // extraSymbols: simboli da caricare comunque (trade aperti), anche se usciti dall'elenco.
    public async Task LoadAsync(DateTime from, DateTime to, IReadOnlyCollection<string>? symbols = null, IEnumerable<string>? extraSymbols = null)
    {
        var now = DateTime.UtcNow;
        from = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc);
        var lastSession = LastCompletedSession(to, now);

        var universe = symbols != null
            ? symbols.Where(s => s != MarketSymbol).Distinct().ToList()
            : await GetUniverseAsync(now);

        var wanted = new HashSet<string>(universe) { MarketSymbol };
        wanted.UnionWith(extraSymbols ?? Enumerable.Empty<string>());

        var meta = ReadMeta();
        // La cache è fresca se il download completo è avvenuto dopo la fine dell'ultima seduta conclusa
        // (la mezzanotte UTC dopo la seduta: Borsa Italiana chiude alle 15:30-16:30 UTC) e parte da una data abbastanza vecchia.
        bool fresh = meta != null && lastSession != null && meta.From <= from && meta.FetchedAt >= lastSession.Value.AddDays(1) && File.Exists(_pricesFile);

        if (!fresh && now >= _retryAfter)
        {
            try
            {
                await DownloadAllAsync(from, wanted, now);
            }
            catch (Exception ex)
            {
                _retryAfter = now + RetryAfterFailure;
                Console.WriteLine($"❌ Yahoo Finance: download fallito ({ex.GetBaseException().Message}); riprovo tra {RetryAfterFailure.TotalMinutes:F0} minuti");
                if (!File.Exists(_pricesFile))
                    throw;
                Console.WriteLine("   ⚠️  Uso i prezzi in cache dell'ultimo download riuscito (possono essere vecchi)");
            }
            meta = ReadMeta();
        }
        else if (fresh)
        {
            // Cache fresca ma mancano simboli (nuovi trade aperti o elenco aggiornato): si scaricano solo quelli
            var missing = wanted.Where(s => !meta!.Attempted.Contains(s)).ToList();
            if (missing.Count > 0)
            {
                try { await DownloadMissingAsync(meta!, from, missing); }
                catch (Exception ex) { Console.WriteLine($"   ⚠️  Yahoo Finance: simboli mancanti non scaricati ({ex.GetBaseException().Message})"); }
                meta = ReadMeta();
            }
        }

        if (meta == null || !File.Exists(_pricesFile))
        {
            _candles = new();
            _universe = new();
            _loadedSymbols = new();
            _loadedKey = "";
            return;
        }

        var key = $"{meta.FetchedAt:O}-{from:yyyyMMdd}-{to:yyyyMMdd}-{meta.Attempted.Count}-{string.Join(',', wanted.OrderBy(s => s))}";
        if (key == _loadedKey)
            return; // nessun dato nuovo: le candele in memoria sono già aggiornate

        var candles = wanted.ToDictionary(s => s, _ => new List<Candle>());
        using (var reader = new StreamReader(new GZipStream(File.OpenRead(_pricesFile), CompressionMode.Decompress)))
        {
            while (reader.ReadLine() is { } line)
            {
                var p = line.Split(',');
                if (p.Length != 7 || !candles.TryGetValue(p[0], out var list))
                    continue;

                var day = DateTime.SpecifyKind(DateTime.ParseExact(p[1], "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc);
                if (day < from || day > to.Date)
                    continue;

                list.Add(new Candle
                {
                    Time = day,
                    Open = Dec(p[2]), High = Dec(p[3]), Low = Dec(p[4]), Close = Dec(p[5]), Volume = Dec(p[6])
                });
            }
        }
        foreach (var list in candles.Values)
            list.Sort((a, b) => a.Time.CompareTo(b.Time));

        _candles = candles;
        _universe = universe.Where(s => candles[s].Count > 0).ToList();
        _loadedSymbols = wanted;
        _loadedKey = key;
    }

    // Ultime `limit` candele giornaliere del simbolo (in ordine di tempo); lista vuota se non caricato.
    public List<Candle> GetCandles(string symbol, int limit) =>
        _candles.TryGetValue(symbol, out var list) ? list.Skip(Math.Max(0, list.Count - limit)).ToList() : new List<Candle>();

    public List<Candle> GetHistoricalCandles(string symbol, DateTime from, DateTime to) =>
        _candles.TryGetValue(symbol, out var list) ? list.Where(c => c.Time >= from && c.Time <= to).ToList() : new List<Candle>();

    private static decimal Dec(string s) => decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    // Ultimo giorno feriale la cui seduta è già conclusa (mezzanotte UTC successiva passata) e non oltre `to`.
    // Le festività non sono considerate: in quel caso la cache viene scaricata una volta e poi resta valida.
    private static DateTime? LastCompletedSession(DateTime to, DateTime now)
    {
        var day = (to < now ? to : now).Date;
        for (int i = 0; i < 10; i++, day = day.AddDays(-1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || day.AddDays(1) > now)
                continue;
            return DateTime.SpecifyKind(day, DateTimeKind.Utc);
        }
        return null;
    }

    // ───────────────────────── Download dello storico ─────────────────────────

    private async Task DownloadAllAsync(DateTime from, HashSet<string> wanted, DateTime now)
    {
        Console.WriteLine($"📥 Yahoo Finance: scarico lo storico giornaliero di {wanted.Count} titoli dal {from:yyyy-MM-dd} (poi resta in cache fino alla prossima seduta)...");
        var rows = await DownloadRowsAsync(wanted.ToList(), from);
        if (!rows.ContainsKey(MarketSymbol))
            throw new InvalidOperationException($"Yahoo non ha restituito i dati di {MarketSymbol}, necessari per il regime di mercato");

        WritePrices(rows);
        WriteMeta(new CacheMeta { FetchedAt = now, From = from, Attempted = wanted.OrderBy(s => s).ToList() });
        Console.WriteLine($"✅ Yahoo Finance: {rows.Count} titoli scaricati ({wanted.Count - rows.Count} non restituiti da Yahoo)");
    }

    private async Task DownloadMissingAsync(CacheMeta meta, DateTime from, List<string> missing)
    {
        Console.WriteLine($"📥 Yahoo Finance: scarico {missing.Count} titoli mancanti dalla cache...");
        var rows = await DownloadRowsAsync(missing, meta.From < from ? meta.From : from);

        // Riscrive la cache con i titoli nuovi: tolti quelli già presenti con lo stesso simbolo, poi aggiunti i nuovi
        var existing = ReadAllPriceLines().Where(l => !rows.ContainsKey(l.Split(',', 2)[0])).ToList();
        WritePricesLines(existing.Concat(rows.SelectMany(kv => kv.Value)));
        meta.Attempted = meta.Attempted.Concat(missing).Distinct().OrderBy(s => s).ToList();
        WriteMeta(meta);
    }

    // Scarica in blocchi; i simboli che falliscono vengono ritentati una volta con meno richieste in parallelo.
    // Restituisce per simbolo le righe CSV "ticker,data,o,h,l,c,v".
    private async Task<Dictionary<string, List<string>>> DownloadRowsAsync(List<string> symbols, DateTime from)
    {
        var request = HistoryRequest.ForDates(new DateTimeOffset(from, TimeSpan.Zero), null, "1d")
            with { AutoAdjust = false, IncludeActions = false, Repair = false };
        var result = new Dictionary<string, List<string>>();
        var failed = new List<string>();

        for (int i = 0; i < symbols.Count; i += ChunkSize)
        {
            var chunk = symbols.Skip(i).Take(ChunkSize).ToList();
            var download = await _client.TryDownloadAsync(chunk, request, _maxConcurrency);
            foreach (var (symbol, history) in download.Histories)
                Add(result, symbol, history);
            failed.AddRange(download.Errors.Keys);

            if (symbols.Count > ChunkSize)
                Console.WriteLine($"   📥 {Math.Min(i + ChunkSize, symbols.Count)}/{symbols.Count} titoli...");
        }

        // Alcuni simboli falliscono per limiti di richieste temporanei; i titoli davvero inesistenti falliscono di nuovo e restano esclusi
        var retry = failed.Where(s => !result.ContainsKey(s)).ToList();
        if (retry.Count > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            var download = await _client.TryDownloadAsync(retry, request, 2);
            foreach (var (symbol, history) in download.Histories)
                Add(result, symbol, history);
        }

        return result;

        static void Add(Dictionary<string, List<string>> into, string symbol, HistoricalData history)
        {
            var lines = new List<string>();
            foreach (var bar in history.Bars)
            {
                if (bar.Open is not { } o || bar.High is not { } h || bar.Low is not { } l || bar.Close is not { } c)
                    continue;
                var close = Convert.ToDecimal(c);
                if (close <= 0)
                    continue;

                // La data è quella locale di New York (la candela inizia alle 9:30), non quella UTC
                lines.Add(string.Join(',', symbol, bar.Time.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Num(o), Num(h), Num(l), Num(c), Num(bar.Volume ?? 0)));
            }
            if (lines.Count > 0)
                into[symbol] = lines;
        }
    }

    private static string Num(object value) => Convert.ToDecimal(value).ToString(CultureInfo.InvariantCulture);

    private IEnumerable<string> ReadAllPriceLines()
    {
        if (!File.Exists(_pricesFile))
            yield break;
        using var reader = new StreamReader(new GZipStream(File.OpenRead(_pricesFile), CompressionMode.Decompress));
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private void WritePrices(Dictionary<string, List<string>> rows) => WritePricesLines(rows.Values.SelectMany(r => r));

    private void WritePricesLines(IEnumerable<string> lines)
    {
        var tmp = _pricesFile + ".tmp";
        using (var writer = new StreamWriter(new GZipStream(File.Create(tmp), CompressionLevel.Optimal)))
            foreach (var line in lines)
                writer.WriteLine(line);
        File.Move(tmp, _pricesFile, overwrite: true);
    }

    private CacheMeta? ReadMeta()
    {
        try { return File.Exists(_metaFile) ? JsonConvert.DeserializeObject<CacheMeta>(File.ReadAllText(_metaFile)) : null; }
        catch { return null; }
    }

    private void WriteMeta(CacheMeta meta) => File.WriteAllText(_metaFile, JsonConvert.SerializeObject(meta));

    // ───────────────────────── Elenco delle azioni analizzate ─────────────────────────

    // Azioni italiane (Borsa Italiana) con prezzo ≥ MinPriceEur e volume medio a 3 mesi ≥ MinAvgDailyVolume,
    // le prime MaxStocksToAnalyze per capitalizzazione. Salvato su disco e riscaricato una volta a settimana.
    private async Task<List<string>> GetUniverseAsync(DateTime now)
    {
        if (File.Exists(_universeFile) && now - File.GetLastWriteTimeUtc(_universeFile) < TimeSpan.FromDays(ScreenerRefreshDays))
        {
            var cached = File.ReadAllLines(_universeFile).Where(s => s.Length > 0).ToList();
            if (cached.Count >= _maxStocks || cached.Count > 0 && File.GetLastWriteTimeUtc(_universeFile) > now.AddHours(-12))
                return cached.Take(_maxStocks).ToList();
        }

        try
        {
            var query = new ScreenerQuery("and", new object[]
            {
                new ScreenerQuery("eq", new object[] { "region", "it" }),
                new ScreenerQuery("eq", new object[] { "exchange", Exchanges[0] }), // una sola borsa: "or" richiede almeno due operandi
                new ScreenerQuery("gt", new object[] { "intradayprice", (double)_minPrice }),
                new ScreenerQuery("gt", new object[] { "avgdailyvol3m", _minAvgVolume })
            });

            var symbols = new List<string>();
            for (int offset = 0, pages = 0; symbols.Count < _maxStocks && pages < 60; pages++)
            {
                var page = await _client.ScreenAsync(new ScreenerRequest
                {
                    Query = query,
                    QuoteType = ScreenerQuoteType.Equity,
                    Count = 250,
                    Offset = offset,
                    SortField = "intradaymarketcap",
                    SortAscending = false
                });

                if (page.Quotes.Count == 0)
                    break;
                foreach (var quote in page.Quotes)
                {
                    var symbol = quote.GetProperty("symbol").GetString();
                    if (!string.IsNullOrEmpty(symbol) && symbol != MarketSymbol && !symbols.Contains(symbol))
                        symbols.Add(symbol);
                }
                offset += page.Quotes.Count; // Yahoo restituisce a pagine da 25 anche se se ne chiedono di più
            }

            if (symbols.Count > 0)
            {
                symbols = symbols.Take(_maxStocks).ToList();
                File.WriteAllLines(_universeFile, symbols);
                Console.WriteLine($"📋 Yahoo Finance: elenco aggiornato, {symbols.Count} azioni italiane per capitalizzazione");
                return symbols;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ⚠️  Yahoo Finance: elenco delle azioni non scaricato ({ex.GetBaseException().Message})");
        }

        if (File.Exists(_universeFile))
            return File.ReadAllLines(_universeFile).Where(s => s.Length > 0).Take(_maxStocks).ToList(); // elenco vecchio, meglio di niente

        throw new InvalidOperationException("Impossibile ottenere l'elenco delle azioni da Yahoo Finance (screener non raggiungibile e nessun elenco in cache)");
    }
}
