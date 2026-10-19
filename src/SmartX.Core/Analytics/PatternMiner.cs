namespace SmartX.Core.Analytics;

public sealed class PatternMinerOptions
{
    /// <summary>An action is linked to every context that occurred within this window before it.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Learned counts halve after this long, so the engine adapts when habits change.</summary>
    public TimeSpan HalfLife { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Minimum (decayed) number of supporting observations before a rule is trusted.</summary>
    public double MinSupport { get; set; } = 2.0;

    /// <summary>Minimum confidence P(action | context) before a rule is suggested.</summary>
    public double MinConfidence { get; set; } = 0.3;

    /// <summary>Two operator actions this close together count as a sequence for the Markov model.</summary>
    public TimeSpan SequenceGap { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>A ranked recommendation produced by the miner.</summary>
public sealed record MinedRecommendation(
    string Action,
    double Score,
    double Confidence,
    double Support,
    double Lift,
    string Source,
    string[] Triggers);

/// <summary>A learned context → action rule.</summary>
public sealed record MinedRule(string Context, string Action, double Support, double Confidence, double Lift);

/// <summary>
/// Learns what operators do after something happens, and what they do after each other.
/// <list type="number">
/// <item><b>Context → action association rules</b>: when an operator acts (searches, issues a command),
/// every context event (an alert, a threshold breach) seen in the preceding window gets credit.
/// For each rule the miner keeps support, confidence = P(action | context) and lift, the standard
/// measures from association-rule mining.</item>
/// <item><b>First-order Markov chain</b> over consecutive operator actions, e.g.
/// "search Pump A" is usually followed by "turn Pump A on".</item>
/// </list>
/// Data structures: a <see cref="Queue{T}"/> holds the sliding window of recent contexts (oldest leaves
/// first), nested <see cref="Dictionary{TKey,TValue}"/>s hold the sparse rule counts with O(1) updates,
/// a <see cref="HashSet{T}"/> on each context occurrence stops one occurrence being credited twice,
/// and a bounded <see cref="PriorityQueue{TElement,TPriority}"/> selects the top-k recommendations in
/// O(n log k). Not thread-safe; callers synchronise.
/// </summary>
public sealed class PatternMiner
{
    private readonly PatternMinerOptions _options;
    private readonly Queue<ContextOccurrence> _window = new();
    private readonly Dictionary<string, DecayingCounter> _contextCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DecayingCounter> _actionCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, DecayingCounter>> _rules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, DecayingCounter>> _transitions = new(StringComparer.Ordinal);
    private DecayingCounter _totalActions;
    private string? _lastAction;
    private DateTimeOffset _lastActionAt;

    public PatternMiner(PatternMinerOptions? options = null)
    {
        _options = options ?? new PatternMinerOptions();
    }

    public PatternMinerOptions Options => _options;

    public long ContextsObserved { get; private set; }

    public long ActionsObserved { get; private set; }

    public int RuleCount
    {
        get
        {
            int count = 0;
            foreach (Dictionary<string, DecayingCounter> actions in _rules.Values)
            {
                count += actions.Count;
            }

            return count;
        }
    }

    public string? LastAction => _lastAction;

    /// <summary>Records that a context (for example "CriticalMoisture@sz-b") started at <paramref name="at"/>.</summary>
    public void ObserveContext(string context, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        Evict(at);
        Increment(_contextCounts, context, at);
        _window.Enqueue(new ContextOccurrence(context, at));
        ContextsObserved++;
    }

    /// <summary>Records an operator action and credits every context in the preceding window.</summary>
    public void ObserveAction(string action, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        Evict(at);

        foreach (ContextOccurrence occurrence in _window)
        {
            if (occurrence.At <= at && occurrence.Credited.Add(action))
            {
                Increment(GetOrAdd(_rules, occurrence.Context), action, at);
            }
        }

        if (_lastAction is not null &&
            !string.Equals(_lastAction, action, StringComparison.Ordinal) &&
            at - _lastActionAt <= _options.SequenceGap &&
            at >= _lastActionAt)
        {
            Increment(GetOrAdd(_transitions, _lastAction), action, at);
        }

        Increment(_actionCounts, action, at);
        _totalActions.Add(1, at, _options.HalfLife);
        _lastAction = action;
        _lastActionAt = at;
        ActionsObserved++;
    }

    /// <summary>
    /// Ranks actions for the contexts that are active right now, plus the action most likely to follow
    /// the operator's last action.
    /// </summary>
    public List<MinedRecommendation> Recommend(IReadOnlyCollection<string> activeContexts, DateTimeOffset now, int top = 5)
    {
        var best = new Dictionary<string, MinedRecommendation>(StringComparer.Ordinal);
        double total = Math.Max(1, _totalActions.ValueAt(now, _options.HalfLife));

        foreach (string context in activeContexts)
        {
            if (!_rules.TryGetValue(context, out Dictionary<string, DecayingCounter>? actions) ||
                !_contextCounts.TryGetValue(context, out DecayingCounter contextCounter))
            {
                continue;
            }

            double occurrences = contextCounter.ValueAt(now, _options.HalfLife);
            foreach ((string action, DecayingCounter counter) in actions)
            {
                double support = counter.ValueAt(now, _options.HalfLife);
                if (support < _options.MinSupport)
                {
                    continue;
                }

                double confidence = Math.Min(1, support / Math.Max(occurrences, support));
                if (confidence < _options.MinConfidence)
                {
                    continue;
                }

                double prior = _actionCounts.TryGetValue(action, out DecayingCounter actionCounter)
                    ? actionCounter.ValueAt(now, _options.HalfLife) / total
                    : 0;
                double lift = prior > 0 ? confidence / prior : confidence;

                // Shrink confidence towards zero when evidence is thin: n / (n + 1).
                double score = confidence * (support / (support + 1)) * (lift > 1 ? 1 : 0.7);
                Merge(best, new MinedRecommendation(action, score, confidence, support, lift, "Pattern", new[] { context }));
            }
        }

        if (_lastAction is not null &&
            now - _lastActionAt <= _options.SequenceGap &&
            _transitions.TryGetValue(_lastAction, out Dictionary<string, DecayingCounter>? next))
        {
            double outgoing = 0;
            foreach (DecayingCounter counter in next.Values)
            {
                outgoing += counter.ValueAt(now, _options.HalfLife);
            }

            foreach ((string action, DecayingCounter counter) in next)
            {
                double support = counter.ValueAt(now, _options.HalfLife);
                double probability = outgoing > 0 ? support / outgoing : 0;
                if (support < _options.MinSupport || probability < _options.MinConfidence)
                {
                    continue;
                }

                double score = probability * (support / (support + 1)) * 0.9;
                Merge(best, new MinedRecommendation(action, score, probability, support, 1, "Sequence", new[] { "after:" + _lastAction }));
            }
        }

        return TopK(best.Values, top);
    }

    /// <summary>The strongest learned rules, for transparency in the UI.</summary>
    public List<MinedRule> Rules(DateTimeOffset now, int top = 20)
    {
        var rules = new List<MinedRule>();
        double total = Math.Max(1, _totalActions.ValueAt(now, _options.HalfLife));

        foreach ((string context, Dictionary<string, DecayingCounter> actions) in _rules)
        {
            double occurrences = _contextCounts.TryGetValue(context, out DecayingCounter c) ? c.ValueAt(now, _options.HalfLife) : 0;
            foreach ((string action, DecayingCounter counter) in actions)
            {
                double support = counter.ValueAt(now, _options.HalfLife);
                if (support < 0.5)
                {
                    continue;
                }

                double confidence = Math.Min(1, support / Math.Max(occurrences, support));
                double prior = _actionCounts.TryGetValue(action, out DecayingCounter a) ? a.ValueAt(now, _options.HalfLife) / total : 0;
                rules.Add(new MinedRule(context, action, Math.Round(support, 2), Math.Round(confidence, 3), Math.Round(prior > 0 ? confidence / prior : 0, 2)));
            }
        }

        return rules
            .OrderByDescending(r => r.Confidence * (r.Support / (r.Support + 1)))
            .Take(top)
            .ToList();
    }

    private static List<MinedRecommendation> TopK(IEnumerable<MinedRecommendation> candidates, int k)
    {
        // Min-heap of size k: the weakest of the current top-k sits at the root and is evicted first.
        var heap = new PriorityQueue<MinedRecommendation, double>();
        foreach (MinedRecommendation candidate in candidates)
        {
            heap.Enqueue(candidate, candidate.Score);
            if (heap.Count > k)
            {
                heap.Dequeue();
            }
        }

        var result = new List<MinedRecommendation>(heap.Count);
        while (heap.TryDequeue(out MinedRecommendation? item, out _))
        {
            result.Add(item);
        }

        result.Reverse(); // strongest first
        return result;
    }

    private static void Merge(Dictionary<string, MinedRecommendation> best, MinedRecommendation candidate)
    {
        if (!best.TryGetValue(candidate.Action, out MinedRecommendation? existing))
        {
            best[candidate.Action] = candidate;
            return;
        }

        // Several active contexts pointing at the same action reinforce each other.
        MinedRecommendation stronger = candidate.Score >= existing.Score ? candidate : existing;
        string[] triggers = existing.Triggers.Concat(candidate.Triggers).Distinct().ToArray();
        double combined = 1 - ((1 - existing.Score) * (1 - candidate.Score));
        best[candidate.Action] = stronger with { Score = combined, Triggers = triggers };
    }

    private void Evict(DateTimeOffset now)
    {
        while (_window.Count > 0 && now - _window.Peek().At > _options.Window)
        {
            _window.Dequeue();
        }
    }

    private void Increment(Dictionary<string, DecayingCounter> map, string key, DateTimeOffset at)
    {
        map.TryGetValue(key, out DecayingCounter counter);
        counter.Add(1, at, _options.HalfLife);
        map[key] = counter; // DecayingCounter is a struct, so write the updated copy back
    }

    private static Dictionary<string, DecayingCounter> GetOrAdd(Dictionary<string, Dictionary<string, DecayingCounter>> map, string key)
    {
        if (!map.TryGetValue(key, out Dictionary<string, DecayingCounter>? inner))
        {
            inner = new Dictionary<string, DecayingCounter>(StringComparer.Ordinal);
            map[key] = inner;
        }

        return inner;
    }

    private sealed class ContextOccurrence
    {
        public ContextOccurrence(string context, DateTimeOffset at)
        {
            Context = context;
            At = at;
        }

        public string Context { get; }

        public DateTimeOffset At { get; }

        /// <summary>Actions already credited to this occurrence, so each counts once.</summary>
        public HashSet<string> Credited { get; } = new(StringComparer.Ordinal);
    }
}
