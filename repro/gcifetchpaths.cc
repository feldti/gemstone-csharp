/*
 * Reproduces: GciFetchPaths crashes the gem (SIGSEGV in checkFetchPathOffset) when a path
 * step is applied to a special object such as nil. See docs/bugreport-gcifetchpaths.md.
 *
 * Build (Linux x86-64, as in the GemBuilder for C manual, "Linking a GCI application"; the
 * GemStone headers are C++, so compile with g++):
 *   g++ -g -O0 -m64 -D_REENTRANT -D_GNU_SOURCE -pthread -fno-strict-aliasing -fno-exceptions \
 *       -I$GEMSTONE/include -c gcifetchpaths.cc -o gcifetchpaths.o
 *   g++ gcifetchpaths.o $GEMSTONE/lib/gcirtlobj.o -m64 -Wl,-Bdynamic,--no-as-needed -lpthread \
 *       -Wl,--as-needed -ldl -lc -lm -lrt -z noexecstack -Wl,-z,lazy -o gcifetchpaths
 * With the 3.6.x headers and glibc 2.38 or later, add -fpermissive (gcistring.hf redeclares
 * strlcpy/strlcat as static).
 * At run time, GciRtlLoad loads libgcirpc from $GEMSTONE/lib; use the product directory that
 * matches the stone's version.
 *
 * Run:
 *   ./gcifetchpaths <stone> <gemService> <user> <password>
 *   ./gcifetchpaths gs64stone '!@localhost#netldi:gs64ldi!gemnetobject' DataCurator swordfish
 *
 * Only the gem of this session crashes; the stone and other sessions are not affected.
 */
#include <stdio.h>
#include <stdlib.h>
#include <time.h>
#include "gci.hf"

static int check(const char *what)
{
  GciErrSType err;
  if (GciErr(&err)) {
    printf("  %s: GemStone error %d: %s\n", what, (int)err.number, err.message);
    return 0;
  }
  return 1;
}

/* One GciFetchPaths call with one path; prints the result. */
static void fetch(const char *label, OopType root, const int path[], int pathSize)
{
  OopType result = OOP_NIL;
  time_t start = time(NULL);
  BoolType ok;

  printf("%s\n", label);
  fflush(stdout);
  ok = GciFetchPaths(&root, 1, path, &pathSize, 1, &result);
  printf("  returned %s, result = 0x%llx (OOP_ILLEGAL = 0x%llx), %ld s\n",
         ok ? "TRUE" : "FALSE", (unsigned long long)result,
         (unsigned long long)OOP_ILLEGAL, (long)(time(NULL) - start));
  check("GciFetchPaths");
}

/* Is the session still usable? */
static void probe(void)
{
  OopType r = GciExecuteStr("3 + 4", OOP_NIL);
  if (check("GciExecuteStr('3 + 4')"))
    printf("  session OK: 3 + 4 = %lld\n", (long long)GciOopToI64(r));
  else
    printf("  session is lost\n");
}

int main(int argc, char **argv)
{
  OopType arr;

  if (argc != 5) {
    fprintf(stderr, "usage: %s <stone> <gemService> <user> <password>\n", argv[0]);
    return 2;
  }
  {
    /* gcirtlobj.o: load libgcirpc from $GEMSTONE/lib at run time. */
    char errBuf[1024];
    if (!GciRtlLoad(TRUE, NULL, errBuf, sizeof errBuf)) {
      printf("GciRtlLoad failed: %s\n", errBuf);
      return 1;
    }
  }
  if (!GciInit()) { printf("GciInit failed\n"); return 1; }
  GciSetNet(argv[1], "", "", argv[2]);
  if (!GciLoginEx(argv[3], argv[4], GCI_LOGIN_QUIET, 0)) {
    check("GciLoginEx");
    return 1;
  }
  printf("logged in, %s\n", GciVersion());

  /* arr = #(nil 42): indexed slot 1 is nil, indexed slot 2 is the SmallInteger 42. */
  arr = GciExecuteStr("Array with: nil with: 42", OOP_NIL);
  if (!check("GciExecuteStr")) return 1;

  {
    /* Paths: negative = indexed slot, positive = named instance variable (1-based). */
    static const int outOfRange[] = { -3 };      /* one step, index out of range */
    static const int toNil[]      = { -1 };      /* one step, gives nil */
    static const int fromNil[]    = { -1, 1 };   /* second step applied to nil -> crash */

    fetch("1. single step, index out of range (expected: FALSE, OOP_ILLEGAL)", arr, outOfRange, 1);
    probe();
    fetch("2. single step to nil (expected: TRUE, OOP_NIL = 0x14)", arr, toNil, 1);
    probe();
    fetch("3. two steps, the second one from nil (expected: FALSE, OOP_ILLEGAL)", arr, fromNil, 2);
    probe();
  }

  GciLogout();
  return 0;
}
