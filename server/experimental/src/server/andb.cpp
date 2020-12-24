#include <iostream>
#include <fstream>
#include <stdexcept>
#include <cstring>
#include <cctype>

#include "common/common.h"
#include "common/catalog.h"
#include "optimizer/optimizer.h"
#include "parser/include/parser.h"
#include "runtime/runtime.h"
#include "parser/include/expr.h"
#include "parser/include/stmt.h"
#include "optimizer/binder.h"
#include "parser/include/sqlparserresult.h"

#ifdef _MSC_VER
#include "common/getopt.h"
#else
#include <getopt.h>
#endif

const char* ANDB_OPTIONS   = "heif:";
const int   ANDB_LINE_SIZE = 8192;
const char  ANDB_HELP[]    = "\n\
andb [-h] [-i] [-e] [-f file]\n\
-h      : Print this help and exit\n\
-e      : EXPLAIN the input statement\n\
-i      : Enter interactive mode, default. QUIT exits\n\
          if -i and -f are specified, -f takes precedence.\n\
-f file : take the input from file\n\
";

bool                 mode_interactive_on{ true }, mode_batch_on{ false }, mode_explain_on{ false };
std::string          inputFile;
static std::ifstream querySrc;
static char          query[ANDB_LINE_SIZE] = { 0 };
static char*         bufptr                = query;

static void processOptions(int argc, char* argv[]);
static void processSQL(void);
static bool setupInput();
static bool getNextStmt();
static void ShowResultSet(std::vector<andb::Row>& rows);

using namespace andb;

class IInstance
{
    public:
    virtual ~IInstance() {}
    virtual void Start()    = 0;
    virtual void Shutdown() = 0;
};

// The global singleton instance
//  - profilers
//
class Instance : public IInstance
{
    public:
    void Start() override {}
    void Shutdown() override {}
};

static Instance       instance_;
static CrtCheckMemory memoryChecker;

int main(int argc, char* argv[])
{
    // crtBreakAlloc = 175;

    instance_.Start();

    auto resource        = DefaultResource::CreateMemoryResource(currentResource_, "current query");
    currentResource_     = resource;
    bool catalogInitDone = false;
    Catalog::Init();
    catalogInitDone = true;

    try {
        processOptions(argc, argv);
        processSQL();
    } catch (...) {
        Catalog::DeInit();
        catalogInitDone = false;
    }

    if (catalogInitDone)
        Catalog::DeInit();

    DefaultResource::DeleteMemoryResource(resource);
    instance_.Shutdown();
}

static void processSQL(void)
{
    setupInput();

    strcpy(query, "select a1 from a");
    bool moreInput = getNextStmt();

    while ((mode_interactive_on || mode_batch_on) && moreInput) {
        SQLParserResult presult;
        SelectStatement* selStmt = nullptr;
        try {
            bool ret = ParseSQL(query, &presult);
            PhysicNode* physic = nullptr;
            LogicNode*  root   = nullptr;
            selStmt = nullptr;

            if (ret) {
                std::cout << "PASSED: " << query << std::endl;

                selStmt = (SelectStmt*)presult.getStatement(0);
                Binder binder (selStmt);
                binder.Bind ();
                if (binder.GetError ()) {
                    std::cout << "ERROR: Binder error\n";
                    continue;
                }
                root = selStmt->CreatePlan();
                if (!root)
                    continue;
                physic = selStmt->Optimize();
                if (!physic)
                    continue;
                std::cout << "EXPLAIN: " << selStmt->Explain() << "\n";
                if (selStmt->Open()) {
                    std::vector<andb::Row> rset = selStmt->Exec();
                    if (!rset.empty())
                        ShowResultSet(rset);
                    selStmt->Close();
                }
            } else {
                const char* emsg = presult.errorMsg();
                int         el   = presult.errorLine();
                int         ec   = presult.errorColumn();
                std::cout << "FAILED: " << query << std::endl;
                std::cout << "ERROR: " << (emsg ? emsg : "unkown") << " L = " << el << " C = " << ec
                          << std::endl;
            }
        }
        catch (const SemanticAnalyzeException& sae) {
            std::cerr << "EXCEPTION: " << sae.what() << std::endl;
        }
        catch (const SemanticExecutionException& see) {
            std::cerr << "EXCEPTION: " << see.what() << std::endl;
        }
        catch (const SemanticException& sme) {
            std::cerr << "EXCEPTION: " << sme.what() << std::endl;
        }
        catch (const RuntimeException& rte) {
            std::cerr << "EXCEPTION: " << rte.what() << std::endl;
        }
        catch (const ParserException& pae) {
            std::cerr << "EXCEPTION: " << pae.what() << std::endl;
        } catch (const NotImplementedException& nyi) {
            std::cerr << "EXCEPTION: " << nyi.what() << std::endl;
        }
        catch (const std::exception& e) {
            std::cerr << "EXCEPTION: " << e.what() << std::endl;
        } catch (...) {
            std::cerr << "EXCEPTION: Unknown exception" << std::endl;
        }
            
        delete selStmt;
            
        presult.reset();
        moreInput = getNextStmt();
    }
}

static void processOptions(int argc, char* argv[])
{
    if (argc < 2)
        return;

    int c;
    while ((c = getopt(argc, argv, ANDB_OPTIONS)) != EOF) {
        switch (c) {
            case 'h':
                std::cout << ANDB_HELP;
                exit(0);
                break; /*NOTREACHED*/

            case 'e':
                mode_explain_on = true;
                break;

            case 'i':
                if (!mode_batch_on)
                    mode_interactive_on = true;
                break;

            case 'f':
                inputFile           = optarg;
                mode_interactive_on = false;
                mode_batch_on       = true;
                break;

            default:
                std::cout << "andb: unknown option " << (char)c << " \n";
                exit(-1);
        }
    }
}

static bool setupInput()
{    
    if (mode_batch_on) {
        querySrc = std::ifstream(inputFile);
        if (querySrc.bad() || querySrc.eof())
            return false;
    }

    return true;
}

static bool getNextStmt()
{
    int blen;

    while (true) {
        if (mode_batch_on) {
            querySrc.getline(bufptr, ANDB_LINE_SIZE - 1, ';');
            if (querySrc.bad() || querySrc.eof())
                return false;
        } else if (mode_interactive_on) {
            std::cout << "ASQL> ";
            std::cin.getline(bufptr, ANDB_LINE_SIZE - 1, ';');
        }

        blen     = strlen(bufptr);
        char* cp = bufptr;
        for (int i = 0; i < blen; ++i) {
            if (*cp == '\r' || *cp == '\n' || isspace(*cp))
                *cp++ = ' ';
            else
                break;
        }

        if (!cp || cp[0] == 0 || *cp == '#' || (*cp == '-' && *(cp + 1) == '-')) {
            continue;
        } else if ((blen = strlen(cp)) >= ANDB_LINE_SIZE - 1 || !andb_strcmp_nocase(cp, "quit") ||
                   !andb_strcmp_nocase(cp, "quit;") || !andb_strcmp_nocase(cp, "\nquit;")) {
            return false;
        }
        /* append ; and return true */
        cp[blen++] = ';';
        cp[blen]   = 0;

        return true;
    }
}

static void ShowResultSet(std::vector<Row>& rows)
{
    for (auto r : rows) {
        std::cout << r.ToString() << std::endl;
    }
}
