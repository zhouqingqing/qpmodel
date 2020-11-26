#include <iostream>
#include <new>
#include <string>
#include <vector>

#include "common/common.h"
#include "common/catalog.h"
#include "common/dbcommon.h"
#include "optimizer/optimizer.h"
#include "parser/include/SQLParserResult.h"
#include "parser/include/expr.h"
#include "parser/include/parser.h"
#include "parser/include/stmt.h"
#include "optimizer/binder.h"
#include "runtime/runtime.h"

namespace andb {

void SelectStmt::bindFrom (Binder* binder) {
    std::unordered_set<std::string> aliasMap;
    for (int i = 0; i < from_.size (); ++i) {
        TableRef* tref = from_[i];
        std::string* alias = tref->getAlias ();
        auto itb = aliasMap.find (*alias);
        auto ite = aliasMap.end ();
        if (itb != ite)
            throw SemanticAnalyzeException ("duplicate table " + *alias + " in same scope");
        aliasMap.insert (*alias);
        TableDef* tdef = binder->ResolveTable (tref->getAlias ());
        if (!tdef) throw SemanticAnalyzeException ("table " + *tref->alias_ + " not found");
    }

    if (from_.size () > 1) {
        binder->SetError (-1);
        std::cout << "ANDB: JOIN not supported\n";
        return;
    }
}

void SelectStmt::bindSelections (Binder* binder) {}

void SelectStmt::bindWhere (Binder* binder) {}
}  // namespace andb
