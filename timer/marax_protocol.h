// Parses the machine's serial line and builds the status packet for the PC.
// Free of Arduino dependencies so it can be tested on the PC (tools/protocol_test.cpp).

#ifndef MARAX_PROTOCOL_H
#define MARAX_PROTOCOL_H

#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

const int MARAX_LINE_SIZE = 32;
const int MARAX_MAX_TEMP = 200;
const int MARAX_NO_TARGET = -1;

struct MaraxData {
  char mode;
  char firmware[8];
  int steamTemp;
  int steamTarget;
  int hxTemp;
  int boostCountdown;
  bool heating;
  bool hasPump;
  bool pump;
};

struct MaraxStatus {
  const MaraxData* machine;  // NULL while no valid line arrives from the machine
  const char* rawLine;       // last line received, valid or not, "" if there is none
  bool pump;
  int shotSeconds;
  int rssi;
  unsigned long uptimeSeconds;
  unsigned long badLines;
};

inline bool maraxIsNumber(const char* text) {
  if (*text == '\0') {
    return false;
  }
  for (; *text; text++) {
    if (!isdigit((unsigned char)*text)) {
      return false;
    }
  }
  return true;
}

inline bool maraxIsFlag(const char* text) {
  return (text[0] == '0' || text[0] == '1') && text[1] == '\0';
}

inline bool maraxIsNumber(const char* text, size_t digits) {
  return strlen(text) == digits && maraxIsNumber(text);
}

inline bool maraxParseTemp(const char* text, int* value) {
  if (!maraxIsNumber(text, 3)) {
    return false;
  }
  *value = atoi(text);
  return *value <= MARAX_MAX_TEMP;
}

// Field layout, separated by commas:
//   0  mode and firmware, e.g. "C1.06" or "+1.22"
//      V1 sends C (coffee) or V (steam), V2 sends C or +
//   1  steam boiler temperature, three digits
//   2  steam boiler target, three digits, or a code like "D07" while the boiler fills
//   3  heat exchanger temperature, three digits
//   4  boost mode countdown, four digits
//   5  heating element on/off
//   6  brew group on/off (the lever is up), missing on V1 machines
//
// The machine is read through SoftwareSerial, which drops bits now and then while
// WiFi is active and while the vibration pump runs. Anything that does not have exactly
// this layout is rejected: a lost comma would otherwise shift the boost countdown into
// the temperature.
inline bool parseMaraxLine(const char* line, MaraxData* out) {
  char copy[MARAX_LINE_SIZE];
  size_t length = strlen(line);
  if (length == 0 || length >= sizeof(copy)) {
    return false;
  }
  memcpy(copy, line, length + 1);
  while (length > 0 && (copy[length - 1] == '\r' || copy[length - 1] == ' ')) {
    copy[--length] = '\0';
  }

  const int maxFields = 7;
  char* fields[maxFields];
  int fieldCount = 1;
  fields[0] = copy;
  for (char* c = copy; *c; c++) {
    if (*c == ',') {
      if (fieldCount == maxFields) {
        return false;
      }
      *c = '\0';
      fields[fieldCount++] = c + 1;
    }
  }
  if (fieldCount < 6) {
    return false;
  }

  char mode = fields[0][0];
  const char* firmware = fields[0] + 1;
  if (!(isalpha((unsigned char)mode) || mode == '+')) {
    return false;
  }
  if (*firmware == '\0' || strlen(firmware) >= sizeof(out->firmware)) {
    return false;
  }
  for (const char* c = firmware; *c; c++) {
    if (!isalnum((unsigned char)*c) && *c != '.') {
      return false;
    }
  }

  MaraxData data;
  memset(&data, 0, sizeof(data));
  data.mode = mode;
  strcpy(data.firmware, firmware);

  if (!maraxParseTemp(fields[1], &data.steamTemp) || !maraxParseTemp(fields[3], &data.hxTemp)) {
    return false;
  }
  if (!maraxIsNumber(fields[4], 4)) {
    return false;
  }
  data.boostCountdown = atoi(fields[4]);
  if (!maraxIsFlag(fields[5])) {
    return false;
  }
  data.heating = fields[5][0] == '1';
  if (fieldCount == 7) {
    if (!maraxIsFlag(fields[6])) {
      return false;
    }
    data.hasPump = true;
    data.pump = fields[6][0] == '1';
  }

  if (strlen(fields[2]) != 3) {
    return false;
  }
  for (const char* c = fields[2]; *c; c++) {
    if (!isdigit((unsigned char)*c) && !isupper((unsigned char)*c)) {
      return false;
    }
  }
  if (!maraxParseTemp(fields[2], &data.steamTarget) || data.steamTarget == 0) {
    data.steamTarget = MARAX_NO_TARGET;
  }

  *out = data;
  return true;
}

// Copies the line into JSON-safe text, so that a garbled line cannot break the packet.
inline void maraxSanitize(char* out, size_t size, const char* text) {
  size_t i = 0;
  for (; text[i] && i + 1 < size; i++) {
    char c = text[i];
    out[i] = (c < 0x20 || c > 0x7E || c == '"' || c == '\\') ? '?' : c;
  }
  out[i] = '\0';
}

// Returns the length of the JSON text, or 0 if it does not fit into the buffer.
inline size_t formatMaraxStatus(char* out, size_t size, const MaraxStatus& status) {
  char raw[MARAX_LINE_SIZE];
  maraxSanitize(raw, sizeof(raw), status.rawLine);

  int written;
  if (status.machine) {
    const MaraxData& m = *status.machine;
    char target[8];
    if (m.steamTarget == MARAX_NO_TARGET) {
      strcpy(target, "null");
    } else {
      snprintf(target, sizeof(target), "%d", m.steamTarget);
    }
    written = snprintf(out, size,
      "{\"dev\":\"marax\",\"v\":1,\"online\":true,\"mode\":\"%c\",\"fw\":\"%s\",\"hx\":%d,"
      "\"steam\":%d,\"target\":%s,\"boost\":%d,\"heat\":%s,\"pump\":%s,\"shot\":%d,"
      "\"rssi\":%d,\"uptime\":%lu,\"bad\":%lu,\"raw\":\"%s\"}",
      m.mode, m.firmware, m.hxTemp, m.steamTemp, target, m.boostCountdown,
      m.heating ? "true" : "false", status.pump ? "true" : "false", status.shotSeconds,
      status.rssi, status.uptimeSeconds, status.badLines, raw);
  } else {
    written = snprintf(out, size,
      "{\"dev\":\"marax\",\"v\":1,\"online\":false,\"pump\":%s,\"shot\":%d,"
      "\"rssi\":%d,\"uptime\":%lu,\"bad\":%lu,\"raw\":\"%s\"}",
      status.pump ? "true" : "false", status.shotSeconds,
      status.rssi, status.uptimeSeconds, status.badLines, raw);
  }

  if (written < 0 || (size_t)written >= size) {
    return 0;
  }
  return (size_t)written;
}

#endif
