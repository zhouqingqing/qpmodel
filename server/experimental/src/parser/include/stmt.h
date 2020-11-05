#pragma once

#include "common/common.h"
#include "common/dbcommon.h"
#include "parser/include/expr.h"
#include "parser/include/logicnode.h"

namespace andb {
class SQLStatement : public UseCurrentResource {
public:
    virtual LogicNode* CreatePlan (void) = 0;
};

class SelectStmt : public SQLStatement {
public:
    std::pmr::vector<TableRef*> from_{currentResource_};
    Expr* where_ = nullptr;
    std::pmr::vector<Expr*> selection_{currentResource_};

private:
    LogicNode* transformFromClause ();

public:
    LogicNode* CreatePlan () override;
};

class CreateTableStmt : public SQLStatement {};
}  // namespace andb
