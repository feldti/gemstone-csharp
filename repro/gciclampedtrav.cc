/*
 * Reproduces: GciClampedTrav crashes the gem (SIGSEGV in fullMethodLookup, rawSelectorId=0,
 * called from findNextOopToReport) when the ClampSpecification has instance variable levels
 * and its instVarLevelsSelector is nil. See docs/bugreport-gciclampedtrav.md.
 *
 * Build (Linux x86-64, as in the GemBuilder for C manual, "Linking a GCI application"; the
 * GemStone headers are C++, so compile with g++):
 *   g++ -g -O0 -m64 -D_REENTRANT -D_GNU_SOURCE -pthread -fno-strict-aliasing -fno-exceptions \
 *       -I$GEMSTONE/include -c gciclampedtrav.cc -o gciclampedtrav.o
 *   g++ gciclampedtrav.o $GEMSTONE/lib/gcirtlobj.o -m64 -Wl,-Bdynamic,--no-as-needed -lpthread \
 *       -Wl,--as-needed -ldl -lc -lm -lrt -z noexecstack -Wl,-z,lazy -o gciclampedtrav
 * With the 3.6.x headers and glibc 2.38 or later, add -fpermissive (gcistring.hf redeclares
 * strlcpy/strlcat as static).
 * At run time, GciRtlLoad loads libgcirpc from $GEMSTONE/lib; use the product directory that
 * matches the stone's version.
 *
 * Run:
 *   ./gciclampedtrav <stone> <gemService> <user> <password>
 *   ./gciclampedtrav gs64stone '!@localhost#netldi:gs64ldi!gemnetobject' DataCurator swordfish
 *
 * Every case logs in on its own, because a crash ends the session. Only the gem of that
 * session crashes; the stone and other sessions are not affected.
 */
#include <stdio.h>
#include <stdlib.h>
#include "gci.hf"

static const char *stone, *gemService, *user, *password;

static int check(const char *what)
{
  GciErrSType err;
  if (GciErr(&err)) {
    printf("  %s: GemStone error %d: %s\n", what, (int)err.number, err.message);
    return 0;
  }
  return 1;
}

static int login(void)
{
  GciSetNet(stone, "", "", gemService);
  if (!GciLoginEx(user, password, GCI_LOGIN_QUIET, 0)) {
    check("GciLoginEx");
    return 0;
  }
  return 1;
}

/* Is the session still usable? */
static int probe(void)
{
  OopType r = GciExecuteStr("3 + 4", OOP_NIL);
  if (check("GciExecuteStr('3 + 4')")) {
    printf("  session OK: 3 + 4 = %lld\n", (long long)GciOopToI64(r));
    return 1;
  }
  printf("  session is lost\n");
  return 0;
}

/*
 * One clamped traversal, level 4, of the object answered by rootSource, with the
 * ClampSpecification answered by clampSource (NULL: no ClampSpecification, OOP_NIL).
 */
static void traverse(const char *label, const char *rootSource, const char *clampSource)
{
  GciClampedTravArgsSType args;
  GciTravBufType *buf;
  OopType root;
  BoolType done;
  unsigned long long bytes = 0;
  int calls = 1;

  printf("%s\n", label);
  fflush(stdout);
  if (!login()) return;
  root = GciExecuteStr(rootSource, OOP_NIL);
  if (!check("root")) { GciLogout(); return; }
  if (clampSource != NULL) {
    args.clampSpec = GciExecuteStr(clampSource, OOP_NIL);
    if (!check("ClampSpecification")) { GciLogout(); return; }
  }
  buf = GciTravBufType::malloc(64 * 1024);
  args.travBuff = buf;
  args.level = 4;
  done = GciClampedTrav(&root, 1, &args);
  if (check("GciClampedTrav")) {
    bytes += buf->usedBytes;
    while (!done) {
      done = GciMoreTraversal(buf);
      calls++;
      if (!check("GciMoreTraversal")) break;
      bytes += buf->usedBytes;
    }
    printf("  traversal done: %llu bytes of object reports in %d call(s)\n", bytes, calls);
  }
  buf->free();
  if (probe()) GciLogout();
}

int main(int argc, char **argv)
{
  if (argc != 5) {
    fprintf(stderr, "usage: %s <stone> <gemService> <user> <password>\n", argv[0]);
    return 2;
  }
  stone = argv[1]; gemService = argv[2]; user = argv[3]; password = argv[4];
  {
    /* gcirtlobj.o: load libgcirpc from $GEMSTONE/lib at run time. */
    char errBuf[1024];
    if (!GciRtlLoad(TRUE, NULL, errBuf, sizeof errBuf)) {
      printf("GciRtlLoad failed: %s\n", errBuf);
      return 1;
    }
  }
  if (!GciInit()) { printf("GciInit failed\n"); return 1; }
  if (!login()) return 1;
  printf("logged in, %s\n", GciVersion());
  GciLogout();

  /* An Array with an Association whose value is another Array: kernel classes only. */
  const char *root = "Array with: (Association newWithKey: #k value: (Array with: 1 with: 2)) with: 'text'";

  traverse("1. no ClampSpecification (expected: traversal done)", root, NULL);
  traverse("2. empty ClampSpecification (expected: traversal done)", root,
           "ClampSpecification newWithIdClamps: #() classClamps: #() instVarLevels: #()");
  traverse("3. ClampSpecification example1, from the image (expected: traversal done)", root,
           "ClampSpecification example1");
  traverse("4. instVarLevels {Association . #(#value 0)}, instVarLevelsSelector nil (expected: traversal done)", root,
           "ClampSpecification newWithIdClamps: #() classClamps: #() instVarLevels: { {Association . #(#value 0)} }");
  traverse("5. the same with instVarLevelsSelector: #myDependents (answers nil for these objects)", root,
           "(ClampSpecification newWithIdClamps: #() classClamps: #() instVarLevels: { {Association . #(#value 0)} })"
           " instVarLevelsSelector: #myDependents; yourself");
  return 0;
}
