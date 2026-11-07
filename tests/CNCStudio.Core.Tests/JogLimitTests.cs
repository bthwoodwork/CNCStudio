using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

/// <summary>Jog limits come from the connected machine ($130–$132), not from a project made for another size.</summary>
public class JogLimitTests
{
    [Fact]
    public void A_750_machine_can_jog_toward_its_switches_even_if_the_project_says_500()
    {
        var project = new MachineProfile { WorkWidthMm = 500, WorkLengthMm = 500, HomingDirInvertMask = 3 };
        var machine = new Dictionary<int, double>(SimulatedGrbl.DefaultSettings);       // 750 × 750, $23=3
        var status = new GrblStatus { State = "Idle", MPos = new[] { -375.0, -375.0, -10.0 } };  // middle of the table

        Assert.NotNull(GrblMachineCommands.CheckJog(status, project, -200, -200, 0));    // the old way: refused
        var limits = GrblSettings.ApplyToProfile(project, machine);
        Assert.Null(GrblMachineCommands.CheckJog(status, limits, -200, -200, 0));        // the machine's own travel
        Assert.NotNull(GrblMachineCommands.CheckJog(status, limits, -400, 0, 0));       // past its switches: still refused
    }
}
