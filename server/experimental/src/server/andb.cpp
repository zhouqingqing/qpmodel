#include <iostream>

#include "common/common.h"
#include "optimizer/optimizer.h"
#include "parser/parser.h"
#include "runtime/runtime.h"

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

    auto query = "select count(*) from a join b on a.i=b.i";
    auto logplan = ParseAndAnalyze ((char*)query);
    auto phyplan = Optimize (logplan);
    Execute (phyplan, [] (Row* l) {
        std::cout << l->ToString () << std::endl;
        return false;
    });

    std::cout << phyplan->Explain () << std::endl;

    DefaultResource::DeleteMemoryResource (resource);
    instance_.Shutdown ();
}