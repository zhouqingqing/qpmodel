#pragma once

#include "expr.h"
#include "logicnode.h"
#include "stmt.h"

namespace andb {
extern SQLStatement* Parse (char* query);
extern LogicNode* ParseAndAnalyze (char* query);
extern Expr* ParseExpr (char* expr);
}  // namespace andb