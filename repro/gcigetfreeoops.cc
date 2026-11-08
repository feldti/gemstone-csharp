/*
 * Reproduces two problems of GciGetFreeOops, see docs/bugreport-gcigetfreeoops.md:
 *   1. Windows client only: GciGetFreeOops(n) with n > 2000 and n not a multiple of 2000 fails
 *      with error 2166 (lgcUncompress ... destSize < uncomprSize 16000); the session is broken
 *      afterwards.
 *   2. All clients: at most 100,000 OOPs per call; the rest of the result array stays 0 and
 *      GciErr reports nothing.
 *
 * Build on Linux x86-64 (as in the GemBuilder for C manual, "Linking a GCI application"; the
 * GemStone headers are C++, so compile with g++; with the 3.6.x headers and glibc 2.38 or later
 * also add -fpermissive):
 *   g++ -g -O0 -m64 -D_REENTRANT -D_GNU_SOURCE -pthread -fno-strict-aliasing -fno-exceptions \
 *       -I$GEMSTONE/include -c gcigetfreeoops.cc -o gcigetfreeoops.o
 *   g++ gcigetfreeoops.o $GEMSTONE/lib/gcirtlobj.o -m64 -Wl,-Bdynamic,--no-as-needed -lpthread \
 *       -Wl,--as-needed -ldl -lc -lm -lrt -z noexecstack -Wl,-z,lazy -o gcigetfreeoops
 *
 * Build on Windows x64 (Visual Studio or Build Tools, "x64 Native Tools Command Prompt"; GEMSTONE
 * is the Windows client directory):
 *   cl /W3 /MD /O2 /EHsc /TP /nologo /D_LP64 /D_AMD64_ /D_CONSOLE /D_DLL /DWIN32_LEAN_AND_MEAN
 *      /D_CRT_SECURE_NO_WARNINGS /DNATIVE /I "%GEMSTONE%\include" gcigetfreeoops.cc
 *      /link /FORCE:MULTIPLE "%GEMSTONE%\lib\gcirtlobj.obj" "%GEMSTONE%\lib\gcirpc.lib"
 *      ws2_32.lib netapi32.lib advapi32.lib user32.lib Secur32.lib Dbghelp.lib
 *      /NODEFAULTLIB:libcmt.lib /out:gcigetfreeoops.exe
 *
 * Run (GEMSTONE must be set; GciRtlLoad loads libgcirpc from the product directory):
 *   gcigetfreeoops <stone> <gemService> <user> <password>
 *   gcigetfreeoops gs64stone '!@localhost#netldi:gs64ldi!gemnetobject' DataCurator swordfish
 *
 * The program only asks for free OOPs; it creates no objects and commits nothing.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "gci.hf"

static OopType *buffer;

static int report(const char *what)
{
  GciErrSType err;
  if (GciErr(&err)) {
    printf("  %s: GemStone error %d: %s\n", what, (int)err.number, err.message);
    return 0;
  }
  return 1;
}

static int compareOops(const void *a, const void *b)
{
  OopType x = *(const OopType *)a, y = *(const OopType *)b;
  return x < y ? -1 : x > y;
}

/* One GciGetFreeOops call: how many entries are 0, how many distinct OOPs came back. */
static int getFreeOops(int count)
{
  int zeros = 0, distinct = 0, i, ok;

  memset(buffer, 0, sizeof(OopType) * (size_t)count);
  GciGetFreeOops(count, buffer);
  ok = report("GciGetFreeOops");
  qsort(buffer, (size_t)count, sizeof(OopType), compareOops);
  for (i = 0; i < count; i++) {
    if (buffer[i] == 0) zeros++;
    else if (i == 0 || buffer[i] != buffer[i - 1]) distinct++;
  }
  printf("GciGetFreeOops(%6d): %s, %6d distinct OOPs, %6d entries 0%s\n", count,
         ok ? "no error" : "ERROR   ", distinct, zeros,
         ok && zeros == 0 && distinct == count ? "" : "   <-- not as expected");
  fflush(stdout);
  return ok;
}

/* Is the session still usable? */
static void probe(void)
{
  OopType r = GciExecuteStr("3 + 4", OOP_NIL);
  if (report("GciExecuteStr('3 + 4')"))
    printf("  session OK: 3 + 4 = %lld\n", (long long)GciOopToI64(r));
  else
    printf("  the session is broken\n");
}

int main(int argc, char **argv)
{
  /* Issue 2 first: these counts are whole multiples of 2000 (or below 2000), which the
     Windows client handles; only the 100,000 limit shows. */
  static const int counts[] = { 1, 1000, 2000, 4000, 10000, 100000, 102000, 150000 };
  size_t i;
  char errBuf[1024];

  if (argc != 5) {
    fprintf(stderr, "usage: %s <stone> <gemService> <user> <password>\n", argv[0]);
    return 2;
  }
  buffer = (OopType *)malloc(sizeof(OopType) * 150000);
  if (!GciRtlLoad(TRUE, NULL, errBuf, sizeof errBuf)) {
    printf("GciRtlLoad failed: %s\n", errBuf);
    return 1;
  }
  if (!GciInit()) { printf("GciInit failed\n"); return 1; }
  GciSetNet(argv[1], "", "", argv[2]);
  if (!GciLoginEx(argv[3], argv[4], GCI_LOGIN_QUIET, 0)) {
    report("GciLoginEx");
    return 1;
  }
  printf("logged in, client %s\n", GciVersion());
  printf("\nIssue 2: at most 100,000 OOPs per call (expected: all OOPs, or an error)\n");
  for (i = 0; i < sizeof counts / sizeof counts[0]; i++)
    getFreeOops(counts[i]);
  probe();

  /* Issue 1: a count above 2000 that is not a multiple of 2000. Fails on the Windows client
     (error 2166) and breaks the session; works with the Linux client. */
  printf("\nIssue 1: a count > 2000 that is not a multiple of 2000 (expected: 4001 OOPs)\n");
  getFreeOops(4001);
  probe();

  GciLogout();
  return 0;
}
