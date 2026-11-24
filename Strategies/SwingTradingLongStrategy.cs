using BotCripto.Models;

namespace BotCripto.Strategies;

// Swing trading su candele chiuse (timeframe configurato, tipicamente 1d), LONG e (opzionale) SHORT speculare.
// LONG:
//  1) trend di fondo rialzista: Close > EMA50 e EMA20 > EMA50 con EMA50 in salita, Close > EMA200
//  2) ritracciamento: nelle ultime candele l'RSI14 è sceso sotto RsiPullback e il minimo ha toccato l'EMA20
//  3) ripartenza: candela rialzista che chiude sopra l'EMA20 e sopra il massimo della candela precedente,
//     RSI in crescita e non ipercomprato
// SHORT (solo se AllowShorts): stesse regole specchiate (trend ribassista, rimbalzo fino all'EMA20, ripartenza al ribasso).
// Stop loss oltre lo swing recente (più un buffer ATR); il target lo calcola il RiskManager (R:R configurato).
public class SwingTradingLongStrategy
{
    // Direzioni abilitate e filtro di regime: letti da appsettings.json (TradingSettings.AllowLong / AllowShort /
    // UseMarketRegimeFilter) all'avvio in Program.cs. Con UseMarketRegimeFilter=false long e short sono attivi
    // insieme, a prescindere dal regime del FTSE MIB.
    public static bool AllowLongs { get; set; } = true;
    public static bool AllowShorts { get; set; } = true;
    public static bool UseMarketRegimeFilter { get; set; } = false;

    private const int FastEma = 20;
    private const int SlowEma = 50;
    private const int LongEma = 200;             // filtro di trend di lungo periodo
    private const int PullbackLookback = 5;      // candele entro cui deve essere avvenuto il ritracciamento
    private const int SwingLookback = 10;        // candele per il minimo/massimo dello swing (stop)
    private const int AtrPeriod = 14;
    // Parametri ereditati dal bot 13 (ottimizzati da backtest 4 anni sugli USA): stop molto larghi + target vicino.
    // Letti da appsettings.json (TradingSettings.AtrStopBuffer / MinStopPercent / MaxStopPercent); default = valori del bot 13.
    public static decimal AtrStopBuffer { get; set; } = 12m;   // buffer oltre lo swing, in ATR
    public static decimal MaxStopPercent { get; set; } = 0.70m; // stop più largo = setup scartato
    public static decimal MinStopPercent { get; set; } = 0.30m; // stop più stretto = setup scartato
    private const decimal RsiPullback = 55m;     // long: l'RSI deve essere sceso sotto questa soglia durante il ritracciamento
    private const decimal RsiPullbackShort = 45m; // short: l'RSI deve essere salito sopra questa soglia
    private const decimal RsiMax = 70m;          // long: niente ingresso se RSI sopra
    private const decimal RsiMin = 30m;          // short: niente ingresso se RSI sotto
    public const int MinCandles = SlowEma + 20;

    private const int MarketRegimeEmaPeriod = 50;

    // Regime di mercato: true se l'indice (FTSE MIB) chiude sopra la sua EMA50, false se sotto, null se dati insufficienti.
    public static bool? IsMarketBullish(List<Candle> marketCandles)
    {
        var ema = TechnicalIndicators.CalculateEMA(marketCandles.Select(c => c.Close).ToList(), MarketRegimeEmaPeriod);
        if (ema.Count == 0)
            return null;
        return marketCandles.Last().Close > ema.Last();
    }

    // marketBullish: usato solo se UseMarketRegimeFilter è attivo (long con mercato rialzista, short con mercato ribassista).
    public AnalysisResult Analyze(string symbol, List<Candle> candles, bool? marketBullish)
    {
        var result = new AnalysisResult
        {
            Symbol = symbol,
            StrategyName = "Swing Trading",
            IsSignal = false,
            Signal = "No Signal",
            AnalysisTime = DateTime.UtcNow
        };

        if (candles.Count < MinCandles)
        {
            result.Rejection = RejectionReason.InsufficientData;
            return result;
        }

        var closes = candles.Select(c => c.Close).ToList();
        var ema20 = TechnicalIndicators.CalculateEMA(closes, FastEma);
        var ema50 = TechnicalIndicators.CalculateEMA(closes, SlowEma);
        var rsi = TechnicalIndicators.CalculateRSI(closes);
        if (ema20.Count < PullbackLookback + 1 || ema50.Count < 6 || rsi.Count < PullbackLookback + 2)
        {
            result.Rejection = RejectionReason.InsufficientData;
            return result;
        }

        var last = candles.Last();
        var prev = candles[candles.Count - 2];
        result.CurrentPrice = last.Close;

        // Le serie sono allineate alla fine: l'indice i-esimo dalla fine corrisponde alla candela i-esima dalla fine
        decimal E20(int back) => ema20[ema20.Count - 1 - back];
        decimal E50(int back) => ema50[ema50.Count - 1 - back];
        decimal Rsi(int back) => rsi[rsi.Count - 1 - back];

        var e20 = E20(0);
        var e50 = E50(0);
        var rsiNow = Rsi(0);
        result.Indicators["EMA20"] = e20;
        result.Indicators["EMA50"] = e50;
        result.Indicators["RSI"] = rsiNow;

        var e200 = TechnicalIndicators.CalculateEMA(closes, LongEma);
        var atr = CalculateAtr(candles, AtrPeriod);

        // Direzione: trend di fondo rialzista → long, ribassista → short
        bool uptrend = last.Close > e50 && e20 > e50 && e50 > E50(5);
        bool downtrend = last.Close < e50 && e20 < e50 && e50 < E50(5);

        if (!uptrend && !downtrend)
        {
            result.Rejection = RejectionReason.TrendNotAligned;
            return result;
        }

        bool isLong = uptrend;
        if ((isLong && !AllowLongs) || (!isLong && !AllowShorts))
        {
            result.Signal = isLong ? "Rejected - longs disabled" : "Rejected - shorts disabled";
            result.Rejection = RejectionReason.TrendNotAligned;
            return result;
        }

        // Regime di mercato (opzionale)
        if (UseMarketRegimeFilter && (marketBullish == null || marketBullish != isLong))
        {
            result.Signal = "Rejected - Against market regime";
            result.Rejection = RejectionReason.AgainstMarketRegime;
            return result;
        }

        // Trend di lungo periodo (servono almeno 200 candele, altrimenti il titolo è scartato)
        if (e200.Count == 0 || (isLong ? last.Close <= e200.Last() : last.Close >= e200.Last()))
        {
            result.Rejection = RejectionReason.TrendNotAligned;
            return result;
        }

        // 2) Ritracciamento recente verso l'EMA20 (senza sfondare l'EMA50 oltre il 3%) con RSI oltre soglia
        bool pullback = false;
        for (int back = 1; back <= PullbackLookback; back++)
        {
            var c = candles[candles.Count - 1 - back];
            if (isLong
                ? c.Low <= E20(back) && c.Low >= E50(back) * 0.97m && Rsi(back) < RsiPullback
                : c.High >= E20(back) && c.High <= E50(back) * 1.03m && Rsi(back) > RsiPullbackShort)
            {
                pullback = true;
                break;
            }
        }
        if (!pullback)
        {
            result.Rejection = RejectionReason.NoBreakout;
            return result;
        }

        // 3) Ripartenza nella direzione del trend, RSI in movimento ma non estremo
        bool reversal = isLong
            ? last.Close > last.Open && last.Close > e20 && last.Close > prev.High
            : last.Close < last.Open && last.Close < e20 && last.Close < prev.Low;
        bool rsiOk = isLong ? rsiNow <= RsiMax && rsiNow > Rsi(1) : rsiNow >= RsiMin && rsiNow < Rsi(1);
        if (!reversal || !rsiOk)
        {
            result.Rejection = RejectionReason.RsiExtreme;
            return result;
        }

        // Stop oltre lo swing recente, con buffer ATR
        decimal stop, stopDistance;
        if (isLong)
        {
            stop = candles.Skip(candles.Count - SwingLookback).Min(c => c.Low) - atr * AtrStopBuffer;
            stopDistance = last.Close - stop;
        }
        else
        {
            stop = candles.Skip(candles.Count - SwingLookback).Max(c => c.High) + atr * AtrStopBuffer;
            stopDistance = stop - last.Close;
        }
        if (stop <= 0 || stopDistance <= 0 || stopDistance / last.Close > MaxStopPercent || stopDistance / last.Close < MinStopPercent)
        {
            result.Rejection = RejectionReason.StopLossError;
            return result;
        }

        result.IsSignal = true;
        result.Signal = isLong ? "BUY" : "SELL";
        result.Indicators["StopLoss"] = stop;
        result.Indicators["ATR"] = atr;
        // Più forte = trend più marcato (distanza EMA20 da EMA50) e ripartenza più decisa (RSI)
        result.Indicators["SignalStrength"] = isLong
            ? (e20 - e50) / e50 * 100m + rsiNow / 10m
            : (e50 - e20) / e50 * 100m + (100m - rsiNow) / 10m;
        return result;
    }

    private static decimal CalculateAtr(List<Candle> candles, int period)
    {
        var trs = new List<decimal>();
        for (int i = candles.Count - period; i < candles.Count; i++)
        {
            var c = candles[i];
            var prevClose = candles[i - 1].Close;
            trs.Add(Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose))));
        }
        return trs.Average();
    }
}
