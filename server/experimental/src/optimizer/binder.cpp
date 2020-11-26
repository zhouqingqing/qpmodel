#include <iostream>
#include <new>
#include <string>
#include <vector>


#include "common/common.h"
#include "common/dbcommon.h"
#include "common/catalog.h"
#include "optimizer/optimizer.h"
#include "parser/include/SQLParserResult.h"
#include "parser/include/expr.h"
#include "parser/include/parser.h"
#include "parser/include/stmt.h"
#include "optimizer/binder.h"
#include "runtime/runtime.h"

namespace andb {

void Binder::Bind () { return stmt_->Bind (this); }

// Get the column reference from the tabes in local scope
ColExpr* Binder::GetColumnRef (std::string* colName, std::string* tabName) {
    TableRef* tref;
    ColExpr* cref = nullptr;

    if ((tref = GetTableRef (tabName))) {
        cref = static_cast<ColExpr*>(tref->findColumn (colName));
    }
    return cref;
}

TableDef* Binder::ResolveTable (std::string* tname) {
    Binder* current = this;
    Binder* next = this;
    auto tref = tablesInScope_.find (tname);

    while (next && tref != next->tablesInScope_.end ()) {
        if ((tref != next->tablesInScope_.end ())) {
            return tref->second->tabDef_;
        }
        next = next->parent_;
    }

    // table not found in any scope, bring it in from catalog, if possible
    auto tdef = Catalog::systable_->TryTable (tname);
    if (tdef && next == current) {
        // found the table in local scope in the catalog, add it to local scope
        TableRef* ltref = new TableRef (BaseTableRef_, tname, tdef);
        ltref->tabDef_ = tdef;
        AddTableRefToScope (ltref);
    }
    return tdef;
}
}
