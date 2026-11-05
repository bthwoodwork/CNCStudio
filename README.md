# CNC Studio – källkod (senaste: 0.5.7)

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
