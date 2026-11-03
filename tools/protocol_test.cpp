// Tests timer/marax_protocol.h on the PC, no ESP8266 needed.
//
//   g++ -std=c++17 tools/protocol_test.cpp -o protocol_test
//   protocol_test                 runs the tests
//   protocol_test --samples       prints packets for tools/protocol_samples.jsonl,
//                                 which the tray app's tests read

#include <cstdio>
#include <cstring>

#include "../timer/marax_protocol.h"

static int failures = 0;

#define CHECK(condition)                                         \
  do {                                                           \
    if (!(condition)) {                                          \
      printf("FAILED line %d: %s\n", __LINE__, #condition);      \
      failures++;                                                \
    }                                                            \
  } while (0)

static void testLineFormats() {
  MaraxData d;

  CHECK(parseMaraxLine("+1.22,112,124,093,0000,1,0", &d));
  CHECK(d.mode == '+');
  CHECK(strcmp(d.firmware, "1.22") == 0);
  CHECK(d.steamTemp == 112);
  CHECK(d.steamTarget == 124);
  CHECK(d.hxTemp == 93);
  CHECK(d.boostCountdown == 0);
  CHECK(d.heating);
  CHECK(d.hasPump);
  CHECK(!d.pump);

  CHECK(parseMaraxLine("C1.06,116,124,093,0840,1,1", &d));
  CHECK(d.mode == 'C');
  CHECK(d.boostCountdown == 840);
  CHECK(d.pump);

  // V1 has no field for the brew group
  CHECK(parseMaraxLine("C1.06,112,124,097,0000,0", &d));
  CHECK(strcmp(d.firmware, "1.06") == 0);
  CHECK(d.hxTemp == 97);
  CHECK(!d.heating);
  CHECK(!d.hasPump);
  CHECK(!d.pump);

  // a real line of a Mara X V2 with firmware 1.09, ending in a carriage return
  CHECK(parseMaraxLine("C1.09,129,136,092,0000,1,1\r", &d));
  CHECK(d.steamTarget == 136);
  CHECK(d.pump);

  CHECK(parseMaraxLine("V1.06,116,124,093,0840,1,0\r", &d));
  CHECK(d.mode == 'V');
  CHECK(!d.pump);
}

static void testMissingTarget() {
  MaraxData d;

  // a code instead of a target while the boiler fills after power on
  CHECK(parseMaraxLine("C1.09,022,D07,022,0000,0,0", &d));
  CHECK(d.steamTarget == MARAX_NO_TARGET);
  CHECK(d.hxTemp == 22);

  CHECK(parseMaraxLine("C1.06,045,000,040,0000,0", &d));
  CHECK(d.steamTarget == MARAX_NO_TARGET);
}

static void testRejectedLines() {
  MaraxData d;

  const char* bad[] = {
    "",
    "\r",
    ",,,,,,",
    "+1.22",
    "+1.22,112,124",
    "+1.22,112,124,093,0000",
    "+1.22,11?,124,093,0000,1,0",
    "+1.22,112,124,0x3,0000,1,0",
    "+1.22,112,124,,0000,1,0",
    "+1.22,112,124,999,0000,1,0",
    "+1.22,300,124,093,0000,1,0",
    "+1.22,112,124,093,00000,1,0",
    "+1.22,112,124,093,0000,2,0",
    "+1.22,112,124,093,0000,1,7",
    "+1.22,112,124,093,0000,1,",
    "+1.22,112,124,093,0000,1,0,5",
    "\"1.22,112,124,093,0000,1,0",
    "+,112,124,093,0000,1,0",
    "+1.2\"2,112,124,093,0000,1,0",
    "+1.2222222,112,124,093,0000,1,0",
    "\xff\xfe\x80,112,124,093,0000,1,0",
    "+1.22,112,124,093,0000,1,0 this line is far too long",
    // seen on a real machine: a garbled comma turned the boost countdown into HX 0
    ("C1.09,127,126\xac" "094,0000,0,0"),
    "C1.09,12,124,093,0000,1,0",
    "C1.09,0129,124,093,0000,1,0",
    "C1.09,129,124,93,0000,1,0",
    "C1.09,129,124,093,000,1,0",
    "C1.09,129,24,093,0000,1,0",
    "C1.09,129,d07,093,0000,1,0",
    "C1.09,129,D0?,093,0000,1,0",
  };
  for (const char* line : bad) {
    // a valid line first, so that a truncated line can't pass on what is left on the stack
    CHECK(parseMaraxLine("+1.22,112,124,093,0000,1,0", &d));
    d.hxTemp = 12345;
    if (parseMaraxLine(line, &d)) {
      printf("FAILED: accepted \"%s\"\n", line);
      failures++;
    }
    CHECK(d.hxTemp == 12345);  // rejected lines leave the last value alone
  }
}

static MaraxStatus baseStatus(const MaraxData* machine, const char* raw) {
  MaraxStatus s;
  s.machine = machine;
  s.rawLine = raw;
  s.pump = false;
  s.shotSeconds = 0;
  s.rssi = -61;
  s.uptimeSeconds = 1234;
  s.badLines = 2;
  return s;
}

static void testJson() {
  char out[256];
  MaraxData d;
  CHECK(parseMaraxLine("+1.22,112,124,093,0000,1,0", &d));

  MaraxStatus s = baseStatus(&d, "+1.22,112,124,093,0000,1,0");
  s.pump = true;
  s.shotSeconds = 12;
  size_t length = formatMaraxStatus(out, sizeof(out), s);
  CHECK(length == strlen(out));
  CHECK(strcmp(out,
    "{\"dev\":\"marax\",\"v\":1,\"online\":true,\"mode\":\"+\",\"fw\":\"1.22\",\"hx\":93,"
    "\"steam\":112,\"target\":124,\"boost\":0,\"heat\":true,\"pump\":true,\"shot\":12,"
    "\"rssi\":-61,\"uptime\":1234,\"bad\":2,\"raw\":\"+1.22,112,124,093,0000,1,0\"}") == 0);

  CHECK(parseMaraxLine("C1.09,022,D07,022,0000,0,0", &d));
  s = baseStatus(&d, "C1.09,022,D07,022,0000,0,0");
  CHECK(formatMaraxStatus(out, sizeof(out), s) > 0);
  CHECK(strstr(out, "\"target\":null,") != NULL);

  s = baseStatus(NULL, "");
  CHECK(formatMaraxStatus(out, sizeof(out), s) > 0);
  CHECK(strcmp(out,
    "{\"dev\":\"marax\",\"v\":1,\"online\":false,\"pump\":false,\"shot\":0,"
    "\"rssi\":-61,\"uptime\":1234,\"bad\":2,\"raw\":\"\"}") == 0);

  // quotes and control characters in a garbled line must not break the JSON
  s = baseStatus(NULL, "ab\"c\\d\x01\xff");
  CHECK(formatMaraxStatus(out, sizeof(out), s) > 0);
  CHECK(strstr(out, "\"raw\":\"ab?c?d??\"}") != NULL);

  // the largest possible packet has to fit into the buffer of timer.ino
  CHECK(parseMaraxLine("+1.2222,200,200,200,9999,1,1", &d));
  s = baseStatus(&d, "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx");
  s.shotSeconds = 2147483;
  s.rssi = -100;
  s.uptimeSeconds = 4294967;
  s.badLines = 4294967295UL;
  length = formatMaraxStatus(out, sizeof(out), s);
  CHECK(length > 0);
  printf("largest packet: %u bytes\n", (unsigned)length);

  char tiny[20];
  CHECK(formatMaraxStatus(tiny, sizeof(tiny), s) == 0);
}

static void printSample(const char* line, bool valid, bool pump, int shot) {
  char out[256];
  MaraxData d;
  MaraxStatus s = baseStatus(NULL, line);
  if (valid && parseMaraxLine(line, &d)) {
    s.machine = &d;
  }
  s.pump = pump;
  s.shotSeconds = shot;
  formatMaraxStatus(out, sizeof(out), s);
  puts(out);
}

static void printSamples() {
  printSample("+1.22,112,124,070,0900,1,0", true, false, 0);
  printSample("+1.22,124,124,093,0000,0,0", true, false, 0);
  printSample("C1.06,124,124,093,0000,1,1", true, true, 12);
  printSample("C1.06,112,124,097,0000,0", true, false, 0);
  printSample("C1.09,022,D07,022,0000,0,0", true, false, 0);
  printSample("", false, false, 0);
  printSample("Z,unknown,format", false, false, 0);
}

int main(int argc, char** argv) {
  if (argc > 1 && strcmp(argv[1], "--samples") == 0) {
    printSamples();
    return 0;
  }
  testLineFormats();
  testMissingTarget();
  testRejectedLines();
  testJson();
  printf(failures == 0 ? "all tests passed\n" : "%d test(s) failed\n", failures);
  return failures == 0 ? 0 : 1;
}
