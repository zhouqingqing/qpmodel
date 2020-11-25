// #include "optimizer/binder.h"

#include <iostream>
#include <new>
#include <string>
#include <vector>

#include "common/catalog.h"
#include "common/common.h"
#include "common/dbcommon.h"
#include "optimizer/optimizer.h"
#include "parser/include/SQLParserResult.h"
#include "parser/include/expr.h"
#include "parser/include/parser.h"
#include "parser/include/stmt.h"
// #include "optimizer/binder.h"
#include "runtime/runtime.h"

namespace andb {

void Binder::Bind () { return stmt_->Bind (this); }
#if 0
void Binder::AddTableRefToScope (TableRef* tref) {
    auto it = tablesInScope_.insert (std::make_pair(tref->getAlias (), tref));
}

void Binder::addTableToScope (std::string* tblName) {
    auto it = tablesInScope_.find (tblName);
    if (it == tablesInScope_.end ()) {
        // read table def from Catalog and add it here
    }
}

TableRef* Binder::GetTableRef (std::string* tabName) {
    auto ti = tablesInScope_.find (tabName);
    if (ti != tablesInScope_.end ()) return ti->second;

    return nullptr;
}

ColExpr* Binder::GetColumnRef (std::string* colName, std::string* tabName) {
    // not chasing the outer scopes yet
    TableRef* tref = nullptr;
    if (tabName) {
        tref = GetTableRef (tabName);
        tref->findColumn (colName);
    }

    // no table name, again, not checking for duplicate columns
    for (auto tr : tablesInScope_) {
        tref = tr.second;
        Expr* er = tref->findColumn (colName);
        if (er) {
            assert (er->classTag_ == ColExpr_);
            return static_cast<ColExpr*> (er);
        }
    }
    return nullptr;
}
#endif
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
#if 0
    if (tdef && next == current) {
        // found the table in local scope in the catalog, add it to local scope
        TableRef* ltref = new TableRef (BaseTableRef_, tname, tdef);
        AddTableRefToScope (ltref);
    }
#endif
    return tdef;
}
}
