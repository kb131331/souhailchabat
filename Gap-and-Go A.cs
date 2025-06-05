using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using cAlgo.Indicators;

namespace cAlgo.Robots
{
    /// <summary>
    /// RTH Gap-and-Go Strategy with Performance Protection
    /// 
    /// This strategy identifies and trades gap continuations during Regular Trading Hours (RTH).
    /// It looks for gaps above/below the EMA at market open and trades 3-bar momentum patterns
    /// in the direction of the gap.
    /// 
    /// Key Features:
    /// - Identifies bullish gaps (price above EMA) and bearish gaps (price below EMA)
    /// - Trades 3-bar continuation patterns with specific criteria
    /// - Implements 4th bar rules for pattern confirmation
    /// - Risk management with position sizing based on dollar risk
    /// - Daily trade limits and P/L-based trade conditions
    /// - Performance protection: Stops trading if profit factor < 0.8 in Q1 (Jan-Mar)
    /// - Conservative mode: Checks price movement vs original entry instead of P/L
    /// </summary>
    
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class RthGapStrategy : Robot
    {
        #region Parameters
        
        [Parameter("EMA Period", DefaultValue = 20, Group = "Indicator")]
        public int Period { get; set; }
        
        [Parameter("Source", DefaultValue = cAlgo.API.PriceType.Close, Group = "Indicator")]
        public cAlgo.API.PriceType Source { get; set; }
        
        [Parameter("Trading Session (HH:mm-HH:mm ET)", DefaultValue = "09:30-16:15", Group = "Session")]
        public string Session { get; set; }
        
        [Parameter("Entry Limit (HH:mm ET)", DefaultValue = "14:30", Group = "Session")]
        public string EntryLimit { get; set; }
        
        [Parameter("Max Trades (-1 = unlimited)", DefaultValue = 5, MinValue = -1, MaxValue = 20, Group = "Risk Management")]
        public int MaxTradesPerDay { get; set; }
        
        [Parameter("Conditional Trades", DefaultValue = AdditionalTradeConditionType.Conservative, Group = "Risk Management")]
        public AdditionalTradeConditionType AdditionalTradeCondition { get; set; }
        
        [Parameter("Risk per Trade ($)", DefaultValue = 100.0, MinValue = 10.0, MaxValue = 10000.0, Group = "Risk Management")]
        public double RiskAmountPerTrade { get; set; }
        
        [Parameter("Performance Protection", DefaultValue = true, Group = "Performance Protection")]
        public bool EnablePerformanceProtection { get; set; }
        
        [Parameter("Min Profit Factor (Q1)", DefaultValue = 0.8, Group = "Performance Protection")]
        public double MinProfitFactor { get; set; }
        
        [Parameter("Min Average Trade (Q1)", DefaultValue = -1.0, Group = "Performance Protection")]
        public double MinAverageTrade { get; set; }
        
        #endregion
        
        #region Fields
        
        private const int TimeframeMinutes = 5;
        private const string StrategyLabel = "RTH Gap-and-Go";
        
        private EMA_RTH _rthEma;
        private TimeZoneInfo _nyZone;
        private TimeSpan _rthStart, _rthEnd, _entryLimit;
        private TimeSpan _emaSessionStart, _emaSessionEnd;
        
        private bool _dailyInitDone = false;
        private GapType _currentGap = GapType.None;
        private int _tradesTakenToday = 0;
        private DateTime _currentTradingDay = DateTime.MinValue;
        private bool _entryLimitCheckDone = false;
        
        // Performance Protection Fields
        private DateTime? _strategyStartDate = null;
        private bool _tradingSuspendedForYear = false;
        private int _currentYear = 0;
        private readonly List<TradeRecord> _allTimeTradeRecords = new();
        private bool _q1PerformanceChecked = false;
        
        private readonly List<Bar> _dayBars = new();
        private readonly HashSet<PatternSignature> _processedPatterns = new();
        private readonly List<TradeRecord> _dailyTradeRecords = new();
        private readonly List<PatternOrder> _pendingPatternOrders = new();
        private readonly Dictionary<string, PatternOrder> _patternOrdersByLabel = new();
        
        #endregion
        
        #region Enums and Classes
        
        public enum AdditionalTradeConditionType
        {
            Conservative,
            Aggressive
        }
        
        public enum GapType
        {
            None,
            Up,
            Down
        }
        
        private class Bar
        {
            public double Open { get; }
            public double High { get; }
            public double Low { get; }
            public double Close { get; }
            public DateTime DateTime { get; }
            public DateTime EtDateTime { get; }
            
            public Bar(double open, double high, double low, double close, DateTime dateTime, DateTime etDateTime)
            {
                Open = open;
                High = high;
                Low = low;
                Close = close;
                DateTime = dateTime;
                EtDateTime = etDateTime;
            }
        }
        
        private class PatternSignature : IEquatable<PatternSignature>
        {
            public DateTime Bar1Time { get; }
            public DateTime Bar2Time { get; }
            public DateTime Bar3Time { get; }
            
            public PatternSignature(Bar b1, Bar b2, Bar b3)
            {
                Bar1Time = b1.DateTime;
                Bar2Time = b2.DateTime;
                Bar3Time = b3.DateTime;
            }
            
            public override bool Equals(object obj) => Equals(obj as PatternSignature);
            
            public bool Equals(PatternSignature other)
            {
                if (other == null) return false;
                return Bar1Time == other.Bar1Time && Bar2Time == other.Bar2Time && Bar3Time == other.Bar3Time;
            }
            
            public override int GetHashCode() => Bar1Time.GetHashCode() ^ Bar2Time.GetHashCode() ^ Bar3Time.GetHashCode();
        }
        
        private class TradeRecord
        {
            public string Id { get; }
            public DateTime EntryTime { get; }
            public bool IsClosed { get; set; }
            public double NetProfit { get; set; }
            public double GrossProfit { get; set; }
            public double OriginalEntryPrice { get; set; }  // Store original intended entry price
            public bool IsBullish { get; set; }             // Store trade direction
            
            public TradeRecord(string id, DateTime entryTime, bool isClosed, double netProfit, double grossProfit = 0, 
                              double originalEntryPrice = 0, bool isBullish = true)
            {
                Id = id;
                EntryTime = entryTime;
                IsClosed = isClosed;
                NetProfit = netProfit;
                GrossProfit = grossProfit;
                OriginalEntryPrice = originalEntryPrice;
                IsBullish = isBullish;
            }
        }
        
        private class PatternOrder
        {
            public PatternSignature Signature { get; }
            public Bar Bar3 { get; }
            public string OrderId { get; }
            public double EntryPrice { get; }
            public double StopLoss { get; }
            public double TakeProfit { get; }
            public double Size { get; }
            public bool IsGapUp { get; }
            public DateTime TimeCreated { get; }
            
            public PatternOrder(PatternSignature signature, Bar bar3, string orderId, 
                              double entryPrice, double stopLoss, double takeProfit, 
                              double size, bool isGapUp)
            {
                Signature = signature;
                Bar3 = bar3;
                OrderId = orderId;
                EntryPrice = entryPrice;
                StopLoss = stopLoss;
                TakeProfit = takeProfit;
                Size = size;
                IsGapUp = isGapUp;
                TimeCreated = DateTime.UtcNow;
            }
        }
        
        #endregion
        
        #region Initialization
        
        protected override void OnStart()
        {
            Print($"=== RTH GAP STRATEGY STARTING ===");
            Print($"Strategy Parameters:");
            Print($"  - EMA Period: {Period}");
            Print($"  - Max Trades: {MaxTradesPerDay}");
            Print($"  - Risk per Trade: ${RiskAmountPerTrade}");
            Print($"  - Additional Trade Condition: {AdditionalTradeCondition}");
            Print($"  - Performance Protection Enabled: {EnablePerformanceProtection}");
            Print($"  - Min Average Trade (Q1): {MinAverageTrade:C2}");
            
            // Debug initial state
            Print($"=== INITIAL STATE DEBUG ===");
            Print($"Daily records count: {_dailyTradeRecords.Count}");
            Print($"All-time records count: {_allTimeTradeRecords.Count}");
            Print($"Trades taken today: {_tradesTakenToday}");
            Print($"Trading suspended: {_tradingSuspendedForYear}");
            Print($"Q1 checked: {_q1PerformanceChecked}");
            Print($"Current gap: {_currentGap}");
            Print($"Daily init done: {_dailyInitDone}");
            
            ParseSessionTimes();
            InitializeIndicators();
            InitializeTimeZone();
            InitializePerformanceTracking();
            
            Positions.Opened += OnPositionOpened;
            Positions.Closed += OnPositionClosed;
            
            CheckForExistingOrders();
            Print($"=== STRATEGY INITIALIZATION COMPLETE ===");
        }
        
        protected override void OnStop()
        {
            Print($"=== RTH GAP STRATEGY STOPPING ===");
            Positions.Opened -= OnPositionOpened;
            Positions.Closed -= OnPositionClosed;
        }
        
        private void ParseSessionTimes()
        {
            Print($"Parsing session times...");
            try
            {
                var sessionParts = Session.Split('-');
                if (sessionParts.Length == 2)
                {
                    _rthStart = _emaSessionStart = TimeSpan.Parse(sessionParts[0].Trim());
                    _rthEnd = _emaSessionEnd = TimeSpan.Parse(sessionParts[1].Trim());
                    Print($"RTH Session: {_rthStart} - {_rthEnd} ET");
                }
                
                _entryLimit = TimeSpan.Parse(EntryLimit);
                Print($"Entry Limit: {_entryLimit} ET");
            }
            catch (Exception ex)
            {
                Print($"ERROR parsing session times: {ex.Message}");
            }
        }
        
        private void InitializeIndicators()
        {
            Print($"Initializing EMA indicator...");
            string emaStart = _emaSessionStart.ToString(@"hh\:mm");
            string emaEnd = _emaSessionEnd.ToString(@"hh\:mm");
            _rthEma = Indicators.GetIndicator<EMA_RTH>(Period, Source, 0, emaStart, emaEnd);
            Print($"EMA RTH indicator initialized with period {Period}");
        }
        
        private void InitializeTimeZone()
        {
            Print($"Initializing timezone...");
            try 
            { 
                _nyZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                Print($"Timezone set to Eastern Standard Time");
            }
            catch 
            {
                try 
                { 
                    _nyZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
                    Print($"Timezone set to America/New_York");
                }
                catch 
                { 
                    _nyZone = TimeZoneInfo.CreateCustomTimeZone("ET", new TimeSpan(-5, 0, 0), "ET", "Eastern Time");
                    Print($"Using custom Eastern Time zone");
                }
            }
        }
        
        private void InitializePerformanceTracking()
        {
            Print($"Initializing performance tracking...");
            var etTime = TimeZoneInfo.ConvertTimeFromUtc(Server.Time, _nyZone);
            _currentYear = etTime.Year;
            _strategyStartDate = new DateTime(_currentYear, 1, 1);
            
            Print($"Current year: {_currentYear}, Strategy start date: {_strategyStartDate}");
            Print($"Performance protection enabled: {EnablePerformanceProtection}");
            
            CheckPerformanceProtection();
        }
        
        #endregion
        
        #region Event Handlers
        
        private void OnPositionOpened(PositionOpenedEventArgs args)
        {
            if (!args.Position.Label.StartsWith(StrategyLabel)) return;
            
            Print($"Position opened: {args.Position.Label}, Entry: {args.Position.EntryPrice}, Size: {args.Position.VolumeInUnits}");
            
            // Get original entry price and direction from pattern order
            double originalEntryPrice = args.Position.EntryPrice; // Default to actual entry
            bool isBullish = args.Position.TradeType == TradeType.Buy; // Default based on trade type
            
            if (_patternOrdersByLabel.TryGetValue(args.Position.Label, out PatternOrder patternOrder))
            {
                originalEntryPrice = patternOrder.EntryPrice; // Use original intended entry price
                isBullish = patternOrder.IsGapUp; // Use original trade direction
                Print($"Found pattern order - Original entry: {originalEntryPrice}, Is bullish: {isBullish}");
            }
            
            var tradeRecord = new TradeRecord(
                args.Position.Id.ToString(), 
                args.Position.EntryTime, 
                false, 
                args.Position.NetProfit,
                args.Position.GrossProfit,
                originalEntryPrice,
                isBullish
            );
            
            _dailyTradeRecords.Add(tradeRecord);
            _allTimeTradeRecords.Add(tradeRecord);
            
            AdjustPositionToAbsoluteLevels(args.Position);
        }
        
        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (!args.Position.Label.StartsWith(StrategyLabel)) return;
            
            Print($"Position closed: {args.Position.Label}, Net P/L: {args.Position.NetProfit:C2}");
            
            // Update daily records
            var dailyRecord = _dailyTradeRecords.FirstOrDefault(r => r.Id == args.Position.Id.ToString());
            if (dailyRecord != null)
            {
                dailyRecord.IsClosed = true;
                dailyRecord.NetProfit = args.Position.NetProfit;
                dailyRecord.GrossProfit = args.Position.GrossProfit;
                Print($"Updated daily record for position {args.Position.Id}: P/L = {args.Position.NetProfit:C2}");
            }
            
            // Update all-time records
            var allTimeRecord = _allTimeTradeRecords.FirstOrDefault(r => r.Id == args.Position.Id.ToString());
            if (allTimeRecord != null)
            {
                allTimeRecord.IsClosed = true;
                allTimeRecord.NetProfit = args.Position.NetProfit;
                allTimeRecord.GrossProfit = args.Position.GrossProfit;
                Print($"Updated all-time record for position {args.Position.Id}: P/L = {args.Position.NetProfit:C2}");
            }
        }
        
        protected override void OnTick()
        {
            CheckForNewDay();
            CheckEntryTimeLimit();
        }
        
        protected override void OnBar()
        {
            ProcessLatestBar();
        }
        
        protected override void OnBarClosed()
        {
            var barEtTime = TimeZoneInfo.ConvertTimeFromUtc(Bars.LastBar.OpenTime, _nyZone);
            if (IsOutsideSession(barEtTime.TimeOfDay, _rthEnd))
            {
                Print($"End of session reached at {barEtTime.TimeOfDay} - closing all positions");
                ClosePositionsEndOfDay();
            }
        }
        
        #endregion
        
        #region Performance Protection
        
        private void CheckPerformanceProtection()
        {
            if (!EnablePerformanceProtection || _strategyStartDate == null || _q1PerformanceChecked) return;
    
            var currentTimeEt = TimeZoneInfo.ConvertTimeFromUtc(Server.Time, _nyZone);
            var AprilFools = new DateTime(_currentYear, 4, 1);
            
            Print($"Checking performance protection - Current date: {currentTimeEt.Date}, April 1st: {AprilFools}");
            
            // Only check if we're past March 31st and not already suspended
            if (currentTimeEt.Date >= AprilFools && !_tradingSuspendedForYear)
            {
                _q1PerformanceChecked = true;
                var averageTrade = CalculateAverageTrade();
                var profitFactor = CalculateProfitFactor();
                
                Print($"Q1 Performance check - Average trade: {averageTrade:C2}, Min required: {MinAverageTrade:C2}");
                
                bool profitFactorBelowThreshold = profitFactor < MinProfitFactor;
                bool averageTradeOutsideRange = averageTrade < MinAverageTrade;
                
                if (averageTradeOutsideRange && profitFactorBelowThreshold)
                {
                    _tradingSuspendedForYear = true;
                    Print($"!!! TRADING SUSPENDED FOR YEAR - Average trade {averageTrade:C2} below minimum {MinAverageTrade:C2} !!!");
                    
                    CancelPendingOrders();
                }
                else
                {
                    Print($"Performance protection passed - continuing trading");
                }
            }
        }
        
        private double CalculateProfitFactor()
        {
            if (_allTimeTradeRecords.Count == 0) return MinProfitFactor;
            
            var currentYearStart = new DateTime(_currentYear, 1, 1);
            
            double grossProfit = 0;
            double grossLoss = 0;
            
            foreach (var trade in _allTimeTradeRecords)
            {
                // Only include trades from current year
                var tradeTimeEt = TimeZoneInfo.ConvertTimeFromUtc(trade.EntryTime, _nyZone);
                if (tradeTimeEt.Date < currentYearStart) continue;
                
                // For open positions, use current net profit
                var profit = trade.IsClosed ? trade.NetProfit : 
                            (Positions.FirstOrDefault(p => p.Id.ToString() == trade.Id)?.NetProfit ?? trade.NetProfit);
                
                if (profit > 0)
                    grossProfit += profit;
                else if (profit < 0)
                    grossLoss += Math.Abs(profit);
            }
            
            return grossLoss == 0 ? (grossProfit > 0 ? 999 : 0) : grossProfit / grossLoss;
        }
        
        private double CalculateAverageTrade()
        {
            if (_allTimeTradeRecords.Count == 0) return MinAverageTrade;
            
            var currentYearStart = new DateTime(_currentYear, 1, 1);
            var applicableTrades = new List<double>();
            
            foreach (var trade in _allTimeTradeRecords)
            {
                var tradeTimeEt = TimeZoneInfo.ConvertTimeFromUtc(trade.EntryTime, _nyZone);
                if (tradeTimeEt.Date < currentYearStart) continue;
                
                var profit = trade.IsClosed ? trade.NetProfit : 
                            (Positions.FirstOrDefault(p => p.Id.ToString() == trade.Id)?.NetProfit ?? trade.NetProfit);
                
                applicableTrades.Add(profit);
            }
            
            var average = applicableTrades.Count == 0 ? 0 : applicableTrades.Average();
            Print($"Average trade calculation: {applicableTrades.Count} trades, average: {average:C2}");
            
            return average;
        }
        
        private bool IsTradingSuspended()
        {
            bool suspended = EnablePerformanceProtection && _tradingSuspendedForYear;
            if (suspended)
            {
                Print($"Trading suspended due to performance protection");
            }
            return suspended;
        }
        
        #endregion
        
        #region Bar Processing
        
        private void ProcessLatestBar()
        {
            // Check if trading is suspended
            if (IsTradingSuspended())
            {
                return;
            }
            
            int completedBarIndex = Bars.Count - 2;
            if (completedBarIndex < 0) return;
            
            var barUtcTime = Bars.OpenTimes[completedBarIndex];
            var barEtTime = TimeZoneInfo.ConvertTimeFromUtc(barUtcTime, _nyZone);
            var etTimeOfDay = barEtTime.TimeOfDay;
            
            Print($"Processing bar at {barEtTime} ET (Index: {completedBarIndex})");
            
            CheckPendingPatternOrders();
            
            if (MaxTradesReached())
            {
                Print($"Max trades reached ({_tradesTakenToday}/{MaxTradesPerDay}) - skipping bar processing");
                return;
            }
            
            if (_tradesTakenToday > 0 && !AdditionalTradeAllowed())
            {
                Print($"Additional trade not allowed - skipping bar processing");
                return;
            }
            
            if (etTimeOfDay >= _entryLimit)
            {
                Print($"Entry time limit reached ({etTimeOfDay} >= {_entryLimit}) - skipping bar processing");
                return;
            }
            
            if (!IsWithinRTH(etTimeOfDay))
            {
                Print($"Outside RTH ({etTimeOfDay}) - skipping bar processing");
                return;
            }
            
            if (!_dailyInitDone)
            {
                Print($"Identifying gap for the day...");
                IdentifyGap(completedBarIndex);
            }
            
            _dayBars.Add(CreateBarFromIndex(completedBarIndex, barUtcTime, barEtTime));
            Print($"Added bar to day collection. Total bars: {_dayBars.Count}");
            
            if (_currentGap != GapType.None && _dayBars.Count >= 3)
            {
                Print($"Sufficient bars for pattern analysis ({_dayBars.Count} bars, gap: {_currentGap})");
                ProcessPotentialPatterns();
            }
        }
        
        private Bar CreateBarFromIndex(int index, DateTime utcTime, DateTime etTime)
        {
            var bar = new Bar(
                Bars.OpenPrices[index],
                Bars.HighPrices[index],
                Bars.LowPrices[index],
                Bars.ClosePrices[index],
                utcTime,
                etTime
            );
            
            Print($"Created bar: O:{bar.Open} H:{bar.High} L:{bar.Low} C:{bar.Close} @ {etTime.ToString("HH:mm")}");
            return bar;
        }
        
        private void IdentifyGap(int barIndex)
        {
            double emaValue = _rthEma.RthEmaSeries[barIndex];
            
            Print($"Identifying gap - EMA value: {emaValue}, Bar High: {Bars.HighPrices[barIndex]}, Bar Low: {Bars.LowPrices[barIndex]}");
            
            if (!double.IsNaN(emaValue))
            {
                if (Bars.LowPrices[barIndex] > emaValue)
                {
                    _currentGap = GapType.Up;
                    Print($"GAP UP identified - Bar low ({Bars.LowPrices[barIndex]}) > EMA ({emaValue})");
                }
                else if (Bars.HighPrices[barIndex] < emaValue)
                {
                    _currentGap = GapType.Down;
                    Print($"GAP DOWN identified - Bar high ({Bars.HighPrices[barIndex]}) < EMA ({emaValue})");
                }
                else
                {
                    _currentGap = GapType.None;
                    Print($"NO GAP - Bar overlaps with EMA");
                }
                
                _dailyInitDone = true;
                Print($"Daily initialization complete. Gap type: {_currentGap}");
            }
            else
            {
                Print($"EMA value is NaN - cannot identify gap yet");
            }
        }
        
        private void ProcessPotentialPatterns()
        {
            Print($"Processing potential patterns from {_dayBars.Count} bars...");
            var expectedInterval = TimeSpan.FromMinutes(TimeframeMinutes);
            
            for (int i = 0; i <= _dayBars.Count - 3; i++)
            {
                var b1 = _dayBars[i];
                var b2 = _dayBars[i + 1];
                var b3 = _dayBars[i + 2];
                
                if (!AreConsecutiveBars(b1, b2, b3, expectedInterval))
                {
                    Print($"Bars {i}-{i+2} are not consecutive - skipping");
                    continue;
                }
                
                var signature = new PatternSignature(b1, b2, b3);
                if (_processedPatterns.Contains(signature))
                {
                    Print($"Pattern {i}-{i+2} already processed - skipping");
                    continue;
                }
                
                Print($"Checking pattern for bars {i}-{i+2}:");
                Print($"  Bar1: O:{b1.Open} H:{b1.High} L:{b1.Low} C:{b1.Close}");
                Print($"  Bar2: O:{b2.Open} H:{b2.High} L:{b2.Low} C:{b2.Close}");
                Print($"  Bar3: O:{b3.Open} H:{b3.High} L:{b3.Low} C:{b3.Close}");
                
                if (CheckPattern(b1, b2, b3))
                {
                    _processedPatterns.Add(signature);
                    Print($"Pattern {i}-{i+2} VALID and order placed");
                }
                else
                {
                    Print($"Pattern {i}-{i+2} does not meet criteria");
                }
                
                if (MaxTradesReached() || !AdditionalTradeAllowed()) 
                {
                    Print($"Stopping pattern processing - max trades reached or additional trade not allowed");
                    break;
                }
            }
        }
        
        private bool AreConsecutiveBars(Bar b1, Bar b2, Bar b3, TimeSpan expectedInterval)
        {
            bool consecutive = (b2.DateTime - b1.DateTime) == expectedInterval && 
                              (b3.DateTime - b2.DateTime) == expectedInterval;
            
            if (!consecutive)
            {
                Print($"Bars not consecutive: {b1.DateTime} -> {b2.DateTime} -> {b3.DateTime}");
            }
            
            return consecutive;
        }
        
        #endregion
        
        #region Pattern Recognition
        
        private double BodyRatio(Bar bar)
        {
            var range = bar.High - bar.Low;
            var ratio = range < 0.000001 ? 1.0 : Math.Abs(bar.Close - bar.Open) / range;
            Print($"Body ratio calculation: Range={range}, Body={Math.Abs(bar.Close - bar.Open)}, Ratio={ratio:F3}");
            return ratio;
        }
        
        private bool CheckPattern(Bar b1, Bar b2, Bar b3)
        {
            if (IsTradingSuspended()) return false;
            
            Print($"Checking {_currentGap} pattern...");
            
            double mid1 = (b1.High + b1.Low) / 2;
            double b2BodyRatio = BodyRatio(b2);
            double b3BodyRatio = BodyRatio(b3);
            
            Print($"Bar1 midpoint: {mid1}, Bar2 body ratio: {b2BodyRatio:F3}, Bar3 body ratio: {b3BodyRatio:F3}");
            
            bool patternValid = false;
            double entry = 0, takeProfit = 0, stopLoss = 0;
            
            if (_currentGap == GapType.Up)
                patternValid = CheckBullishPattern(b1, b2, b3, mid1, b2BodyRatio, b3BodyRatio, out entry, out takeProfit, out stopLoss);
            else if (_currentGap == GapType.Down)
                patternValid = CheckBearishPattern(b1, b2, b3, mid1, b2BodyRatio, b3BodyRatio, out entry, out takeProfit, out stopLoss);
            
            if (patternValid)
            {
                Print($"PATTERN VALID! Entry: {entry}, SL: {stopLoss}, TP: {takeProfit}");
                
                if (!MaxTradesReached() && AdditionalTradeAllowed())
                {
                    double positionSize = CalculatePositionSize(entry, stopLoss);
                    Print($"Calculated position size: {positionSize}");
                    return PlacePatternOrder(b1, b2, b3, entry, stopLoss, takeProfit, positionSize);
                }
                else
                {
                    Print($"Cannot place order - max trades reached or additional trade not allowed");
                }
            }
            
            return false;
        }
        
        private bool CheckBullishPattern(Bar b1, Bar b2, Bar b3, double mid1, double b2BodyRatio, double b3BodyRatio, 
                                         out double entry, out double takeProfit, out double stopLoss)
        {
            entry = takeProfit = stopLoss = 0;
            
            bool cond1 = b1.Close > b1.Open;
            bool cond2 = b2.Close > b2.Open && b2.Close > b1.High && b2.Low > b1.Low && 
                        (b2BodyRatio >= 0.5 || (b2BodyRatio >= 0.3 && b2.Low > mid1));
            bool cond3 = b3.Close > b3.Open && b3.Close > b2.High && b3.Low > b2.Low && 
                        (b3BodyRatio >= 0.7 || (b3BodyRatio >= 0.5 && b3.Low > b1.High));
            
            Print($"Bullish pattern conditions:");
            Print($"  Cond1 (Bar1 bullish): {cond1} ({b1.Close} > {b1.Open})");
            Print($"  Cond2 (Bar2 criteria): {cond2}");
            Print($"    - Bar2 bullish: {b2.Close > b2.Open}");
            Print($"    - Bar2 close > Bar1 high: {b2.Close > b1.High}");
            Print($"    - Bar2 low > Bar1 low: {b2.Low > b1.Low}");
            Print($"    - Body ratio check: {(b2BodyRatio >= 0.5 || (b2BodyRatio >= 0.3 && b2.Low > mid1))}");
            Print($"  Cond3 (Bar3 criteria): {cond3}");
            Print($"    - Bar3 bullish: {b3.Close > b3.Open}");
            Print($"    - Bar3 close > Bar2 high: {b3.Close > b2.High}");
            Print($"    - Bar3 low > Bar2 low: {b3.Low > b2.Low}");
            Print($"    - Body ratio check: {(b3BodyRatio >= 0.7 || (b3BodyRatio >= 0.5 && b3.Low > b1.High))}");
            
            if (cond1 && cond2 && cond3)
            {
                entry = b3.High + 2;
                takeProfit = b3.High + (b3.High - b1.Low);
                stopLoss = b2.Low - 1;
                Print($"BULLISH PATTERN CONFIRMED");
                return true;
            }
            
            return false;
        }
        
        private bool CheckBearishPattern(Bar b1, Bar b2, Bar b3, double mid1, double b2BodyRatio, double b3BodyRatio,
                                         out double entry, out double takeProfit, out double stopLoss)
        {
            entry = takeProfit = stopLoss = 0;
            
            bool cond1 = b1.Close < b1.Open;
            bool cond2 = b2.Close < b2.Open && b2.Close < b1.Low && b2.High < b1.High && 
                        (b2BodyRatio >= 0.5 || (b2BodyRatio >= 0.3 && b2.High < mid1));
            bool cond3 = b3.Close < b3.Open && b3.Close < b2.Low && b3.High < b2.High && 
                        (b3BodyRatio >= 0.7 || (b3BodyRatio >= 0.5 && b3.High < b1.Low));
            
            Print($"Bearish pattern conditions:");
            Print($"  Cond1 (Bar1 bearish): {cond1} ({b1.Close} < {b1.Open})");
            Print($"  Cond2 (Bar2 criteria): {cond2}");
            Print($"    - Bar2 bearish: {b2.Close < b2.Open}");
            Print($"    - Bar2 close < Bar1 low: {b2.Close < b1.Low}");
            Print($"    - Bar2 high < Bar1 high: {b2.High < b1.High}");
            Print($"    - Body ratio check: {(b2BodyRatio >= 0.5 || (b2BodyRatio >= 0.3 && b2.High < mid1))}");
            Print($"  Cond3 (Bar3 criteria): {cond3}");
            Print($"    - Bar3 bearish: {b3.Close < b3.Open}");
            Print($"    - Bar3 close < Bar2 low: {b3.Close < b2.Low}");
            Print($"    - Bar3 high < Bar2 high: {b3.High < b2.High}");
            Print($"    - Body ratio check: {(b3BodyRatio >= 0.7 || (b3BodyRatio >= 0.5 && b3.High < b1.Low))}");
            
            if (cond1 && cond2 && cond3)
            {
                entry = b3.Low - 2;
                takeProfit = b3.Low - (b1.High - b3.Low);
                stopLoss = b2.High + 1;
                Print($"BEARISH PATTERN CONFIRMED");
                return true;
            }
            
            return false;
        }
        
        #endregion
        
        #region Order Management
        
        private bool PlacePatternOrder(Bar b1, Bar b2, Bar b3, double entry, double stopLoss, double takeProfit, double size)
        {
            if (IsTradingSuspended()) return false;
            
            var tradeType = (_currentGap == GapType.Up) ? TradeType.Buy : TradeType.Sell;
            var orderLabel = $"{StrategyLabel} {Guid.NewGuid():N}";
            
            Print($"Placing {tradeType} stop order:");
            Print($"  Symbol: {Symbol.Name}");
            Print($"  Size: {size}");
            Print($"  Entry: {entry}");
            Print($"  Stop Loss: {stopLoss}");
            Print($"  Take Profit: {takeProfit}");
            Print($"  Label: {orderLabel}");
            
            var result = PlaceStopOrder(tradeType, Symbol.Name, size, entry, orderLabel);
            
            if (result.IsSuccessful)
            {
                Print($"Order placed successfully!");
                
                var signature = new PatternSignature(b1, b2, b3);
                var patternOrder = new PatternOrder(
                    signature, b3, 
                    result.PendingOrder?.Id.ToString() ?? result.Position?.Id.ToString(), 
                    entry, stopLoss, takeProfit, size, _currentGap == GapType.Up
                );
                
                _patternOrdersByLabel[orderLabel] = patternOrder;
                
                if (result.Position != null)
                {
                    Print($"Order filled immediately - setting stop loss and take profit");
                    SetAbsoluteStopLossAndTakeProfit(result.Position, stopLoss, takeProfit);
                }
                else if (result.PendingOrder != null)
                {
                    Print($"Pending order created - setting stop loss and take profit pips");
                    double slPips = Math.Abs(CalculatePipsFromEntry(entry, stopLoss, tradeType));
                    double tpPips = Math.Abs(CalculatePipsFromEntry(entry, takeProfit, tradeType));
                    
                    Print($"SL Pips: {slPips}, TP Pips: {tpPips}");
                    
                    result.PendingOrder.ModifyStopLossPips(slPips);
                    result.PendingOrder.ModifyTakeProfitPips(tpPips);
                    
                    _pendingPatternOrders.Add(patternOrder);
                    Print($"Added to pending pattern orders list");
                }
                
                _tradesTakenToday++;
                Print($"Trades taken today: {_tradesTakenToday}");
                return true;
            }
            else
            {
                Print($"Order placement FAILED: {result.Error}");
            }
            
            return false;
        }
        
        private void CheckPendingPatternOrders()
        {
            if (_pendingPatternOrders.Count == 0) return;
            
            Print($"Checking {_pendingPatternOrders.Count} pending pattern orders...");
            
            int completedBarIndex = Bars.Count - 2;
            if (completedBarIndex < 0) return;
            
            var currentBar = CreateBarFromIndex(
                completedBarIndex,
                Bars.OpenTimes[completedBarIndex],
                TimeZoneInfo.ConvertTimeFromUtc(Bars.OpenTimes[completedBarIndex], _nyZone)
            );
            
            var expectedInterval = TimeSpan.FromMinutes(TimeframeMinutes);
            var ordersToRemove = new List<PatternOrder>();
            
            foreach (var patternOrder in _pendingPatternOrders)
            {
                var pendingOrder = PendingOrders.FirstOrDefault(o => o.Id.ToString() == patternOrder.OrderId);
                
                if (pendingOrder == null)
                {
                    Print($"Pending order {patternOrder.OrderId} no longer exists - removing from list");
                    ordersToRemove.Add(patternOrder);
                    continue;
                }
                
                var timeDiff = currentBar.DateTime - patternOrder.Bar3.DateTime;
                Print($"Checking order {patternOrder.OrderId}: Time diff = {timeDiff}, Expected = {expectedInterval}");
                
                if (timeDiff == expectedInterval)
                {
                    Print($"Processing 4th bar rules for order {patternOrder.OrderId}");
                    ProcessFourthBarRules(patternOrder, pendingOrder, currentBar, ordersToRemove);
                }
                // Removed expiration check to allow orders to remain pending beyond 12 hours
            }
            
            ordersToRemove.ForEach(order => _pendingPatternOrders.Remove(order));
            if (ordersToRemove.Count > 0)
            {
                Print($"Removed {ordersToRemove.Count} orders from pending list");
            }
        }
        
        private void ProcessFourthBarRules(PatternOrder patternOrder, PendingOrder pendingOrder, 
                                          Bar currentBar, List<PatternOrder> ordersToRemove)
        {
            bool isInsideBar = currentBar.High < patternOrder.Bar3.High && currentBar.Low > patternOrder.Bar3.Low;
            bool breakAgainstTrade = patternOrder.IsGapUp ? 
                currentBar.Low < patternOrder.Bar3.Low : 
                currentBar.High > patternOrder.Bar3.High;
            
            Print($"4th bar analysis:");
            Print($"  Current bar: H:{currentBar.High} L:{currentBar.Low}");
            Print($"  Bar3: H:{patternOrder.Bar3.High} L:{patternOrder.Bar3.Low}");
            Print($"  Inside bar: {isInsideBar}");
            Print($"  Break against trade: {breakAgainstTrade}");
            
            if (isInsideBar)
            {
                Print($"4th bar is inside bar - canceling pending order and executing market order");
                pendingOrder.Cancel();
                ExecuteFourthBarMarketOrder(patternOrder, 
                    patternOrder.IsGapUp ? TradeType.Buy : TradeType.Sell, 
                    currentBar, ordersToRemove);
            }
            else if (breakAgainstTrade)
            {
                Print($"4th bar breaks against trade direction - canceling order");
                pendingOrder.Cancel();
                _tradesTakenToday--;
                ordersToRemove.Add(patternOrder);
            }
            else
            {
                Print($"4th bar meets neither inside nor break criteria - keeping pending order");
            }
        }
        
        private void ExecuteFourthBarMarketOrder(PatternOrder patternOrder, TradeType tradeType, 
                                               Bar fourthBar, List<PatternOrder> ordersToRemove)
        {
            if (IsTradingSuspended())
            {
                Print($"Trading suspended - cannot execute 4th bar market order");
                ordersToRemove.Add(patternOrder);
                return;
            }
            
            var orderLabel = $"{StrategyLabel} 4th Bar {Guid.NewGuid():N}";
            
            Print($"Executing 4th bar market order:");
            Print($"  Type: {tradeType}");
            Print($"  Size: {patternOrder.Size}");
            Print($"  Current price: {fourthBar.Close}");
            
            var result = ExecuteMarketOrder(tradeType, Symbol.Name, patternOrder.Size, orderLabel);
            
            if (result.IsSuccessful && result.Position != null)
            {
                Print($"4th bar market order executed successfully at {result.Position.EntryPrice}");
                
                double slDistance = Math.Abs(patternOrder.EntryPrice - patternOrder.StopLoss);
                double tpDistance = Math.Abs(patternOrder.TakeProfit - patternOrder.EntryPrice);
                
                double absoluteSL, absoluteTP;
                if (tradeType == TradeType.Buy)
                {
                    absoluteSL = fourthBar.Close - slDistance;
                    absoluteTP = fourthBar.Close + tpDistance;
                }
                else
                {
                    absoluteSL = fourthBar.Close + slDistance;
                    absoluteTP = fourthBar.Close - tpDistance;
                }
                
                Print($"Adjusted levels - SL: {absoluteSL}, TP: {absoluteTP}");
                
                var fourthBarPatternOrder = new PatternOrder(
                    patternOrder.Signature, fourthBar, result.Position.Id.ToString(),
                    result.Position.EntryPrice, absoluteSL, absoluteTP,
                    patternOrder.Size, patternOrder.IsGapUp
                );
                
                _patternOrdersByLabel[orderLabel] = fourthBarPatternOrder;
                SetAbsoluteStopLossAndTakeProfit(result.Position, absoluteSL, absoluteTP);
            }
            else
            {
                Print($"4th bar market order FAILED: {result.Error}");
            }
            
            ordersToRemove.Add(patternOrder);
        }
        
        private double CalculatePipsFromEntry(double entryPrice, double targetPrice, TradeType tradeType)
        {
            double priceDiff = targetPrice - entryPrice;
            if (tradeType == TradeType.Sell) priceDiff = -priceDiff;
            return priceDiff / Symbol.PipSize;
        }
        
        private void SetAbsoluteStopLossAndTakeProfit(Position position, double absoluteStopLoss, double absoluteTakeProfit)
        {
            double slPips = Math.Abs(position.EntryPrice - absoluteStopLoss) / Symbol.PipSize;
            double tpPips = Math.Abs(absoluteTakeProfit - position.EntryPrice) / Symbol.PipSize;
            
            Print($"Setting position levels - SL: {slPips} pips, TP: {tpPips} pips");
            
            position.ModifyStopLossPips(slPips);
            position.ModifyTakeProfitPips(tpPips);
        }
        
        private void AdjustPositionToAbsoluteLevels(Position position)
        {
            if (_patternOrdersByLabel.TryGetValue(position.Label, out PatternOrder patternOrder))
            {
                Print($"Adjusting position {position.Label} to absolute levels");
                SetAbsoluteStopLossAndTakeProfit(position, patternOrder.StopLoss, patternOrder.TakeProfit);
                _patternOrdersByLabel.Remove(position.Label);
            }
        }
        
        #endregion
        
        #region Position Management
        
        private void CheckForExistingOrders()
        {
            Print($"=== CHECKING FOR EXISTING ORDERS/POSITIONS ===");
            int activeTradeCount = 0;
            
            var existingPositions = Positions.Where(p => p.Label.StartsWith(StrategyLabel)).ToList();
            Print($"Found {existingPositions.Count} existing positions with strategy label");
            
            foreach (var position in existingPositions)
            {
                activeTradeCount++;
                Print($"Existing position {activeTradeCount}: {position.Label}, Entry: {position.EntryTime}, P/L: {position.NetProfit:C2}");
                
                // For existing positions, we don't have original entry info, so use actual entry and trade type
                double originalEntryPrice = position.EntryPrice;
                bool isBullish = position.TradeType == TradeType.Buy;
                
                var tradeRecord = new TradeRecord(
                    position.Id.ToString(),
                    position.EntryTime,
                    false,  // Position is open
                    position.NetProfit,
                    position.GrossProfit,
                    originalEntryPrice,
                    isBullish
                );
                _dailyTradeRecords.Add(tradeRecord);
                _allTimeTradeRecords.Add(tradeRecord);
            }
            
            var existingPendingOrders = PendingOrders.Where(o => o.Label.StartsWith(StrategyLabel)).ToList();
            Print($"Found {existingPendingOrders.Count} existing pending orders with strategy label");
            
            foreach (var order in existingPendingOrders)
            {
                activeTradeCount++;
                Print($"Existing pending order {activeTradeCount}: {order.Label}, Type: {order.OrderType}, Entry: {order.TargetPrice}");
            }
            
            _tradesTakenToday = activeTradeCount;
            Print($"=== TOTAL ACTIVE TRADES SET TO: {activeTradeCount} ===");
        }
        
        private void CancelPendingOrders()
        {
            var pendingOrders = PendingOrders.Where(o => o.Label.StartsWith(StrategyLabel)).ToList();
            Print($"Canceling {pendingOrders.Count} pending orders");
            
            foreach (var pendingOrder in pendingOrders)
            {
                Print($"Canceling pending order: {pendingOrder.Label}");
                pendingOrder.Cancel();
            }
            
            _pendingPatternOrders.Clear();
        }
        
        private void ClosePositionsEndOfDay()
        {
            var positions = Positions.Where(p => p.Label.StartsWith(StrategyLabel)).ToList();
            Print($"Closing {positions.Count} positions at end of day");
            
            foreach (var position in positions)
            {
                Print($"Closing position: {position.Label}, Current P/L: {position.NetProfit:C2}");
                position.Close();
            }
        }
        
        #endregion
        
        #region Risk Management
        
        private double CalculatePositionSize(double entryPrice, double stopLoss)
        {
            double riskInPoints = Math.Abs(entryPrice - stopLoss);
            
            Print($"Position sizing calculation:");
            Print($"  Entry: {entryPrice}, Stop Loss: {stopLoss}");
            Print($"  Risk in points: {riskInPoints}");
            
            if (riskInPoints < 0.000001)
            {
                Print($"Risk too small - using minimum volume");
                return Symbol.VolumeInUnitsMin;
            }
            
            double riskPerLot = riskInPoints * Symbol.PipValue;
            double calculatedSize = RiskAmountPerTrade / riskPerLot;
            
            Print($"  Risk per lot: {riskPerLot:C2}");
            Print($"  Calculated size: {calculatedSize}");
                        
            calculatedSize = Math.Round(calculatedSize, 1, MidpointRounding.AwayFromZero);
            calculatedSize = Math.Max(calculatedSize, Symbol.VolumeInUnitsMin);
            calculatedSize = Math.Min(calculatedSize, Symbol.VolumeInUnitsMax);
            
            Print($"  Final size: {calculatedSize} (min: {Symbol.VolumeInUnitsMin}, max: {Symbol.VolumeInUnitsMax})");
            
            return calculatedSize;
        }
        
        private bool MaxTradesReached()
        {
            bool maxReached = MaxTradesPerDay != -1 && _tradesTakenToday >= MaxTradesPerDay;
            if (maxReached)
            {
                Print($"Max trades reached: {_tradesTakenToday}/{MaxTradesPerDay}");
            }
            return maxReached;
        }
        
        private bool AdditionalTradeAllowed()
        {
            if (_tradesTakenToday == 0)
            {
                Print($"First trade of the day - allowed");
                return true;
            }
            
            if (AdditionalTradeCondition == AdditionalTradeConditionType.Aggressive)
            {
                Print($"Aggressive mode - additional trade allowed");
                return true;
            }
            
            var mostRecentTrade = FindMostRecentTrade();
            if (mostRecentTrade == null)
            {
                Print($"No recent trade found - additional trade allowed");
                return true;
            }
            
            // NEW LOGIC: Check current price against original entry price based on trade direction
            double currentPrice = Symbol.Bid; // Use bid for current price comparison
            double originalEntryPrice = mostRecentTrade.OriginalEntryPrice;
            bool isBullish = mostRecentTrade.IsBullish;
            
            bool priceConditionMet;
            if (isBullish)
            {
                // For bullish trades, current price should be >= original entry price
                priceConditionMet = currentPrice >= originalEntryPrice;
                Print($"Conservative mode - Bullish trade check: Current price {currentPrice} >= Original entry {originalEntryPrice} = {priceConditionMet}");
            }
            else
            {
                // For bearish trades, current price should be <= original entry price  
                priceConditionMet = currentPrice <= originalEntryPrice;
                Print($"Conservative mode - Bearish trade check: Current price {currentPrice} <= Original entry {originalEntryPrice} = {priceConditionMet}");
            }
            
            Print($"Conservative mode - Most recent trade: ID={mostRecentTrade.Id}, Original entry: {originalEntryPrice}, " +
                  $"Is bullish: {isBullish}, Current price: {currentPrice}, Additional trade allowed: {priceConditionMet}");
            
            return priceConditionMet;
        }
        
        private TradeRecord FindMostRecentTrade()
        {
            TradeRecord mostRecentTrade = null;
            DateTime mostRecentTime = DateTime.MinValue;
            
            Print($"DEBUG - Finding most recent trade from {_dailyTradeRecords.Count} daily records");
            
            // Update P/L for open positions
            foreach (var position in Positions.Where(p => p.Label.StartsWith(StrategyLabel)))
            {
                var record = _dailyTradeRecords.FirstOrDefault(r => r.Id == position.Id.ToString());
                if (record != null)
                {
                    record.NetProfit = position.NetProfit;
                    Print($"DEBUG - Updated open position {record.Id}: P/L = {record.NetProfit:C2}, Entry = {record.EntryTime}");
                    if (record.EntryTime > mostRecentTime)
                    {
                        mostRecentTrade = record;
                        mostRecentTime = record.EntryTime;
                        Print($"DEBUG - New most recent trade (open): {record.Id}");
                    }
                }
            }
            
            // Check all records if no open position found
            if (mostRecentTrade == null)
            {
                Print($"DEBUG - No open positions found, checking all daily records");
                mostRecentTrade = _dailyTradeRecords
                    .OrderByDescending(r => r.EntryTime)
                    .FirstOrDefault();
                
                if (mostRecentTrade != null)
                {
                    Print($"DEBUG - Most recent trade from records: {mostRecentTrade.Id}, P/L: {mostRecentTrade.NetProfit:C2}, Closed: {mostRecentTrade.IsClosed}");
                }
            }
            
            return mostRecentTrade;
        }
        
        #endregion
        
        #region Time Management
        
        private void CheckForNewDay()
        {
            var etTime = TimeZoneInfo.ConvertTimeFromUtc(Server.Time, _nyZone);
            var today = etTime.Date;
            
            if (today != _currentTradingDay)
            {
                Print($"=== NEW TRADING DAY DETECTED: {today.ToShortDateString()} ===");
                _currentTradingDay = today;
                
                // Check for new year and reset trading suspension
                if (etTime.Year != _currentYear)
                {
                    Print($"=== NEW YEAR DETECTED: {etTime.Year} ===");
                    _currentYear = etTime.Year;
                    _q1PerformanceChecked = false;
                    
                    if (_tradingSuspendedForYear)
                    {
                        Print($"Resetting trading suspension for new year");
                        _tradingSuspendedForYear = false;
                        _strategyStartDate = new DateTime(_currentYear, 1, 1);
                        _allTimeTradeRecords.Clear();
                    }
                    else
                    {
                        _strategyStartDate = new DateTime(_currentYear, 1, 1);
                    }
                }
                
                CheckPerformanceProtection();
                CancelPendingOrders();
                ResetDailyVariables();
                CheckForExistingOrders();
                
                Print($"=== DAY INITIALIZATION COMPLETE ===");
            }
        }
        
        private void ResetDailyVariables()
        {
            Print($"Resetting daily variables...");
            
            int previousBars = _dayBars.Count;
            int previousPatterns = _processedPatterns.Count;
            int previousTrades = _tradesTakenToday;
            
            _dayBars.Clear();
            _dailyInitDone = false;
            _tradesTakenToday = 0;
            _currentGap = GapType.None;
            _entryLimitCheckDone = false;
            _processedPatterns.Clear();
            _dailyTradeRecords.Clear();
            _pendingPatternOrders.Clear();
            _patternOrdersByLabel.Clear();
            
            Print($"Reset complete - Previous: {previousBars} bars, {previousPatterns} patterns, {previousTrades} trades");
        }
        
        private void CheckEntryTimeLimit()
        {
            if (_entryLimitCheckDone) return;
            
            var etTime = TimeZoneInfo.ConvertTimeFromUtc(Server.Time, _nyZone);
            if (etTime.TimeOfDay >= _entryLimit)
            {
                Print($"Entry time limit reached at {etTime.TimeOfDay} - canceling pending orders");
                CancelPendingOrders();
                _entryLimitCheckDone = true;
            }
        }
        
        private bool IsWithinRTH(TimeSpan currentTimeOfDay)
        {
            return currentTimeOfDay >= _rthStart && currentTimeOfDay <= _rthEnd;
        }
        
        private bool IsOutsideSession(TimeSpan currentTimeOfDay, TimeSpan sessionEnd)
        {
            return currentTimeOfDay >= sessionEnd;
        }
        
        #endregion
    }
}
