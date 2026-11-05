# GRBL 1.1 – verifierade fakta för CNC Studio

Allt i CNC Studio som nämner en GRBL-kod, `$`-inställning eller G/M-kod ska finnas i den här listan och vara kontrollerat mot GRBL:s egen dokumentation eller källkod. Ingenting får läggas till från minnet. Det som inte är verifierat ska stå under **Ej verifierat** och får inte användas i körbar kod.

Kontrollerat 2026-11-05 mot:
- Konfiguration: https://github.com/gnea/grbl/wiki/Grbl-v1.1-Configuration
- Kommandon: https://github.com/gnea/grbl/wiki/Grbl-v1.1-Commands
- Källkod: `grbl/limits.c`, `grbl/system.c`, `grbl/settings.c`, `grbl/gcode.c`, `grbl/config.h` (gnea/grbl, master)
- LinuxCNC G-code (G10 L2/L20, som GRBL hänvisar till): http://linuxcnc.org/docs/html/gcode/g-code.html

## Inställningar

| Kod | Betydelse | Källa |
|---|---|---|
| `$2` | Inverterar **steg**pulsen per axel (X=1, Y=2, Z=4). **Inte** samma sak som `$3`. | Configuration-wikin, `stepper.c` |
| `$3` | Inverterar **riktnings**signalen per axel (X=1, Y=2, Z=4). Exempel från wikin: `$3=2` inverterar bara Y. Bara den elektriska riktningen vänds. Positionsräknaren använder de oinverterade riktningsbitarna, så **koordinaterna påverkas inte**. Motorn går bara åt andra hållet. | Configuration-wikin, `stepper.c` (`dir_outbits = direction_bits ^ dir_port_invert_mask`, `sys_position` räknas på `direction_bits`) |
| `$20` | Mjuka gränser på (1) eller av (0). Varje rörelse kontrolleras mot maxrörelsen. | Configuration-wikin |
| `$21` | Hårda gränser på (1) eller av (0). Om en brytare löser ut stoppas maskinen och går i ALARM. | Configuration-wikin |
| `$22` | Homing på (1) eller av (0). Med 1 är G-code spärrad tills homing har körts. | Configuration-wikin |
| `$23` | Mask för inverterad homingriktning: X=1, Y=2, Z=4. **Standard 0, då alla axlar homar i positiv riktning.** En satt bit gör att den axeln homar negativt. | Configuration-wikin |
| `$27` | Pull-off: hur långt maskinen backar från brytarna efter homing (mm). | Configuration-wikin |
| `$130`/`$131`/`$132` | Maxrörelse för X/Y/Z i mm, från ände till ände. Anges som positivt tal och lagras internt som negativt (`settings.c`: `max_travel = -value`). | Configuration-wikin, `settings.c` |

## Kommandon

| Kod | Betydelse | Källa |
|---|---|---|
| `$H` | Kör homing-cykeln. *"This command is the only way to perform the homing cycle in Grbl."* | Commands-wikin |
| `$X` | Häver ALARM-spärren utan att homa. Wikin varnar att positionen troligen är okänd. **Används inte i stället för `$H`.** | Commands-wikin |

## Ordning vid inställning av en maskin

1. **Först `$3`.** Wikin skriver: *"First, ensure your machine axes are moving in the correct directions per Cartesian coordinates (right-hand rule). If not, fix it with the `$3` direction invert setting."* Rätt riktning är X+ åt höger, Y+ bakåt (bort från operatören) och Z+ uppåt.
2. **Sedan homing (`$22`, `$23`, `$27`).** `$23` anger homingriktningen i **koordinater**: i `limits.c` sätts sökmålet till `+max_travel` eller `−max_travel` utifrån `homing_dir_mask`. Därför bestämmer `$3` vilket fysiskt håll som räknas som plus. `$3` måste alltså vara rätt innan `$23` kan bli rätt.
   - Att ordningen `$3` → `$23` står uttryckligen i wikin är **inte** bekräftat. Den följer av koden och av wikins anvisning att kontrollera riktningarna innan man homar.

## Homing och maskinkoordinater

- När `$22=1` startar GRBL i **ALARM** tills `$H` har körts. Det styrs av `HOMING_INIT_LOCK` i `config.h`, som är på som standard.
- Ordningen i homing-cykeln: först **Z**, sedan **X och Y** samtidigt (`HOMING_CYCLE_0 = Z`, `HOMING_CYCLE_1 = X|Y` i `config.h`).
- Position efter homing (`limits.c`, `HOMING_FORCE_SET_ORIGIN` är av som standard): en axel som homar positivt får **MPos = −$27**. Brytaren ligger alltså vid **MPos 0**, och maskinen står strax under den efter pull-off.
- Mjuka gränser (`system.c`, `system_check_travel_limits`): tillåtet område är **−$13x ≤ MPos ≤ 0** för varje axel.
- **Följder i CNC Studio:**
  - Z-brytaren = MPos Z 0. Lägsta Z = −$132.
  - Avståndet från materialytan upp till brytaren = −(MPos Z vid materialytan).
  - Marginalen till gränslägena måste vara minst `$27`.
  - Z måste homa uppåt, så bit 4 i `$23` får inte vara satt.

## Nollpunkter

- `G10 L20 P1 X0 Y0`
  - **P1 = G54** (`gcode.c` räknar ned P1 till lagringsplats 0, och LinuxCNC:s tabell säger P1 = G54).
  - L20 sätter offseten så att **nuvarande position blir X0 Y0** (`gcode.c`: `WCS = MPos − G92 − TLO − WPos`).
  - **Z ändras inte**, eftersom `gcode.c` bara uppdaterar axlar som finns i kommandot: *"Else, keep current stored value."*
  - Wikin varnar för att lägga G10 i startblock (`$N`), eftersom det skriver till EEPROM.

## Seriell kommunikation (Interface-wikin, `report.c`, `system.c`, `settings.c`, `config.h`)

- **115200 baud, 8-N-1** (`config.h` `BAUD_RATE`, Commands-wikin).
- **Välkomstmeddelande:** `Grbl <version> ['$' for help]`, med CRLF före (`report.c`).
  - **Ej bekräftat:** om porten startar om Arduino när den öppnas (DTR). CNC Studio väntar därför på välkomstmeddelandet, frågar annars med `?` och skickar aldrig någon återställning på egen hand.
- **Varje rad får svaret `ok` eller `error:N`.** Wikin rekommenderar att skicka en rad och vänta på `ok`/`error` innan nästa skickas (send-response).
  - Inställningar **får inte** strömmas med teckenräkning: *"Only the simple send-response protocol works."*
  - Svaren kommer i ordning. Om en rad inte hinner få svar ignoreras dess sena svar, så att det inte tas för svaret på nästa rad.
- **Radlängd:** radbufferten är 80 tecken (`protocol.h` som standard). CNC Studio skickar högst 70 tecken per rad.
- **`$$`** skriver ut en rad `$N=värde` per inställning (tre decimaler, utan beskrivning) och avslutar med `ok`.
- **`$N=värde`:**
  - Svarar `ok` och skrivs **direkt till EEPROM** (`settings.c`).
  - Negativa värden ger `error:4`.
  - Godtas **bara i läge Idle eller Alarm**, annars kommer `error:8` (`system.c`).
- **`$#`** skriver ut `[G54:x,y,z]` … `[G92:x,y,z]`, `[TLO:z]`, `[PRB:x,y,z:s]` och sedan `ok`.
  - **WCO = G54 + G92 + TLO**, och **WPos = MPos − WCO** (`report.c`).
  - Med `$13=1` rapporteras värdena i tum. CNC Studio vägrar då att läsa Z-nollan.
- **Statusrapport:** `<Läge|MPos:x,y,z|…|Pn:…>`.
  - Lägen: Idle, Run, Hold:0/1, Jog, Home, Alarm, Check, Door:0–3, Sleep.
  - `WCO` skickas med jämna mellanrum.
- **Realtidskommandon:** `?` (status), `!` (feed hold), `~` (fortsätt), `0x18`/Ctrl-X (mjuk återställning) och `0x85` (avbryt jogg). De behöver ingen radbrytning och får inget `ok`.
- **`$H`** svarar först när homing är klar. CNC Studio väntar därför upp till 2 minuter.
- **Meddelanden:** `ALARM:N`, `[MSG:…]`, `[GC:…]` (från `$G`), `[VER:…]`/`[OPT:…]` (från `$I`).
- **Felkoder** och **larmkoder** kommer ordagrant från `error_codes_en_US.csv` och `alarm_codes_en_US.csv`. Inställningarnas namn och enheter kommer från `setting_codes_en_US.csv`.

## Gränslägesbrytare (`report.c`, `limits.c`, `config.h`)

- **`Pn:`** i statusrapporten listar utlösta ingångar: `X`, `Y`, `Z` för gränslägen, `P` för probe och `D`/`R`/`H`/`S` för styringångar.
  - Fältet saknas när ingen ingång är utlöst.
  - `REPORT_FIELD_PIN_STATE` är på som standard.
  - `$5` är redan tillämpad (`limits_get_state`), så `X` betyder alltid "utlöst".
  - Fältet påverkas inte av `$10` eller `$21`.
- **`$5`** inverterar alla gränslägesingångar. Används för normalt slutna brytare. Det kan krävas en omstart av kontrollern innan ändringen gäller.
- **Med hårda gränser (`$21=1`)** stoppar GRBL vid **varje** ändring på en brytaringång, även i Idle: både vid tryck och vid släpp (`mc_reset`, hard limit-larm). `HARD_LIMIT_FORCE_STATE_CHECK` är av som standard.
  - Efter ett larm från en gräns krävs **återställning** innan GRBL tar emot andra kommandon (`[MSG:Reset to continue]`). Sedan krävs `$H`, eller `$X` i nödfall.
  - Under ett sådant kritiskt larm svarar GRBL inte på `?`.
  - Därför erbjuder brytartestet att tillfälligt sätta `$21=0`, men bara efter bekräftelse. `$21=1` skrivs alltid tillbaka och kontrolläses.
- **`CHECK_LIMITS_AT_INIT`** är på: vid start eller återställning varnar GRBL om en brytare redan är aktiv.

## Antaganden (inte GRBL, utan hur maskinen står)

- Vilket hörn som blir hem utifrån `$23` förutsätter att **+X är åt höger och +Y bort från operatören (bakåt)**. Det måste stämmas av mot den egna maskinen.

## Ej verifierat (får inte användas i körbar kod förrän det är kontrollerat)

- Om `$X` godtas direkt efter ett larm från en hård gräns, utan återställning. Wikin och koden säger olika saker, så CNC Studio följer wikin: återställ först.
- Hur `EXEC_ALARM_HARD_LIMIT` blir `ALARM:1` i `protocol.c`. Larmtabellen säger att 1 = Hard limit, men den vägen genom koden är inte läst.

- Hur `$130` mappas till inställningsindex i `settings.h`. Betydelsen är verifierad i wikin, men mappningen i koden är inte läst.
- Exakt hur `gcode.c` skriver G10-offseten till EEPROM i exekveringsfasen.
- Alla G/M-koder för G-code-generering (G0/G1, G17, G21, G90, G54, M3/M5, S, F, G4 osv.) och **i vilken ordning programmet ska skicka dem**. Det ska verifieras innan 0.6.
