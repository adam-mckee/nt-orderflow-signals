# OrderFlowSignals — NinjaTrader 8 Indicator

Mean-reversion signal system for NQ / ES futures. Entries are gated on the confluence of three independent order-flow detectors: statistical volume extremes, cumulative delta divergences, and institutional order block levels. All three modules must agree before a signal is painted.

---

## System Architecture

```
OrderFlowSignals.cs  (main indicator, NinjaTrader.NinjaScript.Indicators)
│
├── Modules/
│   ├── VolumeProfileModule.cs    — session volume distribution & extreme detection
│   ├── CumulativeDeltaModule.cs  — bid/ask delta tracking & divergence detection
│   └── OrderBlockModule.cs       — institutional order block identification & zone management
│
└── Models/
    ├── SignalResult.cs            — immutable signal snapshot (direction, strength, gate flags)
    └── ZoneLevel.cs              — persistent price zone with touch/invalidation logic
```

### Gate Logic

A signal fires when **at least 2 of 3** gates are active (configurable to require all 3 via `ShowWeakSignals = false`):

| Gate | Module | Condition |
|------|--------|-----------|
| Volume Extreme | `VolumeProfileModule` | Price is at a bucket with z-score ≥ threshold vs. session mean |
| Delta Divergence | `CumulativeDeltaModule` | Price makes new swing extreme without delta confirmation |
| Order Block | `OrderBlockModule` | Price re-enters an untested institutional order block zone |

**Direction** is resolved from the delta divergence polarity and order block type:
- **Long**: bullish delta divergence (price down, delta up) + untested bullish OB
- **Short**: bearish delta divergence (price up, delta down) + untested bearish OB

---

## Module Details

### VolumeProfileModule

Builds a session volume-at-price histogram bucketed at `TicksPerBucket` tick intervals. On each bar:

1. Volume is linearly apportioned across the bar's price range into buckets.
2. The full bucket distribution is used to compute mean and standard deviation.
3. The bucket containing the current close is z-scored against the distribution.
4. A z-score ≥ `ExtremeZScoreThreshold` (default 1.5σ) marks the bar as an extreme.
5. POC, VAH, and VAL are maintained using the standard 70 % value-area convention.

**Key parameters**

| Parameter | Default | Description |
|-----------|---------|-------------|
| `TicksPerBucket` | 4 | Histogram bucket width in ticks |
| `ExtremeZScoreThreshold` | 1.5 | σ cutoff for HVN/LVN classification |
| `ValueAreaPercent` | 0.70 | Fraction of session volume defining the value area |

---

### CumulativeDeltaModule

Tracks running cumulative delta (ask volume − bid volume) and compares its trajectory against price over a rolling lookback window.

- **Bearish divergence**: price makes a higher high, delta makes a lower high → exhaustion top, short bias.
- **Bullish divergence**: price makes a lower low, delta makes a higher low → exhaustion bottom, long bias.

A `MinDeltaThreshold` (default 200 contracts) prevents noise from triggering on negligible delta swings. Requires Tick Replay enabled in NinjaTrader to populate true bid/ask volume; falls back to a 50/50 split of bar volume when unavailable.

**Key parameters**

| Parameter | Default | Description |
|-----------|---------|-------------|
| `DivergenceLookback` | 10 | Rolling bars compared for swing comparison |
| `MinDeltaThreshold` | 200 | Minimum absolute delta change to qualify |
| `ResetOnSession` | true | Zero session delta at RTH open |

---

### OrderBlockModule

Identifies the last opposite-colour candle preceding a significant impulsive move (Smart Money Concepts convention):

- **Bullish OB**: last bearish candle before a bullish impulse that breaks the prior structure high.
- **Bearish OB**: last bullish candle before a bearish impulse that breaks the prior structure low.

The impulse must exceed `MinImpulseMultiple × ATR(14)` to qualify. Zones are stored as `ZoneLevel` instances and invalidated on a close through the zone body. The module maintains at most `MaxActiveZones` live zones, discarding the oldest when the cap is reached.

**Key parameters**

| Parameter | Default | Description |
|-----------|---------|-------------|
| `ImpulseLookback` | 3 | Bars after candidate OB required to form the impulse |
| `MinImpulseMultiple` | 1.5 | ATR multiple the impulse must exceed |
| `MaxActiveZones` | 6 | Maximum simultaneous live zones |

---

## Installation

1. Copy the `Indicators/` folder contents to:
   `Documents/NinjaTrader 8/bin/Custom/Indicators/`
2. In NinjaTrader: **Tools → Edit NinjaScript → Compile** (or press F5 in the editor).
3. Add **OrderFlowSignals** to any NQ or ES chart from the Indicators dialog.
4. Enable **Tick Replay** on the data series for accurate bid/ask delta (optional but recommended).

### Strategy consumption

```csharp
// Inside a Strategy's OnStateChange / Configure:
var ofs = OrderFlowSignals(
    vP_TicksPerBucket:         4,
    vP_ExtremeZScoreThreshold: 1.5,
    vP_ValueAreaPercent:       0.70,
    cD_DivergenceLookback:     10,
    cD_MinDeltaThreshold:      200,
    oB_ImpulseLookback:        3,
    oB_MinImpulseMultiple:     1.5,
    oB_MaxActiveZones:         6,
    showWeakSignals:           false);

// Inside OnBarUpdate:
if (ofs.Signal[0] == 1)  EnterLong();
if (ofs.Signal[0] == -1) EnterShort();
```

---

## Design Principles

- **Mean reversion, not trend following.** Signals are generated at institutional reaction levels where order flow is exhausted, not at breakout points.
- **Volume as the primary gate.** Without a statistical volume extreme, delta and order block signals are suppressed. Volume context anchors all entries.
- **No repainting.** All calculations reference `Close[0]` on bar close (`Calculate.OnBarClose`). No future bar data is accessed.
- **Session-relative volume.** The profile resets at the RTH open to keep z-scores meaningful within the current session's distribution.
- **Zone invalidation.** Order blocks are voided on a close through their body, not on a wick, to avoid premature invalidation from stop hunts.

---

## File Reference

| File | Purpose |
|------|---------|
| `Indicators/OrderFlowSignals.cs` | Main indicator: state machine, gate evaluation, chart drawing, NT boilerplate |
| `Indicators/Modules/VolumeProfileModule.cs` | Session histogram, POC/VAH/VAL, z-score extreme detection |
| `Indicators/Modules/CumulativeDeltaModule.cs` | Cumulative delta series, divergence detection |
| `Indicators/Modules/OrderBlockModule.cs` | OB identification, zone lifecycle, confluence check |
| `Indicators/Models/SignalResult.cs` | Immutable value type capturing a complete signal state |
| `Indicators/Models/ZoneLevel.cs` | Persistent zone with touch tracking and invalidation |

---

## Requirements

- NinjaTrader 8.1+
- .NET Framework 4.8 (bundled with NT8)
- Tick Replay recommended for true bid/ask delta on `CumulativeDeltaModule`
- Tested on NQ (MNQ) and ES (MES) CME Globex data feeds
