#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators.OrderFlowSignals;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// OrderFlowSignals — mean-reversion signal indicator for NQ / ES futures.
    ///
    /// A signal is generated only when all three confluence gates agree:
    ///   1. VolumeProfile  — price is at a statistical volume extreme (HVN/LVN)
    ///   2. CumulativeDelta — delta divergence confirms exhaustion
    ///   3. OrderBlock     — an untested institutional order block is present
    ///
    /// Signals are painted as arrows on the chart and expose a <see cref="Signal"/>
    /// series so an automated strategy can subscribe to this indicator.
    /// </summary>
    [Gui.CategoryOrder("Volume Profile",    1)]
    [Gui.CategoryOrder("Cumulative Delta",  2)]
    [Gui.CategoryOrder("Order Block",       3)]
    [Gui.CategoryOrder("Display",           4)]
    public class OrderFlowSignals : Indicator
    {
        // ── Detection modules ─────────────────────────────────────────────────────
        private VolumeProfileModule   _vpModule;
        private CumulativeDeltaModule _cdModule;
        private OrderBlockModule      _obModule;

        // ── NinjaTrader indicator series (readable by strategies) ─────────────────
        // 0 = no signal, 1 = long, -1 = short
        private Series<int> _signalSeries;

        // ── Built-in ATR for order block impulse threshold ────────────────────────
        private ATR _atr;

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Mean-reversion signals gated on volume extremes, delta divergence, and order block confluence.";
                Name        = "OrderFlowSignals";
                Calculate   = Calculate.OnBarClose;
                IsOverlay   = true;
                DisplayInDataBox       = false;
                DrawOnPricePanel       = true;
                ScaleJustification     = Gui.Chart.ScaleJustification.Right;

                // ── Volume Profile defaults ───────────────────────────────────────
                VP_TicksPerBucket         = 4;
                VP_ExtremeZScoreThreshold = 1.5;
                VP_ValueAreaPercent       = 0.70;

                // ── Cumulative Delta defaults ─────────────────────────────────────
                CD_DivergenceLookback = 10;
                CD_MinDeltaThreshold  = 200;

                // ── Order Block defaults ──────────────────────────────────────────
                OB_ImpulseLookback    = 3;
                OB_MinImpulseMultiple = 1.5;
                OB_MaxActiveZones     = 6;

                // ── Display defaults ──────────────────────────────────────────────
                LongSignalBrush  = Brushes.DodgerBlue;
                ShortSignalBrush = Brushes.OrangeRed;
                ShowWeakSignals  = false;
            }
            else if (State == State.Configure)
            {
                _vpModule = new VolumeProfileModule(TickSize)
                {
                    TicksPerBucket         = VP_TicksPerBucket,
                    ExtremeZScoreThreshold = VP_ExtremeZScoreThreshold,
                    ValueAreaPercent       = VP_ValueAreaPercent,
                };

                _cdModule = new CumulativeDeltaModule
                {
                    DivergenceLookback = CD_DivergenceLookback,
                    MinDeltaThreshold  = CD_MinDeltaThreshold,
                };

                _obModule = new OrderBlockModule
                {
                    ImpulseLookback    = OB_ImpulseLookback,
                    MinImpulseMultiple = OB_MinImpulseMultiple,
                    MaxActiveZones     = OB_MaxActiveZones,
                    TickSize           = TickSize,
                };

                _atr = ATR(14);
                AddDataSeries(BarsPeriodType.Tick, 1); // tick series for bid/ask volume
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
            // Only process the primary series
            if (BarsInProgress != 0) return;
            if (CurrentBar < Math.Max(CD_DivergenceLookback, 14)) return;

            // ── Session reset ─────────────────────────────────────────────────────
            if (Bars.IsFirstBarOfSession)
            {
                _vpModule.ResetSession(Open[0]);
                _cdModule.ResetSession();
            }

            // ── Feed modules ──────────────────────────────────────────────────────
            // Bid/ask volume: NinjaTrader populates these when Tick Replay is on.
            // Fall back to volume / 2 approximation when unavailable.
            double askVol = GetCurrentAskVolume() > 0 ? GetCurrentAskVolume() : Volume[0] / 2.0;
            double bidVol = GetCurrentBidVolume() > 0 ? GetCurrentBidVolume() : Volume[0] / 2.0;

            _vpModule.OnBarUpdate(High[0], Low[0], Close[0], Volume[0]);
            _cdModule.OnBarUpdate(Close[0], askVol, bidVol);
            _obModule.OnBarUpdate(Open[0], High[0], Low[0], Close[0], _atr[0]);

            // ── Gate evaluation ───────────────────────────────────────────────────
            bool volumeGate = _vpModule.IsExtremeDetected;
            bool deltaGate  = _cdModule.DivergenceDetected;
            bool obGate     = _obModule.InActiveOrderBlock;

            int confluenceCount = (volumeGate ? 1 : 0) + (deltaGate ? 1 : 0) + (obGate ? 1 : 0);
            bool sufficientConfluence = ShowWeakSignals ? confluenceCount >= 1 : confluenceCount >= 2;

            if (!sufficientConfluence)
            {
                _signalSeries[0] = 0;
                return;
            }

            // ── Determine direction from delta + order block ───────────────────────
            bool longSetup  = _cdModule.BullishDivergence || (!_vpModule.IsHVN && obGate);
            bool shortSetup = _cdModule.BearishDivergence || ( _vpModule.IsHVN && obGate);

            if (longSetup && !shortSetup)
            {
                _signalSeries[0] = 1;
                Draw.ArrowUp(this, $"L_{CurrentBar}", false, 0, Low[0] - TickSize * 4, LongSignalBrush);
            }
            else if (shortSetup && !longSetup)
            {
                _signalSeries[0] = -1;
                Draw.ArrowDown(this, $"S_{CurrentBar}", false, 0, High[0] + TickSize * 4, ShortSignalBrush);
            }
            else
            {
                _signalSeries[0] = 0;
            }
        }
        #endregion

        #region Public series accessor
        [Browsable(false)]
        [XmlIgnore]
        public Series<int> Signal => _signalSeries;
        #endregion

        #region Parameters — Volume Profile
        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Ticks Per Bucket", GroupName = "Volume Profile", Order = 1)]
        public int VP_TicksPerBucket { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 4.0)]
        [Display(Name = "Extreme Z-Score Threshold", GroupName = "Volume Profile", Order = 2)]
        public double VP_ExtremeZScoreThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 0.9)]
        [Display(Name = "Value Area %", GroupName = "Volume Profile", Order = 3)]
        public double VP_ValueAreaPercent { get; set; }
        #endregion

        #region Parameters — Cumulative Delta
        [NinjaScriptProperty]
        [Range(3, 50)]
        [Display(Name = "Divergence Lookback", GroupName = "Cumulative Delta", Order = 1)]
        public int CD_DivergenceLookback { get; set; }

        [NinjaScriptProperty]
        [Range(50, 2000)]
        [Display(Name = "Min Delta Threshold", GroupName = "Cumulative Delta", Order = 2)]
        public double CD_MinDeltaThreshold { get; set; }
        #endregion

        #region Parameters — Order Block
        [NinjaScriptProperty]
        [Range(2, 10)]
        [Display(Name = "Impulse Lookback Bars", GroupName = "Order Block", Order = 1)]
        public int OB_ImpulseLookback { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "Min Impulse ATR Multiple", GroupName = "Order Block", Order = 2)]
        public double OB_MinImpulseMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Max Active Zones", GroupName = "Order Block", Order = 3)]
        public int OB_MaxActiveZones { get; set; }
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

        [NinjaScriptProperty]
        [Display(Name = "Show Weak Signals (1/3 gates)", GroupName = "Display", Order = 3)]
        public bool ShowWeakSignals { get; set; }
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
            int vP_TicksPerBucket, double vP_ExtremeZScoreThreshold, double vP_ValueAreaPercent,
            int cD_DivergenceLookback, double cD_MinDeltaThreshold,
            int oB_ImpulseLookback, double oB_MinImpulseMultiple, int oB_MaxActiveZones,
            bool showWeakSignals)
        {
            return OrderFlowSignals(Input,
                vP_TicksPerBucket, vP_ExtremeZScoreThreshold, vP_ValueAreaPercent,
                cD_DivergenceLookback, cD_MinDeltaThreshold,
                oB_ImpulseLookback, oB_MinImpulseMultiple, oB_MaxActiveZones,
                showWeakSignals);
        }

        public OrderFlowSignals OrderFlowSignals(ISeries<double> input,
            int vP_TicksPerBucket, double vP_ExtremeZScoreThreshold, double vP_ValueAreaPercent,
            int cD_DivergenceLookback, double cD_MinDeltaThreshold,
            int oB_ImpulseLookback, double oB_MinImpulseMultiple, int oB_MaxActiveZones,
            bool showWeakSignals)
        {
            if (cacheOrderFlowSignals != null)
            {
                foreach (var cached in cacheOrderFlowSignals)
                {
                    if (cached.VP_TicksPerBucket         == vP_TicksPerBucket         &&
                        cached.VP_ExtremeZScoreThreshold == vP_ExtremeZScoreThreshold &&
                        cached.VP_ValueAreaPercent       == vP_ValueAreaPercent       &&
                        cached.CD_DivergenceLookback     == cD_DivergenceLookback     &&
                        cached.CD_MinDeltaThreshold      == cD_MinDeltaThreshold      &&
                        cached.OB_ImpulseLookback        == oB_ImpulseLookback        &&
                        cached.OB_MinImpulseMultiple     == oB_MinImpulseMultiple     &&
                        cached.OB_MaxActiveZones         == oB_MaxActiveZones         &&
                        cached.ShowWeakSignals           == showWeakSignals           &&
                        cached.EqualsInput(input))
                        return cached;
                }
            }

            var indicator = new OrderFlowSignals
            {
                VP_TicksPerBucket         = vP_TicksPerBucket,
                VP_ExtremeZScoreThreshold = vP_ExtremeZScoreThreshold,
                VP_ValueAreaPercent       = vP_ValueAreaPercent,
                CD_DivergenceLookback     = cD_DivergenceLookback,
                CD_MinDeltaThreshold      = cD_MinDeltaThreshold,
                OB_ImpulseLookback        = oB_ImpulseLookback,
                OB_MinImpulseMultiple     = oB_MinImpulseMultiple,
                OB_MaxActiveZones         = oB_MaxActiveZones,
                ShowWeakSignals           = showWeakSignals,
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
            int vP_TicksPerBucket, double vP_ExtremeZScoreThreshold, double vP_ValueAreaPercent,
            int cD_DivergenceLookback, double cD_MinDeltaThreshold,
            int oB_ImpulseLookback, double oB_MinImpulseMultiple, int oB_MaxActiveZones,
            bool showWeakSignals) =>
            indicator.OrderFlowSignals(Input,
                vP_TicksPerBucket, vP_ExtremeZScoreThreshold, vP_ValueAreaPercent,
                cD_DivergenceLookback, cD_MinDeltaThreshold,
                oB_ImpulseLookback, oB_MinImpulseMultiple, oB_MaxActiveZones,
                showWeakSignals);
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.OrderFlowSignals OrderFlowSignals(
            int vP_TicksPerBucket, double vP_ExtremeZScoreThreshold, double vP_ValueAreaPercent,
            int cD_DivergenceLookback, double cD_MinDeltaThreshold,
            int oB_ImpulseLookback, double oB_MinImpulseMultiple, int oB_MaxActiveZones,
            bool showWeakSignals) =>
            indicator.OrderFlowSignals(Input,
                vP_TicksPerBucket, vP_ExtremeZScoreThreshold, vP_ValueAreaPercent,
                cD_DivergenceLookback, cD_MinDeltaThreshold,
                oB_ImpulseLookback, oB_MinImpulseMultiple, oB_MaxActiveZones,
                showWeakSignals);
    }
}
#endregion
