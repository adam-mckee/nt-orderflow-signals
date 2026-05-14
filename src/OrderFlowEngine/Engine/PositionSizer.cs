using Microsoft.Extensions.Options;
using OrderFlowEngine.Config;
using OrderFlowEngine.Tradovate;

namespace OrderFlowEngine.Engine;

/// <summary>
/// Calculates position size using an ATR-based fixed-risk model:
///
///   RiskDollars        = AccountBalance × RiskPercent
///   StopDistance       = ATR × StopAtrMultiple          (in price)
///   StopDollarsPerLot  = (StopDistance / TickSize) × TickValue
///   Contracts          = floor(RiskDollars / StopDollarsPerLot)
///                        clamped to [1, MaxContracts]
///   Target             = Entry ± StopDistance × RewardRatio
///
/// Example (MNQ, $25k account):
///   ATR=20pts, Stop=1.5×ATR=30pts → StopTicks=120 → $60/contract
///   Risk=$250 → 4 contracts (risking $240, within $250 budget)
///   Target = Entry ± 60pts (2:1 on 30pt stop)
/// </summary>
public sealed class PositionSizer
{
    private readonly TradingSettings _cfg;
    private readonly double          _tickSize;

    public PositionSizer(IOptions<AppSettings> opts)
    {
        _cfg      = opts.Value.Trading;
        _tickSize = opts.Value.Signal.TickSize;
    }

    /// <summary>
    /// Computes the full position spec for a signal. <paramref name="accountBalance"/>
    /// should be the current net liquidation value fetched from Tradovate.
    /// </summary>
    public PositionSize Calculate(string direction, double entryPrice,
                                  double atr, double accountBalance)
    {
        double stopDist   = atr * _cfg.StopAtrMultiple;
        double targetDist = stopDist * _cfg.RewardRatio;

        bool isLong = direction == "Long";
        double stopPrice   = isLong ? entryPrice - stopDist : entryPrice + stopDist;
        double targetPrice = isLong ? entryPrice + targetDist : entryPrice - targetDist;

        double riskDollars       = accountBalance * _cfg.RiskPercent;
        double stopTicks         = stopDist / _tickSize;
        double stopDollarsPerLot = stopTicks * _cfg.TickValue;

        int contracts = stopDollarsPerLot > 0
            ? (int)Math.Floor(riskDollars / stopDollarsPerLot)
            : 1;
        contracts = Math.Max(1, Math.Min(contracts, _cfg.MaxContracts));

        return new PositionSize(
            Contracts:    contracts,
            StopPrice:    stopPrice,
            TargetPrice:  targetPrice,
            RiskDollars:  stopDollarsPerLot * contracts,
            StopDistance: stopDist);
    }
}
