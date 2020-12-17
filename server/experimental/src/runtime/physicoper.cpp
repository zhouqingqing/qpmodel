// This module contains non-inlined methods of many Physical Nodes

#include <vector>

#include "common/common.h"
#include "common/dbcommon.h"
#include "common/catalog.h"
#include "parser/include/expr.h"
#include "runtime/datum.h"
#include "parser/include/logicnode.h"
#include "runtime/physicnode.h"

namespace andb {

// for readonly operations
const std::vector<Row*>* PhysicScan::getSourceReader(int distId) const
{
    const std::vector<Row*>* result = nullptr;
    LogicScan*               lscan  = static_cast<LogicScan*>(logic_);
    TableRef*               tref   = lscan->getBaseTableRef();
    result         = &(tref->tabDef_->distributions_[distId].heap_);

    return result;
}

// for read/write, perhaps for UPDATE
std::vector<Row*>* PhysicScan::getSourceWriter(int distId)
{
    std::vector<Row*>* result = nullptr;

    return result;
}
} // namespace andb

