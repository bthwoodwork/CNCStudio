# CNC Studio – källkod (senaste: 0.9.47)

## 0.5.6 – CAM-kontroll av fastspänningar (XY)

Baserad på 0.5.5.1 med bevarat GUI, ritning, import, maskinvy och prestandaändringar.

## Nytt

- `CNCStudio.Core/CamSafetyReview.cs`: deterministisk, konservativ XY-granskning av konturernas centrumlinjer med hänsyn till verktygsradie och individuell marginal för varje fastspänningsblock.
- Koordinatöversättning: designkoordinater + materialets X/Y-position = planerade maskinkoordinater.
- Kontrollerar också att hela verktygets svepta XY-område ligger inom maskinprofilens deklarerade X/Y-område.
- CAM-panelens **Kontrollera hinder (XY)** ger en textlista med konflikter; på den vanliga konturförhandsvisningen färgas delsträckor med möjliga konflikter röda.
- Ett fel i en CAM-operation rapporteras som ej analyserad, inte som godkänd.

## Viktiga begränsningar

Detta är INTE en fullständig kollisionskontroll. Det finns inga beräknade G0-förflyttningar, Z-rörelser, hållargeometrier, fastspänningsplan i olika höjder, kontroll av verklig materialplacering eller GRBL-kommunikation. Markerade konflikter är konservativa möjliga XY-konflikter. Avsaknad av konflikt är inte ett säkerhetsgodkännande. Maskinprofilen måste kalibreras mot GRBL innan riktiga program får köras. Ingen körbar G-code exporteras.

## Test i Visual Studio

1. Välj `CNCStudio.Desktop` som startprojekt.
2. Skapa en rektangel och en CAM-operation (utvändig kontur, Ø3,175 mm).
3. Växla till Maskinvy och rita en fastspänning över den väntade verktygsbanan.
4. Klicka `CAM → Kontrollera fastspänningar (endast XY)` eller knappen i CAM-panelen.
5. Kontrollera röd linjesträcka och rapport. Flytta sedan fastspänningen bort från banan och kontrollera igen.
6. Testa även material nära maskinens ytterkant och en för stor fräs.

Kompilering i Windows krävs; den här miljön har inte .NET SDK/WPF.


## 0.5.6.1 – Synlig CAM och enklare fastspänning

- Sparad CAM visas automatiskt som grön streckad XY-centrumlinje i både materialvy och maskinvy.
- CAM-panelen har en kryssruta för att visa/dölja banorna; den tidigare menyfunktionen finns kvar.
- En tydlig **+ Rita fastspänning**-knapp finns i översta verktygsraden och i Maskin-menyn. Klick växlar till maskinvy, öppnar egenskapspanelen, och nästa drag på maskinbordet skapar blocket.
- Maskinarbetsytan visar egenskapspanelen så blockens höjd och säkerhetsmarginal går att redigera.
- Grön knapp visar att du är i ritläge för fastspänning.
- G-code och fysisk maskinstyrning är inte implementerat; XY-vyn verifierar inte säker Z-rörelse, tabbar eller hållarkollisioner.
- Källkodens XML/XAML och handlers är statiskt granskade, men WPF-programmet har inte byggts eller körts i Windows här.


## 0.5.6.2 – Precisare konfliktmarkering och ritmarkör

- Kontrollen av fastspänningar markerar nu bara den del av en bansträcka som faktiskt ligger inom klämmans zon (klämma + säkerhetsmarginal + fräsradie), inte hela sträckan. `CamSafetyReview.Issue` har fått `TStart`/`TEnd` (0..1) som anger konfliktens del av sträckan. Gäller både materialvy och maskinvy.
- Sträckor utanför maskinens arbetsområde och ogiltiga koordinater markeras fortfarande i sin helhet (konservativt, aldrig godkänt).
- Ritläget för fastspänning visar ett tunt hårkors över hela maskinbordet i stället för materialets flyttmarkör. Esc återställer nu även knappens färg.
- Versionsnummer uppdaterat i fönstertitel, Om-ruta och kontrollrapport; inaktuella texter om 0.5.4/0.5.5 borttagna.
- Byggd och körd i Windows (.NET 8, 0 fel, 0 varningar). Användaren har verifierat röd delmarkering och hårkors.

## 0.5.6.3 – Läsbar fastspänningsetikett och ritverktygspanel

- Fastspänningens namn och höjd visas inuti blocket när det får plats, annars ovanför blocket på en ljus bakgrund (under blocket om det ligger vid bordets överkant). Etiketten ligger inte längre ovanpå designen utan bakgrund.
- Ritverktygspanelen är 144 px bred (tidigare 126) och rubrikknapparna är kompaktare, så "RITVERKTYG" får plats. Rubriken avslutas med "…" om panelen görs smalare.
- Versionsnummer 0.5.6.3.
- Byggd och körd i Windows (0 fel, 0 varningar). Användaren har godkänt ändringarna.

## 0.5.7 – Automatisk CAM-kontroll och cachade körbanor

**CAM-planerare (`CNCStudio.Core/CamPlanner.cs`)**
- Alla körbanor beräknas via en gemensam `CamPlanner` som sparar (cachar) dem per operation. Både materialvy, maskinvy och kontrollrapporten använder samma resultat.
- En bana räknas om bara när något som påverkar dess form ändras: storlek, kontur, CAM-inställningar eller materialtjocklek. Zoom, markering, flytt av material/fastspänningar räknar aldrig om.
- **Flytt av objekt** förskjuter den sparade banan i stället för att räkna om den – snabbt även för stora DXF/SVG-filer.
- Enkla former (rektangel, ellips, vektorformer upp till 1000 punkter) räknas om automatiskt. **Tunga former** markeras som *inaktuella* efter storleks-/konturändring: banan döljs, objektet får orange streckad ram och operationen räknas som ej kontrollerad tills man klickar **Beräkna körbanor** (CAM-panelen eller CAM-menyn). Spara CAM beräknar alltid objektets bana direkt.

**Tre nivåer i kontrollen (CAM Safety & Validation Engine)**
- ⛔ Kritiskt: fräsen skär in i en fastspänning, banan utanför maskinens område, ogiltiga CAM-inställningar (t.ex. djup > materialtjocklek), bana som inte kunde beräknas, CAM utan ritobjekt.
- ⚠ Varning: banan inom fastspänningens säkerhetsmarginal, djup per pass > fräsdiameter, genomskärning utan hållflikar, hållflikar valda men ännu inte tillämpade, inaktuell körbana.
- ℹ Info: genomskärning (offerskiva, Z-nollpunkt).
- Kritiska delsträckor ritas röda, varningar orange – bara den berörda delen.
- Operationer utan beräknad bana visas med röd ram "CAM-fel – ej kontrollerad" i stället för att tyst försvinna.

**Gränssnitt**
- Ny statusruta i CAM-panelen som uppdateras automatiskt: antal kritiska/varningar/info och de viktigaste meddelandena, markerat objekt först. Anger alltid vad som inte kontrolleras (Z, hållare, materialgränser) – aldrig ett säkerhetsgodkännande.
- Konflikter uppdateras live när en fastspänning eller materialet dras i maskinvyn.
- "Uppdatera konturvisning (XY)" har ersatts av **Beräkna körbanor**.
- Versionsnummer på ett ställe i koden (`AppTitle`) för dialoger.

**Tester**
- Nytt testprojekt `tests/CNCStudio.Core.Tests` (xUnit) med 38 tester för konturbanor, XY-kontroll mot fastspänningar, svårighetsgrader, cachning, förflyttning och inaktuella banor. Kör med `dotnet test`.

Byggd i Windows (0 fel, 0 varningar), alla tester godkända. Användaren har godkänt versionen.

## 0.5.8 – Hållflikar, materialgränser och kontroll mellan delar

**Hållflikar (`CNCStudio.Core/CamTabs.cs`)**
- Nya CAM-inställningar: antal flikar, flikbredd och flikhöjd (standard 4 st, 6 mm, 3 mm). Visas i CAM-panelen när Hållflikar är ikryssat. Gamla projektfiler laddas med standardvärden.
- Flikar placeras jämnt längs varje sluten bana vid genomskärning. Längden på banan är flikbredd + fräsdiameter. Korta banor får färre flikar; får ingen plats ges en varning.
- Flikarna visas som tjocka gula partier med mörk kant och anger djupet där fräsen ska lyftas (planerat för kommande G-code; ingen körbar kod skapas).

**Nya kontroller (`CNCStudio.Core/CamGeometryChecks.cs`)**
- Varning: objekt helt eller delvis utanför materialet. Info: fräsen går utanför materialkanten.
- Varning: mindre än 1 mm material kvar under ett spår som inte går igenom.
- Varning: flikhöjd lika stor som skärdjupet. Kritiskt: ogiltiga flikinställningar.
- Varning: banan skär in i en annan del med CAM (hål i DXF/Boolean-former räknas inte; ellipser kontrolleras med marginal). Resultatet cachas per operation.

**Gränssnitt**
- CAM-kontrollen visar färgade nivåer (KRITISKT / Varning / Info) i stället för symboler.
- Flikinställningar följer med vid duplicering.

**Tester:** 69 tester (31 nya) för flikar, materialgränser, tunn botten, delar nära varandra, hål, ellipskontur och laddning av gamla projektfiler.

Byggd i Windows (0 fel, 0 varningar), alla tester godkända. Användaren har verifierat flikarna i appen.

## 0.5.9 – Säkra Z-rörelser och maskininställningar

**Rörelseplan (`CNCStudio.Core/MotionPlanner.cs`)**
- Planerar alla rörelser mellan skären: lyft till förflyttningshöjd, snabbt ned till höjd före nedmatning, nedmatning pass för pass, lyft över hållflikar (bara i pass under flikens topp) och lyft ur spåret.
- Snabbförflyttningar över en fastspänning lyfts över blockets höjd + marginal (visas lila). Start och slut går till en höjd över alla fastspänningar, eftersom maskinens startläge är okänt.
- Kontrollerar att ingen snabbförflyttning i sidled går i materialet.
- Ordning: invändiga konturer före utvändiga, inre banor före yttre.

**Z-gränser via homing**
- Maskinen homas med `$H` vid start. Z-brytaren sitter upptill vid MPos Z 0, och efter `$H` står Z på −`$27`.
- Användaren anger MPos Z vid materialytan. Kritiskt om någon planerad höjd kommer närmare Z-brytaren än marginalen, eller om djupaste skär går under −`$132` + marginal.

**Maskininställningar (Maskin → Maskininställningar…)**
- Arbetsområde X/Y/Z (`$130`–`$132`), homing vid start, axelriktning (`$3`), homingriktning (`$23`, visar hemhörnet), pull-off (`$27`), mjuka gränser (`$20`) och marginal till gränslägen (minst `$27`; gäller både XY och Z).
- Grön HEM-markering i maskinvyn. Z måste homa uppåt.
- Alla GRBL-koder är kontrollerade mot GRBL 1.1:s källkod. Se `docs/GRBL-verifierat.md`.

## 0.5.10 – GRBL-anslutning, konsol, inställningsminne och brytartest

- **Seriell anslutning** (115200 8-N-1) i Maskinkontroll-panelen med live-läge, MPos och WPos. Inget skickas automatiskt utom `?`, och programmet återställer aldrig GRBL på egen hand.
- **GRBL-konsol:** rad för rad med väntan på `ok`/`error` (send-response). Realtidsknappar `?` `!` `~` Ctrl-X. `$H` med bekräftelse, `$X` med varning. Sena svar efter timeout förväxlas inte med nästa kommando.
- **Inställningsminne:** läs hela `$$` med GRBL:s namn och enheter, jämför med maskinprofilen, skriv ändrade värden (bara i Idle/Alarm, ett i taget, stopp vid första fel, kontrolläsning efteråt) och använd maskinens värden i profilen.
- **Test av gränslägesbrytare utan rörelse:** följer `Pn:` i statusrapporten. Varje axel godkänns när brytaren syns intryckt och sedan släppt. Varnar för brytare som är utlösta från start. Med `$21=1` kan hårda gränser tillfälligt stängas av efter bekräftelse; de slås alltid på igen och kontrolläses.
- **Z-nolla från maskinen:** MPos Z vid materialytan = G54 Z + G92 Z + TLO via `$#`. Vägrar vid `$13=1` (tum) eller annat aktivt koordinatsystem än G54.
- Nollning och jogg i panelen är fortfarande demo.

**Tester:** 166 tester, varav 46 mot en simulerad GRBL.

Byggd i Windows (0 fel, 0 varningar), alla tester godkända. **Inte testad mot riktig maskin ännu.**

## 0.5.11 – Ny verktygsrad och tydliga arbetsytor

- Verktygsrad med ikoner under menyerna (Ny, Öppna, Spara | Ångra, Gör om | Importera | Anpassa, 1:1) med tooltips och kortkommandon. Allt finns kvar i menyerna.
- Arbetsytorna Design, CAM och Maskin visas som flikar; den aktiva är blå och fetstilt och har en bock i Visa-menyn. Kortkommandon F1, F2 och F3.
- Knapparna Materialvy och Maskinvy är borttagna. Arbetsytan styr vyn: Design och CAM visar materialet, Maskin visar maskinbordet.
- "+ Rita fastspänning" byter till arbetsytan Maskin med rätt paneler.

Byggd i Windows (0 fel, 0 varningar), 166 tester godkända. Användaren har godkänt utseendet.

## 0.5.12 – Tydligare layout

- Mörka, läsbara menyer med markering, kortkommandon och bock för aktiv arbetsyta.
- Panelerna följer arbetsytan: Design visar Markerat objekt överst och sedan Projekt och material; Maskin visar panelen Maskinbord med Fastspänningar, Materialplacering, Z-höjder och Maskin. Maskinkontrollen visas inte i Design.
- "+ Rita fastspänning" ligger under Fastspänningar i stället för i huvudverktygsraden.
- Zoom och rutnät visas i statusraden.
- Läsbara rubriker och etiketter i hopfällbara sektioner, maskinsammanfattningen fylls i vid start och hjälpraden döljs inte längre av linjalen.

Byggd i Windows (0 fel, 0 varningar), kontrollerad med skärmbilder. Användaren har godkänt layouten.

## 0.6 – G-code för GRBL

- **Export** (CAM → Exportera G-code…, Ctrl+E, eller knappen i CAM-panelen) skapar en `.nc`-fil i ren ASCII med CRLF.
- **Bara verifierade koder:** G0 G1 G4 G17 G21 G54 G90 G94 M0 M3 M5 M30 med X Y Z F S P. Fakta och källor finns i `docs/GRBL-verifierat.md`.
- **Programmets ordning:** filhuvud med nollpunkt och krav → G21/G90/G94/G17/G54 → **lyft Z först** → spindelstart på säker höjd → till startpunkten → pass med nedmatning, kontur och lyft över hållflikar → lyft → avslut.
- **Nollpunkt:** X0 Y0 = materialets främre vänstra hörn, Z0 = ovansidan. Y speglas från ritningen.
- **Matning, nedmatning och varvtal** anges per operation i CAM-panelen. Spindelns uppstartstid finns under Z-höjder.
- **Fräs:** *Manuell* (standard) skriver inga M3/S/M5. Programmet pausar i stället med M0 på säker höjd, med en kommentar om vilket varvtal som ska ställas in; fortsätt med `~`. *Styrs av GRBL* skriver M3 S, G4 för uppstart och M5. Varvtalsbyten mellan operationer görs på förflyttningshöjd.
- **Exporten blockeras** vid kritiska fel, inaktuella eller okontrollerade körbanor, okänd MPos Z vid materialytan, eller om homing vid start är avstängd. Varningar visas, måste godkännas och skrivs i filhuvudet.
- **Oberoende kontroll:** `GCodeVerifier` läser programmet rad för rad och kontrollerar bland annat att Z lyfts först, att ingen snabbförflyttning sker i materialet, att spindeln är på (eller att det finns en M0-paus) före skär, att F är satt före G1, Z-gränserna, avslutet, att raderna är ASCII och högst 70 tecken, och att bara tillåtna koder används. Vid fel skapas ingen fil.
- Maxhastigheterna `$110`–`$112` läses in från maskinen och ger en varning om matningen är högre.

**Tester:** 199 tester. Programmet är inte kört på en riktig maskin än – kontrollera filen i gSender och kör gärna i luften första gången.

## 0.7 – Fickfräsning

- **Fickfräsning** för rektanglar, ellipser och DXF/SVG/Boolean-former, även med hål och öar. Offset-banor beräknas med Clipper2 (Boost-licens).
- Fickan fräses inifrån och ut, och sista varvet följer väggen (finfräsning). Den fräses ett djup i taget.
- Mellan varven matar fräsen över på djupet om den raka länken ligger helt inom fickan; annars lyfts den. Länkarna skrivs som `G1 X Y` i G-coden.
- **Stegning** i % av fräsdiametern (standard 40 %).
- **Kontroll av kvarvarande material:** varning om material blir kvar mellan varven, information om rundade inre hörn (fräsens radie).
- Fickor och invändiga konturer fräses före utvändiga konturer.
- Varje varv förenklas (avvikelse 0,005 mm), så att stora fickor beräknas snabbt (en ellipsficka gick från 47 s till 0,4 s).

## 0.7.1 – Fräsar, nollpunkt, körtid och rampning

- **Verktygsbibliotek** (CAM-panelen → Fräsar…) med typ, diameter, vinkel, antal skär och standardvärden. Det gäller alla projekt och sparas i `%APPDATA%\CNCStudio\tools.json`. Fräsens namn skrivs i G-codens filhuvud. En V-fräs för kontur eller ficka ger ett kritiskt fel, och en kulfräs ger information om rundad botten.
- **Nollpunkt X0 Y0:** främre vänstra hörnet eller materialets mitt. En röd markering visar nollpunkten i båda vyerna, och G-code, filhuvud och exportdialog följer valet.
- **Uppskattad körtid** i CAM-kontrollen och exportdialogen. Den räknas från matningar och `$110`–`$112`; acceleration ingår inte.
- **Rampad nedmatning** längs banan (standard 3°), bara genom material som inte redan är fräst. Hela varvet fräses ändå på fullt djup. Pass som når hållflikarna använder rak nedmatning.
- **Fräsriktning:** medfräsning eller motfräsning (för medurs spindel, M3), med rätt riktning för utvändiga och invändiga konturer, fickor och öar.
- **Markörens koordinater** i arbetskoordinater visas i statusraden.

**Tester:** 247 tester.

## 0.7.2 – Riktig jogg och nollning

- Demodelen i maskinpanelen är ersatt med riktiga kontroller (aktiva när maskinen är ansluten).
- **Koordinatvisning:** stora arbetskoordinater, MPos i små siffror, maskinens läge och val av koordinatsystem G54–G59 (läses med `$G`).
- **Nollning:** per axel, X + Y (Z ändras inte) och "Sätt" för ett eget värde, med `G10 L20 P0`, bara i Idle. När Z nollas på materialet uppdateras MPos Z vid materialytan automatiskt.
- **Gå till X0/Y0:** Z lyfts först till nära överkanten med `G53 G0`, sedan `G90 G0` i XY.
- **Jogg:** klick ger ett steg, håll inne ger kontinuerlig jogg enligt GRBL-wikin (korta G91-steg som väntar på `ok`, `0x85` vid släpp). STOPP-knapp. Förinställningar Precis/Normal/Snabb. Hastigheten begränsas av `$110`–`$112`.
- **Säkerhet:** ingen jogg i larmläge, ingen jogg utanför arbetsområdet minus marginalen (även utan mjuka gränser; rörelse bort från brytaren tillåts alltid), och en varning om ett annat koordinatsystem än G54 är aktivt.
- `docs/GRBL-verifierat.md`: regler för `$J=`, `0x85`, `G10 L20 P0`, `G53` och `$G`.

**Tester:** 273 tester. Inte testad mot riktig maskin ännu.

## 0.7.3 – 3D-vy och ny 2D-stil
- 3D-vy (F4 eller knappen 3D): simulerat material som höjdkarta, skärande banor, ned/upp-rörelser, snabbförflyttningar och fastspänningar. Byggs bara om när innehållet ändras.
- Ny mörk 2D-stil: tunna skarpa linjer, diskret rutnät och tydlig materialkant.
- Körbanor visas med verklig fräsbredd (genomskinligt band), tunn mittlinje och riktningspilar. Hållflikar som bryggor över bandet, konflikter som röd/orange glöd på just den delen av banan.
- Linjebredder följer zoomen (ritas om när zoomningen stannar).
- Rättat: en ficka som ligger inuti en annan del (t.ex. en urtagning i en skiva som sågas ut) varnas inte längre som "skär in i annan del".

## 0.7.4 – Tydliga dialoger med förslag
- Alla popupfönster är nya: mörka i appens stil, med färg och ikon efter typ. Enter väljer standardknappen och Esc det säkra alternativet (Avbryt/Nej).
- När G-code inte kan exporteras visas varje orsak med "Gör så här" och en knapp som tar dig dit: t.ex. MPos Z-fältet (markerat i gult), Maskininställningar, Beräkna körbanor eller objektet i CAM.
- Felaktiga fältvärden: markören hamnar i fältet som ska rättas.

**Tester:** 284 tester. Inte testad mot riktig maskin ännu.
## 0.7.5 – Homing, nollpunkt och demomaskin
- Demomaskin: välj "DEMO (simulerad X-Carve 750)" i portlistan. En simulerad GRBL 1.1 med brytare fram till vänster och Z-brytaren upptill: larmlås, homing, jogg, nollning, mjuka och hårda gränser, $$/$#/$G. Förenklad: ingen acceleration, inga bågar, ingen probe. Inget bevis för att något fungerar på den riktiga maskinen.
- Homing-knapp ($H) bland nollningsknapparna i maskinstyrningen.
- Maskininställningar: klicka i hörnet där X/Y-brytarna sitter. Det sätter $23 och blir bordets nollpunkt; materialets och fastspänningarnas position räknas därifrån.
- Testknappar per axel (−/+) med valbart avstånd för att kontrollera axelriktningen ($3) före homing.
- Fräsens position visas i maskinvyn efter homing (kan slås av).
- "Arbetsområde hit…": jogga till yttersta punkten efter homing och sätt $130/$131 (mjuka gränser) därifrån.
- Verifierat i GRBL-källan: maskinkoordinaterna är alltid −$13x…0; en axel som homar åt minus står på −$13x + $27 efter homing.

**Tester:** 285 tester. Inte testad mot riktig maskin ännu.
## 0.8 – Kör program och återuppta efter strömavbrott
- **Kör program** i maskinstyrningen: Ladda från projektet (samma kontroller som Exportera), Kontrollkör ($C), Starta, Paus/Fortsätt, Stopp och matningsöverstyrning (−10/−1/100/+1/+10 %). Sändning med send-response; varje rad väntar på ok.
- Starta kräver homing. Programmets M0-paus (manuell fräs) visas tydligt; Fortsätt skickar ~.
- Stopp = feed hold, vänta på stillastående, sedan mjuk återställning – positionen behålls.
- **Återuppta:** efter varje ok sparas jobbets läge och en kopia av programmet i %APPDATA%\CNCStudio\job (skrivs säkert till disk). Ett avbrutet jobb visas som "AVBRUTET JOBB" i maskinstyrningen. Efter homing fortsätter jobbet från en punkt som säkert redan är fräst (minst 17 rader före sista ok, eftersom GRBL kan ha 16 rader i kö): Z upp nära brytaren, XY dit, fräsen startas (M0-paus för manuell fräs), ned i det redan frästa spåret med nedmatningshastighet och vidare längs banan.
- Varnar om nollpunkten (G54) har ändrats sedan jobbet startade. G54 sparas i maskinens minne och finns kvar efter strömavbrott.
- Demomaskinen kan simulera strömavbrott (Maskin-menyn) och har matningsöverstyrning.
- Ett larm (ALARM:N) stoppar nu den väntande raden direkt i stället för att vänta på ett svar som aldrig kommer.

**Tester:** 293 tester. Inte testad mot riktig maskin ännu.
## 0.8.1 – Första test mot maskinen (steg för steg)
- Maskin-menyn och maskinstyrningen: **Första test mot maskinen…**. Ett eget fönster med egen anslutning (maskinstyrningen kopplas från först).
- Steg: förberedelser (checklista) → anslut (kontrollerar GRBL 1.1) → läs $$ med säkerhetskopia och kontroll av $13/$32/$22/$23/$21/$20/$27/$11x/$13x (kan rätta det farligaste efter fråga) → brytartest → axelriktning före homing med minus-knappar och "Vänd axel" ($3) → homing med kontroll av MPos → jogg efter homing → luftkörning (50 × 50 mm-kvadrat i luften, inåt från brytarna, Z nära toppen) med paus/fortsätt/matning → sammanfattning.
- STOPP och Återställ (Ctrl-X) syns hela tiden. Allt loggas till %APPDATA%\CNCStudio\logs.
- Övningsläge: välj DEMO som port; brytarna kan tryckas in i brytarsteget.

**Tester:** 303 tester. Inte testad mot riktig maskin ännu.
## 0.8.2 – Säkrare test och loggar
- **Larm som kräver återställning** ("Reset to continue", t.ex. en brytare med hårda gränser på): CNC Studio känner igen det, skickar inget mer som aldrig kan besvaras och visar "LARM – ÅTERSTÄLL KRÄVS". Ny knapp **Återställ (Ctrl-X)** i maskinstyrningen, i brytartestet och i första testet.
- **Brytartestet** leder i rätt ordning: med $21=1 är steg 1 att stänga av hårda gränser för testet (rutorna är spärrade tills dess). $21=1 skrivs tillbaka och kontrolläses när testet stängs. Rättat: krasch när fönstret stängdes efter att hårda gränser varit av.
- **Första test:** riktningssteget har Återställ, "Lås upp för test ($X)" och "gå till homing"; en levande karta över maskinbordet visar fräsen efter homing; sista knappen heter "Avsluta testet" och skriver sammanfattningen i loggen. Knappar kraschar inte längre programmet.
- **Hjälp → Loggar och fellogg:** fellogg (senaste först, med detaljer), loggar från första test och säkerhetskopior av $$. Sök, kopiera, öppna mappen, rensa.
- **Skyddsnät:** oväntade fel visas och loggas i stället för att stänga programmet.
- Demomaskinen svarar inte heller på status medan den väntar på återställning, som riktig GRBL.

**Tester:** 306 tester. Inte testad mot riktig maskin ännu.
## 0.8.3 – Egen ikon
- Ny programikon: en fräs som skär en grön verktygsbana i ett material, i appens mörkblå färger. Syns i Utforskaren, aktivitetsfältet och alla fönsters titelrad.
- Ritas av `tools/make-icon.ps1` i alla storlekar 16–256 px (små storlekar förenklade så att de syns tydligt).
## 0.8.4 – Synkmapp (Google Drive)
- **Arkiv → Synkmapp (Google Drive)…**: välj en mapp som Google Drive för datorn (eller OneDrive/Dropbox) synkar. CNC Studio hittar Google Drive själv ("Min enhet"/"My Drive") och föreslår `…\CNC Studio`.
- Delas mellan datorer: projekt (Öppna/Spara börjar i mappen Projekt), fräsbiblioteket och standardmaskinprofilen för nya projekt (Inställningar). Stannar lokalt: avbrutna jobb, loggar och $$-kopior.
- Befintliga filer i synkmappen (från en annan dator) skrivs aldrig över. Hittas inte mappen vid start varnar programmet och sparar lokalt.
- Projekt och inställningar sparas via en tillfällig fil så att synken aldrig får en halv fil.

**Tester:** 311 tester. Inte testad mot riktig maskin ännu.
## 0.8.5 – Verktygs- och materialbibliotek
- **CAM → Verktygsbibliotek:** fräsar i kategorier (Raka fräsar, Kulfräsar, V-fräsar eller egna), standardvärden och **skärdata per material** (matning, nedmatning, djup per pass, varvtal).
- **CAM → Materialbibliotek:** material i kategorier (Trä, Skivmaterial, Plast, Metall, Övrigt); visar vilka fräsar som har skärdata för materialet.
- **Materialtyp per projekt** (Projekt och material): när du väljer fräs i CAM fylls skärdatan för projektets material i, annars fräsens standardvärden. Byte av material erbjuder att uppdatera befintliga operationer.
- **Mappar** under CNC Studio-mappen (synkmappen eller Dokument\CNC Studio): `Projekt`, `Verktyg`, `Material`, `Inställningar`. Det gamla verktygsbiblioteket flyttas in automatiskt.

**Tester:** 316 tester. Inte testad mot riktig maskin ännu.
### Release-bygge
`powershell -ExecutionPolicy Bypass -File tools\publish.ps1` bygger en fristående `CNC Studio.exe` (ingen .NET-installation behövs) med `Installera.txt` till `<CNC Studio-mappen>\Programversioner\CNC Studio <version>`. CNC Studio-mappen är synkmappen (Arkiv → Synkmapp), annars Dokument\CNC Studio. En befintlig version skrivs aldrig över.

## 0.9.47 – Projektet sparas alltid som .cncstudio.json

- **Du behöver inte skriva ändelsen:** skriver du bara `Bricka` (eller `Bricka.json`) i Spara-dialogen sparas projektet som `Bricka.cncstudio.json`. Tidigare kunde filen bli `Bricka.json`, och då syntes den inte bland projekten på startsidan eller i Öppna.
- Finns en fil med det namnet redan frågar programmet innan den ersätts.
- **Öppna** visar nu även vanliga `.json`-filer, så att projekt som redan sparats med fel ändelse går att öppna. Spara dem sedan med *Spara som* för att få rätt ändelse.
- Samma sak gäller hanens projekt när en inlay görs.

## 0.9.46 – Flytta materialet i 3D-vyn

- **Dra materialet med vänster musknapp** i 3D-vyn: markören blir ett flyttkors över materialet, och materialet, banorna och nollpunkten följer musen över bordet. Positionen visas medan du drar.
- När du släpper uppdateras projektet som i 2D: ett ångrasteg, och 2D, Maskin och fräsningen följer med. Vyn står kvar där draget slutade.
- Materialet hålls inom arbetsområdet. Höger musknapp roterar och mittenknappen panorerar som förut.
- **Samma rutnät på bordet som i 2D:** 10 mm svaga, 50 mm mellan och 100 mm tydliga linjer, räknat från bordets bakre vänstra hörn som i 2D-vyerna (tidigare bara 100 mm från hemhörnet).

## 0.9.45 – Flytta materialet på bordet

- **I Design:** dra i materialets kant (markören blir ett flyttkors) för att flytta materialet på maskinbordet. Formerna följer med, och ett drag går att ångra i ett steg.
- **I Maskin:** materialet går att ta tag i var som helst på det, även där former, körbanor eller nollpunkten ligger ovanpå. Tidigare fångade de klicket så att materialet inte gick att dra.

## 0.9.44 – Maskinbordet och materialet i 2D-vyn

- **Maskinbordet syns kvar i Design** när projektet har material: bordet med 10/50/100 mm-rutor ritas runt materialet, och materialet ligger där det är placerat på bordet (som i Maskin och 3D-vyn).
- **Materialet är en solid skiva** i en varm träton med ljus kant, utan bordets rutnät, så att det inte går att förväxla med bordet.
- Utan material är arbetsytan fortfarande hela bordet med ritrutnätet.

## 0.9.43 – Smal verktygslist och kortare fräsningspanel

- **Rita** är nu en **smal list** längs vänsterkanten, som i gSender: en ikon med kort text under, och hela namnet i verktygstipset. Ritytan får mer plats.
  - Knapparna i listan är: Markera, Rektangel, Ellips, Linje, Text, Figurer, Bild, 3D, Importera, Snap │ Kombinera ▾, Justera ▾, Offset, Rutnät, Packa │ Duplicera och Ta bort.
  - **Kombinera ▾** öppnar en meny med Förena, Subtrahera, Skärning, XOR och Kontrollera vektorer.
  - **Justera ▾** öppnar en meny med de sex justeringarna och Fördela i sidled och höjdled.
  - *Stäng linje* ligger kvar i Rita-menyn. Enter stänger också en polylinje.
- **Fräsning** har blivit kortare. De numrerade rubrikerna är borta och korten ligger tätare. Nedmatning och fräsriktning ligger under en hopfälld rad, **AVANCERAT**.
- Den sparade fönsterlayouten byter namn igen (`fönsterlayout-4.xml`), så att den smala listan får rätt bredd.

## 0.9.42 – Design: jobbpanel och lugnare paneler

- **Jobb** är en egen panel under ritytan i Design, på samma plats som Program och Körning ligger i Maskin.
  - Till vänster finns alla operationer i körordning, ett program per fräs. Klicka på en rad för att markera formen, eller högerklicka för att kopiera inställningar, ta bort eller exportera.
  - Till höger finns hela projektets kontroll, *Beräkna körbanor*, *Kontrollera hinder*, *Visa körbanorna* och **Exportera G-code**.
- **Fräsning** visar inställningarna bara när en form är markerad. Annars syns en rad om att markera en form.
- **Markerat objekt** fälls ihop när inget är markerat, så att projekt och material syns helt. Formen visas med namn, till exempel "Rektangel 2", i stället för "Rectangle".
- *Visa → Visa jobb* tar fram panelen om den har stängts.

## 0.9.41 – Design-ytan: rita och fräsning på samma ställe

- **Två arbetsytor** i stället för tre, som gSender plus designverktyg: **Design** (F1) och **Maskin** (F3). CAM är inte längre en egen arbetsyta.
- I **Design** sitter, till höger och under varandra:
  - **Egenskaper** överst: den markerade formens plats och storlek, samt projekt och material.
  - **Fräsning** direkt under (tidigare Smart CAM): hur den markerade formen ska fräsas (kontur, ficka, V-carving, inlay, 3D …), fräs ur biblioteket, djup, matning och andra fräs. Längst ned finns hela projektets jobblista per fräs med kontrollen.
  - Markerar du en form ser du alltså direkt både vad den är och hur den ska fräsas. Körbanorna visas på ritytan som förut.
- **F2** öppnar Design med Fräsning i fokus. Knappar och meddelanden som tidigare ledde till "CAM" leder hit.
- Den sparade fönsterlayouten byter namn (`fönsterlayout-3.xml`), så alla får den nya standardlayouten en gång. Därefter sparas dina egna ändringar som vanligt.

## 0.9.40 – Maskin-ytan byggd som gSender

Första steget mot "CNC Studio = gSender + designverktyg" (se Miro: *CNC Studio – flödet*, version 3). Arbetsytan **Maskin** (F3) är uppställd som gSender:

- **Mitten:** maskinvyn eller 3D-vyn, stor.
- **Till höger, Maskinstyrning:**
  - Status och koordinatsystem (G54 …), Homing och Återställ.
  - Positionen med **X0 / Y0 / Z0** (nolla) till vänster, arbets- och maskinkoordinater i mitten och **Gå till X / Y / Z** till höger.
  - **Nolla X + Y** (▾: vid materialet) och **Gå till X0 Y0**.
  - Joggkorset med Z, och under det steg, Z-steg och hastighet med **Precis / Normal / Snabb**.
- **Under vyn, tre paneler bredvid varandra:**
  - **Program:** ladda från projektet eller öppna G-code (Fusion …), kontrollkör, rita konturen, starta från rad, senast körda och avbrutet jobb.
  - **Körning:** stora **Starta / Paus / Stopp**, förlopp, överstyrning av matning, snabbgång och spindel, samt spindel, kylning och dimma.
  - **Probning och mer** (flik): probning, planhet, platser, sätt värde, arbetsområde, material och fastspänningar, gamepad och tangenter, konsol, maskininfo och brytartest. **Makron** finns som flik bredvid.
- Egenskapspanelen döljs i Maskin. Material, fastspänningar och Z-höjder öppnas med *Material och fastspänningar…*.
- Inga funktioner har ändrats, bara var de sitter. Panelerna kan fortfarande dras och dockas. En sparad layout från en tidigare version ersätts automatiskt av den nya. *Visa → Återställ fönsterlayout* tar alltid tillbaka den.

## 0.9.39 – Ikoner och samma stil i alla fönster

- **Ikoner på knapparna** i ritpanelen (Markera, Rektangel, Ellips, Polylinje, Text, Figurer, Bild, 3D, Förena …, Offset, Kopior, Packa, Duplicera, Ta bort) och i maskinpanelen (Maskiner, Inställningar, Konsol, Info, Testa brytare, Återställ, Homing, Nolla X + Y, Gå till X0 Y0, Probning, Planhet, Arbetsområde, Platser, Gamepad, Tangenter). Ikonerna har samma stil (Segoe-ikoner) som verktygsfältet och står kvar även när knapptexten ändras. Det sköts av egenskapen `local:Ui.Icon` (Ui.cs).
- **Mörk titelrad** på alla fönster i Windows 10 (1809 eller senare) och Windows 11, i stället för den vita systemraden.
- **De egna fönstren** (Platser, Inlay, Figurbank, Text, Bild till vektor, Starta från rad, Tangentbord, Gamepad, Makron, Maskiner m.fl.) har fått samma knapprad som dialogrutorna:
  - egen bakgrund med en linje ovanför,
  - knapparna till höger och lika breda,
  - huvudknappen (*Spara*, *Lägg till*, *Skapa* …) i accentfärg.
- Det görs på ett ställe (WindowLook.cs) när varje fönster öppnas. Ingen funktion ändras, och med Visa → *Nytt utseende* avstängt används det gamla utseendet.

## 0.9.38 – 3D-fräsning från STL

- **Rita → 3D-modell från STL…** (eller *3D (STL)…* under RITA). Filen kan vara binär eller ASCII.
  - Du anger bredd och djup i mm. Längden följer bredden.
  - Modellen läggs mitt på materialet med sin högsta punkt på materialets yta.
  - STL-filen sparas i projektet, så projektet går att öppna på en annan dator.
  - I ritytan visas modellen som sin omgivande rektangel med namn och djup.
- **Ny bearbetning: 3D-fräsning (STL).** Väljs automatiskt för en 3D-modell. Fräsen går i rader längs X över modellens höjdkarta. Fräsens spets ligger alltid så att den raka eller runda änden precis nuddar ytan, så fräsen gör inga felskär i modellen.
  - **En fräs:** nivå för nivå (djup per pass) ned till modellen.
  - **Med andra fräs** (*Finfräs med en andra fräs efteråt*):
    1. Den första fräsen grovfräser nivå för nivå. Den lämnar 0,5 mm och fräser bara där det finns material kvar på nivån.
    2. Den andra, helst en kulfräs, finfräser hela ytan i ett sammanhängande sicksack med tät stegning (10 %).
    3. Det blir två program, ett per fräs.
  - Fräsen går aldrig utanför modellens rektangel. Kanterna får därför fräsens radie.
- Banorna har ett Z-värde per punkt hela vägen: i G-code (G1 X Y Z), i 3D-vyn, i materialsimuleringen och i Z-kontrollerna.
- Packa på materialet kan vrida 3D-modeller, och Duplicera och Kopior i rutnät kopierar dem.
- Logiken finns i `Stl`, `Heightmap` och `Carve3DToolpaths` (Core) med tester:
  - läsning av binär och ASCII-STL,
  - höjdkarta och vridning,
  - att fräsen aldrig går under ytan (kul och rak),
  - grovfräsning med tillägg,
  - hela kedjan till två program G-code.

## 0.9.37 – Styr från mobilen

- Välj **Maskin → Styr från mobilen…** och klicka **Starta**. Fönstret visar en adress, till exempel `http://192.168.1.23:8765/`, och en **PIN-kod** med 6 siffror. Öppna adressen i telefonens webbläsare. Telefonen måste vara på samma wifi som datorn.
- **På telefonen finns:**
  - en stor **STOPP**-knapp,
  - X/Y/Z-positionen och maskinens läge, samt programmets förlopp med **Paus** och **Fortsätt**,
  - **jogg** (ett tryck = ett steg, Precis/Normal/Snabb),
  - Nolla XY eller Z, Till X0 Y0, Homing, Lås upp och Kylning,
  - knappar för dina **sparade platser**,
  - matnings-, spindel- och snabbgångsöverstyrning.
- **Säkerhet:**
  - Varje anrop kräver PIN-koden, och efter fem fel väntar servern en minut.
  - Telefonen kan bara ge de kommandona ovan. Den kan inte skicka egen G-code eller ändra inställningar.
  - Program startas och spindeln slås på **bara vid datorn**.
  - STOPP på telefonen är en omedelbar paus (feed hold). Du kan sedan fortsätta, eller stoppa programmet helt vid datorn.
- Statusraden visar **📱 Mobilen på** medan servern är igång. Servern stängs när programmet stängs.
- Windows frågar första gången om CNC Studio får ta emot anslutningar. Tillåt för privata nätverk.
- Servern (`RemoteServer`, Core) är en liten inbyggd webbserver som inte kräver administratörsrättigheter. Den har tester för PIN, spärr och tillåtna kommandon, och en riktig anslutning över nätverket.

## 0.9.36 – Grov- och finbearbetning med två fräsar

- I Smart CAM har **Ficka**, **V-carving** och **Inlay-hane** fått rutan **Andra fräs**. Välj fräs ur biblioteket (diameter, matning och varvtal fylls i) eller ange den själv.
  - **Ficka – detaljer och hörn med en mindre fräs efteråt:** den stora fräsen tar fickan. Den mindre fräsen tar bara det den stora inte når, alltså hörn och smala partier (restbearbetning). Den går inte runt igen där allt redan är fräst. Om den stora fräsen redan når allt får du veta att den andra inte behövs.
  - **V-carving – rensa breda bottnar med en rak fräs först:** den raka fräsen fickfräser de plana bottnarna på fullt djup. V-fräsen gör sedan bara kanterna och en smal remsa av bottnen längs dem. Det går mycket fortare än att rensa med V-fräsen, och den raka fräsen rör aldrig V-väggarna.
- Varje fräs blir **ett eget program**, som alltid när fräsarna är olika. Nolla Z med varje ny fräs. Bottnarna körs före V-carvingen, och detaljerna efter fickan.
- Andra-fräsens pass syns som egna rader i CAM-trädet (*Ficka – detaljer*, *V-carving – bottnar*) och kontrolleras som allt annat. De sparas som en del av operationen.
- **Rättat:** inlay-hanen kunde inte beräknas för vektorformer, som text, i programmet. Det fungerade bara i testerna.
- Logiken finns i `CamSecondPass` (Core) med tester: ordning, restområde i en smal slits, ingen extra fräsning där den stora redan nått, bottnar för den raka fräsen och två program.

## 0.9.35 – Planhetskontroll

- **Planhet…** i maskinpanelen, även under Maskin → *Planhetskontroll…*. Fräsen probar 5 eller 9 punkter över det markerade, eller över hela materialet, med den vanliga probplattan.
  - Programmet guidar en punkt i taget: fräsen åker dit, du lägger plattan under och klickar *Proba*. Du kan hoppa över punkter.
- **Rapporten** visar:
  - skillnaden mellan högsta och lägsta punkt, och varje punkts höjd jämfört med medel,
  - om ojämnheten är **lutning** (materialet ligger snett) eller **buktighet** (kupat eller skevt). Det räknas fram med ett anpassat plan.
- **Vad det betyder för jobbet.** Med en V-fräs i projektet anges hur mycket smalare spåren blir där ytan ligger lägst. Rapporten föreslår också vad du kan göra: kontrollera underlaget, planfräsa offerskivan, hyvla eller planfräsa ytan.
- Z-nollan ändras inte. Rapporten föreslår att du nollar på den högsta punkten.
- Full höjdkarta med autonivellering har flyttats till *Kanske senare*. Den kräver en 3D-touchprob som känner av trä.
- Logiken finns i `Flatness` och `ProbeCycle.ProbeSurfaceHereAsync` (Core) med tester, bland annat probning på demomaskinen.

## 0.9.34 – Nesting och figurbank

- **Packa på materialet…** (nesting) finns under OBJEKT och i Objekt-menyn. Det packar de markerade delarna, eller alla om inget är markerat, tätt på materialet.
  - Delarna packas efter sin **verkliga form**, inte bara den omgivande rektangeln. Till exempel blir två trianglar en rektangel.
  - De största delarna placeras först, så långt bak till vänster som möjligt, och skjuts sedan upp och åt vänster så långt de glider.
  - Du väljer avstånd mellan delarna och till kanten. Förslaget är största fräsen plus 2 mm, så att spåret mellan delarna ryms.
  - Delarna får vridas 90/180/270° om du tillåter det. En grupp packas som en del. En vriden text blir en vanlig form.
  - Delar som inte får plats står kvar och listas.
- **Figurbank…** (knappen *Figurer…* under RITA och i Rita-menyn): stjärna, polygon, hjärta, pil, ring och rundad rektangel. Du väljer bredd och höjd, antal spetsar eller sidor, inre storlek och hörnradie, och ser en förhandsvisning direkt. Figuren hamnar mitt på materialet.
- Logiken finns i `Nesting` och `Figures` (Core) med tester: ingen överlappning, rotation när något inte får plats annars, verklig form, figurernas storlek.

## 0.9.33 – Justera, fördela, offset och kopior

- **Justera:** vänster, mitten, höger, överkant, mitten eller nederkant. Finns under OBJEKT och i Objekt-menyn.
  - Flera objekt justeras mot markeringen.
  - Ett enda objekt, eller en grupp, justeras mot **materialet**. Det är det snabbaste sättet att centrera en skylt på skivan.
  - En grupp flyttas alltid som en del.
- **Fördela ↔ / ↕:** lika mellanrum mellan minst tre objekt. De två yttersta står kvar.
- **Offset…:** en ny kontur ett valt antal mm utanför (+) eller innanför (−) formen, med rundade eller skarpa hörn. Används till exempel för en ram runt en text eller en förminskad kopia. Hålen följer med, och originalet ändras inte.
- **Kopior i rutnät…:** till exempel "3 x 2, 10" ger 3 × 2 med 10 mm mellanrum. CAM-inställningarna följer med varje kopia. Statusraden varnar om rutnätet går utanför materialet.
- Logiken finns i `Arrange` (Core) med tester.

## 0.9.32 – Bild till vektor

- **Rita → Bild till vektor…** (eller knappen *Bild…* under RITA). Välj en PNG, JPG, BMP, GIF eller TIFF. Bilden visas med den spårade konturen ovanpå, och konturen ändras direkt när du drar i reglagen:
  - **Tröskel**: hur mörkt som räknas som formen.
  - **Invertera**: ljusa delar blir formen, till exempel vit text på svart.
  - **Utjämning**: tar bort pixeltrappsteg och ger färre punkter.
  - **Ta bort prickar och hål**: tar bort smuts och små hål.
  - **Bredd i mm.**
- Resultatet blir en vanlig vektorform mitt på materialet. Hål behålls, till exempel i bokstäver och ringar. Formen kan fickfräsas, V-carvas, skäras ut eller användas till inlay.
- Bäst blir logotyper, teckningar och silhuetter med tydliga kanter, inte foton. Stora bilder skalas ned till högst 1200 pixlar på längsta sidan, och genomskinligt räknas som vitt.
- Logiken finns i `ImageTrace` (Core) med tester: ring med hål, prickar, invertering, skala och fickfräsning.

## 0.9.31 – Sparade platser

- Maskinpanelen har fått en ny del, **PLATSER**: en knapp per sparad plats, till exempel *Fräsbyte* eller *Parkering*. Ett klick och maskinen åker dit. Z lyfts alltid först, sedan går den till X/Y och sist ned till platsens Z om den har ett (G53, maskinkoordinater).
- **Platser…** (även under Maskin-menyn) visar dina platser:
  - **Spara där fräsen står:** jogga dit, ge platsen ett namn och klicka.
  - Skriv in värdena själv, ändra ordningen eller ta bort platser.
  - **Lägg till förslag** ger *Fräsbyte* framtill i mitten och *Parkering* baktill.
  - *Z stannar uppe* är förvalt, vilket passar fräsbyte och parkering.
- Platserna hör till **maskinen**, inte till projektet. De sparas i `Inställningar\Maskiner\Platser\<id>.json` och följer med som de andra maskininställningarna.
- Maskinen måste vara homad. Platser utanför maskinens rörelse, minus marginalen, godtas inte.
- Logiken finns i `SavedLocations` (Core) med tester, bland annat en körning på demomaskinen.

## 0.9.30 – Starta från rad

- **Starta från rad…** i Program-panelen, som i gSender och Easel. Visar programmets rader. Välj rad genom att klicka, skriva radnumret eller **hoppa till en del**. Programmets egna kommentarer blir delarna, till exempel en operation eller ett pass.
- Ligger raden mitt i en fräsning börjar programmet på **närmaste säkra punkt före** den, med samma logik som *Återuppta* efter ett avbrott. Det kan vara en förflyttning på säker höjd, början av ett djuppass, eller nedmatning i ett spår som redan är fräst. Fönstret visar var det börjar och hur maskinen kör in.
- **Inkörningen:**
  1. Lägena sätts.
  2. Z lyfts i maskinkoordinater.
  3. Fräsen går till XY.
  4. Spindeln startar. Styrs den för hand pausar programmet med M0.
  5. Fräsen går ned och fortsätter.
- Samma kontroller som Starta: homing och att X/Y-nollan håller fräsen inom maskinen.
- Inkörningen (gäller även *Återuppta*) följer nu programmets egna enheter (G20/G21) och koordinatsystem (G54–G59). Det spelar roll för G-code från andra program. Z lyfts alltid i mm innan enheterna byts.
- Logiken finns i `ResumePlanner.PlanFromLine` (Core) med tester, bland annat en körning på demomaskinen.

## 0.9.29 – Spindel, snabbgång och kylning

- Program-panelen har fått två nya rader under **Matning**:
  - **Snabbgång** 25 / 50 / 100 %. Sänker G0-förflyttningarna medan programmet kör.
  - **Spindel** −10 / −1 / 100 / +1 / +10 %. Ändrar programmets varvtal, 10–200 %. Fungerar bara när spindeln styrs av GRBL.
  - Procenten som visas kommer från maskinen själv.
- Nya knappar för **Spindel på/av**, **Kylning** (M8) och **Dimma** (M7). De lyser gröna när maskinen rapporterar att något är på.
  - **Stillastående:** Spindel på startar med M3 och varvtalet i rutan, efter en säkerhetsfråga. Spindel av stoppar med M5.
  - **Pausat:** spindeln stoppas och startas med GRBL:s 0x9E och startar själv igen när du klickar Fortsätt.
  - **Medan programmet kör** styr programmet spindeln. Då ändrar du varvtalet med spindelöverstyrningen.
  - **Kylning och dimma** går att slå på och av även medan programmet kör.
- Styrs spindeln för hand (inställning per maskin) är spindelknapparna avstängda.
- Demomaskinen har spindelöverstyrning, kylning och dimma, så allt går att prova utan maskin. Reglerna finns i `SpindleCoolant` (Core) med tester.

## 0.9.28 – V-carve inlay (hona + hane)

- Markera formen, till exempel en text, och välj **CAM → Skapa inlay (hona + hane)…**. Välj V-fräs, honans djup, hanens startdjup och djup, spel och tjockleken på hanens skiva. Dialogen visar vad passningen ger.
- **Honan** blir en V-carving till honans djup i det öppna projektet.
- **Hanen** sparas som ett **eget projekt** bredvid. Formen är speglad, och skivan är precis så stor som behövs. Hanen fräses med den nya bearbetningen **Inlay-hane (V-fräs)**: V-fräsen fräser runt formen, med början på startdjupet precis vid konturen. Formen står kvar som en plugg och görs lite mindre med *spelet*.
- **Varför det passar:** vänd hanen så går den ned startdjupet i honan innan väggarna möts. Det ger en glipa i botten för limmet (honans djup minus startdjupet). Hanen sticker upp hanens djup minus startdjupet och sågas eller planas bort när limmet har torkat.
- Bokstäver som står tätt slås ihop till en enda form i hanen, så att fräsningen runt en bokstav aldrig skär in i nästa.
- Den gamla inlay-panelen, som bara sparade inställningar och inte gav någon G-code, är borttagen.
- Logiken finns i `Inlay` och `VCarveToolpaths` (Core) med tester: passningsmått, spegling, hanens djup och G-code för hanens projekt.

## 0.9.27 – V-carving följer varje del inåt

- När en bokstav delar sig på djupet, till exempel benen i n och m eller runt hålen i B, e och ö, fräser V-carvingen nu **en del i taget hela vägen inåt** innan den byter till nästa. Tidigare hoppade den mellan delarna för varje varv, med ett lyft och en nedmatning varje gång.
- Fräsen lyfter nu ungefär en gång per del och djupnivå i stället för en gång per varv.

## 0.9.26 – V-carving bokstav för bokstav

- V-carving fräser nu **en bokstav i taget**. Varje del fräses klar, med alla djupnivåer, innan fräsen flyttar till närmaste nästa del. Tidigare gick den ett varv i varje bokstav och hoppade sedan vidare, vilket gav massor av förflyttningar över hela texten.
- **Inga lyft för varje litet steg inåt.** När nästa varv ligger nära och konen håller sig inne i varvet som just fräses, fortsätter fräsen dit på samma djup och matar sedan ned det sista. Den lyfter bara när den måste, till exempel mellan bokstäver eller runt hål.
- Det ger mycket kortare körtid och en lugnare 3D-bild.

## 0.9.25 – V-carving

- Ny bearbetning **V-carving (V-fräs)** i Smart CAM. Välj en V-fräs i listan så väljs V-carving automatiskt, och vinkeln hämtas från fräsen (fältet *V-vinkel*).
- V-fräsen fräser formen med väggar som följer konturen exakt: skarpa hörn blir skarpa, smala partier grunda och breda djupa. Det passar text, skyltar och logotyper. Hål i bokstäverna behålls.
- **Djupet** följer formens bredd. Det begränsas av *Totalt djup* och av fräsens bredd, eftersom konen aldrig är bredare än fräsen. Partier som är bredare än så får **plan botten** på det djupet och rensas med V-fräsen i täta varv (åsar högst 0,2 mm). Stora ytor går fortare att fickfräsa först med en rak fräs.
- *Djup per pass* gäller: djupa partier fräses i flera nivåer.
- Hur det går till: formen dras in i små steg, och varje varv fräses på det djup där konen precis når konturen vid ytan. Mellan varven lyfts fräsen till nedmatningshöjden.
- Ritytan visar varje varvs verkliga bredd. 3D-vyn och materialsimuleringen använder fräsens rätta vinkel (tidigare alltid 90°).
- En V-fräs vid kontur eller ficka ger fortfarande ett kritiskt fel, men nu med tips om V-carving.
- Logiken finns i `VCarveToolpaths` (Core) med tester: djup efter bredd, plan botten, fräsens bredd, validering samt rörelseplan och G-code.
- Inlay (hane och hona) kommer senare.

## 0.9.24 – Text med typsnitt

- **Rita → Text…** (eller knappen *Text…* under RITA): skriv en eller flera rader, välj bland datorns typsnitt, fet/kursiv, justering, radavstånd och höjden i mm. Förhandsvisningen visar texten och dess storlek direkt.
- Texten blir en vektorform precis som en importerad SVG: **fickfräs** den (gravyr) eller **skär ut** den. Hål i bokstäverna (o, a, e, å …) behålls, och bokstäver som går ihop (skrivstil, accenter) slås ihop till en yta så att inget fräses två gånger.
- **Dubbelklicka på texten** (eller Rita → *Redigera text…*) för att ändra ord, typsnitt eller höjd. Den behåller plats, grupp och CAM-operation.
- Texten och typsnittet sparas i projektet. Saknas typsnittet på en annan dator syns formen ändå, men den kan bara redigeras med ett annat typsnitt.
- Tunna bokstäver kräver en smal fräs – CAM-kontrollen visar det som fräsen inte når. V-carve kommer senare.
- Logiken finns i `TextShapes` (Core) med tester: skalning, sammanslagning, hål och fickfräsning runt hålen.

## 0.9.23 – Jogga med tangentbordet

- **Jogga med tangenterna** (som i gSender och UGS): ← → för X, ↑ ↓ för Y, Page Up/Page Down för Z. Ett kort tryck tar ett steg; håller du tangenten inne längre än 250 ms joggar maskinen tills du släpper (jog cancel 0x85). **Esc** stoppar joggen, **1/2/3** väljer Precis/Normal/Snabb.
- **Egna kortkommandon:** Maskin → *Tangentbord…* eller *⌨ Tangenter…* i JOGG-panelen. Klicka på en åtgärd och tryck den nya tangenten, gärna med Ctrl/Shift/Alt. Förutom joggen (även diagonalt) finns hastighet, Nolla X + Y, Nolla Z, Gå till X0 Y0, Homing, Paus och Fortsätt. Samma tangent för två åtgärder, eller tangenter programmet redan använder (F1–F4, Ctrl+S, Ctrl+Z …), godtas inte.
- Säkert: tangenterna verkar bara i arbetsytan Maskin (kan stängas av), aldrig när du skriver i ett fält, inte medan ett program eller makro körs (utom Stopp, Paus och Fortsätt), och joggen stoppar om fönstret tappar fokus.
- Sparas i `Inställningar\tangentbord.json` och följer med till andra datorer som övriga inställningar.

## 0.9.22 – Rita konturen i luften

- **Program → Rita konturen…** (som gSenders *Outline*): fräsen lyfts till säker höjd (maskinkoordinater, som Gå till) och åker runt det laddade programmets yttre gräns – i luften, utan att gå ned – och sedan tillbaka dit den stod. Då ser du att jobbet ryms på materialet och går fri från fastspänningarna innan du startar.
- Välj **Yttre form** (konvext hölje runt alla fräsrörelser, som ett gummiband) eller **Rektangel** (programmets yttre rektangel). Snabbförflyttningar räknas inte.
- Hastigheten är joggens mm/min (begränsad till maskinens max). Kräver homing; samma kontroll som Starta görs först – konturen måste rymmas i maskinen med den nolla som är satt.
- Körs som ett makro: **Stopp** i Program-panelen avbryter. Datorn hålls vaken under tiden.
- Logiken finns i `JobOutline` (Core) med tester, bland annat en körning på den simulerade maskinen.

## 0.9.21 – Håller datorn vaken
Om Windows går i viloläge mitt i ett jobb avbryts sändningen, och om datorn somnar medan maskinen är ansluten bryts USB-anslutningen. CNC Studio sköter nu viloläget, som gSender gör.
- **Ansluten:** datorn får inte gå i viloläge, men skärmen får släckas.
- **Program eller makro körs:** både datorn och skärmen hålls vakna, så att du kan följa körningen.
- **Frånkopplad eller klar:** Windows får sova som vanligt igen.
- Varje ändring skrivs i maskinloggen och i konsolen. Tekniskt används Windows `SetThreadExecutionState`, och nivåerna räknas ut i `KeepAwake` i Core.
- Det skyddar inte mot att någon stänger locket på en bärbar dator eller trycker på strömknappen. Det skyddar inte heller mot Windows energisparfunktion för USB-portar (*USB selective suspend* i energiinställningarna). Stäng gärna av den på datorn vid maskinen.

**Tester:** 567 tester. Kompilerad utan fel och varningar.

## 0.9.20 – Kör G-code från andra program
Program från Fusion 360, VCarve, Easel och andra CAM-program kan nu köras i CNC Studio, som i gSender, UGS och Easel.
- **Program → Öppna G-code-fil…** öppnar .nc-, .gcode-, .ngc-, .tap-, .cnc-, .gc- och .txt-filer.
- **Filen granskas för GRBL innan den laddas** (`ExternalGCode`). Kommentarer, tomma rader och `%` tas bort. Det som GRBL 1.1 inte kan köra stoppar filen, med radnummer och förklaring:
  - M6 (verktygsbyte). Spara ett program per fräs i CAM-programmet.
  - G41/G42 (radiekompensering). Välj kompensering i datorn.
  - G81–G89 (borrcykler). Låt CAM-programmet skriva ut dem som vanliga rörelser.
  - G43 (som inte är G43.1), A-, B- och C-axlar, programnummer (O) och okända G- och M-koder.
  - Inställningsändringar och annat som inte heller får finnas i makron, samt för långa rader.
  - Rutan föreslår GRBL-postprocessorn i CAM-programmet.
- **Fakta om filen innan den laddas** (som gSenders *file insight*):
  - antal rader och om filen har bågar eller är i tum
  - utbredning i X och Y, djupaste och högsta Z
  - matning, varvtal och om filen startar spindeln
  - verktyg (T-ord och T-kommentarer), pauser och ungefärlig tid
  - Rutan varnar för tum, relativa rörelser och flera verktyg. Den påminner om var nollpunkten ska vara och om att filen inte kontrolleras mot material eller fastspänningar.
- **Det laddade programmet körs som ett projektprogram:** Kontrollkör ($C), Starta, Paus, Stopp, matning, återupptagning efter avbrott, banan i maskinvyn och 3D-vyn och maskinloggen. Senast körda öppnar samma fil igen.
- **Bågar (G2/G3) läses nu** (`GCodePath`), med I/J, R och hela cirklar, i tum och mm. De delas i bitar på högst 1 mm, och bågens yttersta punkter tas alltid med, så att utbredningen och kontrollerna stämmer. Demomaskinen kan också köra bågar och följer GRBL:s mjuka gränser längs bågen, så att filer kan provas på den.
- **Kontrollen före start omfattar nu Z** (`JobEnvelope`). Med den Z-nolla som är satt (G54 Z + G92 Z + verktygslängd) får programmet inte gå upp i Z-brytaren eller under −$132. Rutan säger hur långt utanför det går och att Z-nollan ska kontrolleras. Det gäller även projektens program.

**Tester:** 563 tester. En fil i Fusion-stil (bågar, spindel, lyft) körs till slut på den simulerade maskinen efter kontrollerna. Kompilerad utan fel och varningar, men inte körd i Windows ännu.

## 0.9.19 – Nytt utseende
Lappen "Snygga till GUI, ser lite ut som ett 90-tals windows-program". 90-talskänslan kom från Windows egna standardkontroller, som låg kvar i det mörka gränssnittet: knappar med grå gradient och 3D-kant, vita inmatningsfält med svart text, ljusgrå rullgardinsmenyer och Windows kryssrutor, rullningslister och verktygstips.
- **Ett gemensamt tema** (`Theme.xaml`) gäller i alla fönster, både huvudfönstret och dialogerna:
  - **Knappar:** platta med rundade hörn. Hover ljusar upp och tryck mörkar knappens egen färg, så att gröna, röda och blå knappar behåller sin färg. En knapp med tangentbordsfokus får en blå kant, och avstängda knappar tonas ned.
  - **Inmatningsfält:** mörka med ljus text, rundade hörn, ljusare kant vid hover och blå kant vid fokus. Fält som bara går att läsa är något mörkare.
  - **Rullgardinsmenyer:** mörka med pil, och listan är mörk med blå markering. Det gäller även menyer där man kan skriva, som kategori och skärmaterial.
  - **Kryssrutor och radioknappar:** mörka rutor som blir blå med vit bock eller prick.
  - **Rullningslister:** smala och rundade, utan pilknappar.
  - **Listor, tabeller och förloppsindikatorer:** mörka listor med rundade rader och blå markering, mörka tabeller (skärdata och $$) med mörka kolumnrubriker, och rundade förloppsindikatorer.
  - **Verktygstips och högerklicksmenyer:** mörka med rundade hörn. Långa verktygstips radbryts.
- Färger som en enskild knapp har, till exempel Starta i grönt och Stopp i rött, gäller fortfarande. Temat ritar med kontrollens egen färg.
- **Visa → Nytt utseende** stänger av temat och går tillbaka till Windows egna kontroller. Valet sparas per dator och gäller när CNC Studio startas om.
- Fönstrens egna knapp- och fältstilar bygger nu på temat (`BasedOn`) i stället för att ersätta det.

**Tester:** 549 tester. Kompilerad utan fel och varningar, men **inte körd i Windows ännu**. Utseendet har ingen sett än. Ser något fel ut, ta en skärmbild och stäng av *Visa → Nytt utseende* så länge.

## 0.9.18 – Maskininformation
Motsvarar gSenders *Machine Information*. Du öppnar den med **Maskin → Maskininformation…** eller med knappen **Maskininfo…** i Maskinstyrning. Det är ett vanligt fönster som kan ligga öppet bredvid arbetet, och med **Alltid överst** stannar det framför huvudfönstret.
- **Överst:** maskin, firmware och läge (Idle, Run, Alarm …).
- **Lägen (ur `$G`)** i klartext: rörelse, koordinatsystem, plan, enheter, avståndsläge, matningsläge, spindel, kylning, verktyg, senaste matning och varvtal.
  - **Gult** markerar ett läge som CNC Studios program inte räknar med: tum (G20), relativt (G91), ett annat koordinatsystem än G54, inverterad tid (G93) eller ett annat plan än XY.
  - Lägena läses när fönstret öppnas, med **Läs lägen ($G)**, och automatiskt varje gång maskinen har stannat efter en körning eller jogg. Det sker bara när maskinen är Idle och inget program, makro eller jogg pågår.
- **Ingångar (live, ur `Pn:`):** X-, Y- och Z-brytare, prob, dörr, håll, återställning och cykelstart. En röd prick betyder att ingången är aktiv.
- **Lås stegmotorerna ($1=255):** håller motorerna strömsatta även när maskinen står still, så att axlarna inte kan skjutas ur läge, till exempel vid fräsbyte. Motorerna blir varmare.
  - Ändringen frågar först och skrivs med en kopia av $$ och kontrolläsning.
  - Avbockad går `$1` tillbaka till värdet från före låsningen, som sparas i maskinloggen. Finns inget sparat blir det GRBL:s standard, 25 ms.
- Logiken finns i `MachineInfo` i Core och har tester. Bland annat kontrolleras att den simulerade GRBL:ens `$G`-svar går att läsa.

**Tester:** 549 tester. Kompilerad utan fel och varningar, men inte körd i Windows ännu.

## 0.9.17 – Makron
Egna kodbitar som körs med ett klick. Panelen **Makron** ligger som flik bredvid *Program* i arbetsytan Maskin och finns också under **Visa → Visa makron**.
- **Panelen:** listan med makron (namn och beskrivning, koden som tooltip), **▶ Kör** eller dubbelklick, *Ändra…*, *Ny…*, *Ta bort*, *Från G-code-fil…* och **■ Stoppa makrot**. Under listan visas maskinens svar, till exempel utskriften från `$#`.
- **Editorn:**
  - namn, beskrivning och koden, en rad per kommando med kommentarer i `( )` eller efter `;`
  - **Infoga** lägger in färdiga rader som var och en förklaras: lyft Z i maskinkoordinater, gå till X0 Y0, relativ flytt, matning, nolla X/Y eller Z, homing, spindel på/av, vänta, paus, `$#` och `$G`
  - **Läs in G-code-fil…** hämtar kod från en .nc- eller .gcode-fil
  - **Kontrollera** visar hur många rader som skickas, om makrot kan röra maskinen och vad som skulle stoppas
- **Kontroll innan makrot körs** (`MacroCode.Prepare`): kommentarer och tomma rader tas bort. Följande stoppas, med en förklaring på vilken rad:
  - inställningar `$n=` (görs i Maskininställningar, där $$ kopieras först)
  - `$RST`
  - startblock `$N`
  - `$SLP`
  - rena realtidstecken `! ~ ?` (de är knappar)
  - tecken utanför ASCII
  - rader längre än 70 tecken
- **Makron som kan röra maskinen** (G0–G3, G28/G30, G38.x, M3/M4, `$H`, `$J=`) visar raderna och frågar innan de körs. Är maskinen inte homad påpekar frågan det. Makron som bara läser, som `$#` och `$G`, körs direkt.
- **Körningen** sker med samma körare som programmen: en rad i taget och väntan på `ok`. Ett `error` eller larm stoppar makrot. **Stoppa makrot** bromsar in, väntar och återställer utan att positionen tappas, som Stopp för program. Ett makro och ett program kan inte köras samtidigt.
- **Lagring:** makrona sparas i `Inställningar\makron.json` och synkas som verktygsbiblioteket. Fyra exempel följer med: *Lyft Z till toppen*, *Gå till arbetsnollan*, *Visa nollpunkter och lägen* och *Spindel av*.
- **Tester:** de körs mot den simulerade GRBL:en. Ett makro körs till slut, och ett makro som skulle gå förbi en mjuk gräns stoppas med ALARM:2.

**Tester:** 544 tester. Kompilerad utan fel och varningar, men inte körd i Windows ännu.

## 0.9.16 – Diagonal jogg
- **Joggkrysset har diagonaler** i de fyra hörnen: ↖ ↗ ↙ ↘.
  - **Klick:** ett steg i både X och Y, till exempel 5 mm åt höger och 5 mm bakåt. Det blir `$J=G91 G21 X5 Y5 F…`, som i gSender.
  - **Håll inne:** kontinuerlig jogg snett, som de raka knapparna. Varje kort jogg-steg är lika långt som rakt, så att fräsen stannar lika fort när knappen släpps.
  - Gränskontrollen räknar med båda axlarna. En diagonal som skulle gå förbi en gräns på någon av axlarna stoppas, och statusraden säger varför.
- Gamepadens spak joggade redan diagonalt.
- Riktningen läses ur knappens tagg av `GrblMachineCommands.JogDirection` och `JogMove` i Core, som har tester.

**Tester:** 515 tester. Kompilerad utan fel och varningar.

## 0.9.15 – Anslutning, maskinstyrning och program var för sig
Lappen "Dela upp maskinkontroll och maskinstart". Maskinen ansluts en gång, men styrs hela tiden, och programmen hör inte hemma i styrningen.
- **Anslutningen ligger i verktygsraden längst till vänster**, som i gSender: en statusbricka (EJ ANSLUTEN / ANSLUTEN / DEMOMASKIN, med detaljer i tooltip), maskinväljaren med ☰, porten med ⟳ och **Anslut**. *Första test mot maskinen* finns kvar i Maskin-menyn.
- **Maskinstyrning** (tidigare Maskinkontroll) innehåller bara det man gör med maskinen: läge och koordinatsystem, GRBL-konsol, brytartest, Återställ, koordinaterna, nollning, homing, probning och jogg.
- **Program** är en ny dockbar panel, som standard under Maskinstyrning:
  - *Avbrutet jobb* (återuppta eller glöm)
  - *Kör program*: Ladda från projektet, Kontrollkör, Starta, Paus, Stopp, förlopp och matning
  - **Senast körda på den här maskinen**, ur maskinloggen. Varje rad visar program, tid, längd och om körningen blev klar, stoppades eller avbröts. **Dubbelklick** eller **Öppna och ladda** öppnar projektet och laddar samma program igen, och det kontrolleras på nytt mot den nollpunkt som gäller nu. Har projektet flera program laddas samma program som förra gången. *Hela loggen…* öppnar maskinloggen.
  - Programpanelen visas i arbetsytan Maskin och kan dockas, lossas och läggas som flik som de andra panelerna. Den finns också under **Visa → Visa program**.
- Maskinloggen sparar nu också projektets sökväg och programnummer för varje körning. Körningar från osparade projekt och återupptagna jobb kan inte öppnas igen.
- En fönsterlayout som sparats med 0.9.9–0.9.14 saknar programpanelen och ersätts därför en gång av standardlayouten.

**Tester:** 499 tester. Kompilerad utan fel och varningar, men inte körd i Windows ännu.

## 0.9.14 – Maskinlogg och underhåll
Motsvarar gSenders *Your Machine*. Varje maskin har nu en egen logg. Du öppnar den med **Maskin → Maskinlogg och underhåll…** eller med knappen **Logg och underhåll…** på maskinens rad i Maskiner.
- **Loggas automatiskt** på den anslutna maskinen:
  - **Varje körning:** start, slut, programnamn, rader körda av totalt, och om den blev klar, stoppades eller avbröts (med meddelandet), samt om den var återupptagen.
  - **Varje larm (ALARM:n):** kod, förklaring och vilket program som körde.
  - **Varje anslutning:** tid, dator och firmware.
- **Statistik:** total körtid, antal jobb (varav klara), genomsnittlig och längsta körning.
- **Underhåll som räknas i körtimmar,** till exempel *Rengör skenor och hjul var 10:e timme*. Varje uppgift visar hur många timmar som gått av intervallet. Den blir gul när 80 % har gått och röd (*DAGS*) när intervallet är fullt. **Gjort** nollställer timmarna. Uppgifter kan läggas till, ändras och tas bort.
  - En ny maskin får fyra **förslag** för en remdriven hobbymaskin: skenor och hjul (10 h), remspänning (25 h), hjul och glapp (50 h) och spännhylsan (25 h). Ändra dem efter din maskins manual.
  - När ett program är klart nämner klart-rutan det underhåll som är förfallet.
- **Fil:** loggen sparas i `Inställningar\Maskiner\Historik\<id>.json`, bredvid maskinprofilen men inte i den, så att projektfilerna inte följer med. Den synkas som profilerna. De senaste 500 jobben och larmen sparas, och äldre jobb räknas fortfarande in i körtiden. Tas maskinen bort följer loggen med.
- Logiken finns i `MachineLog` i Core.

**Tester:** 499 tester. Kompilerad utan fel och varningar, men inte körd i Windows ännu.

## 0.9.13 – Planfräsning över hela rörelseområdet
En stor fräs, till exempel 35 mm, når utanför maskinens rörelseområde. Offerskivan är ofta större än rörelseområdet, och då ska hela ytan som fräsen når kunna planas.
- **Planfräsa offerskivan → "Fräsens centrum över hela rörelseområdet – fräsen går utanför"** (förvalt):
  - Spindeln, alltså fräsens centrum, går ända ut till marginalen vid gränslägena på alla fyra sidor, och fräsen planar en radie utanför. Med en 35 mm fräs, 500 × 500 mm rörelse och 3 mm marginal går centrum 3…497 mm och fräsen planar −14,5…514,5 mm.
  - Storlek och placering bestäms av maskinen, så de fälten är gråa. Formuläret talar om exakt vad som planas och hur långt utanför rörelseområdet fräsen går. Se till att inget annat står där.
  - Utan valet håller sig hela fräsen innanför, som tidigare. Då väljer du själv ytans storlek och placering.
- **CAM-panelen, planfräsning → "Fräsens centrum får nå gränslägena"**: samma sak för en vanlig planfräsning. CAM-kontrollen räknar då bara med fräsens centrum, inte hela fräsen. Det gäller bara planfräsning. Alla andra bearbetningar kräver fortfarande att hela fräsen är innanför.
- GRBL:s mjuka gränser gäller spindelns position, och kontrollen före start (0.9.11) räknar med fräsens centrum. Den skyddar alltså fortfarande mot ALARM:2.
- Materialet som ritas i maskinvyn och 3D-vyn är radernas område: centrumets yta minus utgången förbi kanten längs raderna. Den planade ytan är större än så.

**Tester:** 492 tester. Hela kedjan, från CAM-kontroll via G-code och den oberoende kontrollen till kontrollen före start, är testad med 35 mm fräs för olika hemhörn och radriktningar. Testerna visar att centrum når exakt −497…−3 i maskinkoordinater.

## 0.9.12 – Nolla X/Y vid materialet
- **Nolla X + Y ▾ → Vid materialet…** i Maskinkontroll, eller **Maskin → Nolla X + Y vid materialet…**, sätter X0 Y0 där materialet är ritat i maskinvyn, utan att du behöver jogga dit:
  - Nollpunkten är materialets främre vänstra hörn eller mitten, beroende på projektets nollpunkt.
  - Programmet räknar ut punkten från materialplaceringen och maskinens egna $23, $130 och $131, på samma sätt som fräsmarkören. Rutan visar avståndet från hemhörnet och MPos innan något ändras.
  - Nollan sätts med `G10 L20 P0` från där fräsen står, så **maskinen rör sig inte**. Z ändras inte.
  - Efteråt läses nollan tillbaka med `$#` och jämförs med det beräknade värdet. En laddad körning kontrolleras om mot den nya nollan.
  - Kräver att maskinen är homad. Det stämmer bara om materialet ligger där det är ritat. Vid planfräsning av offerskivan gör det alltid det.
- Knappen **Nolla X + Y** nollar fortfarande där fräsen står. Pilen **▾** bredvid har båda valen, *Här* och *Vid materialet*, så det finns inte två knappar för samma sak.
- **Planfräsa offerskivan** när maskinen är ansluten och homad:
  - Maskinens Z-nolla läses in direkt (G54 Z + G92 Z + TLO, med samma kontroller som *Läs från maskinen*). Du behöver alltså inte fylla i *Maskinens Z vid materialytan* för hand. Har du bytt fräs ska du proba Z igen.
  - Rutan efter *Skapa projekt* har knappen **Nolla X/Y vid ytan nu**.
- Logiken finns i `WorkZeroPlacement` i Core. Testerna kör alla fyra hemhörn och kontrollerar mot den simulerade GRBL:en att G54 hamnar på den beräknade maskinpositionen.

**Tester:** 487 tester. Kompilerad utan fel och varningar.

## 0.9.11 – Kontroll av X/Y-nollan före start
Rättning efter första körningen av planfräsningen. X och Y var aldrig nollade, så X0 Y0 låg kvar på maskinens nollpunkt i hörnet mitt emot hemhörnet. Programmet gick därifrån +409 mm, och GRBL stoppade med **ALARM:2 – Soft limit** innan något rörde sig. Programmets egen kontroll granskade bara Z mot nollan, inte X och Y.
- **Starta** och **Återuppta** läser nu maskinens verkliga nollpunkt med `$#` (G54 + G92). De räknar om hela programmets bana till maskinkoordinater och stoppar om den skulle gå utanför −$130…0 eller −$131…0, alltså samma gränser som GRBL:s mjuka gränser.
- Rutan som visas talar om vilken axel som går hur långt förbi gränsen, var X/Y-nollan står nu och var den ska stå (materialets främre vänstra hörn eller mitt). Den säger också att du ska jogga dit och klicka *Nolla X + Y*.
- Om `$#` inte går att läsa får du välja själv om du vill fortsätta.
- Logiken finns i `JobEnvelope` i Core och har tester, bland annat med just den nollpunkt som orsakade larmet.

**Tester:** 478 tester. Kompilerad utan fel och varningar.

## 0.9.10 – Planfräsa offerskivan
Verktyget motsvarar gSenders *Wasteboard Surfacing* och finns under **Maskin → Planfräsa offerskivan…**. Alla inställningar görs i ett formulär:
- **Fräs:** välj en fräs ur verktygsbiblioteket. Planfräsar visas först, sedan de bredaste. Diameter, matning, nedmatning och varvtal fylls i från fräsen. Ställ in stegning (standard 60 %) och om raderna ska gå längs X eller Y.
- **Yta:** bredd × djup och offerskivans tjocklek. **Hela bordet** fyller i den största ytan som går att planfräsa: maskinens arbetsområde minus det som krävs för att raderna ska gå ut så långt att hela fräsen lämnar ytan, plus marginalen till gränslägena. Ytan kan ligga **centrerad på bordet** eller på ett avstånd **från hemhörnet**, som under Materialplacering. En yta för nära kanten flyttas in.
- **Djup:** totalt djup och djup per pass (standard 0,5 mm), högst 5 mm. Offerskivan ska göras plan, inte bytas.
- **Skapa projekt** gör om formuläret till ett vanligt projekt: offerskivan blir material, en rektangel täcker ytan och planfräsningen läggs på den. Därför gäller samma CAM-kontroll, G-code-kontroll och körning som för alla andra projekt. Projektet kan sparas och köras igen nästa gång.
- **Det öppna projektet:** om det har innehåll frågar programmet först om det ska sparas.
- **Instruktioner efter Skapa projekt:** en ruta talar om hur programmet körs. Homa, och jogga sedan tills fräsmarkören i maskinvyn visar X/Y för ytans främre vänstra hörn (talen räknas fram åt dig). Nolla X och Y där, nolla Z på offerskivans ovansida och ladda programmet.
- Logiken finns i `WasteboardSurfacing` (Core). Testerna kör hela kedjan för alla fyra hemhörn: CAM-kontroll, rörelseplan, G-code och den oberoende kontrollen.

- **Efter första provet:** rutan efter *Skapa projekt* sa "Ladda program", men knappen heter **Ladda från projektet**. Texten är rättad. *KÖR PROGRAM* säger nu "Inget program laddat – klicka Ladda från projektet", och när Starta är grå förklarar dess tooltip varför: inte ansluten, inget program laddat eller ett program körs redan.

**Tester:** 474 tester. Kompilerad utan fel och varningar, men inte körd i Windows ännu.

## 0.9.9 – Dockning som i Visual Studio
Panelerna dockas nu med **AvalonDock** (Dirkster.AvalonDock 4.74.1, fri komponent för WPF, med det mörka temat VS2013).
- **Ritytan** med 2D- och 3D-vyn ligger i mitten och kan inte lossas.
- **Ritverktyg, Egenskaper, Maskinkontroll och Smart CAM** är paneler som går att flytta:
  - **Dra i panelens rubrik** för att lossa den till ett eget fönster, som går att flytta och ändra storlek på, även till en annan skärm.
  - **Släpp den på en dockningsmarkör** för att docka den till vänster, höger, upptill eller nedtill, eller som **flik** tillsammans med en annan panel.
  - **Dubbelklicka på rubriken** på ett lossat fönster för att docka tillbaka det.
  - Pilen i rubriken har menyn *Flytande*, *Dockad*, *Dölj* med mera.
- Panelernas egna rubrikrader med ↗ och × är borta, eftersom dockningen ger varje panel en riktig rubrikrad. Panelen Egenskaper heter *Maskinbord* i arbetsytan Maskin.
- Arbetsytorna Design, CAM och Maskin bestämmer fortfarande vilka paneler som visas. En panel som döljs och visas igen hamnar där du senast hade den.
- **Layouten sparas per dator** i `%APPDATA%\CNCStudio\fönsterlayout.xml` när programmet stängs, eftersom skärmarna skiljer sig mellan datorer. **Visa → Återställ fönsterlayout** lägger tillbaka allt som från början. En trasig eller gammal layoutfil ignoreras.
- Den gamla egna lossningen (`FloatPanel_Click`, kolumner och splitters) är borttagen.

**Tester:** 462 tester. Kompilerad utan fel och varningar, men **inte körd i Windows ännu**. Det här är en stor ändring i huvudfönstret. Om något ser fel ut, prova först *Visa → Återställ fönsterlayout*.

## 0.9.8 – Fräsens position i 3D-vyn
- **3D-vyn visar var fräsen står**, som det röda korset i maskinvyn. Den visas när maskinen är ansluten och homad och inte är i larm, och flyttas vid varje statusrapport.
  - Ett rött kors ligger på materialets ovansida när fräsen står över materialet och på bordet annars.
  - Om fräsens höjd är känd visas också fräsen själv: en röd spets på verklig höjd och ett grått skaft. Höjden är arbets-Z, alltså Z0 på materialets ovansida, och den stämmer bara när Z är nollad. En tunn röd linje går från korset upp till spetsen, så att höjden syns mot materialet.
- Kryssrutan **Fräsens position** i 3D-panelen visar eller döljer markeringen.
- Under ett program ersätter markeringen den röda stapeln från tidigare versioner, eftersom båda följer statusrapporterna.
- Placeringen räknas ut på samma sätt som i 2D, från maskinens egna $23, $130 och $131. `MachineTable3D.FromTable` översätter till 3D-vyns koordinater.
- Funktionen går att prova utan maskin: anslut till DEMO-porten, kör homing och jogga.

**Tester:** 462 tester. Kompilerad utan fel och varningar, men inte körd i Windows ännu.

## 0.9.7 – Maskinbordet i 3D-vyn
- **3D-vyn visar maskinens arbetsyta** under materialet, på samma ställe som i maskinvyn i 2D:
  - en mörk bordsskiva med maskinens mått ($130 × $131 från maskinprofilen)
  - ett rutnät räknat från hemhörnet (100 mm, eller 50 mm på ett bord som är mindre än 400 mm)
  - en kantlinje och en grön **HEM**-markering i hemhörnet
  - Materialet ligger på bordet, och fastspänningarna står där de står i maskinvyn.
- Kryssrutan **Maskinbord (arbetsyta)** visar eller döljer bordet.
- **Anpassa vy** zoomar fortfarande till materialet och banorna. Den nya knappen **Hela bordet** zoomar ut till hela maskinbordet.
- Ett projekt utan material visar nu maskinbordet i 3D-vyn i stället för en tom vy.
- Placeringen räknas ut i `MachineTable3D` (Core) och följer samma koordinater som maskinvyn: materialets position räknas från bordets bakre vänstra hörn.

- **Inga kompileringsvarningar längre.** De fem gamla varningarna i `FirstTestWindow`, `LimitSwitchTestWindow`, `LogViewerWindow` och `ProbeWindow` är rättade. Programmet beter sig som förut.

**Tester:** 461 tester. Kompilerad utan fel, men 3D-vyn är inte körd i Windows ännu.

## 0.9.6 – Kontrollera vinkel (XY-squaring)
Kontrollerar att X och Y är vinkelräta, som gSenders squaring-verktyg men med ett bättre råd. Verktyget finns under **Maskin → Maskiner…**, knappen *Kontrollera vinkel…* på maskinens rad. Det går bara på den anslutna maskinen och när maskinen är Idle.
1. **Triangeln:** sträckor i X och Y (standard 80 % av arbetsområdet, minst 50 mm), hastighet och avståndet mellan Y-skenorna, det vill säga gantryts längd (standard X-arbetsområdet, men mät gärna). Riktningarna väljs automatiskt så att triangeln får plats från där fräsen står: åt höger och bakåt om det går, annars åt andra hållet på den axel som inte får plats. De går att ändra.
2. **Märk och flytta:** märk punkt 1 under spetsen och klicka *Flytta X*. Märk punkt 2 och klicka *Flytta Y*. Märk punkt 3. Flyttarna är `$J=`-jogg. Hela triangeln kontrolleras mot maskinens gränser före första flytten. En skiss visar triangeln och var fräsen står. *Tillbaka till punkt 1* går samma väg tillbaka, Y först.
3. **Mät** 1–2, 2–3 och diagonalen 1–3 och klicka *Räkna ut*. Programmet visar:
   - vinkelfelet (cosinussatsen) och diagonalens avvikelse från √(a² + b²).
   - **vilken ände av gantryt som ska flyttas, hur mycket och åt vilket håll:** gantryts längd × tan(vinkelfelet), till exempel *Gantryts högra ände ligger 3,9 mm för långt bak – flytta högra änden 3,9 mm framåt, eller vänstra änden 3,9 mm bakåt*. gSender ger rådet att flytta skenan lika mycket som diagonalen är fel. Med en 300 × 300-triangel på en 750 mm lång gantry är det bara drygt en fjärdedel av det som behövs, och rådet gäller alltid samma håll.
   - **mätosäkerheten:** hur stort vinkelfel ±0,5 mm på varje mått skulle kunna ge. Är felet mindre än så får du rådet att göra triangeln större eller mäta noggrannare innan du justerar.
   - om X- eller Y-sidan avviker mer än 0,3 % från flytten, ett tips om att kontrollera steg/mm med *Kalibrera steg/mm*. Inget skrivs till maskinen, eftersom ett måttband över några hundra millimeter är för grovt för steg/mm.
   - Gränserna är desamma som i gSender: under 0,1° är maskinen vinkelrät. Upp till 2 mm vid gantryts ände räknas som *lite sned*, mer än så som *behöver justeras*.
- **Resultaten sparas per maskin** i `Inställningar\Maskiner\Vinkelkontroll\<id>.json`, så att kontrollen efter en justering kan jämföras med den förra. Fönstret visar de senaste kontrollerna, och efter *Räkna ut* visas förra resultatet bredvid det nya. Tas maskinen bort följer historiken med.
- Riktningarna följer programmets konvention från Första test: X+ åt höger och Y+ bort från dig.

**Tester:** 452 tester, varav 13 för vinkelkontrollen. Beräkningen kontrolleras mot en simulerad gantry som är vriden en känd vinkel, i alla fyra riktningar. Kompilerad utan fel, men inte körd i Windows eller mot maskinen ännu.

## 0.9.5 – Verktygsbibliotek och saker per maskin
Från anteckningarna på Miro-tavlan.
- **Kalibrering och $$-kopior per maskin:** *Kalibrera steg/mm* och *Säkerhetskopia av inställningar ($$)* finns inte längre i Maskin-menyn. De finns i **Maskin → Maskiner…** (eller ☰ vid maskinväljaren), på varje maskins rad:
  - **$$-kopior…** visar bara den maskinens kopior. Jämföra, spara och återställa går när just den maskinen är ansluten.
  - **Kalibrera steg/mm…** går bara på den anslutna maskinen. På de andra är knappen grå med en förklaring.
  - Nya kopior hamnar i `Inställningar\GRBL-kopior\<maskinens namn>`, och varje kopia säger vilken maskin den kommer från (`; Maskin-id:`). Byter du namn på maskinen följer kopiorna med.
  - Äldre kopior, som inte säger vilken maskin de kommer från, visas hos alla riktiga maskiner och märks *äldre kopia, maskin okänd*. Demomaskinens gamla kopior i `GRBL-kopior\Demomaskin` visas bara hos demomaskinen.
- **＋ och − vid maskinväljaren är borta.** Maskiner läggs till och tas bort i Maskiner-fönstret (☰).
- **Huvudfönstret minns sin storlek och sitt läge** när programmet stängs, även om det var maximerat. Det sparas per dator i `%APPDATA%\CNCStudio\settings.json`. Om fönstret skulle hamna utanför skärmen, till exempel för att en extra skärm har kopplats bort, öppnas det i standardläget.
- **Verktygsbiblioteket:**
  - **Spara fräsen** sparar fräsen i biblioteket direkt, och fönstret stängs inte. Det går därför att lägga in flera fräsar i rad. *Ny fräs*, *Kopiera*, *Ta bort* och ändringar av kategorier sparas också direkt. **Stäng** stänger fönstret. Har du ändrat i en fräs utan att spara frågar programmet först (*Spara fräsen*, *Släng ändringarna* eller *Avbryt*). Samma fråga kommer när du byter till en annan fräs i listan. *Ångra ändringar* visar fräsen som den är sparad.
  - **Verktygsnummer** (T-nummer, t.ex. numret på asken), med knappen *Nästa lediga*. Numret visas före namnet i listan och i CAM-panelens fräsväljare. Två fräsar kan inte ha samma nummer. Numret skrivs aldrig i G-code, eftersom maskinen saknar verktygsväxlare.
  - **Inköp:** *Inköpt från*, *Artikelnummer* och *Länk*, med en knapp som öppnar länken i webbläsaren.
  - **Mått som i Fusions verktygsbibliotek:** skaftdiameter, total längd, skärlängd och utstick under spännhylsan. För V-fräsar finns också spetsdiameter. Dessutom skärmaterial (hårdmetall, HSS …). Alla fält är frivilliga. Är skärlängden längre än utsticket visas en varning, men fräsen kan sparas ändå.
  - **Kategorier:** *Ny…*, *Byt namn…* (alla fräsar i kategorin följer med) och *Ta bort…* (en kategori med fräsar kräver att du väljer vart fräsarna ska). Ordningen sparas, och listan och fräsväljaren i CAM visar kategorierna i den ordningen. En tom kategori syns i listan över kategorier när du väljer kategori för en fräs.
  - Gamla bibliotek öppnas som förut, med de nya fälten tomma.

**Tester:** 439 tester. Kompilerad utan fel, men WPF-delen är inte körd i Windows ännu.

## 0.9.4 – En profil per maskin
- **Maskinkontroll → Maskin:** varje maskin har en egen maskinprofil i `Inställningar\Maskiner`, och **demomaskinen är en egen maskin**. Den kan ställas in och testas precis som en riktig maskin nummer två.
  - **Maskin → Maskiner…** (eller ☰ vid väljaren, eller *Maskiner…* under MASKIN) visar alla maskiner. För varje maskin syns mått, hemhörn och port, och märken visar *VALD*, *ANSLUTEN* och *DEMO*. Där kan du välja, ändra (Inställningar…), lägga till och ta bort maskiner.
  - Ansluter du på DEMO-porten väljs demomaskinen. En COM-port väljer maskinen som senast var ansluten där.
  - **＋** skapar en ny maskin som kopia av den valda och öppnar Maskininställningar. **−** tar bort en maskin. Demomaskinen och den sista riktiga maskinen kan inte tas bort.
  - Maskinen byts bara när du är frånkopplad. Den valda maskinen är den som nya projekt får.
  - Den gamla `maskinprofil.json` blir första maskinen ("Min maskin") och demomaskinen startar som en kopia av den.
- **Profil mot maskin:** när profilen och maskinens $$ skiljer sig vid anslutning kan du välja *Använd maskinens värden i profilen*, *Skriv profilens arbetsyta till maskinen* ($130–$132, kopia av $$ först) eller *Låt det vara*. Sparar du Maskininställningar medan du är ansluten och arbetsytan skiljer sig från maskinens, frågar programmet om den ska skrivas till maskinen.
- Kopior av demomaskinens $$ hamnar i `GRBL-kopior\Demomaskin`, skilt från den riktiga maskinens.

**Tester:** 418 tester.

## 0.9.3 – Kalibreringen säger varför den inte öppnas
- **Maskin → Kalibrera steg/mm…** visar nu en ruta med orsaken när maskinen inte är redo, till exempel "maskinen är i larm – kör homing ($H) först" eller "går bara när maskinen är Idle". Förut hamnade texten bara i jogg-panelens statusrad, så det såg ut som att inget hände.

**Tester:** 410 tester.

## 0.9.2 – Kalibrering av steg/mm
- **Maskin → Kalibrera steg/mm…**
  1. Välj axel och sträcka. Standard är 100 mm för X/Y och 20 mm för Z, och både sträcka och hastighet går att ändra.
  2. Markera var fräsen står och klicka *Flytta X+ 100 mm*. Flytten görs som en $J=-jogg och kontrolleras mot maskinens gränser.
  3. Mät hur långt fräsen gick och klicka *Räkna ut*. Nytt = gammalt × beordrat ÷ uppmätt, avrundat till tre decimaler som GRBL lagrar.
  4. Klicka *Skriv $100 = …*. Värdet skrivs med kontrolläsning, och de gamla inställningarna sparas först som kopia, så att ändringen kan ångras.
- Är skillnaden större än 10 % frågar programmet först, eftersom det oftast beror på ett mätfel eller en remskiva som slirar.
- *Flytta tillbaka* tar fräsen till startpunkten, så att du kan kontrollmäta efter ändringen.
- **Demomaskinen minns sina inställningar.** `$$ och G54–G59 sparas i `%APPDATA%\CNCStudio\demomaskin.json` och finns kvar mellan programstarter. Tidigare började den om som X-Carve 750 vid varje start. Demomaskinens ram följer de sparade `$130–`$132, så en demomaskin som är inställd på 500 × 500 *är* 500 × 500 (homing på −499). Porten heter nu *DEMO (simulerad maskin)*.

- **Probning i ett modalt fönster:**
  - Typen väljs med stora knappar (**Z**, **X**, **Y**, **XY** och **XYZ**), och för X/Y-typerna även **hörn**: fram vänster, fram höger, bak vänster eller bak höger. Probrutinen söker åt rätt håll för varje hörn.
  - En skiss visar materialet, plattan, fräsen och sökrörelserna, och en röd punkt markerar var X0 Y0 hamnar.
  - Bara inställningarna för vald typ visas, med hastigheterna under *Avancerat*. Senast använda typ och hörn sparas.
  - Under probningen blir *Stäng* en **STOPP**-knapp (mjuk återställning).
  - För X, Y och XY ställs fräsen **över** plattan som vid XYZ, och Z måste vara nollad först. Fönstret säger det och varnar om det valda hörnet inte är projektets nollpunkt.

**Tester:** 410 tester.

## 0.9.1 – Säkerhetskopia av $$
- **Maskin → Säkerhetskopia av inställningar ($$)…** sparar en kopia av alla GRBL-inställningar, med en valfri anteckning. Kopiorna ligger i `Inställningar\GRBL-kopior` på Drive och är vanlig text i samma form som GRBL skriver dem (`$n=värde`) plus datum, dator, firmware och anledning.
- **Jämför och återställ:** välj en kopia, så visas den mot maskinens inställningar. *Visa bara skillnader* är förvalt, och skillnaderna är markerade och förbockade. *Återställ markerade…* skriver dem en i taget med kontrolläsning och varnar om riktningar, steg/mm, arbetsområde eller gränslägen ändras.
- **Automatisk kopia före varje ändring:** innan CNC Studio ändrar en inställning (maskininställningar, Arbetsområde hit, Första test, brytartest, återställning) sparas nuvarande inställningar med anledningen *Före ändring: $n=…*. Varje ändring går därför att ångra. Rader som skrivs direkt i GRBL-konsolen omfattas inte.
- Kopiorna från Första test (`Loggar\<dator>\$$-backup-*.txt`) visas också i listan.

**Tester:** 395 tester.

## 0.9.0 – Planfräsning
- **Planfräsning (Face)** är en ny bearbetning som fräser hela objektets yta plan (rektangelns eller formens omslutande rektangel).
  - Den fräser rad för rad, fram och tillbaka. Varje rad går ut förbi båda kanterna med fräsradien plus *Ut förbi kanten* (standard 2 mm), så att fräsen lämnar materialet helt.
  - Nedmatningen sker bredvid materialet.
  - Den sista raden ligger alltid på bortre kanten.
  - Stegning (% av fräsdiametern), djup och djup per pass och *Rader längs X eller Y* går att ställa in.
  - Planfräsningen körs alltid **först** och blir eget program om den har en egen fräs.
  - Andra banor får gå över planfräsningsytan utan varning, och planfräsningen varnar inte för delarna under sig.
- **CAM → Planfräs hela materialet…** lägger en rektangel över hela materialet och förbereder en planfräsning. Du väljer fräs och djup och klickar Spara CAM. Planfräsning är aldrig obligatorisk: hoppa över den om materialet redan är hyvlat.
- För att planfräsa offerskivan gör du ett projekt med offerskivans mått som material och planfräsar det.

**Tester:** 391 tester.

## 0.8.9 – Gamepad
- **Styr maskinen med en gamepad.** Xbox-kompatibla kontroller läses via XInput, utan extra program. Upplägget liknar gSender:
  - **Spärrknapp** (standard **LB**) måste hållas in för att något ska hända. Undantaget är **STOPP** (standard **Back**), som alltid fungerar. STOPP avbryter joggen, och under ett program pausar den programmet.
  - **Spakarna joggar steglöst.** Ju längre spaken förs, desto fortare går det, upp till max X/Y respektive Z. Vänster spak styr X/Y och höger spak upp/ned styr Z, och diagonalt fungerar. Joggen sker i korta $J=-steg, och ett joggavbrott (0x85) skickas så fort spaken släpps, spärren släpps eller kontrollen kopplas från. Mjuka gränser och maskinens arbetsområde gäller som vid vanlig jogg.
  - **Knappar som standard:** styrkorset joggar X/Y ett steg, Y/A joggar Z+/Z− ett steg, X nollar X+Y, B nollar Z, vänsterspakens tryck byter Precis/Normal/Snabb, Start är Fortsätt och Back är STOPP.
  - **Andra funktion** (standard **RB**, som Shift): X blir Gå till X0 Y0, B öppnar probning, Y kör homing och Start pausar.
  - **Maskin → Gamepad…** (eller knappen i JOGG): varje knapp kan få en åtgärd och en andra åtgärd. Där väljs också spärrknapp, Shift-knapp, vilken spak som styr vilken axel, invertering, nollgräns (standard 0,25) och maxhastigheter. Knapparna lyser grönt live när de trycks. Inställningarna sparas i `Inställningar\gamepad.json` och följer med via synkmappen.
  - Under ett program fungerar bara Paus, Fortsätt och STOPP från kontrollen.
- **Joggränserna kommer från den anslutna maskinen** ($130–$132, $23 och $27, som läses vid anslutning och efter homing), inte från projektets profil. Tidigare kunde ett projekt gjort för 500 × 500 hindra en 750-maskin från att jogga mot brytarna, fast fräsen stod mitt på bordet.

**Tester:** 386 tester.

## 0.8.8 – Rampfart, slutposition och snabb nedgång
Förbättringarna kommer från ett program som har fungerat på maskinen (Kapsylöppnare.nc). Alla värden går att ändra.
- **Rampens matning** ställs in per bearbetning, i % av matningen (standard 70 %, 5–100 %). Fältet "Matning %" ligger bredvid rampvinkeln. Tidigare rampade programmet i full skärhastighet. Tidsuppskattningen räknar med rampens matning.
- **Slutposition** under Z-höjder: *Stanna över sista skäret*, *Arbetsnollan (X0 Y0)* (standard) eller *Egen punkt* (X/Y i arbetskoordinater). Fräsen åker dit på start- och sluthöjden. En punkt utanför arbetsområdet ger ett kritiskt CAM-fel.
- **Snabbt ned i redan fräst spår** under Z-höjder, med inställbar höjd över det frästa djupet (standard 0,5 mm). När fräsen går ned igen där det redan är fräst, till exempel på fickans nästa djup, går den med G0 genom luften och matar bara den sista biten. Funktionen kan stängas av. En hållflik i startpunkten räknas som material.
- Gamla projekt får standardvärdena när de öppnas.
- **Programmet öppnar blankt:** en startvy med *Nytt projekt*, *Öppna projekt…* och de senast sparade projekten i Projekt-mappen, även i undermappar. Inget material ritas förrän ett projekt öppnas eller skapas. På startvyn fungerar bara Ctrl+N och Ctrl+O. Ett projekt som anges på kommandoraden, till exempel vid dubbelklick på en projektfil, öppnas direkt.
- **Ett nytt projekt har inget material.** Ritytan är maskinens arbetsområde utan materialkant. Under Projekt och material visas en gul ruta och knappen *Lägg till material*. Utan material beräknas inga körbanor (kritiskt CAM-fel *NoMaterial*). Exporten pekar till materialfälten, och maskinvyn och 3D-vyn är tomma. Projektet kan sparas utan material. Äldre projektfiler har alltid material.

- **Ett program per verktyg.** Maskinen har ingen verktygsväxlare, så verktyg blandas aldrig i ett program. Om projektet använder flera verktyg (samma namn, diameter och typ = samma verktyg) blir det ett numrerat program per verktyg, i körordning med fickor och innerkonturer först. Varje program planeras och kontrolleras för sig och börjar med *PROGRAM n AV m* och *BYT VERKTYG … och NOLLA Z*. Exporten sparar alla filer (`<namn> 1 av 3 - 6 mm rak fräs.nc` …). *Ladda program* låter dig välja vilket program som ska köras och föreslår nästa i ordningen. T/M6 används aldrig. Spara-dialogen öppnas i projektets mapp, eller i Projekt-mappen om projektet inte är sparat. Tidsuppskattningen räknar med en manuell paus per program.

- **Borrning (som i Easel).** Den nya bearbetningen *Borrning (Drill)* går rakt ned i objektets mitt och ger ett hål med fräsens diameter. Fräsen borrar i steg om *Djup per steg* och lyfter ur hålet mellan stegen så att spånen kommer ut. Sedan går den snabbt ned till strax ovanför det redan borrade djupet (inställningen *Snabbt ned i redan fräst spår*) och borrar vidare med nedmatningshastigheten. Bara G0/G1 används, eftersom GRBL 1.1 saknar borrcykler (G81/G83). Ett objekt som inte är en cirkel med fräsens diameter ger en varning. Hålen ritas som cirkel med kryss och borras före ytterkonturer. Hållflikar och ramp gäller inte för borrning.

- **Rita ovanpå objekt:** när Rektangel, Ellips eller Polylinje är valt ritar en dragning även inne i andra objekt, till exempel ett hål i en rektangel. Verktyget byter inte längre till Markera. Ctrl-klick markerar ett objekt, ett klick utan dragning markerar objektet under pekaren, och pekaren är ett kors medan ett ritverktyg är valt.

- **Djup per pass större än totalt djup** ger nu ett pass i stället för ett kritiskt fel. Det händer när fräsens skärdata säger till exempel 3 mm per pass men skäret bara är 2 mm djupt.

- **3D-vyn: Material Massivt / Genomskinligt / Dolt.** I läget Genomskinligt syns banorna inne i materialet tillsammans med det som fräses bort, och i läget Dolt syns bara banorna. Materialet har nu också en undersida, så det ser massivt ut underifrån, med hål där det är genomskuret.

- **Smart CAM-panelen är omgjord** i numrerade avsnitt för det markerade objektet: 1 Bearbetning och fräs, 2 Djup (med stegning eller hållflikar beroende på bearbetning), 3 Matning och varvtal, och 4 Nedmatning och fräsriktning (hopfällt). Under dem finns en sammanfattning och *Spara CAM | Ta bort*. Avsnittet *Hela projektet* har CAM-kontrollen, Beräkna körbanor, Kontrollera hinder och Exportera. Förklaringen är hopfälld.
- **V-carve inlay** visas inte längre i panelen. Avsnittet sparar bara inställningar och skapar ingen G-code ännu. Det öppnas från CAM → V-carve inlay-inställningar.

- **Operationsträd** överst i CAM-panelen: ett program per verktyg i körordning (*Program 1 – 5 mm borr*, *Program 2 – 6 mm rak fräs* med ungefärlig tid). Under varje program visas operationerna, till exempel *Ficka Rektangel 7 · 14 mm* och *Kontur ut Rektangel 8 · 21 mm · 4 flikar · genom*. Varje rad har en statuspunkt (grön, gul, röd eller grå), och en tooltip visar vad som är fel. Objekt utan CAM samlas under *Ej tilldelade*. Ett klick på en operation markerar objektet och visar dess inställningar, och ett klick på ett program markerar alla dess objekt. Högerklick ger *Kopiera/Klistra in CAM-inställningar*, *Ta bort CAM* och *Exportera bara program n*. Etiketten för det markerade objektet använder samma namn som trädet.

- **Köra flera program i CNC Studio:**
  - När ett program i ett jobb med flera verktyg är klart talar en dialog om vad som ska göras härnäst: byt till nästa fräs, nolla Z och klicka Starta. Det klara programmet laddas ur, så att det inte kan köras igen av misstag med den nya fräsen.
  - När Z nollas laddas nästa program automatiskt och kontrolleras mot den nya Z-nollan.
  - Om Z nollas om efter att ett program har laddats kontrolleras programmet igen automatiskt.
  - När sista programmet är klart visas *Alla n program är klara*.

- **Probning** (Maskinkontroll → *⊥ Probning…* eller Maskin → Probning…):
  - **Z** är standard. Fräsen söker nedåt mot plattan, först snabbt och sedan långsamt, och Z0 sätts på materialytan.
  - Det finns också **hörn X, Y och Z**, **hörn X och Y**, **bara X** och **bara Y** (plattan i materialets främre vänstra hörn).
  - Alla mått och hastigheter går att ställa in: plattans tjocklek, väggtjocklek X/Y, snabb och långsam matning, sökavstånd, backning, sidoförflyttning, sidodjup och lyft. De sparas i `Inställningar\probplatta.json` och följer med via synkmappen.
  - *Testa kontakt* väntar på probsignalen (Pn:P).
  - Probningen använder G38.2 (verifierad mot GRBL 1.1). Kontaktpunkten läses ur [PRB] med $# i maskinkoordinater. Fräsen flyttas till en exakt maskinposition (G53) innan koordinaten sätts med G10 L20 P0, så att eventuell glidning efter kontakten inte påverkar nollan.
  - Ingen kontakt (ALARM:5) och redan i kontakt (ALARM:4) ger tydliga meddelanden.
  - En Z-probning uppdaterar Z-kontrollen och laddar nästa program i ett jobb med flera verktyg, precis som Nolla Z.
  - Demomaskinen har en simulerad probplatta.

- **Programmet syns under körningen:** det laddade programmet ritas i maskinvyn. Det som återstår är blått, det som redan har körts är orange, och snabbförflyttningar är streckade. Gränsen följer fräsens verkliga position i statusrapporterna, inte raderna som har skickats, eftersom GRBL tar emot raderna långt innan den kör dem. När programmet är klart är hela banan orange. Samma sak visas i **3D-vyn**, med en röd lodrät stapel där fräsen är.

**Tester:** 378 tester.

## 0.8.7 – Loggar på Drive
- **Loggarna ligger i CNC Studio-mappen:** `Loggar\<datornamn>`. Varje dator skriver i sin egen mapp, så två datorer skriver aldrig i samma fil. Med en synkmapp (Google Drive) går alla datorers loggar att läsa från vilken dator som helst.
- Befintliga loggar i `%APPDATA%\CNCStudio\logs` kopieras dit en gång. Felloggen, Första test och ``-kopiorna skrivs nu där.
- **Maskinstyrningen loggas till fil:** varje anslutning blir `maskin-ååååMMdd-HHmmss.txt` med allt som skickats och tagits emot. Filen skrivs varannan sekund, inte för varje rad, så att Drive inte behöver ladda upp den hela tiden.
- **Loggvisaren** (Hjälp → Loggar och fellogg) har en datorväljare och en ny grupp, Maskinstyrning. Andra datorers fellogg kan läsas men bara rensas på den egna datorn.
- **Fräsens position (röda korset) räknas från maskinens egna inställningar** ($23, $130, $131, som läses med $$ vid anslutning och efter homing), inte från projektets profil. Tidigare stod korset på X 251 / Y 251 efter homing när projektet var gjort för 750 × 750 och maskinen är 500 × 500.
- **Vid anslutning jämförs maskinen med projektets maskinprofil** (homingriktning, pull-off, X/Y/Z-rörelse). Om de skiljer sig frågar programmet om maskinens värden ska användas.
- Avbrutna jobb (återupptagning) ligger kvar lokalt, eftersom de hör till datorn som är kopplad till maskinen.

**Tester:** 336 tester.

## 0.8.6 – Efter första maskintestet
- Första testet mot den riktiga X-Carve (GRBL 1.1g Inventables, 500 × 500 × 100 mm) gick igenom alla steg.
- **Version utan välkomstmeddelande:** kontroller som inte startar om när porten öppnas känns nu igen som GRBL 1.1 via `$I` (`[VER:1.1g…]`). Varningen visar vad kontrollern faktiskt svarade.
- **Maskininställningar kan alltid sparas** när profilen är giltig. Material eller fastspänningar som inte längre ryms (t.ex. efter mindre arbetsområde) blockerar bara körbanorna: kritiskt CAM-fel, röd kant i maskinvyn och knappen "Gå till materialplacering". Projekt kan sparas och öppnas ändå.

**Tester:** 328 tester.