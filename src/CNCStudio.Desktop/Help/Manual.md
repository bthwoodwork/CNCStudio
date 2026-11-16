# Välkommen till CNC Studio

CNC Studio är ritprogram, CAM och maskinstyrning i ett: du ritar det du vill fräsa, väljer hur varje form ska fräsas och kör programmet på maskinen – utan att byta program. Det är byggt för GRBL 1.1-maskiner som X-Carve.

Programmet har två arbetsytor:

- **Design (F1)** – rita, placera materialet och välj fräsning för varje form.
- **Maskin (F3)** – anslut, nolla, jogga, proba och kör program.

**F2** visar fräsningen för den markerade formen, **F4** växlar mellan 2D och 3D.

Den här manualen beskriver arbetsgången från ritning till färdig detalj. Använd innehållsförteckningen till vänster, eller sök med rutan ovanför texten.

**Säkerhet först:** CNC Studio stoppar export och körning när något inte är kontrollerat – men programmet vet inte allt. Kontrollera alltid fastspänningar, fräs och nollpunkt, ha handen nära STOPP och kör gärna nya program i luften första gången.

# Kom igång

## Första gången med en ny maskin

1. Välj **Maskin → Maskiner…** och lägg upp din maskin (eller använd den som finns). Varje maskin har egna inställningar, $$-kopior, kalibrering, platser och logg.
2. Kör **Maskin → Första test mot maskinen…**. Testet leder dig steg för steg: anslutning, läsning av $$ (med säkerhetskopia), brytartest, axelriktning, homing, jogg, en luftkörning och paus/STOPP.
3. Ställ in **Maskininställningar…**: arbetsområde ($130/$131/$132), hemhörn (där X/Y-brytarna sitter), marginal till gränslägena och om spindeln styrs av GRBL eller för hand.

Utan maskin kan du öva med porten **DEMO (simulerad maskin)** – den beter sig som en riktig GRBL, med brytare, probplatta och till och med strömavbrott.

## Arbetsgången i korthet

1. **Material:** ange bredd, höjd och tjocklek under *Projekt och material* och klicka *Lägg till material*.
2. **Rita** former eller importera SVG/DXF.
3. **Fräsning:** markera en form och välj bearbetning, fräs och djup i panelen *Fräsning*. Klicka *Spara CAM*.
4. **Kontrollera** i *Jobb*-panelen och i 3D-vyn.
5. **Maskin:** anslut, homa, nolla X/Y och Z (eller proba), *Ladda från projektet*, *Starta*.

## Fönster och paneler

Panelerna kan dras, dockas, flytas ut och döljas. **Visa**-menyn tar fram en dold panel, och **Visa → Återställ fönsterlayout** lägger tillbaka allt som från början.

Statusraden längst ned visar vad som händer och vad nästa steg är. Läs den när du är osäker.

# Projekt och filer

## Spara och öppna

- **Arkiv → Nytt / Öppna / Spara / Spara som.** Projekt sparas som `namn.cncstudio.json`. Skriver du bara ett namn läggs ändelsen till automatiskt.
- Startsidan visar de senaste projekten i Projekt-mappen.
- **Arkiv → Synkmapp (Google Drive)…** lägger projekt, verktyg, material, maskinprofiler och loggar i en mapp som synkas mellan datorer.

## Autospara och osparade ändringar

- Har projektet osparade ändringar sparas en kopia varje minut i en egen mapp på datorn (aldrig över projektfilen).
- Om CNC Studio kraschar, strömmen går eller datorn stängs av med osparat arbete frågar programmet vid nästa start om projektet ska **återställas**. Det öppnas då med ändringarna – spara (Ctrl+S) för att behålla dem.
- Vid **Stäng**, **Nytt** och **Öppna** frågar programmet om osparade ändringar ska sparas.

## Ångra

**Ctrl+Z** ångrar och **Ctrl+Y** gör om – det gäller ritning, CAM-inställningar, materialets placering och fastspänningar.

# Design: rita

## Ritverktygen

Verktygslisten till vänster (och menyn **Rita**):

- **Markera** – klicka, Ctrl+klick för flera, eller dra en ruta. Dra åt höger = bara det som ligger helt innanför, åt vänster = allt rutan nuddar.
- **Rektangel**, **Ellips**, **Linje** (polylinje – dubbelklicka eller klicka på startpunkten för att avsluta).
- **Text** – typsnitt, storlek och stil. Dubbelklicka på en text för att ändra den.
- **Figurer** – stjärna, hjärta, polygon och andra färdiga former.
- **Låda** – en låda med fingerskarvar: ange längd, bredd och höjd (ytter- eller innermått), ungefärlig fingerbredd, spel för lim och om den ska ha botten. Sidorna och botten läggs ut på materialet som en grupp, var och en med utskärning, hållflikar och dogbones, med den valda fräsens skärdata. Fingrarna går omlott så att varje hörn hör till en del.
- **Bild** – gör en bild (foto, logga, skiss) till vektorer.
- **3D** – en 3D-modell från STL som fräses i 3D.
- **Importera** – SVG och DXF.

Mått och läge för den markerade formen ändras exakt under **Egenskaper → Markerat objekt** (Tab eller Enter gäller).

**Snap på/av** styr om formerna fäster mot rutnätet. Rutnätets steg följer zoomen och visas i statusraden.

## Kombinera och ordna

- **Förena, Subtrahera, Skärning, XOR** – booleska operationer på markerade former.
- **Justera** och **Fördela** markerade former.
- **Offset** – en ny kontur ett visst avstånd utanför eller innanför.
- **Rutnät** – kopior i rader och kolumner.
- **Nesta** – lägger delarna tätt på materialet (nesting).
- **Gruppera** (Ctrl+G) håller ihop former som en enhet.
- **Kontrollera vektorer** hittar öppna konturer, dubbletter och korsande linjer som ger problem vid fräsning.

## Navigera i vyn

Hjulet zoomar, mittenknappen panorerar, **F** anpassar vyn till materialet.

**Linjalerna** längs över- och vänsterkanten visar i Design arbetskoordinater: 0 (blå) är materialets nollpunkt, X0 Y0 i programmet, och Y växer bakåt som i G-coden. I Maskin mäter de bordet från hemhörnet – samma mått som under *Material på bordet*. **Visa → Linjal: material** och **Visa → Linjal: bord (arbetsytan)** tänder och släcker dem var för sig.

# Material och placering

## Materialet

Under **Egenskaper → Projekt och material**:

- **Bredd, höjd och tjocklek** i mm, och **materialtyp** (styr fräsarnas skärdata från biblioteket).
- **Nollpunkt X/Y (G54)** – materialets främre vänstra hörn eller mitten. Z0 är alltid materialets ovansida.

Utan material beräknas inga körbanor. Materialet visas som en solid skiva på maskinbordet, i 2D och i 3D.

## Materialet på maskinbordet

Var materialet ligger på bordet spelar roll: CNC Studio kontrollerar att banorna ryms i maskinen och går fria från fastspänningarna.

- **Klicka på materialet** (i 2D eller 3D) för att markera det – det får en streckad ram. **Dra** det sedan till rätt plats.
- Under **Material på bordet** väljer du med 3 × 3 rutor vilken punkt på materialet X/Y gäller (ett hörn, mitten av en kant eller mitten), och skriver in var den punkten ska ligga – mätt från hemhörnet. Enter flyttar materialet.
- **Lås materialet** så att det inte flyttas av misstag.

Materialet flyttas bara i Design. I Maskin står det still.

## Fastspänningar

Rita klämmor och block på bordet (**Maskin → Rita fastspänningsblock**, eller *Material och fastspänningar…*). Ange höjd över materialet och marginal. Fräsen planeras att lyfta över dem, och banor som kommer för nära stoppas.

# Fräsning (CAM)

## Välj bearbetning

Markera en form och öppna panelen **Fräsning** (F2):

- **Utvändig kontur** – skär ut formen, fräsen går utanför linjen.
- **Invändig kontur** – fräsen går innanför linjen (hål, urtag).
- **Fickfräsning** – fräser bort allt innanför, varv för varv.
- **Borrning** – ett hål med fräsens diameter i formens mitt.
- **Planfräsning** – planar hela ytan rad för rad. Körs först.
- **V-carving** – V-fräs, djupare där formen är bredare, skarpa hörn. För text och skyltar.
- **Inlay-hane** – hanen i en V-carve inlay (se *Inlay*).
- **3D-fräsning** – en STL-modell fräses i rader.
- **Fasning** – V-fräsens spets följer formens kant och fasar den. Ange fasens bredd i stället för djup; djupet räknas ut från fräsens vinkel (90° ger lika djupt som brett).

## Fräs, djup och hastigheter

- **Fräs** väljs ur verktygsbiblioteket – diameter, matning, nedmatning, djup per pass och varvtal fylls i. Ändra fritt.
- **Totalt djup** och **djup per pass**. Vid genomskärning läggs **hållflikar** (antal, bredd, höjd) så att delen sitter kvar.
- **Tunt sista lager i stället för flikar** (genomskärning) – sista passet stannar t.ex. 0,3 mm ovanför botten. Delarna sitter kvar i ett tunt lager och skärs med kniv eller slipas loss efteråt – inga flikar att putsa bort.
- **Stegning** för ficka och planfräsning.
- **Fasa övre kanten med V-fräs** (kontur och ficka) – ett eget program med V-fräsen längs kanten innan delen skärs ut, medan den sitter fast. Välj V-fräs ur biblioteket och fasbredd.
- **Dogbones i innerhörn** (invändig och utvändig kontur, ficka) – ett runt urtag i varje skarpt hörn som fräsen inte når, så att en fyrkantig tapp eller del passar i det frästa. Rundade hörn och kurvor lämnas som de är.
- **Andra fräs** – grovbearbetning med en stor fräs och finbearbetning eller detaljer med en mindre (ficka, V-carving och 3D).
- **Avancerat:** rampad nedmatning (vinkel och matning) och fräsriktning (medfräsning eller motfräsning).

Klicka **Spara CAM**. Banan räknas ut direkt och visas som grön streckad linje (fräscentrum) med gula hållflikar.

## CAM-mallar

Överst i Fräsning finns **mallar** – sparade inställningar med ett namn, t.ex. *V-carve 60°, 3 mm* eller *Utskärning 6 mm, 4 flikar*:

- **Använd mall …**: välj en mall, så fylls inställningarna i och sparas på den markerade formen direkt.
- **Spara som mall…**: sparar inställningarna som visas. Är djupet hela materialet blir mallen en genomskärning – den skär genom vilket material den än används på.
- **⋯**: ta bort en mall.

Mallarna följer med via synkmappen, som verktygsbiblioteket. Tre exempel finns från början.

## Jobbet – alla operationer

Panelen **Jobb** (nedanför ritytan) visar alla operationer i körordning, grupperade per fräs, med CAM-kontrollen:

- **Röda** fel stoppar export och körning – till exempel en bana utanför maskinen, för nära en fastspänning eller en V-fräs som valts för ficka.
- **Gula** varningar ska läsas – till exempel tunt golv kvar eller genomskärning utan flikar.
- Klicka på en rad för att gå till formen.

**Beräkna körbanor** räknar om stora former som inte räknas automatiskt.

## Riggblad

**Riggblad…** (i Jobb-panelen och under CAM) öppnar en sida i webbläsaren att skriva ut eller spara som PDF (*Skriv ut → Microsoft Print to PDF*): material och materialtyp, nollpunkt, placering på bordet, en ritning ovanifrån med formerna, körbanorna, fastspänningarna och X0 Y0, programmen i ordning med fräs, varvtal, matning, operationer, djup och tid, rutor att bocka av vid fräsbyten, en checklista före körning och det CAM-kontrollen varnar för.

## Inlay

**CAM → Skapa inlay (hona + hane)…** gör en V-carve inlay: honan V-carvas i det här projektet och hanen blir ett eget, speglat projekt med rätt startdjup och spel för lim.

## Flera fräsar

Maskinen har ingen verktygsväxlare, så varje fräs blir ett eget program, i rätt ordning. Mellan programmen byter du fräs och nollar Z igen – X0 Y0 ändras inte. CNC Studio laddar nästa program när Z är nollad.

När ett program är klart öppnas **Fräsbyte** (finns också under **Maskin → Fräsbyte…**) och leder dig genom bytet:

1. **Till fräsbytet** – maskinen lyfter Z och åker till platsen *Fräsbyte* (lägg till den under Platser…). Utan den lyfts bara Z.
2. **Byt fräs.**
3. **Över probplattan** – maskinen åker tillbaka, högt i luften, dit där Z probades senast. Lägg plattan där och jogga ned till några mm ovanför den. Höjden joggar du alltid själv: en längre fräs skulle annars kunna köra ned i plattan.
4. **Proba Z** – Z0 sätts med den nya fräsen, och nästa program laddas och kontrolleras.
5. **Starta.**

Fönstret låter huvudfönstret vara aktivt, så du kan jogga och trycka STOPP hela tiden. Vill du hellre nolla Z för hand fungerar det som förut – programmet laddas då också.

## 3D-vyn

**F4** visar materialet simulerat i 3D med banorna: blått skär, gult ned/upp, röd streckad snabbförflyttning. Under körning visas det som är kört i orange och fräsens position live.

Höger musknapp roterar, hjulet zoomar, mittenknappen panorerar. *Anpassa vy* och *Hela bordet* zoomar om.

**Linjaler** i 3D: längs materialets främre och vänstra kant (arbetskoordinater, 0 = nollpunkten) och längs bordets kanter (mm från hemhörnet). Kryssrutorna *Linjal: material* och *Linjal: bord* tänder och släcker dem var för sig – samma val som i Visa-menyn, och de gäller även 2D.

# Maskin: ansluta och nolla

## Ansluta

Välj port uppe till vänster och klicka **Anslut**. Statusen (Idle, Run, Hold, Alarm …) visas stort i maskinpanelen. Datorn hålls vaken medan maskinen är ansluten.

Efter start är GRBL oftast låst: kör **Homing ($H)**. Maskinkoordinaterna är kända först efter homing, och de flesta kontroller kräver det.

**Återställ (Ctrl-X)** gör en mjuk återställning – till exempel efter ett larm. Positionen behålls.

## Koordinater

Stora siffror är arbetskoordinater (förhållande till nollpunkten), små är maskinkoordinater (MPos). Koordinatsystemet (G54–G59) väljs överst.

## Nolla

- **X0, Y0, Z0** vid varje axel nollar den axeln där fräsen står – de andra står kvar.
- **Nolla X + Y** nollar båda och behåller Z. Pilen bredvid ger *Vid materialet* – X0 Y0 sätts där materialet ligger enligt placeringen, utan att fräsen rör sig.
- **Gå till X0 Y0** och **Gå till**-knapparna vid varje axel kör dit (Z lyfts först).

När Z nollas står fräsen kvar där den är – nollan flyttas, inte fräsen.

## Jogga

Pilarna joggar X/Y (även diagonalt), Z+/Z− joggar Z. **Klick** = ett steg, **håll inne** = kontinuerlig jogg som stannar när du släpper. Stegen väljs med *Precis / Normal / Snabb* eller fritt.

Tangentbordet fungerar också: piltangenterna för X/Y, Page Up/Down för Z, Esc stoppar. Byt tangenter under *Tangenter…*. En Xbox-kompatibel **gamepad** kan jogga, nolla och pausa (*Gamepad…*, håll LB inne).

## Probning

**Probning…** (Maskin-menyn eller *Probning och mer*):

- **Z** – fräsen söker ned mot plattan, först snabbt sedan långsamt. Z0 sätts på materialytan: kontaktpunkten minus plattans tjocklek.
- **X, Y, XY och XYZ** – i valfritt hörn av materialet. En skiss visar hur fräsen rör sig och var X0 Y0 hamnar.
- **Testa kontakt** innan du probar – rör plattan mot fräsen och se att signalen kommer.
- Plattans tjocklek och alla hastigheter ställs in i fönstret och sparas.

Under probningen blir *Stäng* en STOPP-knapp.

## Planhetskontroll

**Planhet…** probar 5 eller 9 punkter med plattan och visar hur mycket materialet eller offerskivan lutar eller bågnar, med förslag på vad du kan göra.

## Platser

**Platser…** sparar maskinpositioner som *Fräsbyte* och *Parkering*. En knapp per plats i maskinpanelen – ett klick och maskinen åker dit (Z först). Platserna hör till maskinen, inte projektet.

# Köra program

## Ladda

Under **Program**:

- **Ladda från projektet** – skapar och kontrollerar G-code från projektet (samma kontroller som export). Med flera fräsar laddas program 1, och nästa laddas när du nollat Z med nästa fräs.
- **Öppna G-code-fil…** – kör ett program från ett annat CAM-program. Filen kontrolleras för GRBL (kommandon GRBL inte har stoppas) och du ser utbredning, djup, matning och tid innan den laddas.
- **G-code-editor…** – se *G-code-editorn*.
- **Senast körda** – dubbelklicka för att öppna och ladda samma program igen.

## Kontrollera innan körning

- **Kontrollkör ($C)** – GRBL läser hela programmet utan att röra sig och rapporterar fel.
- **Rita konturen…** – fräsen åker runt programmets yttre gräns högt i luften, så att du ser att det ryms på materialet och går fritt från fastspänningarna.
- Starta kontrollerar själv att hela banan ryms i maskinen med den nollpunkt som är satt, och att X/Y-nollan stämmer med materialets placering.

## Starta, paus och stopp

Under **Körning**:

- **▶ Starta** – kräver homing. Programmet skickas en rad i taget.
- **❚❚ Paus** – mjuk inbromsning (feed hold). Fortsätt med samma knapp.
- **■ Stopp** – bromsar, väntar tills maskinen står still och återställer. Positionen behålls.
- **STOPP** i maskinpanelen fungerar alltid.

Medan programmet kör kan du ändra **matning** och **spindel** (−10/−1/100/+1/+10 %) och **snabbgång** (25/50/100 %), och slå på/av spindel och kylning.

Styrs fräsen för hand pausar programmet med M0 där spindeln ska startas eller ställas om – starta fräsen och tryck Fortsätt.

## Avisering

När programmet är **klart**, **väntar** vid en paus i programmet (M0, t.ex. för att starta fräsen), **stoppas** av ett fel eller maskinen får **larm**:

- datorn spelar ett ljud (olika för klart, väntar och larm) och knappen i aktivitetsfältet blinkar om CNC Studio inte är i förgrunden,
- mobilsidan (Styr från mobilen) visar en färgad banderoll, vibrerar och piper. Tryck en gång någonstans på sidan efter att den öppnats, annars får den inte pipa.

Ljudet på datorn stängs av under **Maskin → Ljud vid klart, paus och larm**. Din egen Paus eller Stopp aviseras inte.

## Starta från rad

**Starta från rad…** börjar programmet längre fram, till exempel efter en del som redan är gjord. Välj rad, skriv radnumret eller hoppa till en del. Mitt i en fräsning börjar det på närmaste säkra punkt före raden, och fönstret visar hur maskinen kör in: Z upp, XY dit, spindeln på, ned och vidare.

## Efter ett avbrott

Jobbets läge sparas efter varje rad. Avbryts det – strömavbrott, larm, datorn som startar om – visas **AVBRUTET JOBB** under Program. **Återuppta…** homar och fortsätter från en punkt som säkert redan är fräst. Programmet varnar om nollpunkten har ändrats sedan jobbet startade.

## G-code-editorn

**Arkiv → G-code-editor…** eller knappen under Program öppnar ett fönster för G-code – en fil från ett annat program eller ett av CNC Studios egna:

- **Redigera som text.** Programmet kontrolleras medan du skriver.
- **Spegla** vänster–höger eller fram–bak, **rotera** 90° eller 180° – kring banans mitt eller kring X0 Y0.
- **Flytta** ett antal mm, eller **till nollpunkten** (banans främre vänstra hörn på X0 Y0).
- **Kopior i rutnät** – kolumner, rader och avstånd. Mellan kopiorna lyfts fräsen till programmets högsta Z.
- **Ångra ändring**, **Spara**, och **Ladda för körning** (även osparat).

Bara X och Y ändras – Z, matning och varvtal står kvar, och bågar följer med. Program i tum och rader med G10/G28/G30/G92 ändras inte.

# Mer i Maskin

## Makron

Egna kodbitar som körs med ett klick (fliken **Makron**). *Ny…* öppnar en editor där färdiga rader kan infogas och förklaras. Makron som kan röra maskinen visar raderna och frågar först. Inställningar ($n=) och liknande stoppas – de görs i Maskininställningar.

## Planfräsa offerskivan

**Maskin → Planfräsa offerskivan…** fräser offerskivan plan med en bred fräs, över hela bordet eller en del av det. Det blir ett eget projekt.

## Styr från mobilen

**Maskin → Styr från mobilen…** → *Starta* visar en adress och en PIN-kod. Öppna adressen i telefonens webbläsare (samma wifi). På telefonen finns STOPP, jogg, nollning, homing, paus/fortsätt, platser och överstyrningar. Program startas och spindeln slås på bara vid datorn.

## Maskinens verktyg

- **GRBL-konsol** – skicka kommandon direkt och se svaren.
- **Maskininformation** – firmware, aktiva lägen och ingångarna live.
- **Testa gränslägesbrytare** – tryck på brytarna och se att de registreras.
- **Maskinlogg och underhåll** – körda jobb, larm, total körtid och underhåll i körtimmar (blir gult och rött när det är dags).
- **Kalibrering** (under Maskiner) – steg/mm och vinkelkontroll (squaring).

# Bibliotek

## Verktygsbiblioteket

**CAM → Verktygsbibliotek…** – fräsar i kategorier med diameter, typ (rak, kulfräs, V-fräs), skaftmått, inköpsställe och skärdata per material. Fräsen du väljer i Fräsning fyller i matning, varvtal och djup.

## Materialbiblioteket

**CAM → Materialbibliotek…** – material i kategorier. Materialtypen i projektet bestämmer vilken skärdata som hämtas.

# Felsökning

## Programmet stoppar export eller körning

Dialogen listar vad som måste åtgärdas, med ett förslag och en knapp som tar dig dit. Vanligast:

- **Inget material** – lägg till material under Projekt och material.
- **Körbanan är inaktuell** – klicka *Beräkna körbanor*.
- **Maskinens Z vid materialytan okänd** – homa, nolla Z (eller proba) och klicka *Läs från maskinen ($#)*.
- **Banan utanför maskinen** – flytta materialet på bordet eller formen på materialet.

## Larm

Ett larm (ALARM) stoppar maskinen. Läs meddelandet i maskinpanelen, åtgärda orsaken och klicka **Återställ (Ctrl-X)**, homa sedan igen. Vanliga larm: gränslägesbrytare påverkad (1), mjuk gräns (2), probning utan kontakt (5).

## Loggar

**Hjälp → Loggar och fellogg…** visar felloggen, loggar från maskinstyrningen och första test samt säkerhetskopior av $$. Skicka gärna en logg när något har gått fel.

## Tangentbordsgenvägar

- **F1** Design, **F2** Fräsning, **F3** Maskin, **F4** 2D/3D, **Shift+F1** den här manualen
- **Ctrl+N / O / S** Nytt, Öppna, Spara – **Ctrl+Shift+S** Spara som
- **Ctrl+Z / Y** Ångra, Gör om – **Ctrl+A** Markera alla – **Ctrl+D** Duplicera – **Delete** Ta bort
- **Ctrl+G / Ctrl+Shift+G** Gruppera, Avgruppera
- **Ctrl+I** Importera SVG/DXF – **Ctrl+E** Exportera G-code
- **F** Anpassa vyn till materialet
- Jogg: piltangenterna, Page Up/Down, Esc stoppar (när det är påslaget under Tangenter…)
