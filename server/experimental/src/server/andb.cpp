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
    
    strcpy (query, "select a1 from a");
    bool moreInput = getNextStmt ();

    while ((mode_interactive_on || mode_batch_on) && moreInput) {
        SQLParserResult presult;
        bool ret = ParseSQL (query, &presult);

        if (ret) {
            std::cout << "PASSED: " << query << std::endl;

            const SelectStatement *selStmt = (const SelectStmt *)presult.getStatement (0);
            std::cout << "EXPLAIN: " << selStmt->Explain () << "\n";
        } else {
            const char* emsg = presult.errorMsg ();
            int el = presult.errorLine ();
            int ec = presult.errorColumn ();
            std::cout << "FAILED: " << query << std::endl;
            std::cout << "ERROR: " << (emsg ? emsg : "unkown") << " L = " << el << " C = " << ec
                      << std::endl;
        }

        presult.reset ();
        moreInput = getNextStmt ();
    }
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
                mode_interactive_on = false;
                mode_batch_on = true;
                break;

            default:
                std::cout << "andb: unknown option " << (char)c << " \n";
                exit (-1);
        }
    }
}

static bool setupInput () {
    if (mode_batch_on) {
        querySrc = std::ifstream (inputFile);
        if (querySrc.bad () || querySrc.eof()) return false;
    }

    return true;
}

static bool getNextStmt () {
    int blen;

    while (true) {
        if (mode_batch_on) {
            querySrc.getline (bufptr, ANDB_LINE_SIZE - 1, ';');
            if (querySrc.bad () || querySrc.eof ()) return false;
        } else if (mode_interactive_on) {
            std::cout << "ASQL> ";
            std::cin.getline (bufptr, ANDB_LINE_SIZE - 1, ';');
        }

        blen = strlen (bufptr);
        char* cp = bufptr;
        for (int i = 0; i < blen; ++i) {
            if (*cp == '\r' || *cp == '\n' || isspace(*cp))
                *cp++ = ' ';
            else
                break;
        }

        if (!cp || cp[0] == 0 || *cp == '#' || (*cp == '-' && *(cp + 1) == '-')) {
            continue;
        } else if ((blen = strlen (cp)) >= ANDB_LINE_SIZE - 1 ||
            !strcmp (cp, "QUIT") || !strcmp (cp, "QUIT;") || !strcmp (cp, "\nQUIT;")) {
            return false;
        }
        /* append ; and return true */
        cp[blen++] = ';';
        cp[blen] = 0;

        return true;
    }
}
