# Pullback — Adaptive Moving-Average Pullback Strategy (Design Spec)

**Date:** 2026-07-20
**Status:** Approved design, pre-implementation
**Platform:** NinjaTrader 8 (NinjaScript Strategy, C#)
**Instrument / timeframe:** NQ / MNQ, 1-minute bars

## Problem

A fixed-period pullback strategy (e.g. "buy pullbacks to the SMA 20") only
captures bounces off that one MA. On any given session the market may be
respecting a faster or slower average (9, 14, 34...). A fixed period misses
those moves entirely.

**Goal:** a pullback-to-MA strategy whose moving average is *self-calibrating*
— it continuously selects the MA period (and type) that price has actually
been bouncing off over the recent past, and trades pullbacks to that MA.

No machine learning. The engine is a deterministic bounce-scoring sweep,
fully backtestable in Strategy Analyzer. (An ML layer, if ever justified,
would sit on top of this same scoring engine later.)

## 1. Calibration engine

### Candidate pool
- Periods from `MinPeriod` (default 8) to `MaxPeriod` (default 60) in steps
  of `PeriodStep` (default 4) → 14 periods.
- MA types: SMA, EMA, or both (`CandidateTypes`, default Both → 28 series).
- All candidates update every bar (incremental O(1) per series).

### Bounce detection (per candidate, evaluated retroactively)
Bullish context (mirror for bearish):
- **Touch:** bar low penetrates the MA or comes within `TouchToleranceTicks`
  (default 4) of it.
- **Confirmation:** within the next `ConfirmBars` (default 3) bars, price
  advances ≥ `ConfirmTicks` (default 10) above the touch bar's close.
- Touch + confirmation → **bounce** (+1). Touch without confirmation →
  **failure** (counts against the candidate).

### Scoring
Per candidate, over a rolling window of `LookbackHours` (default 3):

```
score = Σ decay(age) * (bounce ? +1 : -λ)      // λ = FailurePenalty, default 0.5
```

Exponential decay: recent bounces weigh more (`DecayHalfLifeMinutes`,
default 60).

### Selection with hysteresis
- The **active MA** is the highest-scoring candidate.
- A challenger only replaces the incumbent if its score exceeds the
  incumbent's by `SwitchMarginPct` (default 20%) for `SwitchConfirmBars`
  (default 5) consecutive bars. Prevents ping-pong between neighboring
  periods.

### Cold start
- Until the first full lookback window has elapsed, the active MA is the
  default (SMA 20).
- If `RequireWarmup` (default true), no trades are taken during warmup.

## 2. Trading rules

### Trend filter (both conditions required)
- **Local:** slope of the active MA is up (long) / down (short) sustained
  over `TrendSlopeBars` (default 10) bars.
- **Context:** price is above (long) / below (short) a slow context MA,
  `EMA(ContextMAPeriod)` (default 200).
- Range sessions fail one or both conditions → no trades.

### Entry (confirmation entry)
1. Price pulls back and **touches** the active MA (same touch rule as the
   engine).
2. The rejection bar **closes back on the trend side** of the active MA
   (close above the MA for longs).
3. Enter at market on that bar's close. Long only above context MA in
   uptrend; short only below it in downtrend.

### Exit (structural)
- **Stop loss:** below the pullback swing low (long) minus `StopOffsetTicks`
  (default 2), with a hard floor of `MinStopTicks` (default 6).
- **Take profit:** `RewardMultiple` (default 1.5) × initial risk.
- One position at a time. No scale-in in v1.

### Guardrails
- Tradeable session window: `SessionStart` / `SessionEnd` parameters.
- `MaxTradesPerDay` (default 6).
- `MaxDailyLossUSD`: on breach, flatten and stop trading for the day.

## 3. Chart visualization

The strategy draws, every bar:
- The **active MA line** (visibly jumps when the engine switches period).
- A **label** with the current selection (e.g. "EMA 12").
- **Markers** on counted bounces (and optionally failures) so the engine's
  decisions can be audited by eye.

## 4. Code architecture

- Single file `Pullback.cs` — one NinjaScript Strategy, standard NT8
  namespace layout.
- Engine state in flat arrays indexed by candidate (scores, ring buffers of
  bounce events). No satellite classes, no separate indicator in v1.
- Compilation validated with `nt8c` (PostToolUse hook already wired for
  `projects/Trading/*.cs`).

## 5. Repo & NT8 sync

- Project folder: `projects/Trading/Pullback/` with its **own public GitHub
  repo `jalv92/Pullback`** (personal account — same pattern as TBStrategy).
- Repo contents: `Pullback.cs`, `README.md`, `.gitignore` (build artifacts),
  `docs/` (this spec), `scripts/sync-to-nt8.sh`.
- **NT8 sync:** repo lives on ext4 (WSL); NT8 compiles from
  `C:\Users\javlo\Documents\NinjaTrader 8\bin\Custom\Strategies\`. Hardlinks
  are impossible across filesystems, so sync is a copy:
  - `scripts/sync-to-nt8.sh` — `cp Pullback.cs` →
    `/mnt/c/Users/javlo/Documents/NinjaTrader 8/bin/Custom/Strategies/`.
  - Git `post-commit` hook runs the script: every commit leaves the NT8 copy
    current; the NinjaScript Editor only needs a recompile (F5).

## 6. Validation plan

1. **Compile gate:** `nt8c` clean build.
2. **Baseline honesty rule (pre-registered):** backtest NQ 1-min, 60–90
   days, in Strategy Analyzer:
   - Run A: calibration frozen (fixed SMA 20) — the baseline.
   - Run B: engine active.
   - If B does not beat A, the engine has not earned its complexity —
     rework or drop it. This rule is committed to *before* seeing results.
3. **Walk-forward** on the engine's hyperparameters (`LookbackHours`,
   `FailurePenalty`, `SwitchMarginPct`) to check they aren't curve-fit to
   one regime.

## Out of scope (v1)

- Machine learning / contextual bandit period selection (Approach 2 —
  possible later layer on top of the scoring engine).
- Scale-in, multiple concurrent positions.
- ATM strategy mode (TBStrategy has the copyable pattern if wanted later).
- Hybrid limit-at-MA entry with fast cancel (possible future parameter).
