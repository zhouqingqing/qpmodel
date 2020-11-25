#pragma once

#include <functional>
#include <string>

#include "common/common.h"
#include "common/dbcommon.h"
#include "parser/include/expr.h"
#include "datum.h"
#include "PhysicNode.h"

namespace andb {
template <typename Fn>
void Execute (PhysicNode* plan, Fn fn) {
    auto context = std::make_unique<ExecContext> ();
    plan->Open (context.get ());
    plan->Exec (fn);
    plan->Close ();
}
}  // namespace andb
