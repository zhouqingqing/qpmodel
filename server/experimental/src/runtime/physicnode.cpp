#include "runtime/physicnode.h"

namespace andb {
#pragma region dispatchTable
#define LIST_OF_PHYSICNODE \
    X (PhysicAgg)          \
    X (PhysicHashJoin)     \
    X (PhysicScan)

// This function is invoked per row so performance is important
// 1. use template argument for lambda callback for inline possibility
// 2. inline the ExecT() function but not Exec##_T because it is always an indirect call
// 3. do not use dynamic_cast here as we already verified the type matching, so the
// compiler generates an unnecessary function call
//
#define DECL_EXEC_FUNC_FOR(_T)                         \
    template <typename Fn>                             \
    void Exec##_T (PhysicNode* node, Fn&& callback) {  \
        assert (dynamic_cast<_T*> (node) != nullptr);  \
        static_cast<_T*> (node)->_T::ExecT (callback); \
    }

#define X(name) DECL_EXEC_FUNC_FOR (name)
LIST_OF_PHYSICNODE
#undef X

const DispatchTableEntry ExecDispatchTable_[] = {
#define X(name) {name ## _, #name, Exec##name},
    LIST_OF_PHYSICNODE
#undef X
};
#pragma endregion "instead of using vtable, we can dispatch function with indirect call"

void PhysicScan::Open (ExecContext* context) {
    auto logic = static_cast<LogicScan*> (logic_);
    PhysicNode::Open (context);
    eval_.Open (logic->filter_);
}

void PhysicScan::Close () {
    eval_.Close ();
    PhysicNode::Close ();
}
}  // namespace andb
