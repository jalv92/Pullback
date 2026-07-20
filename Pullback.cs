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

        [NinjaScriptProperty, Range(10, 1000)]
        [Display(Name = "Max stop (ticks)", GroupName = "2. Trading", Order = 5)]
        public int MaxStopTicks { get; set; }
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
                MaxStopTicks = 60;
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
            // Guard: a typo like Min=20/Max=2 would yield an empty pool and crash later.
            if (MaxPeriod < MinPeriod) MaxPeriod = MinPeriod;

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
            // All candidate MAs + slope lookback must have data.
            if (CurrentBar < Math.Max(MaxPeriod, ContextMAPeriod) + TrendSlopeBars)
                return;

            if (_firstBarTime == DateTime.MinValue)
                _firstBarTime = Time[0];

            UpdateEngine();
            DrawState();
            TradeLogic();  // Task 4
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

            // Visual audit trail: mark scored events of the ACTIVE candidate only.
            if (i == _active)
            {
                Brush b = isBounce ? Brushes.LimeGreen : Brushes.OrangeRed;
                double y = _touchIsLong[i]
                    ? Low[0]  - 8 * TickSize
                    : High[0] + 8 * TickSize;
                Draw.Dot(this, "pbEvt" + CurrentBar + "_" + i, false, 0, y, b);
            }
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
                _pbTouchBar = -1;   // a switch discards any pending pullback; re-arm on the new MA
            }
        }

        private void DrawState()
        {
            Values[0][0] = _ma[_active][0];

            string label = string.Format("{0} {1}{2}",
                _isEma[_active] ? "EMA" : "SMA",
                _period[_active],
                InWarmup() ? "  (warmup)" : "");
            Draw.Text(this, "pbActiveLabel", label, 0, High[0] + 20 * TickSize, Brushes.White);
        }

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

                // Stop cap: a pullback so deep that the structural stop exceeds the
                // cap is a low-quality, over-extended setup — skip it entirely.
                if (risk > MaxStopTicks * TickSize) { _pbTouchBar = -1; return; }

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

                // Stop cap: mirror of the long-side quality filter.
                if (risk > MaxStopTicks * TickSize) { _pbTouchBar = -1; return; }

                SetStopLoss("PB Short", CalculationMode.Price, stop, false);
                SetProfitTarget("PB Short", CalculationMode.Price, Close[0] - RewardMultiple * risk);
                EnterShort(1, "PB Short");
                _tradesToday++;
                _pbTouchBar = -1;
            }
        }
    }
}
