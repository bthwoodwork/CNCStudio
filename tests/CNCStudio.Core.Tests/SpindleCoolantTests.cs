using System;
using System.Collections.Generic;
using System.Linq;
using CNCStudio.Core.Grbl;
using Xunit;

namespace CNCStudio.Core.Tests;

public class SpindleCoolantTests
{
    private static GrblStatus S(string state) => new() { State = state };

    [Fact]
    public void Status_report_gives_accessories_spindle_speed_and_overrides()
    {
        var s = GrblProtocol.ParseStatus("<Run|MPos:0.000,0.000,0.000|FS:500,12000|Ov:100,50,120|A:SF>")!;
        Assert.Equal(new double[] { 100, 50, 120 }, s.Overrides);
        Assert.True(s.SpindleOn); Assert.True(s.FloodOn); Assert.False(s.MistOn);
        Assert.Equal(12000, s.SpindleSpeed);
        // Ov without A: = everything off; neither = unknown.
        Assert.False(GrblProtocol.ParseStatus("<Idle|MPos:0,0,0|FS:0,0|Ov:100,100,100>")!.SpindleOn);
        Assert.Null(GrblProtocol.ParseStatus("<Idle|MPos:0,0,0|FS:0,0>")!.SpindleOn);
    }

    [Fact]
    public void Spindle_buttons_follow_the_grbl_rules()
    {
        Assert.Equal("M3 S18000", SpindleCoolant.Spindle(S("Idle"), true, 18000, true, false).Line);
        Assert.Equal("M5", SpindleCoolant.Spindle(S("Idle"), false, 0, true, false).Line);
        Assert.Equal(GrblProtocol.SpindleStopToggle, SpindleCoolant.Spindle(S("Hold:0"), false, 0, true, true).Realtime);
        Assert.NotNull(SpindleCoolant.Spindle(S("Run"), false, 0, true, true).Refused);
        Assert.NotNull(SpindleCoolant.Spindle(S("Idle"), true, 18000, true, programRunning: true).Refused);
        Assert.NotNull(SpindleCoolant.Spindle(S("Idle"), true, 18000, spindleByGrbl: false, false).Refused);
        Assert.NotNull(SpindleCoolant.Spindle(S("Idle"), true, 0, true, false).Refused);
        Assert.Equal(GrblProtocol.CoolantFloodToggle, SpindleCoolant.Coolant(S("Run"), mist: false).Realtime);
        Assert.Equal(GrblProtocol.CoolantMistToggle, SpindleCoolant.Coolant(S("Hold:0"), mist: true).Realtime);
        Assert.NotNull(SpindleCoolant.Coolant(S("Alarm"), false).Refused);
    }

    [Fact]
    public void Demo_machine_reports_spindle_override_and_coolant()
    {
        var sim = new SimulatedGrbl();
        var text = new System.Text.StringBuilder();
        sim.DataReceived += t => { lock (text) text.Append(t); };
        List<string> Lines() { lock (text) return text.ToString().Split('\n').Select(x => x.Trim()).ToList(); }
        void Clear() { lock (text) text.Clear(); }
        sim.Open();
        sim.Write("$X\n");
        sim.Write("M3 S10000\n");
        sim.WriteByte(GrblProtocol.SpindleOverrideCoarsePlus);
        sim.WriteByte(GrblProtocol.SpindleOverrideCoarsePlus);
        sim.WriteByte(GrblProtocol.CoolantFloodToggle);
        GrblStatus? last = null;
        for (int i = 0; i < 12 && last?.Overrides is null; i++)
        {
            Clear();
            sim.WriteByte(GrblProtocol.StatusReport);
            last = Lines().Select(GrblProtocol.ParseStatus).LastOrDefault(x => x?.Overrides is not null) ?? last;
        }
        Assert.NotNull(last);
        Assert.Equal(120, last!.Overrides![2]);
        Assert.Equal(12000, last.SpindleSpeed);
        Assert.True(last.SpindleOn); Assert.True(last.FloodOn);
        sim.Write("M5\n");
        sim.Write("M9\n");
        Clear();
        sim.WriteByte(GrblProtocol.StatusReport);
        var off = Lines().Select(GrblProtocol.ParseStatus).Last(x => x is not null)!;
        Assert.Equal(0, off.SpindleSpeed);
    }
}
