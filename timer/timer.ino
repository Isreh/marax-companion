#define D5 (14)
#define D6 (12)
#define D7 (13)

#define PUMP_PIN D7

#include <Adafruit_SSD1306.h>
#include <Adafruit_GFX.h>
#include <ESP8266WiFi.h>
#include <WiFiUdp.h>
#include <Wire.h>
#include <Timer.h>
#include <SoftwareSerial.h>
#include "marax_protocol.h"
#include "secrets.h"

Adafruit_SSD1306 display(128, 64, &Wire, -1);
SoftwareSerial mySerial(D5, D6);
Timer t;
WiFiUDP udp;

// set to true/false when using another type of reed sensor
bool reedOpenSensor = true;
// set to 0 or 2 to turn the display by 180 degrees
int displayRotation = 2;
bool displayOn = true;
int timerCount = 0;
int prevTimerCount = 0;
bool timerStarted = false;
long timerStartMillis = 0;
long timerStopMillis = 0;
long timerDisplayOffMillis = 0;
long serialUpdateMillis = 0;
int pumpInValue = 0;

const byte numChars = 32;
char receivedChars[numChars];
static byte ndx = 0;
char endMarker = '\n';
char rc;

const uint16_t STATUS_PORT = 4210;
const unsigned long STATUS_INTERVAL_MILLIS = 1000;
const unsigned long MACHINE_TIMEOUT_MILLIS = 5000;

MaraxData machineData;
char lastLine[numChars];
unsigned long validLineMillis = 0;
unsigned long lastLineMillis = 0;
unsigned long statusSentMillis = 0;
unsigned long badLines = 0;
bool machineSeen = false;
bool lineSeen = false;
bool wifiConnected = false;
bool lastPumpSent = false;
IPAddress statusTarget;

void setup() {
  setupWifi();

  Serial.begin(9600);
  mySerial.begin(9600);

  pinMode(PUMP_PIN, INPUT_PULLUP);
  pinMode(LED_BUILTIN, OUTPUT);
  digitalWrite(LED_BUILTIN, HIGH);

  t.every(100, updateDisplay);

  memset(receivedChars, 0, numChars );

  display.begin(SSD1306_SWITCHCAPVCC, 0x3C);
  display.setRotation(displayRotation);
  display.clearDisplay();
  display.setTextColor(WHITE);
  display.display();
  mySerial.write(0x11);
}

void loop() {
  t.update();
  detectChanges();
  getMachineInput();
  updateNetwork();
}

void setupWifi() {
  if (strlen(WIFI_SSID) == 0) {
    WiFi.mode(WIFI_OFF);
    return;
  }
  WiFi.persistent(false);
  WiFi.mode(WIFI_STA);
  WiFi.hostname("marax-timer");
  WiFi.setAutoReconnect(true);
  // does not wait for the connection, the timer has to run without a network
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
}

bool machineOnline() {
  return machineSeen && millis() - validLineMillis < MACHINE_TIMEOUT_MILLIS;
}

bool pumpRunning() {
  return timerStarted || (machineOnline() && machineData.hasPump && machineData.pump);
}

void updateNetwork() {
  if (strlen(WIFI_SSID) == 0) {
    return;
  }
  bool connected = WiFi.status() == WL_CONNECTED;
  if (connected != wifiConnected) {
    wifiConnected = connected;
    if (connected) {
      Serial.print("WiFi connected, IP ");
      Serial.println(WiFi.localIP());
      if (!statusTarget.fromString(STATUS_TARGET_IP)) {
        statusTarget = WiFi.broadcastIP();
      }
    } else {
      Serial.println("WiFi disconnected");
    }
  }
  if (connected && (pumpRunning() != lastPumpSent || millis() - statusSentMillis >= STATUS_INTERVAL_MILLIS)) {
    sendStatus();
  }
}

void sendStatus() {
  statusSentMillis = millis();
  lastPumpSent = pumpRunning();

  MaraxStatus status;
  status.machine = machineOnline() ? &machineData : NULL;
  status.rawLine = lineSeen && millis() - lastLineMillis < MACHINE_TIMEOUT_MILLIS ? lastLine : "";
  status.pump = lastPumpSent;
  status.shotSeconds = timerStarted ? (millis() - timerStartMillis) / 1000 : 0;
  status.rssi = WiFi.RSSI();
  status.uptimeSeconds = millis() / 1000;
  status.badLines = badLines;

  char packet[256];
  size_t length = formatMaraxStatus(packet, sizeof(packet), status);
  if (length > 0) {
    udp.beginPacket(statusTarget, STATUS_PORT);
    udp.write((const uint8_t*)packet, length);
    udp.endPacket();
  }
}

void handleMachineLine() {
  strcpy(lastLine, receivedChars);
  lastLineMillis = millis();
  lineSeen = true;

  MaraxData parsed;
  if (parseMaraxLine(receivedChars, &parsed)) {
    machineData = parsed;
    validLineMillis = millis();
    machineSeen = true;
  } else {
    badLines++;
  }
}

void getMachineInput() {
  while (mySerial.available() ) {
    serialUpdateMillis = millis();
    rc = mySerial.read();

    if (rc != endMarker) {
      receivedChars[ndx] = rc;
      ndx++;
      if (ndx >= numChars) {
        ndx = numChars - 1;
      }
    } else {
      receivedChars[ndx] = '\0';
      ndx = 0;
      Serial.println(receivedChars);
      handleMachineLine();
    }
  }

  if (millis() - serialUpdateMillis > 5000) {
    serialUpdateMillis = millis();
    memset(receivedChars, 0, numChars);
    Serial.println("Request serial update");
    mySerial.write(0x11);
  }
}

void detectChanges() {
  digitalWrite(LED_BUILTIN, digitalRead(PUMP_PIN));
  if(reedOpenSensor) {
    pumpInValue = digitalRead(PUMP_PIN);
  } else {
    pumpInValue = !digitalRead(PUMP_PIN);
  }
  if (!timerStarted && !pumpInValue) {
    timerStartMillis = millis();
    timerStarted = true;
    displayOn = true;
    Serial.println("Start pump");
  }
  if (timerStarted && pumpInValue) {
    if (timerStopMillis == 0) {
      timerStopMillis = millis();
    }
    if (millis() - timerStopMillis > 500) {
      timerStarted = false;
      timerStopMillis = 0;
      timerDisplayOffMillis = millis();
      display.invertDisplay(false);
      Serial.println("Stop pump");
    }
  } else {
    timerStopMillis = 0;
  }
  if (!timerStarted && displayOn && timerDisplayOffMillis >= 0 && (millis() - timerDisplayOffMillis > 1000 * 60 * 60)) {
    timerDisplayOffMillis = 0;
    timerCount = 0;
    prevTimerCount = 0;
    displayOn = false;
    Serial.println("Sleep");
  }
}

String getTimer() {
  char outMin[3];
  if (timerStarted) {
    timerCount = (millis() - timerStartMillis ) / 1000;
    if (timerCount > 15) {
      prevTimerCount = timerCount;
    }
  } else {
    timerCount = prevTimerCount;
  }
  if (timerCount > 99) {
    return "99";
  }
  sprintf( outMin, "%02u", timerCount);
  return outMin;
}

void updateDisplay() {
  display.clearDisplay();
  if (displayOn) {
    if (timerStarted) {
      display.setTextSize(7);
      display.setCursor(25, 8);
      display.print(getTimer());
    } else {
      // draw line
      display.drawLine(74, 0, 74, 63, SSD1306_WHITE);
      // draw time seconds
      display.setTextSize(4);
      display.setCursor(display.width() / 2 - 1 + 17, 20);
      display.print(getTimer());
      // draw machine state C/S
      if (receivedChars[0] ) {
        display.setTextSize(2);
        display.setCursor(1, 1);
        if (String(receivedChars[0]) == "C") {
          display.print("C");
        } else if (String(receivedChars[0]) == "V") {
          display.print("S");
        } else {
          display.print("X");
        }
      }
      if (String(receivedChars).substring(18, 22) == "0000") {
        // not in boost heating mode
        // draw fill circle if heating on
        if (String(receivedChars[23]) == "1") {
          display.fillCircle(45, 7, 6, SSD1306_WHITE);
        }
        // draw empty circle if heating off
        if (String(receivedChars[23]) == "0") {
          display.drawCircle(45, 7, 6, SSD1306_WHITE);
        }
      } else {
        // in boost heating mode
        // draw fill rectangle if heating on
        if (String(receivedChars[23]) == "1") {
          display.fillRect(39, 1, 12, 12, SSD1306_WHITE);
        }
        // draw empty rectangle if heating off
        if (String(receivedChars[23]) == "0") {
          display.drawRect(39, 1, 12, 12, SSD1306_WHITE);
        }
      }
      // draw temperature
      if (receivedChars[14] && receivedChars[15] && receivedChars[16]) {
        display.setTextSize(3);
        display.setCursor(1, 20);
        if (String(receivedChars[14]) != "0") {
          display.print(String(receivedChars[14]));
        }
        display.print(String(receivedChars[15]));
        display.print(String(receivedChars[16]));
        display.print((char)247);
        if (String(receivedChars[14]) == "0") {
          display.print("C");
        }
      }
      // draw steam temperature
      if (receivedChars[6] && receivedChars[7] && receivedChars[8]) {
        display.setTextSize(2);
        display.setCursor(1, 48);
        if (String(receivedChars[6]) != "0") {
          display.print(String(receivedChars[6]));
        }
        display.print(String(receivedChars[7]));
        display.print(String(receivedChars[8]));
        display.print((char)247);
        display.print("C");
      }
    }
  }
  display.display();
}
