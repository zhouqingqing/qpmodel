#pragma once

#include "common/common.h"
#include "common/dbcommon.h"
#include "parser/include/stmt.h"
#include "parser/include/expr.h"
#include "runtime/datum.h"
#include "runtime/runtime.h"

// Entry points into execution of DDL, DML, DCL statements.
// They do whatever setup they have to and hand over control to PhysicNode
//
namespace andb
{

bool SelectStmt::Open()
{
    assert(execContext_ == nullptr);
    execContext_ = new ExecContext{};
    physicPlan_->Open(execContext_);

    return true;
}

bool             SelectStmt::Close()
{
    assert(physicPlan_);
    physicPlan_->Close();

    return true;
}

std::vector<Row> SelectStmt::Exec()
{
   std::vector<Row> resultSet;
   
   assert(execContext_ != nullptr);
   bool moreData = true;
   while (true) {
      andb::ExecuteCtx(physicPlan_, [&](Row *row) {
            if (row)
               resultSet.emplace_back(*row);
            else
               moreData = false;

            //
            // This is tricky, at least to me.
            // This row was allocated in the deepest part of the
            // execution framework and it wants to know it should
            // allocate another row in the next call to Execute, or
            // re-use it again.
            //
            // A false return indicates that this function has not
            // taken the ownerhip of the row and Executor can do with it
            // as it please (delete/resue etc).
            // A true return indicates that Executor should thouh this row
            // again and it will have to allocate a new row in the next
            // call to the Exector.
            //
            // Since the row gets copied here, we return false and Executor
            // can delete it or reuse it.
            return false;
       });
   }

   return resultSet;
}
} // namespace andb
