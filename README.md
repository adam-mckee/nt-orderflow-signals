# OrderFlowSignals — NinjaTrader 8 Indicator

Mean-reversion signal system for NQ / ES futures. A signal fires only when **all three** order-flow gates agree simultaneously on the primary (3-min) chart, with the 1-min cumulative delta used as a non-contradiction filter. Confirmed signals are painted as arrows and delivered via [ntfy](https://ntfy.sh) push notification.

---

## System Architecture

```
OrderFlowSignals.cs               ← main indicator + gatekeeper + ntfy delivery
│
├── Modules/
│   ├── Models.cs                 ← DivergenceType enum, OBType enum, OrderBlock struct
│   ├── VolumeProfileModule.cs    ← session + rolling profile, LVN detection
│   ├── CumulativeDeltaModule.cs  ← bid/ask delta tracking, HH/LL divergence
│   └── OrderBlockModule.cs       ← SMC order block identification + mitigation
│
└── Models/
    ├── SignalResult.cs            ← immutable signal snapshot (for strategy use)
    └── ZoneLevel.cs              ← persistent zone abstraction
```

### Primary (3-min) — three gates, all must agree

| Gate | Module | Condition |
|------|--------|-----------|
| 1 — Volume Extreme | `VolumeProfileModule` | Close is within N ticks of an LVN in the session **or** rolling profile |
| 2 — Delta Divergence | `CumulativeDeltaModule` | 3-min cumulative delta diverges from price (bullish or bearish) |
| 3 — Order Block | `OrderBlockModule` | An unmitigated SMC order block is within N ticks |

### 1-min confirmation filter

The 1-min `CumulativeDeltaModule` runs in parallel on the secondary data series. Before firing, the gatekeeper checks that the 1-min divergence does **not** oppose the signal direction.  A long signal is suppressed if the 1-min shows bearish divergence; a short signal is suppressed if the 1-min shows bullish divergence.

### Signal direction

- **Long**: bullish delta divergence (3-min) + bullish OB (last bearish candle before a bullish impulse).
- **Short**: bearish delta divergence (3-min) + bearish OB (last bullish candle before a bearish impulse).

---

## Module Details

### VolumeProfileModule

Builds a session volume-at-price profile and a rolling N-bar profile at **tick-size granularity** (one bucket per tick). On each bar close:

1. Bar volume is distributed uniformly from Low to High across tick buckets.
2. Session profile accumulates from RTH open; rolling profile maintains a sliding `RollingProfileBars`-bar window.
3. LVNs are any bucket where `volume < LVNThresholdPercent × max_bucket_volume`.
4. `IsAtSessionExtreme` / `IsAtRollingExtreme` return `true` when the current price is within `OBProximityTicks` ticks of any LVN.
5. POC, VAH, and VAL are maintained using the standard value-area expansion algorithm.

Enable **NinjaTrader Tick Replay** for per-tick volume accuracy; the OHLCV uniform-distribution fallback works correctly without it.

### CumulativeDeltaModule

Tracks running cumulative delta (ask volume − bid volume) and detects divergences by comparing the **current bar's close and delta** against the **swing high and swing low** of the preceding `DivergenceLookback` bars:

- **Bearish divergence**: current close > prior swing high, but current delta < delta at that high.
- **Bullish divergence**: current close < prior swing low, but current delta > delta at that low.

`MinDeltaThreshold` (default 200 contracts) prevents spurious divergences on low-volume bars. Tick Replay populates true bid/ask volume; without it the module falls back to a 50/50 bar-volume split.

### OrderBlockModule

Identifies order blocks using SMC convention. On each bar close, the module examines the last `ImpulseBars` bars for an impulse whose High-Low range exceeds `ImpulseATRMultiple × ATR(14)`. If found, it walks backwards to identify the **last opposite-colour candle** immediately preceding the impulse:

- **Bullish OB**: last bearish candle before a bullish impulse. Zone = `[Open, Low]` of that candle.
- **Bearish OB**: last bullish candle before a bearish impulse. Zone = `[Open, High]` of that candle.

**Mitigation**: a bullish OB is voided when any subsequent bar's Low trades below the zone Low; a bearish OB is voided when any bar's High trades above the zone High. Only unmitigated blocks are returned by `GetNearestValidOB`.

---

## Installation

1. Copy the **entire `Indicators/` folder** to:
   ```
   Documents/NinjaTrader 8/bin/Custom/Indicators/
   ```
2. In NinjaTrader: **Tools → Edit NinjaScript → Compile** (F5).
3. Apply **OrderFlowSignals** to a 3-min NQ or ES chart from the Indicators dialog.
4. *(Optional but recommended)* Enable **Tick Replay** on the data series for accurate bid/ask delta.

### ntfy push notifications

1. Create a topic at [ntfy.sh](https://ntfy.sh) (free, public) or run a self-hosted instance.
2. Set **Ntfy URL** to your full endpoint, e.g.:
   - Public:      `https://ntfy.sh/my-nq-signals`
   - Self-hosted: `http://192.168.1.10:8080/trading-signals`
3. For access-controlled topics, generate a Bearer token in the ntfy dashboard and paste it into **Ntfy Token**.
4. Subscribe on any device: ntfy app (iOS / Android), browser, or `curl`.

**Payload schema (JSON POST body):**
```json
{
  "symbol":        "NQ 12-24",
  "direction":     "Long",
  "price":         21045.25,
  "timestamp":     "2024-11-15T14:32:00Z",
  "triggerReason": "LVN+BullishDiv3m+BullishOB+BullishDiv1m"
}
```

### Strategy consumption

```csharp
// Inside a Strategy's OnStateChange / Configure:
var ofs = OrderFlowSignals(
    valueAreaPercent:    0.70,
    rollingProfileBars:  50,
    lVNThresholdPercent: 0.15,
    divergenceLookback:  5,
    impulseATRMultiple:  1.5,
    impulseBars:         3,
    oBProximityTicks:    10,
    signalCooldownBars:  3,
    ntfyUrl:             "",        // disable push in automated strategies
    ntfyToken:           "",
    debugMode:           false);

// Inside OnBarUpdate:
if (ofs.Signal[0] ==  1) EnterLong();
if (ofs.Signal[0] == -1) EnterShort();
```

---

## Tuning Guide

All parameters below are exposed in the NinjaTrader indicator dialog. Starting ranges are calibrated for **NQ (MNQ) on a 3-minute chart**.

### Volume Profile group

| Parameter | Default | NQ 3-min range | Notes |
|-----------|---------|----------------|-------|
| `ValueAreaPercent` | 0.70 | 0.65 – 0.75 | 70 % is the market-profile standard. Reducing to 0.65 tightens the VA and creates more LVNs outside it; raising to 0.75 makes the VA broader. |
| `RollingProfileBars` | 50 | 30 – 100 | 50 bars ≈ 2.5 h of 3-min data. Shorter windows react faster to intraday volume shifts; longer windows provide more stable LVN levels. |
| `LVNThresholdPercent` | 0.15 | 0.08 – 0.25 | Fraction of max-bucket volume below which a level is called an LVN. Lower = stricter (fewer LVNs). Start at 0.15 and widen if signals are too rare. |

### Cumulative Delta group

| Parameter | Default | NQ 3-min range | Notes |
|-----------|---------|----------------|-------|
| `DivergenceLookback` | 5 | 3 – 10 | Bars scanned for the swing high / low reference. Shorter lookback catches faster exhaustion; longer lookback filters out minor wiggles. 5 (15 min) is a good balance for NQ mean-reversion entries. |

### Order Block group

| Parameter | Default | NQ 3-min range | Notes |
|-----------|---------|----------------|-------|
| `ImpulseATRMultiple` | 1.5 | 1.0 – 2.5 | Raise to 2.0+ to require only large institutional sweeps; lower to 1.0 to detect more (but lower-quality) OBs. On NQ, ATR(14) on 3-min is typically 30 – 60 points; 1.5× ≈ 45 – 90 point impulse. |
| `ImpulseBars` | 3 | 2 – 5 | The number of bars that must sustain the impulse after the OB candle. 3 bars = 9 minutes. Use 2 for faster-moving sessions, 4–5 during lower-volatility periods. |
| `OBProximityTicks` | 10 | 5 – 20 | NQ tick size = 0.25 points. 10 ticks = 2.5 points. Widen to 15–20 if price often nearly reaches OBs but misses; tighten to 5 for precise re-test entries. This parameter also governs LVN proximity. |

### Alerts group

| Parameter | Default | NQ 3-min range | Notes |
|-----------|---------|----------------|-------|
| `SignalCooldownBars` | 3 | 2 – 6 | Minimum bars (9–18 min) between two signals. Prevents the indicator from hammering the same level repeatedly during a slow grind. Set to 5–6 during choppy sessions. |
| `NtfyUrl` | *(placeholder)* | — | Set to your ntfy topic URL. Leave as default to disable alerts. |
| `NtfyToken` | *(empty)* | — | Leave empty for public ntfy topics. Paste a Bearer token for access-controlled topics. |

### Interaction effects

- **High `ImpulseATRMultiple` + low `OBProximityTicks`**: very selective — expect ≤ 2 signals per session on NQ.
- **Low `LVNThresholdPercent` + low `DivergenceLookback`**: aggressive — more frequent but noisier signals; pair with a wider `SignalCooldownBars`.
- **Tick Replay disabled**: the cumulative delta falls back to a 50/50 bar-volume split, reducing divergence accuracy. Signals will still fire but delta quality degrades.

---

## Design Principles

- **No repainting.** All calculations use `Close[0]` at bar close (`Calculate.OnBarClose`).
- **All three gates required.** Volume, delta, and order block must simultaneously agree. Relaxing this (e.g., 2-of-3) increases signal frequency but reduces precision.
- **Session-relative volume.** The session profile resets at RTH open so LVN z-scores stay meaningful within the day's distribution.
- **Zone invalidation on close through body.** Wicks do not void an OB; only a bar close through the zone boundary does, preventing premature invalidation during stop hunts.
- **Non-blocking alerts.** ntfy HTTP POSTs run on a background `Task.Run` thread and are fire-and-forget. A failed push never stalls the indicator loop.

---

## File Reference

| File | Purpose |
|------|---------|
| `Indicators/OrderFlowSignals.cs` | Main indicator: state machine, gate evaluation, chart drawing, ntfy delivery, NT8 boilerplate |
| `Indicators/Modules/Models.cs` | `DivergenceType`, `OBType` enums; `OrderBlock` struct |
| `Indicators/Modules/VolumeProfileModule.cs` | Session + rolling volume profile, LVN detection, POC/VAH/VAL |
| `Indicators/Modules/CumulativeDeltaModule.cs` | Cumulative delta series, swing HH/LL divergence detection |
| `Indicators/Modules/OrderBlockModule.cs` | SMC order block identification, mitigation tracking, proximity lookup |
| `Indicators/Models/SignalResult.cs` | Immutable signal snapshot with gate flags and R-multiple fields |
| `Indicators/Models/ZoneLevel.cs` | Persistent zone with touch tracking and invalidation |

---

## Requirements

- NinjaTrader 8.1+ (.NET Framework 4.8, bundled)
- Tick Replay recommended for accurate bid/ask delta on `CumulativeDeltaModule` (fallback: 50/50 bar split)
- Outbound HTTP required for ntfy alerts (firewall must allow the ntfy host)
- Tested on NQ (MNQ) and ES (MES) CME Globex data feeds
