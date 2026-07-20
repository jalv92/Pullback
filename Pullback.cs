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
