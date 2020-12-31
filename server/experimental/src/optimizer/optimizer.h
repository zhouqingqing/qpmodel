#pragma once

#include "runtime/physicnode.h"
#include "parser/include/logicnode.h"

namespace andb {
enum OptimizeOption
{
    O0,         // directly convert to physical plan
    O1,         // only minimal efforts with substitution rules only
    O2,         // full efforts with all optimization in
    Ocustomized // customized, see other parameters
};

extern PhysicNode* Optimize(LogicNode*, OptimizeOption = O2);
} // namespace andb
