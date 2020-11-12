#include <iostream>

#include "common/common.h"
#include "optimizer/optimizer.h"
#include "parser/include/parser.h"
#include "runtime/runtime.h"
#include "parser/include/expr.h"
#include "parser/include/stmt.h"
#include "parser/include/SQLParserResult.h"

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

int main () {
    // _crtBreakAlloc = 175;
    instance_.Start ();

    auto resource = DefaultResource::CreateMemoryResource (currentResource_, "current query");
    currentResource_ = resource;

#ifdef __LATER
    auto query = "select count(*) from a join b on a.i=b.i";
    auto logplan = ParseAndAnalyze ((char*)query);
#else
    char *query = "select a1 from a";
    SQLParserResult presult;
    bool ret = ParseSQL (query, &presult);
#endif

#ifdef __LATER
    auto phyplan = Optimize(logplan);
    Execute (phyplan, [] (Row* l) {
        std::cout << l->ToString () << std::endl;
        return false;
    });

    std::cout << phyplan->Explain () << std::endl;
#endif

    DefaultResource::DeleteMemoryResource (resource);
    instance_.Shutdown ();
}
