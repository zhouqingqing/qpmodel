#pragma once

#include <functional>
#include <string>

#include "common/common.h"
#include "common/dbcommon.h"
#include "parser/include/expr.h"
#include "datum.h"
#include "physicnode.h"

namespace andb {
// unit tests (dbtest and dbperf) use this interface, so this one stays
// for some more time. After SQL Interface is starts working this will
// go away and the SQL interface one will be renamed as Execute.
template <typename Fn>
void Execute(PhysicNode* plan, Fn fn)
{
    auto context = std::make_unique<ExecContext> ();
    plan->Open (context.get ());
    plan->Exec (fn);
    plan->Close ();
}


// SQL interface version.
// TODO: Rename as Execute and get rid of the one above.
template <typename Fn>
void ExecuteCtx(PhysicNode* plan, Fn fn)
{
    plan->Exec(fn);
}
}  // namespace andb
