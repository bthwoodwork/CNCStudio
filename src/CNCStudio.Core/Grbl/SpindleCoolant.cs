using System.Globalization;

namespace CNCStudio.Core.Grbl;

/// <summary>What a spindle or coolant button does in the machine's current state.</summary>
public sealed record AccessoryCommand(string? Line, byte? Realtime, string? Refused)
{
    public static AccessoryCommand No(string why) => new(null, null, why);
}

/// <summary>
/// Spindel och kylning (as gSender's and UGS's spindle/coolant buttons), by the GRBL 1.1 rules:
/// <list type="bullet">
/// <item>Idle: the spindle is started with M3 S… and stopped with M5 (lines – only between programs).</item>
/// <item>Hold: the spindle can only be stopped/started with the real-time toggle 0x9E; it starts again by itself on Fortsätt.</item>
/// <item>Run: the program decides; the spindle override (10–200 %) changes the speed while it runs.</item>
/// <item>Coolant: the real-time toggles 0xA0 (flood) and 0xA1 (mist) work in Idle, Run and Hold.</item>
/// </list>
/// </summary>
public static class SpindleCoolant
{
    public static AccessoryCommand Spindle(GrblStatus? status, bool turnOn, double rpm, bool spindleByGrbl, bool programRunning)
    {
        if (!spindleByGrbl)
            return AccessoryCommand.No("Spindeln styrs för hand (maskininställningarna) – starta och stoppa den på fräsen.");
        string state = (status?.State ?? "").Split(':')[0];
        switch (state)
        {
            case "Idle" when programRunning:
                return AccessoryCommand.No("Ett program eller makro körs – spindeln styrs av det.");
            case "Idle":
                if (!turnOn) return new AccessoryCommand("M5", null, null);
                if (!double.IsFinite(rpm) || rpm <= 0 || rpm > 60000) return AccessoryCommand.No("Ange ett varvtal (1–60000 varv/min).");
                return new AccessoryCommand("M3 S" + rpm.ToString("0", CultureInfo.InvariantCulture), null, null);
            case "Hold":
                return new AccessoryCommand(null, GrblProtocol.SpindleStopToggle, null);
            case "Run" or "Jog":
                return AccessoryCommand.No("Medan maskinen kör styr programmet spindeln. Pausa först för att stoppa den, eller ändra varvtalet med spindelöverstyrningen.");
            case "Alarm":
                return AccessoryCommand.No("Maskinen är i larm – lås upp ($X) eller homa först.");
            default:
                return AccessoryCommand.No($"Spindeln kan inte styras i läget {(state.Length == 0 ? "okänt" : state)}.");
        }
    }

    public static AccessoryCommand Coolant(GrblStatus? status, bool mist)
    {
        string state = (status?.State ?? "").Split(':')[0];
        return state is "Idle" or "Run" or "Hold" or "Jog"
            ? new AccessoryCommand(null, mist ? GrblProtocol.CoolantMistToggle : GrblProtocol.CoolantFloodToggle, null)
            : AccessoryCommand.No(state == "Alarm" ? "Maskinen är i larm – lås upp ($X) eller homa först." : $"Kylningen kan inte slås på i läget {(state.Length == 0 ? "okänt" : state)}.");
    }
}
