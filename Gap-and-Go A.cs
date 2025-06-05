using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    /// <summary>
    /// Simplified RTH Gap-and-Go strategy.
    /// Identifies a gap relative to the EMA at market open and trades a basic
    /// three‑bar continuation pattern in the direction of the gap.
    /// </summary>
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class RthGapStrategy : Robot
    {
        #region Parameters
        [Parameter("EMA Period", DefaultValue = 20)]
        public int EmaPeriod { get; set; }


        [Parameter("Trading Session (ET)", DefaultValue = "09:30-16:15")]
        public string Session { get; set; }

        [Parameter("Entry Limit (ET)", DefaultValue = "14:30")]
        public string EntryLimit { get; set; }

        [Parameter("Max Trades", DefaultValue = 5)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Risk per Trade ($)", DefaultValue = 100.0)]
        public double RiskPerTrade { get; set; }

        [Parameter("Performance Protection", DefaultValue = true)]
        public bool EnablePerformanceProtection { get; set; }

        [Parameter("Min Profit Factor (Q1)", DefaultValue = 0.8)]
        public double MinProfitFactor { get; set; }

        [Parameter("Min Avg Trade (Q1)", DefaultValue = 0.0)]
        public double MinAverageTrade { get; set; }
        #endregion

        #region Fields
        private ExponentialMovingAverage _ema;
        private TimeSpan _sessionStart, _sessionEnd, _entryLimitTime;
        private TimeZoneInfo _nyZone;
        private int _tradesToday;
        private GapType _currentGap = GapType.None;
        private bool _gapIdentified;
        private DateTime _currentDay = DateTime.MinValue;
        private readonly List<TradeInfo> _tradeHistory = new();
        private readonly List<BarInfo> _bars = new();
        private bool _tradingSuspended;
        #endregion

        #region Nested Types
        private enum GapType { None, Up, Down }

        private class TradeInfo
        {
            public DateTime Time { get; set; }
            public double Profit { get; set; }
        }

        private class BarInfo
        {
            public double Open, High, Low, Close;
            public DateTime Time;
        }
        #endregion

        protected override void OnStart()
        {
            _ema = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaPeriod);
            ParseTimes();
            _nyZone = GetEasternTimeZone();
        }

        private void ParseTimes()
        {
            var parts = Session.Split('-');
            _sessionStart = TimeSpan.Parse(parts[0]);
            _sessionEnd = TimeSpan.Parse(parts[1]);
            _entryLimitTime = TimeSpan.Parse(EntryLimit);
        }

        private TimeZoneInfo GetEasternTimeZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { return TimeZoneInfo.CreateCustomTimeZone("ET", new TimeSpan(-5,0,0), "ET", "ET"); }
        }

        protected override void OnBar()
        {
            var barTimeEt = TimeZoneInfo.ConvertTimeFromUtc(Bars.LastBar.OpenTime, _nyZone);
            if (barTimeEt.Date != _currentDay) StartNewDay(barTimeEt.Date);

            if (!IsWithinSession(barTimeEt.TimeOfDay)) return;
            if (_tradesToday >= MaxTradesPerDay) return;
            if (barTimeEt.TimeOfDay >= _entryLimitTime) return;

            _bars.Add(new BarInfo
            {
                Open = Bars.LastBar.Open,
                High = Bars.LastBar.High,
                Low = Bars.LastBar.Low,
                Close = Bars.LastBar.Close,
                Time = barTimeEt
            });

            if (!_gapIdentified)
            {
                IdentifyGap();
                return;
            }

            if (_bars.Count < 3) return;
            var last = _bars[^1];
            var prev1 = _bars[^2];
            var prev2 = _bars[^3];

            if (_currentGap == GapType.Up && IsBullishPattern(prev2, prev1, last))
                ExecuteTrade(prev2, prev1, last, TradeType.Buy);
            else if (_currentGap == GapType.Down && IsBearishPattern(prev2, prev1, last))
                ExecuteTrade(prev2, prev1, last, TradeType.Sell);
        }

        private bool IsWithinSession(TimeSpan time) => time >= _sessionStart && time <= _sessionEnd;

        private void StartNewDay(DateTime day)
        {
            _currentDay = day;
            _bars.Clear();
            _gapIdentified = false;
            _currentGap = GapType.None;
            _tradesToday = 0;
            CheckPerformanceProtection();
        }

        private void IdentifyGap()
        {
            var ema = _ema.Result.LastValue;
            var open = Bars.LastBar.Open;
            var high = Bars.LastBar.High;
            var low = Bars.LastBar.Low;
            if (low > ema) _currentGap = GapType.Up;
            else if (high < ema) _currentGap = GapType.Down;
            else _currentGap = GapType.None;
            _gapIdentified = true;
        }

        private static bool IsBullishPattern(BarInfo b1, BarInfo b2, BarInfo b3)
        {
            return b1.Close > b1.Open &&
                   b2.Close > b2.Open && b2.Close > b1.High && b2.Low > b1.Low &&
                   b3.Close > b3.Open && b3.Close > b2.High && b3.Low > b2.Low;
        }

        private static bool IsBearishPattern(BarInfo b1, BarInfo b2, BarInfo b3)
        {
            return b1.Close < b1.Open &&
                   b2.Close < b2.Open && b2.Close < b1.Low && b2.High < b1.High &&
                   b3.Close < b3.Open && b3.Close < b2.Low && b3.High < b2.High;
        }

        private void ExecuteTrade(BarInfo b1, BarInfo b2, BarInfo b3, TradeType type)
        {
            if (_tradesToday >= MaxTradesPerDay) return;

            double entry = type == TradeType.Buy ? b3.High + Symbol.PipSize * 2 : b3.Low - Symbol.PipSize * 2;
            double stop = type == TradeType.Buy ? b2.Low - Symbol.PipSize : b2.High + Symbol.PipSize;
            double take = type == TradeType.Buy ? b3.High + (b3.High - b1.Low) : b3.Low - (b1.High - b3.Low);
            double volume = CalculateVolume(entry, stop);

            var result = PlaceStopOrder(type, Symbol.Name, volume, entry, $"GapGo {Guid.NewGuid()}");
            if (result.IsSuccessful)
            {
                var slPips = Math.Abs(entry - stop) / Symbol.PipSize;
                var tpPips = Math.Abs(take - entry) / Symbol.PipSize;
                result.PendingOrder?.ModifyStopLossPips(slPips);
                result.PendingOrder?.ModifyTakeProfitPips(tpPips);
                result.Position?.ModifyStopLossPrice(stop);
                result.Position?.ModifyTakeProfitPrice(take);
                _tradesToday++;
            }
        }

        private double CalculateVolume(double entry, double stop)
        {
            var risk = Math.Abs(entry - stop) * Symbol.PipValue;
            if (risk <= 0) return Symbol.VolumeInUnitsMin;
            var volume = RiskPerTrade / risk;
            volume = Math.Round(volume, 1, MidpointRounding.AwayFromZero);
            volume = Math.Max(volume, Symbol.VolumeInUnitsMin);
            volume = Math.Min(volume, Symbol.VolumeInUnitsMax);
            return volume;
        }

        protected override void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (!args.Position.Label.StartsWith("GapGo")) return;
            _tradeHistory.Add(new TradeInfo { Time = args.Position.EntryTime, Profit = args.Position.NetProfit });
        }

        private void CheckPerformanceProtection()
        {
            if (!EnablePerformanceProtection || _tradingSuspended) return;
            var today = TimeZoneInfo.ConvertTimeFromUtc(Server.Time, _nyZone).Date;
            if (today < new DateTime(today.Year, 4, 1)) return;

            var yearTrades = _tradeHistory.Where(t => t.Time.Year == today.Year).ToList();
            if (yearTrades.Count == 0) return;

            double grossProfit = yearTrades.Where(t => t.Profit > 0).Sum(t => t.Profit);
            double grossLoss = yearTrades.Where(t => t.Profit < 0).Sum(t => Math.Abs(t.Profit));
            double profitFactor = grossLoss == 0 ? 999 : grossProfit / grossLoss;
            double averageTrade = yearTrades.Average(t => t.Profit);

            if (profitFactor < MinProfitFactor && averageTrade < MinAverageTrade)
                _tradingSuspended = true;
        }
    }
}
