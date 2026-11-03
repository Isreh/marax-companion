# MaraX in der Windows-Taskleiste

Der ESP schickt jede Sekunde den Status der Maschine ins Netzwerk, und sofort, wenn die Pumpe an- oder ausgeht. Die Tray-App zeigt ihn unten rechts in der Taskleiste an. Timer und OLED funktionieren wie bisher, auch ohne WLAN.

| Icon | Bedeutung |
|---|---|
| Orange mit Temperatur | Heizt noch auf |
| Grün mit Temperatur | Aufgeheizt |
| Blau mit Temperatur | Pumpe läuft |
| Grau `AUS` | ESP ist im Netz, aber die Maschine ist aus |
| Grau `--` | Keine Daten vom ESP |
| Grau `?` | Der ESP empfängt Zeilen, die er nicht lesen kann |
| Rot `H2O` | Wassertank prüfen, zusätzlich mit Windows-Meldung |

Die Zahl ist die HX-Temperatur, also der große Wert auf dem OLED. **Mit der Maus über dem Icon** siehst du zusätzlich Dampftemperatur, Heizelement und Pumpe. **Rechtsklick** zeigt alle Details, die heutigen Kaffees, „Statistik öffnen“ und die Option „Mit Windows starten“. **Doppelklick** öffnet die Statistik.

Während die Pumpe läuft, ist das Icon immer blau. Das gilt auch, wenn die Maschine selbst Wasser nachfüllt.

## Statistik im Browser

Die Tray-App sammelt alles und zeigt es unter **http://localhost:8420**. Vom Handy oder einem anderen Gerät im Heimnetz erreichst du die Seite über die Adresse deines PCs, zum Beispiel `http://192.168.1.20:8420`.

Dort findest du:

- den aktuellen Zustand und die Temperaturkurve jeder Einschaltung, auch rückwirkend. Ein gefüllter Punkt auf der HX-Kurve ist ein Espresso, ein Ring auf der Dampfkurve heißes Wasser.
- Kaffees pro Tag, Aufheizdauer, Bezugsdauer und deine Kaffee-Uhrzeiten
- alle Einschaltungen, Bezüge und Ereignisse als Tabelle
- CSV-Dateien für Excel, darunter alle Messwerte

Fährst du mit der Maus über die Kurve, zeigt die Anzeige links die Werte zu diesem Zeitpunkt. Ein Klick auf das Datum in der Tabelle „Einschaltungen“ öffnet die Kurve dieser Einschaltung. Die Seite kann nur lesen und nichts an der Maschine ändern.

### So wird gezählt

| Wert | Regel |
|---|---|
| **Maschine gestartet** | Das erste gültige Paket. Läuft der ESP weiter, wenn die Maschine aus ist, ist das der Moment des Einschaltens. Geht der ESP mit der Maschine aus, zählt seine Laufzeit rückwirkend. Das erkennt die App, sobald sie einen solchen Start einmal gesehen hat. Danach stimmt die Startzeit auch, wenn der PC erst später angeht. |
| **Aufheizdauer** | Vom Start bis zum ersten „Aufgeheizt“. Der Durchschnitt nutzt nur Kaltstarts mit HX unter 50 °C, weil ein Warmstart nur wenige Minuten dauert. |
| **Espresso** | Ein Pumpenlauf von 15 bis 90 Sekunden, bei dem die Maschine die Brühgruppe meldet, also der Hebel oben ist. Kürzere Läufe mit Brühgruppe sind Spülen. |
| **Heißwasser** | Ein Pumpenlauf ab 15 Sekunden ohne Brühgruppe. Die Pumpe füllt dann den Dampfkessel nach, aus dem das heiße Wasser kam. Langes Nachfüllen nach dem Aufschäumen erscheint ebenfalls hier. Kurze Läufe ohne Brühgruppe füllt die Maschine von selbst nach, sie erscheinen nicht. |
| **Wassertank** | Die Maschine sendet 20 Sekunden lang einen Code statt des Dampf-Sollwerts, ohne dass die Pumpe läuft. Nach dem Einschalten sendet sie einen solchen Code (etwa `D07`) auch, während sie den Kessel füllt. Dann läuft aber die Pumpe, und es gibt keine Warnung. |
| **Ausgeschaltet** | Der ESP meldet die Maschine 60 Sekunden lang als aus. Geht der ESP mit der Maschine aus, endet die Laufzeit beim letzten Lebenszeichen, und das Ereignis „Ausgeschaltet“ erscheint erst beim nächsten Start im Protokoll, mit der richtigen Uhrzeit. |

### Grenzen

- **Wasser:** Bisher ist nur belegt, dass die Maschine einen leeren Tank beim Einschalten meldet. Wird der Tank im Betrieb leer, sendet sie laut anderen Projekten nichts dazu, und auch die App sieht das dann nicht. Die Warnleuchte an der Maschine bleibt also die verlässliche Anzeige.
- **PC aus:** Gesammelt wird nur, solange die Tray-App läuft. Bezüge bei ausgeschaltetem PC fehlen. War der PC beim Einschalten aus und der ESP läuft durchgehend, ist die Startzeit nur der erste Empfang und erscheint als „ca.“.
- **Ein Bezug ist ein Bezug:** Ein doppelter Espresso zählt als einer, und läuft während eines Espressos gleichzeitig heißes Wasser, zählt nur der Espresso.
- **Ältere Maschinen:** Eine Mara X der ersten Generation meldet die Brühgruppe nicht. Dort zählt jeder Pumpenlauf von 15 bis 90 Sekunden als Espresso, auch heißes Wasser.
- **Gestörte Zeilen:** Während die Pumpe läuft, kommen viele Zeilen der Maschine verstümmelt an. Die App übernimmt Messwerte nur aus sauberen Zeilen, dazwischen gelten die letzten sauberen Werte.

### Wo die Daten liegen

Alles steht in `%APPDATA%\MaraxTray\history.db`, einer SQLite-Datenbank. Zum Sichern kopierst du diese Datei, während die App beendet ist. Stellt eine neue Version die Datenbank um, legt sie vorher eine Sicherung daneben, zum Beispiel `history.db.v1.bak`. Bei täglich zwei Stunden Betrieb wächst sie um etwa 40 MB pro Jahr.

## 1. ESP einrichten

1. Trage in [timer/secrets.h](timer/secrets.h) deinen WLAN-Namen und das Passwort ein. Die Datei wird von Git ignoriert. Der ESP8266 kann nur **2,4 GHz**, und reines WPA3 funktioniert nicht.
2. Lade [timer/timer.ino](timer/timer.ino) wie gewohnt mit der Arduino IDE hoch. Neue Bibliotheken brauchst du nicht.
3. Optional: Im Seriellen Monitor (9600 Baud) erscheint `WiFi connected, IP ...`.

Bleibt `WIFI_SSID` leer, bleibt das WLAN aus und alles läuft wie vorher.

Die Firmware prüft die Zeilen der Maschine seit November 2026 strenger. Ältere Stände ließen einzelne gestörte Zeilen durch. Die Tray-App fängt das ab, du musst also nicht deswegen neu flashen. Beim nächsten Flashen ist die strengere Prüfung aber automatisch dabei.

**Stromversorgung:** Mit WLAN braucht der ESP deutlich mehr Strom, mit kurzen Spitzen von mehreren hundert mA. Am USB-Netzteil ist das kein Problem. Hängt er an einem schwachen Anschluss und startet sich neu, braucht er eine eigene Versorgung.

## 2. Tray-App starten

Baue die App einmal mit dem [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0):

```powershell
dotnet publish pc/MaraxTray -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o pc/publish
```

Starte dann `pc/publish/MaraxTray.exe`. Zum Ausführen genügen die .NET Desktop Runtime und die ASP.NET Core Runtime ab Version 9, das SDK enthält beide.

Beim ersten Start fragt Windows eventuell nach dem Firewall-Zugriff. Erlaube ihn für **private Netzwerke**.

Lege die `.exe` an ihren endgültigen Platz, **bevor** du „Mit Windows starten“ aktivierst. Der Autostart merkt sich den genauen Pfad.

## 3. Icon dauerhaft sichtbar machen

Windows 11 versteckt neue Icons hinter dem Pfeil `^`. Ziehe das Icon von dort auf die Taskleiste. Alternativ: *Einstellungen → Personalisierung → Taskleiste → Andere Taskleistensymbole → MaraxTray*.

## Was „Aufgeheizt“ bedeutet

Die Maschine meldet das nicht selbst, die App leitet es ab. Aufgeheizt ist sie, wenn in drei Paketen nacheinander gilt:

- HX-Temperatur mindestens 90 °C
- Dampfkessel höchstens 2 °C unter seinem Sollwert

Der Zustand bleibt bestehen, bis die HX-Temperatur unter 80 °C fällt. So springt die Anzeige nach einem Bezug nicht sofort zurück. Ob die Maschine nach dem Einschalten auch thermisch stabil ist, sagt das nicht: Die Brühgruppe braucht meist länger als die Sensoren. Wenn das Grün dir zu früh kommt, erhöhe `readyHxTemp`.

Die Werte stehen in `%APPDATA%\MaraxTray\settings.json`. Fehlt eine Einstellung dort, gilt der Standard. Nach einer Änderung die App neu starten.

| Einstellung | Standard | Bedeutung |
|---|---|---|
| `readyHxTemp` | 90 | HX-Temperatur für „Aufgeheizt“ |
| `readySteamTolerance` | 2 | Erlaubter Abstand zum Dampf-Sollwert |
| `cooledDownHxTemp` | 80 | Darunter gilt die Maschine wieder als aufheizend |
| `timeoutSeconds` | 10 | Ohne Paket für diese Zeit erscheint `--` |
| `port` | 4210 | UDP-Port, muss zu `STATUS_PORT` in `timer.ino` passen |
| `listenAddress` | 0.0.0.0 | Netzwerkadresse, auf der die App lauscht |
| `minShotSeconds` | 15 | Kürzester Pumpenlauf, der als Kaffee zählt |
| `maxShotSeconds` | 90 | Längster Pumpenlauf, der als Kaffee zählt |
| `coldStartHxTemp` | 50 | Unter dieser HX-Temperatur gilt ein Start als Kaltstart |
| `sessionGapMinutes` | 20 | Ohne Meldung gilt die Maschine nach dieser Zeit als aus |
| `webPort` | 8420 | Port der Weboberfläche |
| `webListenAddress` | 0.0.0.0 | `0.0.0.0` für das Heimnetz, `127.0.0.1` nur für diesen PC |
| `historyPath` | leer | Anderer Speicherort für die Datenbank |

## Fehlersuche

**Hochladen bricht ab mit „Ein an das System angeschlossenes Gerät funktioniert nicht“:** Viele NodeMCU-Boards haben einen nachgebauten CH340-Chip. Er meldet sich im Geräte-Manager als `USB2.0-Ser!`, und die Windows-Treiber ab Version 3.8 (2023) verweigern ihn. Installiere den älteren Treiber 3.5.2019.1, zum Beispiel mit dem [FakeCH340DriverFixer](https://github.com/SHWotever/FakeCH340DriverFixer). Nach dem Flashen braucht der ESP den USB-Treiber nicht mehr.

**`--` Keine Daten vom ESP**

1. Ist der ESP im WLAN? Das zeigt der Serielle Monitor. Prüfe bei Problemen SSID, Passwort und 2,4 GHz.
2. Blockiert die Firewall? Das zeigt sich nur mit dem echten ESP, siehe unten. Führe in einer PowerShell **als Administrator** aus:
   ```powershell
   New-NetFirewallRule -DisplayName "MaraX Tray" -Direction Inbound -Protocol UDP -LocalPort 4210 -Action Allow -Profile Private
   ```
3. Blockiert der Router Broadcasts? Das kommt bei Gast-WLAN, AP-Isolation oder manchen Mesh-Systemen vor. Trage dann die IP deines PCs als `STATUS_TARGET_IP` in `timer/secrets.h` ein und gib dem PC im Router eine feste Adresse.

**`AUS` obwohl die Maschine läuft:** Am ESP kommen keine Zeilen an. Prüfe die Kabel an D5 und D6. Zeigt das OLED Temperaturen, liegt es nicht an den Kabeln.

**`?` Datenformat unbekannt:** Der Rechtsklick zeigt unter „Letzte Zeile“, was die Maschine sendet. Das Format in [timer/marax_protocol.h](timer/marax_protocol.h) muss dann daran angepasst werden.

**„Verworfene Zeilen“ steigt schnell:** Einzelne verworfene Zeilen sind normal und kommen nicht in der Anzeige an. Steigt der Wert ständig, prüfe die Masseverbindung (GND) zwischen ESP und Maschine.

**`X` auf dem OLED:** Kein Fehler. So zeigt `timer.ino` den `+`-Modus der V2 an.

**Statistik vom Handy nicht erreichbar:** Prüfe, ob das Netzwerk des PCs in Windows als „Privat“ eingestellt ist. Ist die Datei `MaraxTray.exe` umgezogen, gilt die alte Firewall-Regel nicht mehr und Windows fragt beim nächsten Start neu. Auf dem PC selbst sollte `http://localhost:8420` immer gehen.

**„Datenerfassung gestört“ im Menü:** Die Temperaturanzeige läuft weiter, gespeichert wird aber gerade nichts. Der Text nennt den Grund, zum Beispiel eine volle Festplatte.

## Ohne Maschine testen

Der Simulator spielt den ESP und läuft durch alle Zustände:

```powershell
python tools/simulate_esp.py
```

Das prüft die App, aber **nicht die Firewall**, weil die Pakete vom selben PC kommen. Ob die Firewall durchlässt, zeigt erst der echte ESP.

## Für Entwickler

Das Paket ist JSON per UDP an Port 4210. Der ESP sendet an die Broadcast-Adresse seines Netzes oder an `STATUS_TARGET_IP`:

```json
{"dev":"marax","v":1,"online":true,"mode":"+","fw":"1.22","hx":93,"steam":124,"target":124,
 "boost":0,"heat":false,"pump":false,"shot":0,"rssi":-61,"uptime":1234,"bad":0,"raw":"+1.22,124,124,093,0000,0,0"}
```

Die App wertet nur Pakete aus, die vollständig und plausibel sind. Jedes Gerät im Netzwerk kann an den Port senden, steuern lässt sich darüber aber nichts. Gefälschte Pakete könnten höchstens die Statistik verfälschen.

Die Weboberfläche ist eine einzelne Datei, [pc/MaraxTray/Web/index.html](pc/MaraxTray/Web/index.html), ohne externe Bibliotheken. Sie läuft also auch ohne Internet. Ihre Daten kommen aus `/api/...`, die CSV-Dateien aus `/export/...`.

```powershell
dotnet test pc/MaraxTray.Tests
dotnet publish pc/MaraxTray -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o pc/publish
```

Die Firmware-Logik lässt sich ohne ESP testen. Wie, steht oben in [tools/protocol_test.cpp](tools/protocol_test.cpp). Ändert sich das Paketformat, muss `tools/protocol_samples.jsonl` neu erzeugt werden, denn daraus lesen die Tests der App.
