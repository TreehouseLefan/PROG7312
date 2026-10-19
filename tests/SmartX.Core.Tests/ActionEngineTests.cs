using SmartX.Core.Analytics;

namespace SmartX.Core.Tests;

public class ActionEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    private static PatternMiner TrainedMiner(int sessions = 10, int ignored = 2)
    {
        var miner = new PatternMiner();
        DateTimeOffset t = T0;
        for (int i = 0; i < sessions; i++)
        {
            t = t.AddHours(3);
            miner.ObserveContext("CriticalMoisture@sz-b", t);
            if (i < ignored)
            {
                continue; // the operator ignored this occurrence
            }

            miner.ObserveAction("Search:pump a", t.AddSeconds(45));
            miner.ObserveAction("Cmd:TurnOn:PUMP-A", t.AddSeconds(120));
        }

        return miner;
    }

    [Fact]
    public void Learns_that_operators_start_pump_a_after_sub_zone_b_dries_out()
    {
        PatternMiner miner = TrainedMiner();
        DateTimeOffset now = T0.AddDays(2);

        List<MinedRecommendation> suggestions = miner.Recommend(new[] { "CriticalMoisture@sz-b" }, now);

        Assert.Contains(suggestions, s => s.Action == "Cmd:TurnOn:PUMP-A");
        Assert.Contains(suggestions, s => s.Action == "Search:pump a");
        MinedRecommendation pump = suggestions.First(s => s.Action == "Cmd:TurnOn:PUMP-A");
        Assert.InRange(pump.Confidence, 0.7, 0.9); // 8 of 10 occurrences, recency weighted
        Assert.Equal("Pattern", pump.Source);
    }

    [Fact]
    public void Unrelated_situations_produce_no_suggestions()
    {
        PatternMiner miner = TrainedMiner();
        List<MinedRecommendation> suggestions = miner.Recommend(new[] { "PowerSurge@sz-d" }, T0.AddDays(2));
        Assert.Empty(suggestions);
    }

    [Fact]
    public void Actions_outside_the_window_are_not_linked_to_the_context()
    {
        var miner = new PatternMiner();
        for (int i = 0; i < 6; i++)
        {
            DateTimeOffset t = T0.AddHours(i * 3);
            miner.ObserveContext("Disconnected@sz-a", t);
            miner.ObserveAction("Search:valve", t.AddMinutes(30)); // long after the 10-minute window
        }

        Assert.Empty(miner.Recommend(new[] { "Disconnected@sz-a" }, T0.AddDays(1)));
    }

    [Fact]
    public void Sequence_model_predicts_the_next_operator_action()
    {
        PatternMiner miner = TrainedMiner();
        DateTimeOffset now = T0.AddDays(3);
        miner.ObserveAction("Search:pump a", now);

        List<MinedRecommendation> next = miner.Recommend(Array.Empty<string>(), now.AddSeconds(5));

        MinedRecommendation top = Assert.Single(next);
        Assert.Equal("Cmd:TurnOn:PUMP-A", top.Action);
        Assert.Equal("Sequence", top.Source);
    }

    [Fact]
    public void Recommendations_are_ranked_strongest_first_and_capped()
    {
        PatternMiner miner = TrainedMiner();
        List<MinedRecommendation> suggestions = miner.Recommend(new[] { "CriticalMoisture@sz-b" }, T0.AddDays(2), top: 1);

        Assert.Single(suggestions);
    }

    [Fact]
    public void Old_habits_decay_with_the_half_life()
    {
        var counter = new DecayingCounter();
        TimeSpan halfLife = TimeSpan.FromDays(7);
        counter.Add(8, T0, halfLife);

        Assert.Equal(4, counter.ValueAt(T0.AddDays(7), halfLife), 6);
        Assert.Equal(2, counter.ValueAt(T0.AddDays(14), halfLife), 6);
    }

    [Fact]
    public void Least_squares_trend_projects_time_to_threshold()
    {
        // Moisture falling 1% every 60 s from 30%: crosses 20% after 600 s.
        double[] x = Enumerable.Range(0, 10).Select(i => i * 60.0).ToArray();
        double[] y = x.Select(s => 30 - (s / 60)).ToArray();

        TrendFit? fit = LinearTrend.Fit(x, y);

        Assert.NotNull(fit);
        Assert.Equal(-1.0 / 60, fit.Value.Slope, 6);
        Assert.Equal(1.0, fit.Value.RSquared, 6);
        Assert.Equal(600, fit.Value.SecondsUntil(20, 0)!.Value, 3);
        Assert.Null(fit.Value.SecondsUntil(40, 0)); // moving away from 40%
    }
}
