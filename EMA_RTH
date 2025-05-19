using System;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Indicators
{
    public enum PriceType { Open, High, Low, Close, Median, Typical, Weighted }

    [Indicator(IsOverlay = true, TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class EMA_RTH : Indicator
    {
        [Parameter("EMA Period",          DefaultValue = 20)]
        public int Period    { get; set; }

        [Parameter("Source",              DefaultValue = PriceType.Close)]
        public PriceType Source { get; set; }

        [Parameter("Offset (bars)",       DefaultValue = 0)]
        public int Offset    { get; set; }

        [Parameter("Session Start (HH:mm ET)", DefaultValue = "09:30")]
        public string SessionStart { get; set; }

        [Parameter("Session End   (HH:mm ET)", DefaultValue = "16:10")]
        public string SessionEnd   { get; set; }

        [Output("RTH EMA", LineColor = "Orange", PlotType = PlotType.Line)]
        public IndicatorDataSeries RthEmaSeries { get; set; }

        private TimeSpan     _rthStart, _rthEnd;
        private double       _multiplier, _lastEma;
        private bool         _seeded;
        private TimeZoneInfo _nyZone;

        protected override void Initialize()
        {
            _rthStart   = TimeSpan.Parse(SessionStart);
            _rthEnd     = TimeSpan.Parse(SessionEnd);
            _multiplier = 2.0 / (Period + 1);
            _nyZone     = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            _seeded     = false;
        }

        public override void Calculate(int index)
        {
            // get this bar’s price
            double price = GetPrice(index);

            // only update EMA if THIS bar is in RTH
            if (IsRthBar(index))
            {
                if (!_seeded)
                {
                    _lastEma = price;
                    _seeded  = true;
                }
                else
                {
                    _lastEma = (_multiplier * (price - _lastEma)) + _lastEma;
                }
                PlotEma(index, _lastEma);
            }
            else
            {
                // clear any previous point at this index (in case of Offset overlap)
                PlotEma(index, double.NaN);
            }
        }

        // returns true if bar at `idx` falls inside your ET session window
        private bool IsRthBar(int idx)
        {
            var utc    = Bars.OpenTimes[idx];
            var nyTime = TimeZoneInfo.ConvertTimeFromUtc(utc, _nyZone).TimeOfDay;
            return nyTime >= _rthStart && nyTime <= _rthEnd;
        }

        private double GetPrice(int i)
        {
            switch (Source)
            {
                case PriceType.Open:     return Bars.OpenPrices[i];
                case PriceType.High:     return Bars.HighPrices[i];
                case PriceType.Low:      return Bars.LowPrices[i];
                case PriceType.Close:    return Bars.ClosePrices[i];
                case PriceType.Median:   return (Bars.HighPrices[i] + Bars.LowPrices[i]) / 2;
                case PriceType.Typical:  return (Bars.HighPrices[i] + Bars.LowPrices[i] + Bars.ClosePrices[i]) / 3;
                case PriceType.Weighted: return (Bars.HighPrices[i] + Bars.LowPrices[i] + 2 * Bars.ClosePrices[i]) / 4;
                default:                 return Bars.ClosePrices[i];
            }
        }

        // only plots if the *target* bar is inside RTH
        private void PlotEma(int idx, double value)
        {
            int target = idx + Offset;
            if (target >= 0 && target < Bars.Count && IsRthBar(target))
                RthEmaSeries[target] = value;
            else if (target >= 0 && target < Bars.Count)
                RthEmaSeries[target] = double.NaN;
        }
    }
}
