#include "common/common.h"
#include "optimizer/optimizer.h"
#include "parser/include/stmt.h"

namespace andb {
PhysicNode* DirectLogicToPhysic (LogicNode* logic) {
#define PHY(_which) DirectLogicToPhysic (log->children_[_which])

    switch (logic->classTag_) {
        case LogicAgg_: {
            auto log = static_cast<LogicAgg*> (logic);
            return new PhysicAgg (log, PHY (0));
        } break;
        case LogicJoin_: {
            auto log = static_cast<LogicJoin*> (logic);
            return new PhysicHashJoin (log, PHY (0), PHY (1));
        } break;
        case LogicScan_: {
            auto log = static_cast<LogicScan*> (logic);
            return new PhysicScan (log);
        } break;
        default:
            assert (false);
    }
}

PhysicNode* Optimize (LogicNode* logic, OptimizeOption option) {
    return DirectLogicToPhysic (logic);
}
}  // namespace andb