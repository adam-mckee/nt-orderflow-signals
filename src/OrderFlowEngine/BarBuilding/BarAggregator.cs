using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlowEngine.Config;
using OrderFlowEngine.Tradovate;

namespace OrderFlowEngine.BarBuilding;

/// <summary>
/// Aggregates raw trade ticks into time-aligned OHLCV bars for one timeframe.
///
/// Bars snap to clock-aligned boundaries: a 3-min aggregator closes bars at
/// 18:00, 18:03, 18:06 … regardless of when the first tick arrives.
/// A new session is declared whenever the bar open time crosses the configured
/// session-start hour (default 18:00 ET = 6 PM CME Globex open).
///
/// The aggregator also maintains a 14-period ATR (Wilder smoothing) and
/// exposes it alongside each closed bar.
/// </summary>
public sealed class BarAggregator
{
    public event Action<Bar, double>? OnBarClosed;  // (bar, currentATR)

    private readonly int      _barMinutes;
    private readonly int      _sessionStartHour;   // in ET
    private readonly TimeZoneInfo _et;
    private readonly ILogger  _log;

    // Current open bar state
    private DateTime _barOpenUtc;
    private DateTime _barCloseUtc;
    private double   _open, _high, _low, _close;
    private double   _volume, _buyVol, _sellVol;
    private bool     _barOpen;
    private bool     _lastSessionDay;     // tracks session boundary

    // ATR (Wilder 14-period)
    private const int AtrPeriod = 14;
    private readonly Queue<double> _trueRanges = new(AtrPeriod + 1);
    private double _atr;
    private double _prevClose;
    private bool   _atrSeeded;

    public double CurrentATR => _atr;

    public BarAggregator(int barMinutes, int sessionStartHourET, ILogger log)
    {
        _barMinutes       = barMinutes;
        _sessionStartHour = sessionStartHourET;
        _log              = log;
        _et               = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    }

    /// <summary>
    /// Feed a raw tick. Closes the current bar and opens a new one when the
    /// tick's timestamp crosses the next bar boundary.
    /// </summary>
    public void OnTick(Tick tick)
    {
        DateTime barClose = AlignedBarClose(tick.Timestamp);

        // First tick ever — open the bar without emitting a close
        if (!_barOpen)
        {
            OpenBar(tick, barClose);
            return;
        }

        // Tick belongs to a future bar — close the current bar first
        if (tick.Timestamp >= _barCloseUtc)
        {
            CloseCurrentBar();
            // There may be a gap of multiple empty bars between the last tick
            // and this one (thin markets, halts). Fast-forward without emitting
            // synthetic bars — just open fresh from the current tick.
            OpenBar(tick, barClose);
            return;
        }

        // Tick belongs to the current bar
        UpdateBar(tick);
    }

    // ── Bar lifecycle ─────────────────────────────────────────────────────────

    private void OpenBar(Tick tick, DateTime barClose)
    {
        _barOpenUtc  = AlignedBarOpen(tick.Timestamp);
        _barCloseUtc = barClose;
        _open        = tick.Price;
        _high        = tick.Price;
        _low         = tick.Price;
        _close       = tick.Price;
        _volume      = tick.Volume;
        _buyVol      = tick.IsBuy       ? tick.Volume : 0;
        _sellVol     = !tick.IsBuy      ? tick.Volume : 0;
        _barOpen     = true;
    }

    private void UpdateBar(Tick tick)
    {
        if (tick.Price > _high) _high = tick.Price;
        if (tick.Price < _low)  _low  = tick.Price;
        _close   = tick.Price;
        _volume  += tick.Volume;
        if (tick.IsBuy) _buyVol  += tick.Volume;
        else            _sellVol += tick.Volume;
    }

    private void CloseCurrentBar()
    {
        // True Range
        double tr = _high - _low;
        if (_prevClose > 0)
            tr = Math.Max(tr, Math.Max(Math.Abs(_high - _prevClose), Math.Abs(_low - _prevClose)));
        _prevClose = _close;

        // Wilder ATR
        _trueRanges.Enqueue(tr);
        if (_trueRanges.Count > AtrPeriod) _trueRanges.Dequeue();
        if (!_atrSeeded && _trueRanges.Count == AtrPeriod)
        {
            _atr      = _trueRanges.Average();
            _atrSeeded = true;
        }
        else if (_atrSeeded)
        {
            _atr = (_atr * (AtrPeriod - 1) + tr) / AtrPeriod;
        }

        bool isFirst = IsFirstBarOfSession(_barOpenUtc);

        var bar = new Bar(
            OpenTime:            _barOpenUtc,
            CloseTime:           _barCloseUtc,
            Open:                _open,
            High:                _high,
            Low:                 _low,
            Close:               _close,
            Volume:              _volume,
            BuyVolume:           _buyVol,
            SellVolume:          _sellVol,
            BarMinutes:          _barMinutes)
        {
            IsFirstBarOfSession = isFirst
        };

        _log.LogDebug("[{TF}m] Bar closed {Time:HH:mm} O={O} H={H} L={L} C={C} V={V:N0} ATR={A:F2}",
            _barMinutes, _barOpenUtc, _open, _high, _low, _close, _volume, _atr);

        OnBarClosed?.Invoke(bar, _atr);
        _barOpen = false;
    }

    // ── Time alignment ────────────────────────────────────────────────────────

    private DateTime AlignedBarOpen(DateTime utc)
    {
        long ticks     = utc.Ticks;
        long spanTicks = TimeSpan.FromMinutes(_barMinutes).Ticks;
        return new DateTime(ticks - ticks % spanTicks, DateTimeKind.Utc);
    }

    private DateTime AlignedBarClose(DateTime utc)
        => AlignedBarOpen(utc) + TimeSpan.FromMinutes(_barMinutes);

    private bool IsFirstBarOfSession(DateTime barOpenUtc)
    {
        DateTime et      = TimeZoneInfo.ConvertTimeFromUtc(barOpenUtc, _et);
        bool     isFirst = et.Hour == _sessionStartHour && et.Minute < _barMinutes;
        return isFirst;
    }
}
