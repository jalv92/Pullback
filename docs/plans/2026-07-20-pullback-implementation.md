# Pullback Strategy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `Pullback.cs`, an NT8 strategy that trades pullbacks to a self-calibrating moving average (the MA period/type that price has actually been bouncing off recently), plus repo infra and NT8 sync.

**Architecture:** Single NinjaScript Strategy file. A deterministic calibration engine (28 candidate MAs, bounce/failure scoring with decay, hysteresis selection) feeds a confirmation-entry pullback trader with structural exits. No ML, no satellite files.

**Tech Stack:** C# / NinjaScript (NT8), `nt8c` for out-of-editor compilation, bash for sync script, `gh` for the public GitHub repo.

**Spec:** `docs/2026-07-20-pullback-design.md` (same repo). Defaults below are copied from it.

## Global Constraints

- All deliverables (code, comments, README, commit messages) in **English**.
- One strategy file: `Pullback.cs`. No separate indicator, no satellite classes (v1).
- No machine learning. Engine is deterministic counting + decay.
- `Calculate = Calculate.OnBarClose`. Managed order methods (`EnterLong`/`SetStopLoss`/`SetProfitTarget`) — NOT the unmanaged approach TBStrategy uses.
- One position at a time, no scale-in.
- **Testing reality:** NinjaScript has no unit-test harness. Per-task check = `nt8c check` clean compile (the PostToolUse hook runs it automatically on every Edit/Write of `projects/Trading/*.cs` — do NOT run it manually again after edits; only run manually where a step says so, e.g. after creating the file fresh in a terminal). Behavior validation = Strategy Analyzer (Task 6).
- Commit after every task. End commit messages with:
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`
- Working directory for all tasks: `/home/javlo/Code Projects/main-project/projects/Trading/Pullback/`

---

### Task 1: Strategy skeleton — parameters + candidate pool

**Files:**
- Create: `Pullback.cs`

**Interfaces:**
- Produces (used by every later task):
  - `public class Pullback : Strategy` in namespace `NinjaTrader.NinjaScript.Strategies`
  - `public enum PullbackMAType { SmaOnly, EmaOnly, Both }`
  - Candidate arrays: `ISeries<double>[] _ma`, `int[] _period`, `bool[] _isEma`, `int _n` (candidate count), `ISeries<double> _contextMA`
  - All strategy parameters listed below (exact names matter — later tasks reference them).

- [ ] **Step 1: Write `Pullback.cs` with parameters, state, and candidate-pool construction**

```csharp
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum PullbackMAType { SmaOnly, EmaOnly, Both }

    /// <summary>
    /// Pullback — trades pullbacks to a self-calibrating moving average.
    /// The engine scores every candidate MA (period x type) by how many
    /// confirmed bounces price gave it recently, and trades the winner.
    /// Spec: docs/2026-07-20-pullback-design.md
    /// </summary>
    public class Pullback : Strategy
    {
        // ── Candidate pool (index = candidate id, fixed after DataLoaded) ──
        private ISeries<double>[] _ma;
        private int[]             _period;
        private bool[]            _isEma;
        private int               _n;
        private ISeries<double>   _contextMA;

        // ── Calibration engine state (Task 2) ──
        private struct ScoreEvent { public DateTime Time; public bool IsBounce; }
        private int[]               _touchBar;      // -1 = no pending touch
        private double[]            _touchClose;
        private bool[]              _touchIsLong;
        private Queue<ScoreEvent>[] _events;
        private double[]            _score;
        private int      _active           = -1;
        private int      _challenger      = -1;
        private int      _challengerStreak = 0;
        private DateTime _firstBarTime     = DateTime.MinValue;

        // ── Trading state (Task 4) ──
        private int      _pbTouchBar = -1;   // pending pullback touch on the ACTIVE MA
        private double   _pbExtreme;         // pullback low (long) / high (short)
        private bool     _pbIsLong;
        private TimeSpan _sessStart, _sessEnd;
        private int      _tradesToday;
        private double   _sessionStartCum;
        private bool     _lockedOut;
        // ponytail: pullback tracking expires after this many bars; parameterize if backtest asks
        private const int PullbackTimeoutBars = 10;

        #region Parameters — 1. Calibration Engine
        [NinjaScriptProperty, Range(2, 200)]
        [Display(Name = "Min period", GroupName = "1. Calibration Engine", Order = 0)]
        public int MinPeriod { get; set; }

        [NinjaScriptProperty, Range(2, 400)]
        [Display(Name = "Max period", GroupName = "1. Calibration Engine", Order = 1)]
        public int MaxPeriod { get; set; }

        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Period step", GroupName = "1. Calibration Engine", Order = 2)]
        public int PeriodStep { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Candidate MA types", GroupName = "1. Calibration Engine", Order = 3)]
        public PullbackMAType CandidateTypes { get; set; }

        [NinjaScriptProperty, Range(0, 20)]
        [Display(Name = "Touch tolerance (ticks)", GroupName = "1. Calibration Engine", Order = 4)]
        public int TouchToleranceTicks { get; set; }

        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Confirm bars", GroupName = "1. Calibration Engine", Order = 5)]
        public int ConfirmBars { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Confirm ticks", GroupName = "1. Calibration Engine", Order = 6)]
        public int ConfirmTicks { get; set; }

        [NinjaScriptProperty, Range(0.5, 24)]
        [Display(Name = "Lookback (hours)", GroupName = "1. Calibration Engine", Order = 7)]
        public double LookbackHours { get; set; }

        [NinjaScriptProperty, Range(1, 600)]
        [Display(Name = "Decay half-life (minutes)", GroupName = "1. Calibration Engine", Order = 8)]
        public double DecayHalfLifeMinutes { get; set; }

        [NinjaScriptProperty, Range(0, 5)]
        [Display(Name = "Failure penalty (lambda)", GroupName = "1. Calibration Engine", Order = 9)]
        public double FailurePenalty { get; set; }

        [NinjaScriptProperty, Range(0, 200)]
        [Display(Name = "Switch margin (%)", GroupName = "1. Calibration Engine", Order = 10)]
        public double SwitchMarginPct { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Switch confirm bars", GroupName = "1. Calibration Engine", Order = 11)]
        public int SwitchConfirmBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require warmup before trading", GroupName = "1. Calibration Engine", Order = 12)]
        public bool RequireWarmup { get; set; }
        #endregion

        #region Parameters — 2. Trading
        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Trend slope bars", GroupName = "2. Trading", Order = 0)]
        public int TrendSlopeBars { get; set; }

        [NinjaScriptProperty, Range(10, 1000)]
        [Display(Name = "Context MA period (EMA)", GroupName = "2. Trading", Order = 1)]
        public int ContextMAPeriod { get; set; }

        [NinjaScriptProperty, Range(0, 20)]
        [Display(Name = "Stop offset (ticks)", GroupName = "2. Trading", Order = 2)]
        public int StopOffsetTicks { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Min stop (ticks)", GroupName = "2. Trading", Order = 3)]
        public int MinStopTicks { get; set; }

        [NinjaScriptProperty, Range(0.1, 10)]
        [Display(Name = "Reward multiple (R)", GroupName = "2. Trading", Order = 4)]
        public double RewardMultiple { get; set; }
        #endregion

        #region Parameters — 3. Risk & Session
        [NinjaScriptProperty]
        [Display(Name = "Session start (HH:mm)", GroupName = "3. Risk & Session", Order = 0)]
        public string SessionStartTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session end (HH:mm)", GroupName = "3. Risk & Session", Order = 1)]
        public string SessionEndTime { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Max trades per day", GroupName = "3. Risk & Session", Order = 2)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty, Range(0, 100000)]
        [Display(Name = "Max daily loss (USD)", GroupName = "3. Risk & Session", Order = 3)]
        public double MaxDailyLossUSD { get; set; }
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "Pullback";
                Description = "Pullback to a self-calibrating moving average (bounce-scoring engine).";
                Calculate   = Calculate.OnBarClose;
                IsOverlay   = true;
                EntriesPerDirection = 1;

                // Spec defaults
                MinPeriod = 8;  MaxPeriod = 60;  PeriodStep = 4;
                CandidateTypes = PullbackMAType.Both;
                TouchToleranceTicks = 4;  ConfirmBars = 3;  ConfirmTicks = 10;
                LookbackHours = 3;  DecayHalfLifeMinutes = 60;  FailurePenalty = 0.5;
                SwitchMarginPct = 20;  SwitchConfirmBars = 5;  RequireWarmup = true;
                TrendSlopeBars = 10;  ContextMAPeriod = 200;
                StopOffsetTicks = 2;  MinStopTicks = 6;  RewardMultiple = 1.5;
                SessionStartTime = "09:35";  SessionEndTime = "15:45";
                MaxTradesPerDay = 6;  MaxDailyLossUSD = 1000;

                AddPlot(Brushes.Gold, "ActiveMA");
            }
            else if (State == State.Configure)
            {
                _sessStart = TimeSpan.Parse(SessionStartTime);
                _sessEnd   = TimeSpan.Parse(SessionEndTime);
            }
            else if (State == State.DataLoaded)
            {
                BuildCandidates();
                _contextMA = EMA(ContextMAPeriod);
            }
        }

        private void BuildCandidates()
        {
            var periods = new List<int>();
            for (int p = MinPeriod; p <= MaxPeriod; p += PeriodStep)
                periods.Add(p);

            int typesPerPeriod = CandidateTypes == PullbackMAType.Both ? 2 : 1;
            _n = periods.Count * typesPerPeriod;

            _ma          = new ISeries<double>[_n];
            _period      = new int[_n];
            _isEma       = new bool[_n];
            _touchBar    = new int[_n];
            _touchClose  = new double[_n];
            _touchIsLong = new bool[_n];
            _events      = new Queue<ScoreEvent>[_n];
            _score       = new double[_n];

            int i = 0;
            foreach (int p in periods)
            {
                if (CandidateTypes != PullbackMAType.EmaOnly)
                { _ma[i] = SMA(p); _period[i] = p; _isEma[i] = false; i++; }
                if (CandidateTypes != PullbackMAType.SmaOnly)
                { _ma[i] = EMA(p); _period[i] = p; _isEma[i] = true; i++; }
            }

            for (int k = 0; k < _n; k++)
            {
                _touchBar[k] = -1;
                _events[k]   = new Queue<ScoreEvent>();
            }

            // Cold-start default: the SMA (or EMA if SMA excluded) closest to period 20.
            _active = 0;
            int bestDist = int.MaxValue;
            for (int k = 0; k < _n; k++)
            {
                bool preferredType = CandidateTypes == PullbackMAType.EmaOnly ? _isEma[k] : !_isEma[k];
                int dist = Math.Abs(_period[k] - 20);
                if (preferredType && dist < bestDist) { bestDist = dist; _active = k; }
            }
        }

        protected override void OnBarUpdate()
        {
            // Engine + trading added in Tasks 2-4.
        }
    }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `nt8c check Pullback.cs`
Expected: `✓ compiles` (exit 0). If the file was created via Write, the PostToolUse hook already reported this — check its stderr output instead of re-running.

- [ ] **Step 3: Commit**

```bash
git add Pullback.cs
git commit -m "feat: Pullback strategy skeleton — parameters and candidate MA pool

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Calibration engine

**Files:**
- Modify: `Pullback.cs` (fill `OnBarUpdate`, add engine methods)

**Interfaces:**
- Consumes: candidate arrays and parameters from Task 1.
- Produces (Task 3 and 4 rely on these):
  - `_active` — index of the currently selected MA, updated every bar.
  - `bool InWarmup()` — true until the first full lookback window elapsed.
  - `void UpdateEngine()` — called once per bar from `OnBarUpdate`.
  - `_events[i]` receives a `ScoreEvent` on every resolved touch; `AddEvent(int i, bool isBounce)` is the single entry point (Task 3 hooks bounce markers here).

- [ ] **Step 1: Fill `OnBarUpdate` and add the engine methods**

Replace the empty `OnBarUpdate` and add the private methods below it:

```csharp
        protected override void OnBarUpdate()
        {
            // All candidate MAs + slope lookback must have data.
            if (CurrentBar < Math.Max(MaxPeriod, ContextMAPeriod) + TrendSlopeBars)
                return;

            if (_firstBarTime == DateTime.MinValue)
                _firstBarTime = Time[0];

            UpdateEngine();
            // DrawState();   // Task 3
            // TradeLogic();  // Task 4
        }

        private bool InWarmup()
        {
            return Time[0] < _firstBarTime.AddHours(LookbackHours);
        }

        private void UpdateEngine()
        {
            double tol     = TouchToleranceTicks * TickSize;
            double confirm = ConfirmTicks * TickSize;

            for (int i = 0; i < _n; i++)
            {
                double m = _ma[i][0];

                // 1. Resolve a pending touch from an earlier bar.
                if (_touchBar[i] >= 0 && _touchBar[i] < CurrentBar)
                {
                    bool confirmed = _touchIsLong[i]
                        ? High[0] >= _touchClose[i] + confirm
                        : Low[0]  <= _touchClose[i] - confirm;

                    if (confirmed)
                    { AddEvent(i, true);  _touchBar[i] = -1; }
                    else if (CurrentBar - _touchBar[i] >= ConfirmBars)
                    { AddEvent(i, false); _touchBar[i] = -1; }
                }

                // 2. Detect a new touch (one pending touch at a time per candidate).
                if (_touchBar[i] < 0)
                {
                    bool longSide = Open[0] > m;          // price came from above → bullish touch
                    bool touched  = longSide
                        ? Low[0]  <= m + tol
                        : High[0] >= m - tol;

                    if (touched)
                    {
                        _touchBar[i]    = CurrentBar;
                        _touchClose[i]  = Close[0];
                        _touchIsLong[i] = longSide;
                    }
                }

                // 3. Refresh score.
                _score[i] = ComputeScore(i);
            }

            SelectActive();
        }

        private void AddEvent(int i, bool isBounce)
        {
            _events[i].Enqueue(new ScoreEvent { Time = Time[0], IsBounce = isBounce });
        }

        private double ComputeScore(int i)
        {
            Queue<ScoreEvent> q = _events[i];
            DateTime cutoff = Time[0].AddHours(-LookbackHours);
            while (q.Count > 0 && q.Peek().Time < cutoff)
                q.Dequeue();

            double s = 0;
            foreach (ScoreEvent e in q)
            {
                double ageMin = (Time[0] - e.Time).TotalMinutes;
                double w = Math.Pow(0.5, ageMin / DecayHalfLifeMinutes);
                s += e.IsBounce ? w : -FailurePenalty * w;
            }
            return s;
        }

        private void SelectActive()
        {
            int best = 0;
            for (int i = 1; i < _n; i++)
                if (_score[i] > _score[best]) best = i;

            if (best == _active || _score[best] <= 0)
            { _challenger = -1; _challengerStreak = 0; return; }

            // Challenger must beat the incumbent by the margin (incumbent floor 0
            // so a positive challenger can dethrone a negative-scoring incumbent).
            bool beats = _score[best] >
                Math.Max(0, _score[_active]) * (1 + SwitchMarginPct / 100.0);
            if (!beats)
            { _challenger = -1; _challengerStreak = 0; return; }

            if (best == _challenger) _challengerStreak++;
            else { _challenger = best; _challengerStreak = 1; }

            if (_challengerStreak >= SwitchConfirmBars)
            {
                _active = best;
                _challenger = -1;
                _challengerStreak = 0;
            }
        }
```

- [ ] **Step 2: Verify the hook reports a clean compile** (no manual run needed)

Expected: `[nt8c] Pullback.cs: ✓ compiles` on the Edit's stderr.

- [ ] **Step 3: Commit**

```bash
git add Pullback.cs
git commit -m "feat: calibration engine — bounce scoring with decay and hysteresis selection

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: Chart visualization

**Files:**
- Modify: `Pullback.cs`

**Interfaces:**
- Consumes: `_active`, `_period`, `_isEma`, `_ma`, `AddEvent` (Task 2), plot 0 ("ActiveMA", Task 1).
- Produces: `void DrawState()` called from `OnBarUpdate`; bounce-dot drawing inside `AddEvent`.

- [ ] **Step 1: Uncomment the `DrawState()` call in `OnBarUpdate` and add the method**

In `OnBarUpdate`, change `// DrawState();   // Task 3` to `DrawState();`. Add below `SelectActive()`:

```csharp
        private void DrawState()
        {
            Values[0][0] = _ma[_active][0];

            string label = string.Format("{0} {1}{2}",
                _isEma[_active] ? "EMA" : "SMA",
                _period[_active],
                InWarmup() ? "  (warmup)" : "");
            Draw.TextFixed(this, "pbActiveLabel", label, TextPosition.TopRight);
        }
```

- [ ] **Step 2: Mark counted bounces of the active MA**

In `AddEvent`, after the `Enqueue` line, add:

```csharp
            // Visual audit trail: mark scored events of the ACTIVE candidate only.
            if (i == _active)
            {
                Brush b = isBounce ? Brushes.LimeGreen : Brushes.OrangeRed;
                double y = _touchIsLong[i]
                    ? Low[0]  - 8 * TickSize
                    : High[0] + 8 * TickSize;
                Draw.Dot(this, "pbEvt" + CurrentBar + "_" + i, false, 0, y, b);
            }
```

- [ ] **Step 3: Verify the hook reports a clean compile**

Expected: `[nt8c] Pullback.cs: ✓ compiles`.

- [ ] **Step 4: Commit**

```bash
git add Pullback.cs
git commit -m "feat: chart visualization — active MA plot, selection label, bounce markers

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Trading logic — trend filter, confirmation entry, structural exits, guardrails

**Files:**
- Modify: `Pullback.cs`

**Interfaces:**
- Consumes: `_active`, `_ma`, `_contextMA`, `InWarmup()` (Task 2); trading-state fields and all `2. Trading` / `3. Risk & Session` parameters (Task 1).
- Produces: `void TradeLogic()` called from `OnBarUpdate`. Entry signal names: `"PB Long"` / `"PB Short"`.

- [ ] **Step 1: Uncomment the `TradeLogic()` call and add the trading methods**

In `OnBarUpdate`, change `// TradeLogic();  // Task 4` to `TradeLogic();`. Add below `DrawState()`:

```csharp
        private bool InSession()
        {
            TimeSpan t = Time[0].TimeOfDay;
            return t >= _sessStart && t <= _sessEnd;
        }

        private void Flatten()
        {
            if (Position.MarketPosition == MarketPosition.Long)  ExitLong();
            if (Position.MarketPosition == MarketPosition.Short) ExitShort();
        }

        private void TradeLogic()
        {
            // Daily reset on the first bar of each session.
            if (Bars.IsFirstBarOfSession)
            {
                _tradesToday     = 0;
                _lockedOut       = false;
                _sessionStartCum = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                _pbTouchBar      = -1;
            }

            if (!InSession()) { Flatten(); _pbTouchBar = -1; return; }
            if (RequireWarmup && InWarmup()) return;

            // Daily guardrails (realized PnL only — open PnL rides on its SL/TP).
            double dailyPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit
                              - _sessionStartCum;
            if (_lockedOut || dailyPnL <= -MaxDailyLossUSD)
            {
                if (!_lockedOut) { Flatten(); _lockedOut = true; }
                return;
            }
            if (Position.MarketPosition != MarketPosition.Flat) return;
            if (_tradesToday >= MaxTradesPerDay) return;

            double m   = _ma[_active][0];
            double tol = TouchToleranceTicks * TickSize;

            bool upTrend = _ma[_active][0] > _ma[_active][TrendSlopeBars]
                           && Close[0] > _contextMA[0];
            bool downTrend = _ma[_active][0] < _ma[_active][TrendSlopeBars]
                             && Close[0] < _contextMA[0];

            // Pullback tracking is invalidated when the trend dies or times out.
            if (_pbTouchBar >= 0)
            {
                bool trendAlive = _pbIsLong ? upTrend : downTrend;
                if (!trendAlive || CurrentBar - _pbTouchBar > PullbackTimeoutBars)
                    _pbTouchBar = -1;
            }

            // Start or extend pullback tracking on a touch of the active MA.
            if (upTrend && Low[0] <= m + tol)
            {
                if (_pbTouchBar < 0 || !_pbIsLong)
                { _pbTouchBar = CurrentBar; _pbExtreme = Low[0]; _pbIsLong = true; }
                else
                { _pbExtreme = Math.Min(_pbExtreme, Low[0]); }
            }
            else if (downTrend && High[0] >= m - tol)
            {
                if (_pbTouchBar < 0 || _pbIsLong)
                { _pbTouchBar = CurrentBar; _pbExtreme = High[0]; _pbIsLong = false; }
                else
                { _pbExtreme = Math.Max(_pbExtreme, High[0]); }
            }

            if (_pbTouchBar < 0) return;

            // Confirmation entry: rejection bar closes back on the trend side.
            if (_pbIsLong && Close[0] > m)
            {
                double stop = _pbExtreme - StopOffsetTicks * TickSize;
                double risk = Close[0] - stop;
                double minRisk = MinStopTicks * TickSize;
                if (risk < minRisk) { stop = Close[0] - minRisk; risk = minRisk; }

                SetStopLoss("PB Long", CalculationMode.Price, stop, false);
                SetProfitTarget("PB Long", CalculationMode.Price, Close[0] + RewardMultiple * risk);
                EnterLong(1, "PB Long");
                _tradesToday++;
                _pbTouchBar = -1;
            }
            else if (!_pbIsLong && Close[0] < m)
            {
                double stop = _pbExtreme + StopOffsetTicks * TickSize;
                double risk = stop - Close[0];
                double minRisk = MinStopTicks * TickSize;
                if (risk < minRisk) { stop = Close[0] + minRisk; risk = minRisk; }

                SetStopLoss("PB Short", CalculationMode.Price, stop, false);
                SetProfitTarget("PB Short", CalculationMode.Price, Close[0] - RewardMultiple * risk);
                EnterShort(1, "PB Short");
                _tradesToday++;
                _pbTouchBar = -1;
            }
        }
```

- [ ] **Step 2: Verify the hook reports a clean compile**

Expected: `[nt8c] Pullback.cs: ✓ compiles`.

- [ ] **Step 3: Commit**

```bash
git add Pullback.cs
git commit -m "feat: trading logic — trend filter, confirmation entry, structural SL/TP, daily guardrails

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: Repo infra — README, .gitignore, NT8 sync, public GitHub repo

**Files:**
- Create: `.gitignore`, `README.md`, `scripts/sync-to-nt8.sh`, `.git/hooks/post-commit`

**Interfaces:**
- Consumes: `Pullback.cs` (Tasks 1-4).
- Produces: every `git commit` copies `Pullback.cs` to the NT8 Strategies folder; repo public at `https://github.com/jalv92/Pullback`.

- [ ] **Step 1: Create `.gitignore`** (same content as TBStrategy's)

```gitignore
# NinjaScript build artifacts — never commit compiled output
bin/
obj/
*.dll
*.pdb

# Visual Studio / NinjaScript Editor local state
.vs/
*.user
*.suo

# Local backups
*.bak
*~

# OS junk
.DS_Store
Thumbs.db
```

- [ ] **Step 2: Create `scripts/sync-to-nt8.sh`**

```bash
#!/usr/bin/env bash
# Copies Pullback.cs into the NT8 Custom/Strategies folder (WSL → Windows).
# Run manually or via the post-commit hook. NT8 Editor recompile (F5) picks it up.
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")/.."
DEST="/mnt/c/Users/javlo/Documents/NinjaTrader 8/bin/Custom/Strategies"
cp Pullback.cs "$DEST/Pullback.cs"
echo "[sync-to-nt8] Pullback.cs -> $DEST"
```

Run: `chmod +x scripts/sync-to-nt8.sh`

- [ ] **Step 3: Install the post-commit hook** (local, not versioned — README documents it)

```bash
printf '#!/bin/sh\nexec scripts/sync-to-nt8.sh\n' > .git/hooks/post-commit
chmod +x .git/hooks/post-commit
```

- [ ] **Step 4: Create `README.md`**

```markdown
# Pullback

NinjaTrader 8 strategy: trades pullbacks to a **self-calibrating moving
average**. Instead of a fixed period, a deterministic engine scores every
candidate MA (SMA/EMA, periods 8-60) by how many *confirmed bounces* price
gave it over the last few hours (failures subtract, recent events weigh
more) and trades the winner, with hysteresis so the selection doesn't
ping-pong.

- Instrument/timeframe: NQ/MNQ, 1-minute.
- Entry: pullback touch of the active MA + rejection bar closing back on
  the trend side. Trend = active-MA slope + EMA(200) context filter.
- Exit: structural stop under the pullback swing (6-tick floor), target at
  1.5R. Session window, max trades/day, and max daily loss guardrails.
- No ML — the engine is bounce counting with decay. Design doc:
  [`docs/2026-07-20-pullback-design.md`](docs/2026-07-20-pullback-design.md).

## Files

| Path | Purpose |
|------|---------|
| `Pullback.cs` | The strategy (single file, managed orders) |
| `scripts/sync-to-nt8.sh` | Copies the .cs into `Documents/NinjaTrader 8/bin/Custom/Strategies/` |
| `docs/` | Design spec, implementation plan, validation results |

## Dev setup (per machine)

1. Clone, then install the sync hook:
   `printf '#!/bin/sh\nexec scripts/sync-to-nt8.sh\n' > .git/hooks/post-commit && chmod +x .git/hooks/post-commit`
2. Every commit copies `Pullback.cs` to the NT8 Strategies folder; press F5
   in the NinjaScript Editor to recompile.
3. Out-of-editor compile check: `nt8c check Pullback.cs`.
```

- [ ] **Step 5: Commit and verify the sync hook fires**

```bash
git add .gitignore README.md scripts/sync-to-nt8.sh
git commit -m "chore: repo infra — README, gitignore, NT8 sync script + post-commit hook

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
ls -la "/mnt/c/Users/javlo/Documents/NinjaTrader 8/bin/Custom/Strategies/Pullback.cs"
```

Expected: commit output followed by `[sync-to-nt8] Pullback.cs -> ...`, and the `ls` shows the file with today's timestamp.

- [ ] **Step 6: Create the public GitHub repo and push**

```bash
gh repo create jalv92/Pullback --public --source . --remote origin --push
```

Expected: repo created, `main` pushed. If `gh` is authenticated as the EXPO account (Vjagg15959), fall back to: create the repo empty at github.com under jalv92, then
`git remote add origin https://github.com/jalv92/Pullback.git && git push -u origin main`
(the credential helper in `~/.gitconfig` routes by remote URL — see memory `[[github-dual-account]]`).

Verify: `git ls-remote origin main` returns the pushed commit.

---

### Task 6: Validation — NT8 compile + Strategy Analyzer baseline vs engine

**Files:**
- Create: `docs/validation.md`

This task is human-gated: NT8 runs on Windows and the Strategy Analyzer cannot be driven from WSL. The deliverable is the pre-registered results template plus the exact run instructions; Javier executes the runs and fills in the numbers.

**Interfaces:**
- Consumes: the synced `Pullback.cs` in NT8 (Task 5).
- Produces: `docs/validation.md` with the pre-registered honesty rule and both run configs.

- [ ] **Step 1: Create `docs/validation.md`**

```markdown
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
```

- [ ] **Step 2: Commit and push**

```bash
git add docs/validation.md
git commit -m "docs: validation protocol — pre-registered baseline-vs-engine rule

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
git push
```

- [ ] **Step 3: Hand off to Javier**

Instructions to relay: open NT8 → NinjaScript Editor → F5 (compile; the file is already synced) → Strategy Analyzer → run A then B per `docs/validation.md`, fill in the tables, and report back. Any compile error inside NT8 that `nt8c` did not catch → paste it back for a fix (known parity is exact, so this is unexpected).
