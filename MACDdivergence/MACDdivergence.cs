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

    private MacdCrossOver _macd;
    private readonly List<SwingPoint> _swingHighs = new List<SwingPoint>();
    private readonly List<SwingPoint> _swingLows = new List<SwingPoint>();
    private readonly HashSet<string> _chartObjects = new HashSet<string>();
    private readonly HashSet<string> _indicatorObjects = new HashSet<string>();
    private int _lastDetectionIndex = -1;

    protected override void Initialize()
    {
        // The accessor takes the long cycle first, then the short cycle.
        _macd = Indicators.MacdCrossOver(Bars.ClosePrices, SlowPeriod, FastPeriod, SignalPeriod);
    }

    protected override void OnDestroy()
    {
        foreach (var name in _chartObjects)
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
    }

    private void ResetSwings()
    {
        _swingHighs.Clear();
        _swingLows.Clear();
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
        if (swings.Count > 0)
        {
            var previous = swings[swings.Count - 1];
            var kind = Classify(previous, current);

            if (kind.HasValue && ShouldShow(current, kind.Value))
                DrawSignal(previous, current, kind.Value);
        }

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

    private void DrawSignal(SwingPoint previous, SwingPoint current, SignalKind kind)
    {
        var color = GetColor(kind);
        var id = $"{(current.IsHigh ? "H" : "L")}{previous.Index}_{current.Index}";

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
}
