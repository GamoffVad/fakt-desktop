using System;
using System.Collections.Generic;
using System.Linq;
using Fakt.Core.Extraction;
using Fakt.Core.Records;
using Xunit;

namespace Fakt.UnitTests.Core;

public sealed class TokenEstimatorTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("abcd", 3)]
    [InlineData("привет", 5)]
    [InlineData("abяя", 3)]
    public void EstimateRaw_UsesLatinAndCyrillicRates(string text, int expected)
    {
        // ceil(ascii / 3.6 + other / 1.8) + 1
        Assert.Equal(expected, TokenEstimator.EstimateRaw(text));
    }

    [Fact]
    public void CyrillicText_IsEstimatedHigherThanLatinOfSameLength()
    {
        Assert.True(TokenEstimator.EstimateRaw(new string('я', 100)) > TokenEstimator.EstimateRaw(new string('a', 100)));
    }

    [Fact]
    public void NewEstimator_IsUncalibrated()
    {
        var estimator = new TokenEstimator();

        Assert.Equal(1.0, estimator.Calibration);
        Assert.Equal(TokenEstimator.EstimateRaw("Иванов Иван"), estimator.Estimate("Иванов Иван"));
    }

    [Fact]
    public void Observe_FirstSampleSetsRatioThenAveragesSamples()
    {
        var estimator = new TokenEstimator();

        estimator.Observe(100, 200);
        Assert.Equal(2.0, estimator.Calibration, 6);
        Assert.Equal(6, estimator.Estimate("abcd"));

        estimator.Observe(100, 100);
        Assert.Equal(1.5, estimator.Calibration, 6);
    }

    [Theory]
    [InlineData(1000, 3.0)]
    [InlineData(10, 0.5)]
    public void Observe_ClampsRatio(int actual, double expected)
    {
        var estimator = new TokenEstimator();

        estimator.Observe(100, actual);

        Assert.Equal(expected, estimator.Calibration, 6);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-5, 100)]
    public void Observe_IgnoresNonPositiveValues(int estimated, int actual)
    {
        var estimator = new TokenEstimator();

        estimator.Observe(estimated, actual);

        Assert.Equal(1.0, estimator.Calibration);
    }

    [Fact]
    public void Observe_LongRunUsesMinimumWeight()
    {
        var estimator = new TokenEstimator();
        for (var i = 0; i < 100; i++)
        {
            estimator.Observe(100, 100);
        }

        estimator.Observe(100, 300);

        // После 20+ образцов вес нового наблюдения не меньше 0,05: 1,0 · 0,95 + 3,0 · 0,05.
        Assert.Equal(1.1, estimator.Calibration, 6);
    }
}

public sealed class BatchPlannerTests
{
    private const int Overhead = 100;

    private static SourceRecord Record(long ordinal, int chars) =>
        new(ordinal, ordinal, ordinal, new[] { new KeyValuePair<string, string>("text", new string('x', chars)) }, "h" + ordinal, null, null);

    private static (List<PlannedBatch> Batches, List<RowOutcome> Oversized) Plan(BatchPlanner planner, IReadOnlyList<SourceRecord> records, int maxRows, int maxTokens)
    {
        var batches = new List<PlannedBatch>();
        var oversized = new List<RowOutcome>();
        planner.Plan(records, maxRows, maxTokens, batches, oversized);
        return (batches, oversized);
    }

    [Fact]
    public void RowLimit_SplitsIntoBatchesInOrder()
    {
        var planner = new BatchPlanner(new TokenEstimator(), Overhead);
        var records = Enumerable.Range(1, 25).Select(i => Record(i, 10)).ToList();

        var (batches, oversized) = Plan(planner, records, maxRows: 10, maxTokens: 100_000);

        Assert.Empty(oversized);
        Assert.Equal(new[] { 10, 10, 5 }, batches.Select(b => b.Records.Count));
        Assert.Equal(records.Select(r => r.Ordinal), batches.SelectMany(b => b.Records).Select(r => r.Ordinal));
    }

    [Fact]
    public void TokenLimit_SplitsBeforeExceedingLimit()
    {
        var planner = new BatchPlanner(new TokenEstimator(), Overhead);
        var records = Enumerable.Range(1, 9).Select(i => Record(i, 360)).ToList();
        var perRecord = planner.EstimateRecord(records[0]);
        Assert.All(records, r => Assert.Equal(perRecord, planner.EstimateRecord(r)));
        var limit = Overhead + 3 * perRecord + perRecord - 1;

        var (batches, oversized) = Plan(planner, records, maxRows: 100, maxTokens: limit);

        Assert.Empty(oversized);
        Assert.Equal(new[] { 3, 3, 3 }, batches.Select(b => b.Records.Count));
        Assert.All(batches, b => Assert.Equal(Overhead + 3 * perRecord, b.EstimatedInputTokens));
    }

    [Fact]
    public void Batches_RespectBothLimitsAndAreFilledGreedily()
    {
        var planner = new BatchPlanner(new TokenEstimator(), Overhead);
        var random = new Random(7);
        var records = Enumerable.Range(1, 60).Select(i => Record(i, random.Next(50, 900))).ToList();
        const int maxRows = 7;
        const int maxTokens = 1200;

        var (batches, oversized) = Plan(planner, records, maxRows, maxTokens);

        Assert.Empty(oversized);
        Assert.Equal(records.Select(r => r.Ordinal), batches.SelectMany(b => b.Records).Select(r => r.Ordinal));
        for (var i = 0; i < batches.Count; i++)
        {
            var batch = batches[i];
            Assert.InRange(batch.Records.Count, 1, maxRows);
            Assert.Equal(Overhead + batch.Records.Sum(planner.EstimateRecord), batch.EstimatedInputTokens);
            Assert.True(batch.EstimatedInputTokens <= maxTokens);
            if (i + 1 < batches.Count)
            {
                var next = batches[i + 1].Records[0];
                Assert.True(batch.Records.Count == maxRows || batch.EstimatedInputTokens + planner.EstimateRecord(next) > maxTokens,
                    "Пакет закрыт раньше, чем достигнут предел записей или токенов.");
            }
        }
    }

    [Fact]
    public void OversizedRecord_IsReportedAsErrorAndNotTruncated()
    {
        var planner = new BatchPlanner(new TokenEstimator(), Overhead);
        var records = new[] { Record(1, 100), Record(2, 50_000), Record(3, 100) };

        var (batches, oversized) = Plan(planner, records, maxRows: 10, maxTokens: 2000);

        var error = Assert.Single(oversized);
        Assert.Equal(2, error.Ordinal);
        Assert.Equal(RowOutcomeKind.Error, error.Kind);
        Assert.Equal("record_too_large", error.ErrorCode);
        Assert.Equal("h2", error.RowHash);
        Assert.Contains("2000", error.ErrorMessage);
        Assert.Contains("не обрезается", error.ErrorMessage);
        var batch = Assert.Single(batches);
        Assert.Equal(new long[] { 1, 3 }, batch.Records.Select(r => r.Ordinal));
    }

    [Fact]
    public void RecordExactlyAtLimit_IsAccepted()
    {
        var planner = new BatchPlanner(new TokenEstimator(), Overhead);
        var record = Record(1, 500);
        var limit = Overhead + planner.EstimateRecord(record);

        var (batches, oversized) = Plan(planner, new[] { record }, maxRows: 10, maxTokens: limit);

        Assert.Empty(oversized);
        Assert.Equal(limit, Assert.Single(batches).EstimatedInputTokens);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void NonPositiveRowLimit_MeansOneRecordPerBatch(int maxRows)
    {
        var planner = new BatchPlanner(new TokenEstimator(), Overhead);
        var records = Enumerable.Range(1, 3).Select(i => Record(i, 10)).ToList();

        var (batches, _) = Plan(planner, records, maxRows, maxTokens: 100_000);

        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.Single(b.Records));
    }

    [Fact]
    public void EmptyInput_ProducesNoBatches()
    {
        var (batches, oversized) = Plan(new BatchPlanner(new TokenEstimator(), Overhead), Array.Empty<SourceRecord>(), 10, 1000);

        Assert.Empty(batches);
        Assert.Empty(oversized);
    }

    [Fact]
    public void CalibratedEstimator_PacksFewerRecords()
    {
        var records = Enumerable.Range(1, 8).Select(i => Record(i, 360)).ToList();
        var fresh = new BatchPlanner(new TokenEstimator(), Overhead);
        var limit = Overhead + 4 * fresh.EstimateRecord(records[0]);
        var calibratedEstimator = new TokenEstimator();
        calibratedEstimator.Observe(100, 200);
        var calibrated = new BatchPlanner(calibratedEstimator, Overhead);

        var (freshBatches, _) = Plan(fresh, records, 100, limit);
        var (calibratedBatches, _) = Plan(calibrated, records, 100, limit);

        Assert.Equal(new[] { 4, 4 }, freshBatches.Select(b => b.Records.Count));
        Assert.All(calibratedBatches, b => Assert.Equal(2, b.Records.Count));
    }
}
