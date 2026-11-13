# CNC Studio – källkod (senaste: 0.9.12)

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