#include <iostream>
#include <fstream>
#include <cstring>
#include <cctype>

#include "common/common.h"
#include "optimizer/optimizer.h"
#include "parser/include/parser.h"
#include "runtime/runtime.h"
#include "parser/include/expr.h"
#include "parser/include/stmt.h"
#include "parser/include/SQLParserResult.h"

#ifdef _MSC_VER
#include "common/getopt.h"
#else
#include <getopt.h>
#endif

char* ANDB_OPTIONS = "heif:";
const int ANDB_LINE_SIZE = 8192;
const char ANDB_HELP[] =
    "\n\
andb [-h] [-i] [-e] [-f file]\n\
-h      : Print this help and exit\n\
-e      : EXPLAIN the input statement\n\
-i      : Enter interactive mode. QUIT exits\n\
-f file : take the input from file\n\
";

bool mode_interactive_on, mode_batch_on, mode_explain_on;
std::string inputFile;
static std::ifstream querySrc;
static char query[ANDB_LINE_SIZE] = {0};
static char* bufptr = query;

static void processOptions (int argc, char* argv[]);
static void processSQL (void);
static bool setupInput ();
static bool getNextStmt ();

using namespace andb;

class IInstance {
public:
    virtual ~IInstance () {}
    virtual void Start () = 0;
    virtual void Shutdown () = 0;
};

// The global singleton instance
//  - profilers
//
class Instance : public IInstance {
public:
    void Start () override {}
    void Shutdown () override {}
};

static Instance instance_;
static CrtCheckMemory memoryChecker;

int main(int argc, char* argv[])
{
    // _crtBreakAlloc = 175;
    instance_.Start ();

    auto resource = DefaultResource::CreateMemoryResource (currentResource_, "current query");
    currentResource_ = resource;

    processOptions (argc, argv);
    processSQL ();

    DefaultResource::DeleteMemoryResource (resource);
    instance_.Shutdown ();
}

static void processSQL (void) {
    
    setupInput ();
    bool moreInput = true;
    
    strcpy (query, "select a1 from a");
    (void)getNextStmt ();

    do {

        SQLParserResult presult;
        bool ret = ParseSQL (query, &presult);

        if (ret) {
            std::cout << "passed: " << query << std::endl;
        } else {
            const char* emsg = presult.errorMsg ();
            int el = presult.errorLine ();
            int ec = presult.errorColumn ();
            std::cout << "failed: " << query << std::endl;
            std::cout << "error: " << (emsg ? emsg : "unkown") << " L = " << el << " C = " << ec
                      << std::endl;
        }

        moreInput = getNextStmt ();

    } while ((mode_interactive_on || mode_batch_on) && moreInput);
}

static void processOptions (int argc, char* argv[]) {
    if (argc < 2) return;

    int c;
    while ((c = getopt(argc, argv, ANDB_OPTIONS)) != EOF) {
        switch (c) {
            case 'h':
                std::cout << ANDB_HELP;
                exit (0);
                break; /*NOTREACHED*/

            case 'e':
                mode_explain_on = true;
                break;

            case 'i':
                mode_interactive_on = true;
                mode_batch_on = false;
                break;

            case 'f':
                inputFile = optarg;
                break;

            default:
                std::cout << "andb: unknown option " << (char)c << " \n";
                exit (-1);
        }
    }
}

static bool setupInput () {
    if (mode_interactive_on) {
        querySrc = std::ifstream (inputFile);
        if (querySrc.bad () || querySrc.eof()) return false;
    }

    return true;
}

static bool getNextStmt () {

    int blen;
    if (mode_batch_on) {
        querySrc.getline (bufptr, ANDB_LINE_SIZE - 1, ';');
        if (querySrc.bad () || querySrc.eof ()) return false;
    } else if (mode_interactive_on) {
        std::cout << "ASQL> ";
        std::cin.getline (bufptr, ANDB_LINE_SIZE - 1, ';');
    }
    
    blen = strlen (bufptr);
    char *cp = bufptr;
    for (int i = 0; i < blen; ++i) {
        if (*cp == '\r' || *cp == '\n')
            *cp++ = ' ';
        else
            break;
    }

    if (bufptr[0] == 0 || (blen = strlen (bufptr)) >= ANDB_LINE_SIZE - 1 ||
        !strcmp(bufptr, "QUIT;") || !strcmp(bufptr, "\nQUIT;"))
        return false;
    /* append ; and return true */
    bufptr[blen++] = ';';
    bufptr[blen] = 0;

    return true;
}
