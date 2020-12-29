
#include <iostream>
#include <new>
#include <string>
#include <vector>

#include "common/catalog.h"
#include "common/common.h"
#include "common/dbcommon.h"
#include "optimizer/optimizer.h"
#include "parser/include/sqlparserresult.h"
#include "parser/include/expr.h"
#include "parser/include/parser.h"
#include "parser/include/stmt.h"
#include "runtime/runtime.h"
#include "optimizer/binder.h"

namespace andb
{

    void Binder::Bind()
    {
        return stmt_->Bind(this);
    }

    // Get the column reference from the tabes in local scope
    ColExpr* Binder::GetColumnRef(std::string* colName, std::string* tabName)
    {
        TableRef* tref = nullptr;
        ColExpr* cref = nullptr;

        if (tabName) {
            if (tref = GetTableRef(tabName))
                cref = static_cast<ColExpr*>(tref->findColumn(colName));
        }
        else {
            for (auto t : tablesInScope_) {
                if ((tref = GetTableRef(t.first)))
                    if ((cref = static_cast<ColExpr*>(tref->findColumn(colName))))
                        break;
            }
        }

        return cref;
    }

    TableDef* Binder::ResolveTable(std::string* tname)
    {
        Binder* current = this;
        Binder* next = this;
        auto tref = tablesInScope_.find(tname);

        while (next && tref != next->tablesInScope_.end()) {
            if ((tref != next->tablesInScope_.end())) {
                return tref->second->tabDef_;
            }
            next = next->parent_;
        }

        // table not found in any scope, bring it in from catalog, if possible
        auto tdef = Catalog::systable_->TryTable(tname);
        if (tdef && next == current) {
            // found the table in local scope in the catalog, add it to local scope
            TableRef* ltref = new TableRef(BaseTableRef_, tname, tdef);
            std::cerr << "MEMDEBUG: " << __FILE__ << ":" << __LINE__ << ": NEW TableRef : " << (void*)ltref << " : " << *tname << std::endl;
            std::cerr << "MEMDEBUG: " << __FILE__ << ":" << __LINE__ << ": OLD TableDef : " << (void*)tdef << " : " << *tname << std::endl;
            std::cerr << "MEMDEBUG: " << __FILE__ << ":" << __LINE__ << ": NEW TableDef : " << (void*)ltref->tabDef_ << " : " << *tname << std::endl;
            AddTableRefToScope(ltref);

            // return the new tabledef
            tdef = ltref->tabDef_;
        }
        return tdef;
    }

    ColExpr* Binder::ResolveColumn(std::string* cname, std::string* tname)
    {
        Binder* myScope = this;
        Binder* currScope = this;
        ColExpr* cref = nullptr;

        while (currScope && cref == nullptr) {
            if ((cref = currScope->GetColumnRef(cname, tname)))
                break;
            currScope = currScope->parent_;
        }

        if (cref) {
            if (currScope != myScope) {
                // TODO: deal with outer reference
            }
        }

        return cref;
    }

    std::vector<ColExpr*>* Binder::GetTableColumns(std::string* tabName)
    {
        TableRef* tref = GetTableRef(tabName);
        if (!tref) {
            SetError(-1);
            throw SemanticAnalyzeException("table " + *tabName + " not found");
        }

        std::vector<ColExpr*>* colExprVec = new std::vector<ColExpr*>();
        auto tcols = tref->tabDef_->ColumnsInOrder();

        for (auto c : tcols) {
            auto cdef = c->Clone();
            auto ce = new ColExpr(c->ordinal_, c->name_, tabName, nullptr, cdef);
            colExprVec->emplace_back(std::move(ce));
        }

        return colExprVec;
    }

    std::vector<ColExpr*>* Binder::GetAllTableColumns()
    {
        auto colExprVec = new std::vector<ColExpr*>();

        for (auto t : tablesInScope_) {
            auto cev = GetTableColumns(t.first);
            for (auto e : *cev)
                colExprVec->emplace_back(std::move(e));
        }

        return colExprVec;
    }
} // nameapce andb
