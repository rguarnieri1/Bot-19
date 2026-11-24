using BotCripto.Models;
using BotCripto.Strategies;

namespace BotCripto.Services;

public class BotSchedulerService
{
    private const int CandlesPerAnalysis = 300;  // stessa finestra del backtest
    private const int HistoryCalendarDays = 450;  // giorni di calendario che coprono 300 sedute di borsa
    private const int MinCandlesForAnalysis = 60; // minimo richiesto dalla strategia

    private readonly YahooDataService _dataService;
    private readonly NotificationService _notificationService;
    private readonly ReportingService _reportingService;
    private readonly SwingTradingLongStrategy _strategy;
    private readonly RiskManager _riskManager;
    private Timer? _marketCheckTimer;
    private Timer? _weeklyReportTimer;
    private bool _isRunning = false;
    private int _cycleRunning = 0;
    private DateTime? _lastScannedCandle;
    // Il controllo gira spesso rispetto alla chiusura delle candele giornaliere: la seduta appena chiusa viene analizzata
    // al primo ciclo in cui Yahoo Finance la pubblica. La scansione dei simboli avviene una sola volta per seduta.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(20);

    private readonly string _timeframe;
    private readonly TimeSpan _timeframeSpan;
    private decimal _realizedPnl;
    private int _checksPerformed = 0;
    private int _signalsGenerated = 0;
    private int _tradesRecorded = 0;
    private readonly Dictionary<string, int> _cumulativeRejectionCounts = new();

    // Capitale investibile illimitato: nessun importo totale, solo un importo fisso per trade (capitalPerTrade)
    public BotSchedulerService(string timeframe, decimal rewardRiskRatio, decimal capitalPerTrade)
    {
        _timeframe = timeframe;
        _timeframeSpan = BacktestService.TimeframeToTimeSpan(timeframe);
        _dataService = new YahooDataService();
        _notificationService = new NotificationService();
        _reportingService = new ReportingService();
        _strategy = new SwingTradingLongStrategy();
        _riskManager = new RiskManager(
            positionValue: capitalPerTrade,   // importo fisso per trade (€)
            rewardRiskRatio: rewardRiskRatio, // take profit = R:R × distanza dello stop
            commissionPerTransaction: 1.0m,        // €1 fisso per operazione
            taxRate: 0.26m                    // 26% tasse
        );
    }

    public async Task StartAsync()
    {
        if (_isRunning)
        {
            Console.WriteLine("Bot è già in esecuzione.");
            return;
        }

        _isRunning = true;
        Console.WriteLine("\n🤖 Bot 19 - Azioni italiane avviato! (paper trading: nessun ordine reale)");
        Console.WriteLine($"📊 Monitoraggio ogni {CheckInterval.TotalMinutes:F0} minuti, candele {_timeframe}...\n");

        // Esegui il primo controllo immediatamente
        await CheckMarketAsync();

        // Timer di controllo periodico (stop/target dei trade aperti + ricerca segnali a candela chiusa)
        _marketCheckTimer = new Timer(
            async _ => await CheckMarketAsync(),
            null,
            CheckInterval,
            CheckInterval);

        // Timer per il report settimanale (ogni lunedì alle 00:00)
        var now = DateTime.UtcNow;
        var daysToMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        var nextMonday = now.Date.AddDays(daysToMonday); // lunedì alle 00:00 UTC
        if (nextMonday <= now)
            nextMonday = nextMonday.AddDays(7);

        var timeUntilMonday = nextMonday - now;
        _weeklyReportTimer = new Timer(
            _ =>
            {
                try { _reportingService.GenerateWeeklyReport(); }
                catch (Exception ex) { Console.WriteLine($"❌ Errore nel report settimanale: {ex.Message}"); }
            },
            null,
            timeUntilMonday,
            TimeSpan.FromDays(7));

        Console.WriteLine($"⏰ Prossimo report settimanale: {nextMonday:yyyy-MM-dd HH:mm:ss} UTC");

        await Task.Delay(-1);
    }

    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _marketCheckTimer?.Dispose();
        _weeklyReportTimer?.Dispose();
        Console.WriteLine("\n🛑 Bot 19 - Azioni italiane fermato.");
    }

    private async Task CheckMarketAsync()
    {
        // Il timer scatta ogni 30 minuti anche se il ciclo precedente non è finito: mai due cicli insieme,
        // altrimenti lo stesso segnale verrebbe registrato due volte.
        if (Interlocked.Exchange(ref _cycleRunning, 1) == 1)
        {
            Console.WriteLine("⏭️  Ciclo precedente ancora in corso: questo ciclo viene saltato.");
            return;
        }

        CycleRecord? cycle = null;
        try
        {
            _checksPerformed++;
            var now = DateTime.UtcNow;
            cycle = new CycleRecord { CycleNumber = _checksPerformed, StartTime = now, Timeframe = _timeframe };
            Console.WriteLine($"\n⏱️  Ciclo #{_checksPerformed} - {now:yyyy-MM-dd HH:mm:ss} UTC");

            // Candele giornaliere da Yahoo Finance: scarica le sedute mancanti (cache su disco) e aggiorna quelle in memoria.
            // I simboli dei trade aperti vengono caricati anche se usciti dall'elenco delle azioni più liquide.
            var openTradeSymbols = _reportingService.GetAllTrades().Where(t => t.Status == "Open" && t.IsPaperManaged).Select(t => t.Symbol).ToList();
            await _dataService.LoadAsync(now.Date.AddDays(-HistoryCalendarDays), now, extraSymbols: openTradeSymbols);
            await IsinService.PrefetchAsync(openTradeSymbols); // ISIN salvato alla chiusura dei trade aperti
            List<Candle> GetCandles(string symbol) => _dataService.GetCandles(symbol, CandlesPerAnalysis);

            // 1) Paper trading: chiudi i trade aperti che hanno toccato stop loss o target
            int tradesClosed = CloseTradesAtStopOrTarget(GetCandles);

            // Trade gestiti dal paper trading (quelli registrati prima non hanno stop/target e vengono ignorati)
            var managedTrades = _reportingService.GetAllTrades().Where(t => t.IsPaperManaged).ToList();
            var openTrades = managedTrades.Where(t => t.Status == "Open").ToList();
            _realizedPnl = managedTrades.Where(t => t.Status == "Closed").Sum(t => t.Profit ?? 0);

            int signalsFound = 0;
            int signalsFiltered = 0;
            var rejectionCounts = new Dictionary<string, int>();

            void CountRejection(string category)
            {
                rejectionCounts.TryGetValue(category, out var c);
                rejectionCounts[category] = c + 1;

                _cumulativeRejectionCounts.TryGetValue(category, out var cc);
                _cumulativeRejectionCounts[category] = cc + 1;
            }

            // 2) I segnali si valutano solo su candele chiuse (come nel backtest) e solo subito dopo la chiusura
            //    La seduta è l'ultima candela giornaliera del FTSE MIB; vale per l'ingresso finché il mercato non riapre.
            var marketClosed = ClosedCandles(GetCandles(YahooDataService.MarketSymbol), now);
            var lastClosedTime = marketClosed.Count > 0 ? marketClosed.Last().Time : (DateTime?)null;
            bool stillValid = lastClosedTime != null && DateTime.UtcNow < NextSessionOpen(lastClosedTime.Value);
            bool newCandle = stillValid && _lastScannedCandle != lastClosedTime;
            cycle.NewCandle = newCandle;
            cycle.SignalCandleTime = lastClosedTime;

            if (!newCandle)
            {
                Console.WriteLine(lastClosedTime == null
                    ? "🕒 Nessuna candela giornaliera disponibile da Yahoo Finance: nessuna ricerca segnali."
                    : stillValid
                        ? $"🕒 Seduta del {lastClosedTime:yyyy-MM-dd} già analizzata: ricerca segnali alla prossima seduta chiusa."
                        : $"🕒 Ultima seduta disponibile {lastClosedTime:yyyy-MM-dd}, mercato già riaperto: ricerca segnali alla prossima seduta chiusa.");
            }
            else
            {
                // Regime di mercato (FTSE MIB sopra/sotto la EMA50) sulla seduta appena chiusa: filtro direzionale della strategia
                var marketBullish = SwingTradingLongStrategy.IsMarketBullish(marketClosed);
                Console.WriteLine($"🕯️  Seduta del {lastClosedTime:yyyy-MM-dd} chiusa: ricerca segnali");
                Console.WriteLine($"📊 Regime di mercato ({YahooDataService.MarketSymbol}): {(marketBullish == null ? "sconosciuto (nessun segnale)" : marketBullish.Value ? "rialzista" : "ribassista")}{(BotCripto.Strategies.SwingTradingLongStrategy.UseMarketRegimeFilter ? (marketBullish == true ? " (solo long)" : marketBullish == false ? " (solo short)" : "") : " - filtro regime disattivato: long e short")}");
                cycle.BtcRegime = marketBullish == null ? "Sconosciuto" : marketBullish.Value ? "Rialzista" : "Ribassista";

                _lastScannedCandle = lastClosedTime;
                var stocks = _dataService.Universe;
                cycle.CryptosAnalyzed = stocks.Count;
                Console.WriteLine($"📈 Analizzando {stocks.Count} azioni italiane (le più liquide)...");

                var allTrades = _reportingService.GetAllTrades();
                var openSymbols = openTrades.Select(t => t.Symbol).ToHashSet();
                var registeredSignals = allTrades.Where(t => t.SignalCandleTime == lastClosedTime).Select(t => t.Symbol).ToHashSet();
                var candidates = new List<(string symbol, AnalysisResult analysis, List<Candle> candles)>();

                foreach (var symbol in stocks)
                {
                    try
                    {
                        if (openSymbols.Contains(symbol))
                        {
                            CountRejection("Posizione già aperta sul simbolo");
                            continue;
                        }

                        if (registeredSignals.Contains(symbol))
                        {
                            CountRejection("Segnale di questa candela già registrato");
                            continue;
                        }

                        var candles = ClosedCandles(GetCandles(symbol), now);

                        if (candles.Count < MinCandlesForAnalysis)
                        {
                            CountRejection($"Dati insufficienti (candele < {MinCandlesForAnalysis})");
                            continue;
                        }

                        if (candles.Last().Time != lastClosedTime)
                        {
                            CountRejection("Ultima candela non aggiornata");
                            continue;
                        }

                        // ✅ STRATEGIA PRINCIPALE: Swing Trading
                        var signalResult = _strategy.Analyze(symbol, candles, marketBullish);

                        if (signalResult.IsSignal)
                            candidates.Add((symbol, signalResult, candles));
                        else
                            CountRejection(RejectionLabel(signalResult.Rejection));

                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   ⚠️  {symbol}: {ex.Message}");
                    }
                }

                // ISIN dei simboli con segnale (EODHD, in cache su disco), scritto nei segnali e nei trade salvati
                await IsinService.PrefetchAsync(candidates.Select(c => c.symbol));

                // Dal segnale più forte al più debole (capitale illimitato: entrano tutti i segnali validi)
                foreach (var (symbol, signalResult, candles) in candidates.OrderByDescending(c => c.analysis.Indicators.GetValueOrDefault("SignalStrength")))
                {
                    var isLong = signalResult.Signal.Contains("BUY");
                    // Ingresso alla chiusura della candela del segnale (come nel backtest), non al prezzo live:
                    // lo stop della strategia è calcolato su quella candela e i controlli stop/target partono dalla successiva.
                    var entryPrice = candles.Last().Close;
                    var positionResult = _riskManager.CalculatePosition(
                        symbol,
                        entryPrice,
                        CalculateVolatility(candles),
                        isLong,
                        signalResult.Indicators.GetValueOrDefault("StopLoss")
                    );

                    var signalRecord = SignalRecord.FromAnalysis(signalResult, _timeframe, lastClosedTime!.Value, entryPrice, isLong);

                    if (!positionResult.IsValid)
                    {
                        signalsFiltered++;
                        CountRejection(RejectionLabel(positionResult.Rejection));
                        signalRecord.Accepted = false;
                        signalRecord.RejectionReason = RejectionLabel(positionResult.Rejection);
                        _reportingService.SaveSignal(signalRecord);
                        continue;
                    }

                    signalResult.Indicators["StopLoss"] = positionResult.StopLossPrice;
                    signalResult.Indicators["Target"] = positionResult.TargetPrice;
                    signalResult.Indicators["PositionSize"] = positionResult.PositionSize;
                    signalResult.Indicators["RiskRewardRatio"] = positionResult.RiskRewardRatio;
                    signalResult.Indicators["ExpectedProfit"] = positionResult.ExpectedProfit;

                    await _notificationService.SendNotificationAsync(signalResult);
                    var trade = _reportingService.RecordTrade(
                        symbol,
                        entryPrice,
                        "Swing Trading Long (Primary)",
                        isLong,
                        positionResult.StopLossPrice,
                        positionResult.TargetPrice,
                        positionResult.PositionSize,
                        lastClosedTime!.Value);
                    openTrades.Add(trade);

                    signalRecord.Accepted = true;
                    signalRecord.StopLoss = positionResult.StopLossPrice;
                    signalRecord.Target = positionResult.TargetPrice;
                    signalRecord.PositionSize = positionResult.PositionSize;
                    signalRecord.RiskRewardRatio = positionResult.RiskRewardRatio;
                    signalRecord.ExpectedProfit = positionResult.ExpectedProfit;
                    signalRecord.TradeId = trade.Id;
                    _reportingService.SaveSignal(signalRecord);

                    _signalsGenerated++;
                    _tradesRecorded++;
                    signalsFound++;
                }
            }

            cycle.TradesClosed = tradesClosed;
            cycle.SignalsFound = signalsFound;
            cycle.SignalsFiltered = signalsFiltered;
            cycle.OpenTrades = openTrades.Count;
            cycle.CapitalCommitted = openTrades.Sum(t => t.EntryPrice * t.PositionSize);
            cycle.RealizedPnl = _realizedPnl;
            if (rejectionCounts.Count > 0)
                cycle.RejectionCounts = Newtonsoft.Json.JsonConvert.SerializeObject(rejectionCounts);

            // Report del ciclo
            Console.WriteLine($"\n✅ Ciclo completato:");
            Console.WriteLine($"   • Trade chiusi (stop/target): {tradesClosed}");
            Console.WriteLine($"   • Segnali trovati: {signalsFound}");
            Console.WriteLine($"   • Segnali filtrati (rischio): {signalsFiltered}");
            Console.WriteLine($"   • Trade aperti: {openTrades.Count} (capitale impegnato €{openTrades.Sum(t => t.EntryPrice * t.PositionSize):F2})");
            Console.WriteLine($"   • P&L realizzato: €{_realizedPnl:F2}");

            if (rejectionCounts.Count > 0)
            {
                Console.WriteLine("\n📊 Motivi di scarto in questo ciclo:");
                foreach (var kv in rejectionCounts.OrderByDescending(k => k.Value))
                {
                    Console.WriteLine($"   • {kv.Key}: {kv.Value}");
                }
            }

            // Calcola e mostra metriche giornaliere ogni 10 cicli
            if (_checksPerformed % 10 == 0)
            {
                var metrics = _riskManager.CalculatePerformanceMetrics(managedTrades);
                if (metrics.TotalTrades > 0)
                {
                    Console.WriteLine($"\n📊 Metriche cumulative paper trading (dopo {_checksPerformed} cicli):");
                    Console.WriteLine($"   • Trade chiusi: {metrics.TotalTrades}");
                    Console.WriteLine($"   • Win Rate: {metrics.WinRate:P}");
                    Console.WriteLine($"   • Profit Factor: {metrics.ProfitFactor:F2}");
                    Console.WriteLine($"   • Total P&L (netto commissioni): €{metrics.TotalProfit:F2}");
                    Console.WriteLine($"   • Expectancy: €{metrics.Expectancy:F2}");
                }

                if (_cumulativeRejectionCounts.Count > 0)
                {
                    Console.WriteLine($"\n📊 Motivi di scarto cumulativi (dopo {_checksPerformed} cicli):");
                    foreach (var kv in _cumulativeRejectionCounts.OrderByDescending(k => k.Value))
                    {
                        Console.WriteLine($"   • {kv.Key}: {kv.Value}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Errore nel controllo del mercato: {ex.Message}");
            if (cycle != null)
                cycle.Error = ex.Message;
        }
        finally
        {
            // Salva il ciclo nella tabella bot19.bot19_cycles (anche se terminato con errore)
            if (cycle != null)
            {
                cycle.EndTime = DateTime.UtcNow;
                _reportingService.SaveCycle(cycle);
            }
            Interlocked.Exchange(ref _cycleRunning, 0);
        }
    }

    // Apertura della prima seduta successiva a quella indicata: primo giorno feriale dopo, alle 13:30 UTC
    // (apertura di New York con l'ora legale; le festività non sono considerate).
    private static DateTime NextSessionOpen(DateTime session)
    {
        var day = session.Date.AddDays(1);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            day = day.AddDays(1);
        return DateTime.SpecifyKind(day.AddHours(13.5), DateTimeKind.Utc);
    }

    // Candele il cui periodo è già terminato (Time è l'inizio della candela): esclude quella ancora in formazione.
    private List<Candle> ClosedCandles(List<Candle> candles, DateTime now) =>
        candles.Where(c => c.Time + _timeframeSpan <= now).ToList();

    // Paper trading: stessa regola del backtest. Un trade si chiude quando una candela successiva a quella
    // del segnale tocca lo stop loss o il target; se li tocca entrambi si assume lo stop (ipotesi conservativa).
    private int CloseTradesAtStopOrTarget(Func<string, List<Candle>> getCandles)
    {
        int closed = 0;
        var openTrades = _reportingService.GetAllTrades().Where(t => t.Status == "Open" && t.IsPaperManaged).ToList();

        foreach (var trade in openTrades)
        {
            try
            {
                var candles = getCandles(trade.Symbol);
                foreach (var candle in candles.Where(c => c.Time > trade.SignalCandleTime))
                {
                    bool hitStop = trade.IsLong ? candle.Low <= trade.StopLoss : candle.High >= trade.StopLoss;
                    bool hitTarget = trade.IsLong ? candle.High >= trade.TargetPrice : candle.Low <= trade.TargetPrice;
                    if (!hitStop && !hitTarget)
                        continue;

                    // Gap oltre lo stop: l'uscita avviene all'apertura della candela, non al prezzo di stop
                    var exitPrice = hitStop
                        ? (trade.IsLong ? Math.Min(candle.Open, trade.StopLoss) : Math.Max(candle.Open, trade.StopLoss))
                        : trade.TargetPrice;
                    var profitCalc = _riskManager.CalculateProfitAndTaxes(
                        trade.EntryPrice, exitPrice, trade.PositionSize, trade.IsLong);
                    var profit = profitCalc.NetProfitBeforeTax; // tasse sul saldo netto del periodo, non per trade
                    var profitPercent = profit / (trade.EntryPrice * trade.PositionSize) * 100;

                    _reportingService.ClosePaperTrade(trade, exitPrice, profit, profitPercent, hitStop ? "Stop loss" : "Target");
                    Console.WriteLine($"   {(profit > 0 ? "✅" : "❌")} Chiuso {trade.Symbol} {(trade.IsLong ? "LONG" : "SHORT")} per {(hitStop ? "stop loss" : "target")}: €{profit:F2} ({profitPercent:F2}%)");
                    closed++;
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠️  {trade.Symbol} (gestione trade aperto): {ex.Message}");
            }
        }

        return closed;
    }

    // Etichette leggibili per la diagnostica dei motivi di scarto.
    private static string RejectionLabel(RejectionReason reason) => reason switch
    {
        RejectionReason.InsufficientData => "Strategia: dati insufficienti per il calcolo",
        RejectionReason.TrendNotAligned => "Strategia: trend non allineato (EMA5/10/20/30)",
        RejectionReason.NoVolumeData => "Strategia: volume assente",
        RejectionReason.VolumeInsufficient => "Strategia: volume insufficiente",
        RejectionReason.CandleNotStrong => "Strategia: candela non abbastanza forte",
        RejectionReason.PriceVsEma10 => "Strategia: prezzo non conferma vs EMA10",
        RejectionReason.RsiExtreme => "Strategia: RSI estremo",
        RejectionReason.NoBreakout => "Strategia: nessun breakout EMA confermato",
        RejectionReason.AgainstMarketRegime => "Strategia: contro il regime di mercato (FTSE MIB)",
        RejectionReason.MaxConcurrentTrades => "Risk Manager: troppi trade aperti simultaneamente",
        RejectionReason.ProfitBelowCommissions => "Risk Manager: profitto non copre commissioni",
        RejectionReason.RiskRewardTooLow => "Risk Manager: rapporto rischio/rendimento insufficiente",
        RejectionReason.StopLossError => "Risk Manager: errore calcolo stop loss",
        _ => "Scarto non classificato"
    };

    private decimal CalculateVolatility(List<Candle> candles)
    {
        if (candles.Count < 20)
            return 0.02m;

        var closes = candles.TakeLast(20).Select(c => c.Close).ToList();
        var returns = new List<decimal>();

        for (int i = 1; i < closes.Count; i++)
        {
            returns.Add((closes[i] - closes[i - 1]) / closes[i - 1]);
        }

        var avg = returns.Average();
        var variance = returns.Sum(r => (r - avg) * (r - avg)) / returns.Count;
        var stdDev = (decimal)Math.Sqrt((double)variance);

        return Math.Max(0.01m, Math.Min(stdDev, 0.08m));
    }
}
