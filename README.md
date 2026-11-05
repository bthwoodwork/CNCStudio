# CNC Studio – källkod (senaste: 0.5.6.2)

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
