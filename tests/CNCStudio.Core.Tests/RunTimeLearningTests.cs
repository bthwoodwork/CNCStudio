using System;
using CNCStudio.Core;
using Xunit;

public class RunTimeLearningTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0);

    private static LoggedJob Run(int day, double minutes, double? estimate, double held = 0,
        LoggedJobOutcome outcome = LoggedJobOutcome.Finished, bool resumed = false) =>
        new() { Start = T0.AddDays(day), End = T0.AddDays(day).AddMinutes(minutes), EstimatedMinutes = estimate, HeldMinutes = held, Outcome = outcome, Resumed = resumed };

    [Fact]
    public void The_factor_is_the_median_of_finished_runs_without_their_pauses()
    {
        var f = RunTimeLearning.Learn(new[]
        {
            Run(0, 12, 10),                      // 1.2
            Run(1, 15, 10, held: 4),             // 1.1 once the pause is taken away
            Run(2, 13, 10),                      // 1.3
            Run(3, 5, 10, outcome: LoggedJobOutcome.Stopped),
            Run(4, 30, 10, resumed: true),
            Run(5, 90, 10),                      // 9 – not a real run time
            Run(6, 20, null),                    // no estimate (old log)
        });
        Assert.NotNull(f);
        Assert.Equal(3, f!.Runs);
        Assert.Equal(1.2, f.Factor, 6);
        Assert.Equal("Efter 3 körningar på maskinen: 12 min (+20 %)", RunTimeLearning.Describe(f, 10));
    }

    [Fact]
    public void One_run_is_not_enough() => Assert.Null(RunTimeLearning.Learn(new[] { Run(0, 12, 10) }));
}
