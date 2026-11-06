# CNC Studio – källkod (senaste: 0.7.2)

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
