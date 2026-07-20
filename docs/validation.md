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
- Note (pre-validation fix, 2026-07-20): an engine MA switch now discards any
  pending pullback tracking (`_pbTouchBar` reset), so entries always use stop
  structure from the MA they were signaled on. Registered before any run.
- Note (rework, 2026-07-20): first 28-day default run (77 trades) failed —
  PF 0.77, net -$9,193, with single losses up to $2,325 from uncapped
  structural stops. Added `MaxStopTicks` (default 60): setups whose
  structural stop exceeds the cap are skipped as over-extended. Registered
  before the 90-day A/B runs; both runs use the same cap.
- Order fill resolution must be **High (1 tick)** — Standard guesses the
  SL/TP intrabar sequence on 1-minute bars and inflates results.

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
