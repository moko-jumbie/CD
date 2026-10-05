namespace cAlgo.Indicators;

public partial class MACDdivergence : Indicator
{
    private enum SignalKind
    {
        RegularBullish,
        RegularBearish,
        HiddenBullish,
        HiddenBearish,
        BullishConvergence,
        BearishConvergence
    }

    private sealed class SwingPoint
    {
        public int Index;
        public DateTime Time;
        public double Price;
        public double Macd;
        public bool IsHigh;
    }

    private const string ObjectPrefix = "MACDDiv";
    private const double ArrowOffset = 0.6;
    private const double LabelOffset = 1.8;

    private static readonly Color PivotColor = Color.Magenta;
    private static readonly Color PreviousDayLevelColor = Color.Gold;
    private static readonly Color FormingColor = Color.DarkGray;

    private MacdCrossOver _macd;
    private readonly List<SwingPoint> _swingHighs = new List<SwingPoint>();
    private readonly List<SwingPoint> _swingLows = new List<SwingPoint>();
    private readonly HashSet<string> _chartObjects = new HashSet<string>();
    private readonly HashSet<string> _indicatorObjects = new HashSet<string>();
    private readonly List<string> _levelObjects = new List<string>();
    private int _lastDetectionIndex = -1;
    private string _formingHighId;
    private string _formingLowId;

    protected override void Initialize()
    {
        // The accessor takes the long cycle first, then the short cycle.
        _macd = Indicators.MacdCrossOver(Bars.ClosePrices, SlowPeriod, FastPeriod, SignalPeriod);
    }

    protected override void OnDestroy()
    {
        foreach (var name in _chartObjects)
            Chart.RemoveObject(name);

        foreach (var name in _levelObjects)
            Chart.RemoveObject(name);

        if (IndicatorArea == null)
            return;

        foreach (var name in _indicatorObjects)
            IndicatorArea.RemoveObject(name);
    }

    public override void Calculate(int index)
    {
        MacdLine[index] = _macd.MACD[index];
        SignalLine[index] = _macd.Signal[index];
        Histogram[index] = _macd.Histogram[index];

        if (index == _lastDetectionIndex)
            return;

        // A lower index means the series was recalculated from scratch, so the
        // remembered swings no longer describe the bars now being replayed.
        if (index < _lastDetectionIndex)
            ResetSwings();

        _lastDetectionIndex = index;

        DetectSwing(index);

        if (index == Bars.Count - 1)
        {
            DrawDailyLevels(index);
            UpdateForming(index);
        }
    }

    private void ResetSwings()
    {
        _swingHighs.Clear();
        _swingLows.Clear();

        // The remembered forming ids refer to swings of the discarded series.
        _formingHighId = null;
        _formingLowId = null;
    }

    private void DetectSwing(int index)
    {
        // The pivot needs PivotBars closed bars on both sides, so it is only
        // confirmed one bar after the last bar of its window has closed.
        var pivot = index - PivotBars - 1;
        if (pivot < PivotBars)
            return;

        var isHigh = IsPivotHigh(pivot);
        var isLow = IsPivotLow(pivot);

        if (isHigh)
            RegisterSwing(CreateSwing(pivot, true), _swingHighs);

        if (isLow)
            RegisterSwing(CreateSwing(pivot, false), _swingLows);
    }

    private SwingPoint CreateSwing(int index, bool isHigh)
    {
        return new SwingPoint
        {
            Index = index,
            Time = Bars.OpenTimes[index],
            Price = isHigh ? Bars.HighPrices[index] : Bars.LowPrices[index],
            Macd = _macd.MACD[index],
            IsHigh = isHigh
        };
    }

    private bool IsPivotHigh(int pivot)
    {
        var value = Bars.HighPrices[pivot];

        for (var i = pivot - PivotBars; i <= pivot + PivotBars; i++)
            if (Bars.HighPrices[i] > value)
                return false;

        return true;
    }

    private bool IsPivotLow(int pivot)
    {
        var value = Bars.LowPrices[pivot];

        for (var i = pivot - PivotBars; i <= pivot + PivotBars; i++)
            if (Bars.LowPrices[i] < value)
                return false;

        return true;
    }

    private void RegisterSwing(SwingPoint current, List<SwingPoint> swings)
    {
        string drawnId = null;

        if (swings.Count > 0)
        {
            var previous = swings[swings.Count - 1];
            var kind = Classify(previous, current);

            if (kind.HasValue && ShouldShow(current, kind.Value))
            {
                drawnId = SignalId(previous, current);
                DrawSignal(previous, current, kind.Value, GetColor(kind.Value));
            }
        }

        // The confirmed swing supersedes the grey signal that was forming for
        // this side; keep it only when it is the very pair just recoloured.
        var formingId = current.IsHigh ? _formingHighId : _formingLowId;
        if (formingId != null && formingId != drawnId)
            RemoveSignalObjects(formingId);

        if (current.IsHigh)
            _formingHighId = null;
        else
            _formingLowId = null;

        swings.Add(current);
    }

    private static SignalKind? Classify(SwingPoint previous, SwingPoint current)
    {
        if (current.Price == previous.Price || current.Macd == previous.Macd)
            return null;

        var priceUp = current.Price > previous.Price;
        var macdUp = current.Macd > previous.Macd;
        var agrees = priceUp == macdUp;

        if (agrees)
            return priceUp ? SignalKind.BullishConvergence : SignalKind.BearishConvergence;

        if (current.IsHigh)
            return priceUp ? SignalKind.RegularBearish : SignalKind.HiddenBearish;

        return priceUp ? SignalKind.HiddenBullish : SignalKind.RegularBullish;
    }

    private bool ShouldShow(SwingPoint current, SignalKind kind)
    {
        switch (kind)
        {
            case SignalKind.RegularBullish:
            case SignalKind.RegularBearish:
                return ShowRegularDivergences;
            case SignalKind.HiddenBullish:
            case SignalKind.HiddenBearish:
                return ShowHiddenDivergences;
            case SignalKind.BullishConvergence:
                return ShowConvergences
                    && (!ShowBullishConvergenceOnHighsOnly || current.IsHigh);
            default:
                return ShowConvergences
                    && (!ShowBearishConvergenceOnLowsOnly || !current.IsHigh);
        }
    }

    private void DrawSignal(SwingPoint previous, SwingPoint current, SignalKind kind, Color color)
    {
        var id = SignalId(previous, current);

        if (ShowPriceLines)
        {
            var priceLine = $"{ObjectPrefix}_Price_{id}";
            Chart.DrawTrendLine(priceLine, previous.Time, previous.Price, current.Time, current.Price, color, 2);
            _chartObjects.Add(priceLine);

            var arrow = $"{ObjectPrefix}_Arrow_{id}";
            var arrowType = IsBullish(kind) ? ChartIconType.UpArrow : ChartIconType.DownArrow;
            Chart.DrawIcon(arrow, arrowType, current.Time, OffsetFromSwing(current, ArrowOffset), color);
            _chartObjects.Add(arrow);

            var label = $"{ObjectPrefix}_Label_{id}";
            Chart.DrawText(label, GetLabel(kind), current.Time, OffsetFromSwing(current, LabelOffset), color);
            _chartObjects.Add(label);
        }

        if (ShowOscillatorLines && IndicatorArea != null)
        {
            var oscillatorLine = $"{ObjectPrefix}_Osc_{id}";
            IndicatorArea.DrawTrendLine(oscillatorLine, previous.Time, previous.Macd, current.Time, current.Macd, color, 2);
            _indicatorObjects.Add(oscillatorLine);
        }
    }

    private static string SignalId(SwingPoint previous, SwingPoint current)
    {
        return $"{(current.IsHigh ? "H" : "L")}{previous.Index}_{current.Index}";
    }

    private void RemoveSignalObjects(string id)
    {
        if (ShowPriceLines)
        {
            foreach (var name in new[]
            {
                $"{ObjectPrefix}_Price_{id}",
                $"{ObjectPrefix}_Arrow_{id}",
                $"{ObjectPrefix}_Label_{id}"
            })
            {
                Chart.RemoveObject(name);
                _chartObjects.Remove(name);
            }
        }

        if (ShowOscillatorLines && IndicatorArea != null)
        {
            var oscillatorLine = $"{ObjectPrefix}_Osc_{id}";
            IndicatorArea.RemoveObject(oscillatorLine);
            _indicatorObjects.Remove(oscillatorLine);
        }
    }

    private void UpdateForming(int index)
    {
        UpdateFormingSide(index, true, _swingHighs);
        UpdateFormingSide(index, false, _swingLows);
    }

    private void UpdateFormingSide(int index, bool isHigh, List<SwingPoint> swings)
    {
        var formingId = isHigh ? _formingHighId : _formingLowId;
        string newId = null;

        if (swings.Count > 0)
        {
            var previous = swings[swings.Count - 1];
            var candidate = FindFormingCandidate(index, isHigh, previous.Index);

            if (candidate != null)
            {
                var kind = Classify(previous, candidate);

                if (kind.HasValue && ShouldShow(candidate, kind.Value))
                {
                    newId = SignalId(previous, candidate);

                    // Same pair as before: the grey objects are already on the chart.
                    if (newId == formingId)
                        return;

                    DrawSignal(previous, candidate, kind.Value, FormingColor);
                }
            }
        }

        if (formingId != null && formingId != newId)
            RemoveSignalObjects(formingId);

        if (isHigh)
            _formingHighId = newId;
        else
            _formingLowId = newId;
    }

    private SwingPoint FindFormingCandidate(int index, bool isHigh, int previousIndex)
    {
        // A pivot is only ever registered after index - PivotBars - 1 has been
        // tested, so the bars that can still confirm run from index - PivotBars
        // to the last closed bar; anything older has already passed or failed.
        var from = Math.Max(previousIndex + PivotBars + 1, index - PivotBars);
        var to = index - 1;

        for (var candidate = from; candidate <= to; candidate++)
            if (IsExtremeThrough(candidate, isHigh, index - 1))
                return CreateSwing(candidate, isHigh);

        return null;
    }

    private bool IsExtremeThrough(int candidate, bool isHigh, int lastClosed)
    {
        if (isHigh)
        {
            var value = Bars.HighPrices[candidate];

            for (var i = candidate - PivotBars; i <= lastClosed; i++)
                if (Bars.HighPrices[i] > value)
                    return false;

            return true;
        }
        else
        {
            var value = Bars.LowPrices[candidate];

            for (var i = candidate - PivotBars; i <= lastClosed; i++)
                if (Bars.LowPrices[i] < value)
                    return false;

            return true;
        }
    }

    private double OffsetFromSwing(SwingPoint swing, double factor)
    {
        var range = Bars.HighPrices[swing.Index] - Bars.LowPrices[swing.Index];
        if (range <= 0)
            range = Symbol.TickSize;

        // Swing highs are extended upwards, swing lows downwards.
        var direction = swing.IsHigh ? 1.0 : -1.0;
        return swing.Price + direction * range * factor;
    }

    private static bool IsBullish(SignalKind kind)
    {
        return kind == SignalKind.RegularBullish
            || kind == SignalKind.HiddenBullish
            || kind == SignalKind.BullishConvergence;
    }

    private static Color GetColor(SignalKind kind)
    {
        switch (kind)
        {
            case SignalKind.RegularBullish:
                return Color.Lime;
            case SignalKind.RegularBearish:
                return Color.Red;
            case SignalKind.HiddenBullish:
                return Color.Cyan;
            case SignalKind.HiddenBearish:
                return Color.DarkOrange;
            case SignalKind.BullishConvergence:
                return Color.DarkSeaGreen;
            default:
                return Color.IndianRed;
        }
    }

    private static string GetLabel(SignalKind kind)
    {
        switch (kind)
        {
            case SignalKind.RegularBullish:
                return "Bull Divergence";
            case SignalKind.RegularBearish:
                return "Bear Divergence";
            case SignalKind.HiddenBullish:
                return "Hidden Bull";
            case SignalKind.HiddenBearish:
                return "Hidden Bear";
            case SignalKind.BullishConvergence:
                return "Bull Convergence";
            default:
                return "Bear Convergence";
        }
    }

    private sealed class DaySlice
    {
        public DateTime Date;
        public DateTime StartTime;
        public double High;
        public double Low;
        public double Close;
    }

    private List<DaySlice> CollectDays(int lastIndex, int maxDays)
    {
        var days = new List<DaySlice>();
        DaySlice current = null;

        for (var i = lastIndex; i >= 0 && days.Count < maxDays; i--)
        {
            var time = Bars.OpenTimes[i];
            var date = time.Date;

            if (current == null || current.Date != date)
            {
                current = new DaySlice
                {
                    Date = date,
                    StartTime = time,
                    High = Bars.HighPrices[i],
                    Low = Bars.LowPrices[i],
                    Close = Bars.ClosePrices[i]
                };
                days.Add(current);
            }
            else
            {
                current.StartTime = time;

                var high = Bars.HighPrices[i];
                var low = Bars.LowPrices[i];

                if (high > current.High)
                    current.High = high;
                if (low < current.Low)
                    current.Low = low;
            }
        }

        return days;
    }

    private void DrawDailyLevels(int lastIndex)
    {
        foreach (var name in _levelObjects)
            Chart.RemoveObject(name);

        _levelObjects.Clear();

        if ((!ShowPivotPoints && !ShowPreviousDayLevels) || PivotDays <= 0 || lastIndex < 1 || Chart == null)
            return;

        var days = CollectDays(lastIndex, PivotDays + 2);
        var targetCount = Math.Min(PivotDays, days.Count - 1);

        for (var k = 0; k < targetCount; k++)
        {
            var day = days[k];
            var previous = days[k + 1];
            var dayEnd = day.Date.AddDays(1);

            if (ShowPreviousDayLevels)
            {
                DrawLevelLine(day.StartTime, dayEnd, day.Date, "PDH", previous.High, PreviousDayLevelColor, 2);
                DrawLevelLine(day.StartTime, dayEnd, day.Date, "PDL", previous.Low, PreviousDayLevelColor, 2);
            }

            if (!ShowPivotPoints)
                continue;

            var pivot = (previous.High + previous.Low + previous.Close) / 3;
            var range = previous.High - previous.Low;

            DrawLevelLine(day.StartTime, dayEnd, day.Date, "P", pivot, PivotColor, 1);
            DrawLevelLine(day.StartTime, dayEnd, day.Date, "R1", 2 * pivot - previous.Low, PivotColor, 1);
            DrawLevelLine(day.StartTime, dayEnd, day.Date, "R2", pivot + range, PivotColor, 1);
            DrawLevelLine(day.StartTime, dayEnd, day.Date, "R3", previous.High + 2 * (pivot - previous.Low), PivotColor, 1);
            DrawLevelLine(day.StartTime, dayEnd, day.Date, "S1", 2 * pivot - previous.High, PivotColor, 1);
            DrawLevelLine(day.StartTime, dayEnd, day.Date, "S2", pivot - range, PivotColor, 1);
            DrawLevelLine(day.StartTime, dayEnd, day.Date, "S3", previous.Low - 2 * (previous.High - pivot), PivotColor, 1);
        }
    }

    private void DrawLevelLine(DateTime start, DateTime end, DateTime date, string level, double price, Color color, int thickness)
    {
        var name = $"{ObjectPrefix}_Level_{date:yyyyMMdd}_{level}";
        Chart.DrawTrendLine(name, start, price, end, price, color, thickness);
        _levelObjects.Add(name);
    }
}
