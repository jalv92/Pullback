# Pullback — Validation Protocol & Results

## Pre-registered honesty rule (committed BEFORE seeing results)

Run B (engine active) must beat Run A (frozen SMA 20 baseline) on net
profit AND profit factor over the same period, with max drawdown no more
than 20% worse. If it does not, the calibration engine has not earned its
complexity — rework or drop it. No moving the goalposts after the fact.

## Setup (both runs)

- Strategy Analyzer, instrument **NQ 09-26** (or current front month),
  **1-minute bars, last 90 days**, session template as configured.
- Commission: NQ round-turn as configured in NT8; fill = default
  Strategy Analyzer OnBarClose fills.
- All parameters at spec defaults unless stated.

## Deployment preconditions

- The chart's Trading Hours template must open once per day BEFORE the
  strategy's session window (default 09:35). The daily reset (trade
  counter, loss lockout, PnL baseline) rides `Bars.IsFirstBarOfSession`;
  a 24/7 or misaligned template silently corrupts the daily guardrails.
  Use CME US Index Futures RTH (opens 09:30 ET) or equivalent.
- Daily-loss lockout counts REALIZED PnL only (by design): worst case the
  day's loss overshoots MaxDailyLossUSD by one structural stop.

## Run A — Baseline (calibration frozen)

Freeze the engine to a fixed SMA 20:
`Min period = 20, Max period = 20, Period step = 4, Candidate MA types = SmaOnly`
(the pool collapses to one candidate — the engine can only ever pick SMA 20).

| Metric | Value |
|--------|-------|
| Net profit | |
| Profit factor | |
| Max drawdown | |
| # trades | |
| Win rate | |

## Run B — Engine active (spec defaults)

`Min period = 8, Max period = 60, Period step = 4, Candidate MA types = Both`

| Metric | Value |
|--------|-------|
| Net profit | |
| Profit factor | |
| Max drawdown | |
| # trades | |
| Win rate | |

## Verdict

- [ ] B beats A per the pre-registered rule → engine stays.
- [ ] B fails → rework or drop the engine (Approach 1 hyperparameters
      first suspect: LookbackHours, FailurePenalty, SwitchMarginPct).

## Step 3 (only if B passes): walk-forward

Walk-forward optimization in Strategy Analyzer on LookbackHours (1-6),
FailurePenalty (0-1), SwitchMarginPct (0-50): 30-day in-sample / 10-day
out-of-sample windows. Record whether out-of-sample performance holds.
