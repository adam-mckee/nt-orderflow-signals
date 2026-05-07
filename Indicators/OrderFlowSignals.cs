#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators.OrderFlowSignals;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// OrderFlowSignals — multi-timeframe confluence signal indicator for NQ / ES futures.
    ///
    /// A signal fires only when <em>all three</em> gates agree on the primary (3-min) chart,
    /// with the 1-min delta used as a non-contradiction filter:
    ///
    ///   Gate 1 — <see cref="VolumeProfileModule"/>   : price at an LVN (session or rolling).
    ///   Gate 2 — <see cref="CumulativeDeltaModule"/>  : bullish or bearish delta divergence (3-min).
    ///   Gate 3 — <see cref="OrderBlockModule"/>        : an unmitigated OB in proximity.
    ///   Filter — <see cref="CumulativeDeltaModule"/>  : 1-min delta must not contradict direction.
    ///
    /// Confirmed signals are painted as arrows on the chart and delivered via ntfy push
    /// notification (configurable URL + Bearer token). A <see cref="Signal"/> series is also
    /// exposed so an automated strategy can subscribe to this indicator.
    ///
    /// See README.md § Tuning Guide for per-parameter guidance on NQ 3-min charts.
    /// </summary>
    [CategoryOrder("Volume Profile",   1)]
    [CategoryOrder("Cumulative Delta", 2)]
    [CategoryOrder("Order Block",      3)]
    [CategoryOrder("Alerts",           4)]
    [CategoryOrder("Display",          5)]
    [CategoryOrder("General",          6)]
    public class OrderFlowSignals : Indicator
    {
        // ── Detection modules ─────────────────────────────────────────────────────
        private VolumeProfileModule   _vp;
        private CumulativeDeltaModule _cd3m;  // primary 3-min cumulative delta
        private CumulativeDeltaModule _cd1m;  // 1-min confirmation delta
        private OrderBlockModule      _ob;
        private ATR                   _atr;

        // ── Series exposed to strategies ──────────────────────────────────────────
        // Values: 0 = no signal, 1 = long, -1 = short.
        private Series<int> _signalSeries;

        // ── Alert state ───────────────────────────────────────────────────────────
        private int _lastSignalBar = int.MinValue;

        // Shared across all indicator instances on the same chart session.
        private static readonly HttpClient _http = new HttpClient();

        // ── BarsInProgress constants ──────────────────────────────────────────────
        private const int PRIMARY = 0;  // 3-min (or whatever the chart's bar type is)
        private const int CONFIRM = 1;  // 1-min secondary series

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description            = "Multi-timeframe order-flow confluence: LVN + delta divergence + order block, delivered via ntfy.";
                Name                   = "OrderFlowSignals";
                Calculate              = Calculate.OnBarClose;
                IsOverlay              = true;
                DisplayInDataBox       = false;
                DrawOnPricePanel       = true;
                ScaleJustification     = Gui.Chart.ScaleJustification.Right;

                // Volume Profile
                ValueAreaPercent       = 0.70;
                RollingProfileBars     = 50;
                LVNThresholdPercent    = 0.15;

                // Cumulative Delta
                DivergenceLookback     = 5;

                // Order Block
                ImpulseATRMultiple     = 1.5;
                ImpulseBars            = 3;
                OBProximityTicks       = 10;

                // Alerts
                SignalCooldownBars     = 3;
                NtfyUrl                = "http://your-ntfy-server/trading-signals";
                NtfyToken              = "";

                // Display
                LongSignalBrush        = Brushes.DodgerBlue;
                ShortSignalBrush       = Brushes.OrangeRed;

                // General
                DebugMode              = false;
            }
            else if (State == State.Configure)
            {
                // 1-min secondary series for delta confirmation.
                AddDataSeries(BarsPeriodType.Minute, 1);

                double ts = TickSize;

                _vp = new VolumeProfileModule(ts)
                {
                    ValueAreaPercent    = ValueAreaPercent,
                    LVNThresholdPercent = LVNThresholdPercent,
                    RollingProfileBars  = RollingProfileBars,
                };

                _cd3m = new CumulativeDeltaModule
                {
                    DivergenceLookback = DivergenceLookback,
                };

                // 1-min lookback is wider (3×) so we get meaningful swing context
                // from the faster timeframe without hair-trigger divergences.
                _cd1m = new CumulativeDeltaModule
                {
                    DivergenceLookback = DivergenceLookback * 3,
                };

                _ob = new OrderBlockModule
                {
                    ImpulseLookback    = ImpulseBars,
                    MinImpulseMultiple = ImpulseATRMultiple,
                    MaxActiveZones     = 8,
                    TickSize           = ts,
                };

                // ATR(14) on the primary series for order block impulse sizing.
                _atr = ATR(14);
            }
            else if (State == State.DataLoaded)
            {
                _signalSeries = new Series<int>(this);
            }
        }
        #endregion

        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            // ── 1-min confirmation series ─────────────────────────────────────────
            if (BarsInProgress == CONFIRM)
            {
                if (CurrentBars[CONFIRM] < 2) return;

                // Use bid/ask volumes when available (Tick Replay); fall back to 50/50 split.
                double vol1m  = Volumes[CONFIRM][0];
                double ask1m  = GetCurrentAskVolume() > 0 ? GetCurrentAskVolume() : vol1m / 2.0;
                double bid1m  = GetCurrentBidVolume() > 0 ? GetCurrentBidVolume() : vol1m / 2.0;

                _cd1m.OnBarUpdate(Closes[CONFIRM][0], ask1m, bid1m);
                return;
            }

            // ── Primary 3-min series ──────────────────────────────────────────────
            if (BarsInProgress != PRIMARY) return;

            // Warm-up guard: need enough bars for ATR(14) + divergence lookback.
            if (CurrentBar < Math.Max(DivergenceLookback + 2, 16)) return;

            // Session reset
            if (Bars.IsFirstBarOfSession)
            {
                _vp.ResetSession(Open[0]);
                _cd3m.ResetSession();
                if (DebugMode) Print($"[OFS] New session — bar {CurrentBar}  {Time[0]:yyyy-MM-dd HH:mm}");
            }

            // Feed volume profile with this bar's OHLCV.
            _vp.OnBarUpdate(High[0], Low[0], Close[0], Volume[0]);

            // Feed 3-min cumulative delta.
            double askVol = GetCurrentAskVolume() > 0 ? GetCurrentAskVolume() : Volume[0] / 2.0;
            double bidVol = GetCurrentBidVolume() > 0 ? GetCurrentBidVolume() : Volume[0] / 2.0;
            _cd3m.OnBarUpdate(Close[0], askVol, bidVol);

            // Feed order block module with ATR(14).
            _ob.OnBarUpdate(Open[0], High[0], Low[0], Close[0], _atr[0]);

            EvaluateConfluence();
        }
        #endregion

        #region Signal gating
        private void EvaluateConfluence()
        {
            _signalSeries[0] = 0;

            // Signal cooldown: suppress re-entry for SignalCooldownBars bars.
            if (CurrentBar - _lastSignalBar < SignalCooldownBars) return;

            double price = Close[0];

            // ── Gate 1: Volume Profile — price at an LVN ──────────────────────────
            bool atLVN = _vp.IsAtSessionExtreme(price, OBProximityTicks)
                      || _vp.IsAtRollingExtreme(price, OBProximityTicks);

            // ── Gate 2: Cumulative Delta divergence (3-min) ───────────────────────
            DivergenceType div3m = _cd3m.GetDivergence(DivergenceLookback);

            // ── Gate 3: Order Block proximity ────────────────────────────────────
            OrderBlock ob       = _ob.GetNearestValidOB(price, OBProximityTicks);
            bool hasValidOB     = !ob.Mitigated;

            // ── 1-min non-contradiction filter ───────────────────────────────────
            // The 1-min delta must not show an opposing divergence.
            DivergenceType div1m = _cd1m.GetDivergence(_cd1m.DivergenceLookback);

            // ── Long: LVN ∧ bullish delta div ∧ bullish OB ∧ 1-min not bearish ──
            bool longSignal = atLVN
                           && div3m == DivergenceType.Bullish
                           && hasValidOB
                           && ob.Type == OBType.Bullish
                           && div1m  != DivergenceType.Bearish;

            // ── Short: LVN ∧ bearish delta div ∧ bearish OB ∧ 1-min not bullish ─
            bool shortSignal = atLVN
                            && div3m == DivergenceType.Bearish
                            && hasValidOB
                            && ob.Type == OBType.Bearish
                            && div1m  != DivergenceType.Bullish;

            if (DebugMode)
            {
                Print($"[OFS] bar={CurrentBar} price={price:F2}" +
                      $"  LVN={atLVN}  div3m={div3m}  ob={(!hasValidOB ? "none" : ob.Type.ToString())}  div1m={div1m}");
            }

            if (longSignal)
            {
                FireSignal(1, price, "Long", ob, div3m, div1m, atLVN);
            }
            else if (shortSignal)
            {
                FireSignal(-1, price, "Short", ob, div3m, div1m, atLVN);
            }
        }

        private void FireSignal(int direction, double price, string label,
                                OrderBlock ob, DivergenceType div3m, DivergenceType div1m, bool atLVN)
        {
            _signalSeries[0] = direction;
            _lastSignalBar   = CurrentBar;

            string reason = BuildReason(atLVN, div3m, ob, div1m);

            if (direction > 0)
                Draw.ArrowUp(this, $"L_{CurrentBar}", false, 0,
                             Low[0]  - TickSize * 4, LongSignalBrush);
            else
                Draw.ArrowDown(this, $"S_{CurrentBar}", false, 0,
                               High[0] + TickSize * 4, ShortSignalBrush);

            if (DebugMode)
                Print($"[OFS] *** {label.ToUpper()} SIGNAL *** bar={CurrentBar}" +
                      $"  price={price:F2}  reason={reason}");

            SendNtfyAlert(label, price, reason);
        }

        private static string BuildReason(bool atLVN, DivergenceType div3m,
                                          OrderBlock ob, DivergenceType div1m)
        {
            var parts = new List<string>(4);
            if (atLVN)                           parts.Add("LVN");
            if (div3m != DivergenceType.None)    parts.Add(div3m + "Div3m");
            if (!ob.Mitigated)                   parts.Add(ob.Type + "OB");
            if (div1m != DivergenceType.None)    parts.Add(div1m + "Div1m");
            return string.Join("+", parts);
        }
        #endregion

        #region ntfy alert delivery
        /// <summary>
        /// POSTs a JSON payload to <see cref="NtfyUrl"/> with Bearer token auth.
        /// The call is fire-and-forget on a background thread so it never blocks the UI.
        /// </summary>
        private void SendNtfyAlert(string direction, double price, string reason)
        {
            string url = NtfyUrl;
            if (string.IsNullOrEmpty(url) || url == "http://your-ntfy-server/trading-signals")
            {
                if (DebugMode) Print("[OFS] ntfy skipped — NtfyUrl not configured.");
                return;
            }

            string symbol    = Instrument.FullName;
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string token     = NtfyToken;

            // Build JSON manually to avoid a Newtonsoft dependency.
            string json = string.Format(
                "{{\"symbol\":\"{0}\",\"direction\":\"{1}\",\"price\":{2}," +
                "\"timestamp\":\"{3}\",\"triggerReason\":\"{4}\"}}",
                EscJson(symbol), direction, price, timestamp, EscJson(reason));

            Task.Run(async () =>
            {
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                        if (!string.IsNullOrEmpty(token))
                            req.Headers.Authorization =
                                new AuthenticationHeaderValue("Bearer", token);

                        await _http.SendAsync(req).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Swallow — never interrupt the trading thread for a failed push.
                }
            });
        }

        private static string EscJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
        #endregion

        #region Public series accessor
        /// <summary>
        /// Signal series readable by an automated strategy.
        /// Values: <c>1</c> = long entry, <c>-1</c> = short entry, <c>0</c> = no signal.
        /// </summary>
        [Browsable(false)]
        [XmlIgnore]
        public Series<int> Signal => _signalSeries;
        #endregion

        #region Parameters — Volume Profile
        [NinjaScriptProperty]
        [Range(0.50, 0.95)]
        [Display(Name = "Value Area %", Description = "Fraction of session volume defining the Value Area (standard: 0.70).", GroupName = "Volume Profile", Order = 1)]
        public double ValueAreaPercent { get; set; }

        [NinjaScriptProperty]
        [Range(10, 500)]
        [Display(Name = "Rolling Profile Bars", Description = "Number of bars in the secondary rolling volume profile.", GroupName = "Volume Profile", Order = 2)]
        public int RollingProfileBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 0.50)]
        [Display(Name = "LVN Threshold %", Description = "A price level is an LVN when its volume < this fraction of the max bucket volume.", GroupName = "Volume Profile", Order = 3)]
        public double LVNThresholdPercent { get; set; }
        #endregion

        #region Parameters — Cumulative Delta
        [NinjaScriptProperty]
        [Range(2, 30)]
        [Display(Name = "Divergence Lookback", Description = "Bars to scan for swing high/low divergence on the 3-min chart.", GroupName = "Cumulative Delta", Order = 1)]
        public int DivergenceLookback { get; set; }
        #endregion

        #region Parameters — Order Block
        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(Name = "Impulse ATR Multiple", Description = "The impulse High-Low range must exceed this multiple of ATR(14) to form a valid OB.", GroupName = "Order Block", Order = 1)]
        public double ImpulseATRMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Impulse Bars", Description = "Number of bars that must form the impulse leg after the candidate OB candle.", GroupName = "Order Block", Order = 2)]
        public int ImpulseBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "OB Proximity Ticks", Description = "Ticks within which price must be from an OB zone midpoint to activate the gate.", GroupName = "Order Block", Order = 3)]
        public int OBProximityTicks { get; set; }
        #endregion

        #region Parameters — Alerts
        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Signal Cooldown Bars", Description = "Minimum bars between two consecutive signals. Prevents hammering the same zone.", GroupName = "Alerts", Order = 1)]
        public int SignalCooldownBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Ntfy URL", Description = "Full ntfy endpoint, e.g. https://ntfy.sh/my-topic or http://host:8080/topic.", GroupName = "Alerts", Order = 2)]
        public string NtfyUrl { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Ntfy Token (Bearer)", Description = "Optional Bearer token for ntfy access control. Leave empty for public topics.", GroupName = "Alerts", Order = 3)]
        public string NtfyToken { get; set; }
        #endregion

        #region Parameters — Display
        [XmlIgnore]
        [Display(Name = "Long Signal Color", GroupName = "Display", Order = 1)]
        public Brush LongSignalBrush { get; set; }

        [Browsable(false)]
        public string LongSignalBrushSerializable
        {
            get => Serialize.BrushToString(LongSignalBrush);
            set => LongSignalBrush = Serialize.StringToBrush(value);
        }

        [XmlIgnore]
        [Display(Name = "Short Signal Color", GroupName = "Display", Order = 2)]
        public Brush ShortSignalBrush { get; set; }

        [Browsable(false)]
        public string ShortSignalBrushSerializable
        {
            get => Serialize.BrushToString(ShortSignalBrush);
            set => ShortSignalBrush = Serialize.StringToBrush(value);
        }
        #endregion

        #region Parameters — General
        [NinjaScriptProperty]
        [Display(Name = "Debug Mode", Description = "Enables verbose Print() output in the Output window.", GroupName = "General", Order = 1)]
        public bool DebugMode { get; set; }
        #endregion
    }
}

#region NinjaScript generated code — Do not edit manually
namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private OrderFlowSignals[] cacheOrderFlowSignals;

        public OrderFlowSignals OrderFlowSignals(
            double valueAreaPercent, int rollingProfileBars, double lVNThresholdPercent,
            int divergenceLookback,
            double impulseATRMultiple, int impulseBars, int oBProximityTicks,
            int signalCooldownBars, string ntfyUrl, string ntfyToken,
            bool debugMode)
        {
            return OrderFlowSignals(Input,
                valueAreaPercent, rollingProfileBars, lVNThresholdPercent,
                divergenceLookback,
                impulseATRMultiple, impulseBars, oBProximityTicks,
                signalCooldownBars, ntfyUrl, ntfyToken, debugMode);
        }

        public OrderFlowSignals OrderFlowSignals(ISeries<double> input,
            double valueAreaPercent, int rollingProfileBars, double lVNThresholdPercent,
            int divergenceLookback,
            double impulseATRMultiple, int impulseBars, int oBProximityTicks,
            int signalCooldownBars, string ntfyUrl, string ntfyToken,
            bool debugMode)
        {
            if (cacheOrderFlowSignals != null)
            {
                foreach (var cached in cacheOrderFlowSignals)
                {
                    if (cached.ValueAreaPercent    == valueAreaPercent    &&
                        cached.RollingProfileBars  == rollingProfileBars  &&
                        cached.LVNThresholdPercent == lVNThresholdPercent &&
                        cached.DivergenceLookback  == divergenceLookback  &&
                        cached.ImpulseATRMultiple  == impulseATRMultiple  &&
                        cached.ImpulseBars         == impulseBars         &&
                        cached.OBProximityTicks    == oBProximityTicks    &&
                        cached.SignalCooldownBars  == signalCooldownBars  &&
                        cached.NtfyUrl             == ntfyUrl             &&
                        cached.NtfyToken           == ntfyToken           &&
                        cached.DebugMode           == debugMode           &&
                        cached.EqualsInput(input))
                        return cached;
                }
            }

            var indicator = new OrderFlowSignals
            {
                ValueAreaPercent    = valueAreaPercent,
                RollingProfileBars  = rollingProfileBars,
                LVNThresholdPercent = lVNThresholdPercent,
                DivergenceLookback  = divergenceLookback,
                ImpulseATRMultiple  = impulseATRMultiple,
                ImpulseBars         = impulseBars,
                OBProximityTicks    = oBProximityTicks,
                SignalCooldownBars  = signalCooldownBars,
                NtfyUrl             = ntfyUrl,
                NtfyToken           = ntfyToken,
                DebugMode           = debugMode,
            };
            indicator.SetInput(input);
            return CacheIndicator<OrderFlowSignals>(indicator, input, ref cacheOrderFlowSignals);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.OrderFlowSignals OrderFlowSignals(
            double valueAreaPercent, int rollingProfileBars, double lVNThresholdPercent,
            int divergenceLookback,
            double impulseATRMultiple, int impulseBars, int oBProximityTicks,
            int signalCooldownBars, string ntfyUrl, string ntfyToken,
            bool debugMode) =>
            indicator.OrderFlowSignals(Input,
                valueAreaPercent, rollingProfileBars, lVNThresholdPercent,
                divergenceLookback,
                impulseATRMultiple, impulseBars, oBProximityTicks,
                signalCooldownBars, ntfyUrl, ntfyToken, debugMode);
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.OrderFlowSignals OrderFlowSignals(
            double valueAreaPercent, int rollingProfileBars, double lVNThresholdPercent,
            int divergenceLookback,
            double impulseATRMultiple, int impulseBars, int oBProximityTicks,
            int signalCooldownBars, string ntfyUrl, string ntfyToken,
            bool debugMode) =>
            indicator.OrderFlowSignals(Input,
                valueAreaPercent, rollingProfileBars, lVNThresholdPercent,
                divergenceLookback,
                impulseATRMultiple, impulseBars, oBProximityTicks,
                signalCooldownBars, ntfyUrl, ntfyToken, debugMode);
    }
}
#endregion
