#include <iostream>
#include <new>
#include <string>
#include <vector>

#include "common/catalog.h"
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

namespace andb
{
    void SelectStmt::bindFrom(Binder* binder)
    {
        std::unordered_set<std::string> aliasMap;
        for (int i = 0; i < from_.size(); ++i) {
            TableRef* tref = from_[i];
            std::string* alias = tref->getAlias();
            auto itb = aliasMap.find(*alias);
            auto ite = aliasMap.end();
            if (itb != ite)
                throw SemanticAnalyzeException("duplicate table " + *alias + " in same scope");
            aliasMap.insert(*alias);
            TableDef* tdef = binder->ResolveTable(tref->getAlias());
            if (!tdef) throw SemanticAnalyzeException("table " + *tref->alias_ + " not found");
        }

        if (from_.size() > 1) {
            binder->SetError(-1);
            throw SemanticAnalyzeException("ANDB: JOIN not supported");
            return;
        }
    }

    void SelectStmt::BindSelStar(Binder* binder, SelStar& ssref)
    {
        std::vector<ColExpr*>* colExprVec = nullptr;

        if (ssref.tabAlias_)
            colExprVec = binder->GetTableColumns(ssref.tabAlias_);
        else
            colExprVec = binder->GetAllTableColumns();
        // selections_ memory leak here??
        setSelections(colExprVec);
    }

    void SelectStmt::bindSelections(Binder* binder)
    {
        auto seCopy = std::move(selection_);
        selection_.clear();
        for (auto e : seCopy) {
            e->Bind(binder);
        }
    }

    void SelectStmt::bindWhere(Binder* binder)
    {
        where_->Bind(binder);
        where_->type_ = DataType::Bool;
    }
}  // namespace andb
