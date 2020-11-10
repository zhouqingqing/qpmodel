#pragma once

#include "parser/include/expr.h"
#include "parser/include/logicnode.h"
#include "parser/include/stmt.h"

namespace andb {
extern SQLStatement* Parse (char* query);
extern LogicNode* ParseAndAnalyze (char* query);
extern Expr* ParseExpr (char* expr);
}  // namespace andb
