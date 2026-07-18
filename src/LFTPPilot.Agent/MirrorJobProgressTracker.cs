using System.Collections.Immutable;
using LFTPPilot.Core;

namespace LFTPPilot.Agent;

internal sealed class MirrorJobProgressTracker
{
    private readonly object _gate = new();
    private readonly IMirrorPlanner _planner;
    private readonly MirrorDefinition _definition;
    private readonly Dictionary<MirrorAction, int> _remaining;
    private readonly Action<double, string> _report;
    private readonly int _total;
    private readonly double _start;
    private readonly double _span;
    private int _completed;
    private int _lastBucket = -1;

    public MirrorJobProgressTracker(
        IMirrorPlanner planner,
        MirrorDefinition definition,
        ImmutableArray<MirrorAction> reviewedActions,
        double start,
        double span,
        Action<double, string> report)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(report);
        if (reviewedActions.IsDefault) throw new ArgumentException("Reviewed mirror actions are required.", nameof(reviewedActions));
        if (!double.IsFinite(start) || !double.IsFinite(span) || start is < 0 or >= 1 || span <= 0 || start + span >= 1)
            throw new ArgumentOutOfRangeException(nameof(span), "The mirror progress range must remain within a running job.");

        _planner = planner;
        _definition = definition;
        _start = start;
        _span = span;
        _report = report;
        _total = reviewedActions.Length;
        _remaining = [];
        foreach (var action in reviewedActions)
            _remaining[action] = _remaining.GetValueOrDefault(action) + 1;
    }

    public void Observe(object? sender, LftpOutputLine output)
    {
        try { ObserveCore(output); }
        catch (Exception exception) when (!IsFatalRuntimeException(exception)) { }
    }

    private void ObserveCore(LftpOutputLine output)
    {
        if (_total == 0 || string.IsNullOrWhiteSpace(output.Line)) return;
        var parsed = _planner.CreatePreview(_definition, [output.Line]);
        var action = parsed.Actions.Length == 1 ? parsed.Actions[0] : null;
        if (action is null) return;

        double progress;
        string status;
        lock (_gate)
        {
            if (!_remaining.TryGetValue(action, out var count) || count == 0) return;
            _remaining[action] = count - 1;
            _completed++;
            var bucket = (int)Math.Floor((double)_completed * 100 / _total);
            if (bucket <= _lastBucket) return;
            _lastBucket = bucket;
            progress = _start + _span * _completed / _total;
            status = $"Applied {_completed:N0} of {_total:N0} reviewed mirror actions.";
        }
        _report(progress, status);
    }

    private static bool IsFatalRuntimeException(Exception exception) => exception is
        OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException;
}
