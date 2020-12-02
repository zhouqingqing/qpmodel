#pragma once

#include <utility>

#include "common/common.h"
#include "common/dbcommon.h"
#include "common/nodebase.h"
#include "runtime/datum.h"
#include "parser/include/expr.h"

namespace andb {
class Binder : public UseCurrentResource {
    public:
        Binder (SQLStatement* stmt, Binder* parent = nullptr)
            : stmt_ (stmt),
              parent_ (parent),
              globalSubqCounter_ (0),
              globalValueIdCounre_ (1),
              binderError_ (0) {}

        ~Binder () {}

        void Bind ();
        TableDef* ResolveTable (std::string* tref);
        ColExpr* ResolveColumn(std::string* cname, std::string* tname = nullptr);
        int GetError () { return binderError_; }

        void SetError (int err) { binderError_ = err; }

        void AddTableRefToScope (TableRef* tref) {
            auto it = tablesInScope_.insert (std::make_pair (tref->getAlias (), tref));
        }

        TableRef* GetTableRef (std::string* tabName) {
            auto ti = tablesInScope_.find (tabName);
            if (ti != tablesInScope_.end ()) return ti->second;

            return nullptr;
        }

        ColExpr* GetColumnRef (std::string* colName, std::string* tabName = nullptr);

        std::vector<ColExpr*>* GetTableColumns (std::string *tabName);
        std::vector<ColExpr*>* GetAllTableColumns ();

        SQLStatement* stmt_;  // current statement
        Binder* parent_;
        int globalSubqCounter_;
        int globalValueIdCounre_;
        int binderError_;
        std::map<std::string*, TableRef*,  CaselessStringPtrCmp> tablesInScope_;
        std::map<std::pair<std::string*, std::string*>, int, CaselessStringPtrCmp> columnsInScope;
};
} // namespace andb
