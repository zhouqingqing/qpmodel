#include <string>
#include <iostream>
#include <vector>
#include <new>

#include "common/common.h"
#include "common/dbcommon.h"
#include "common/catalog.h"
#include "optimizer/optimizer.h"
#include "parser/include/parser.h"
#include "runtime/runtime.h"
#include "parser/include/expr.h"
#include "parser/include/stmt.h"
#include "optimizer/binder.h"
#include "parser/include/SQLParserResult.h"


namespace andb {

void SelectStmt::bindFrom (Binder* binder)
{
   if (from_.size () > 1) {
      binder->SetError (-1);
      std::cout << "ANDB: JOIN not supported\n";
      return;
   }

   for (int i = 0; i < from_.size (); ++i) {
      TableRef* tref = from_[i];
      TableDef* tdef = binder->ResolveTable (tref->getAlias());
      if (!tdef)
         throw SemanticAnalyzeException ("table " + *tref->alias_ + " not found");
      return;
   }
}

void SelectStmt::bindSelections (Binder *binder)
{
}

void SelectStmt::bindWhere(Binder *binder)
{
}
}
