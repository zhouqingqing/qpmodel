#pragma once

#include <string>
#include <iostream>
#include <vector>
#include <new>

#include "common/common.h"
#include "common/dbcommon.h"
#include "optimizer/binder.h"

namespace andb {

class Expr;
class LogicNode;
class Binder;

class SQLStatement : public UseCurrentResource {
public:
   // BEGIN HYRISE
    enum StatementType {
    kStmtError, // unused
    kStmtSelect,
    kStmtImport,
    kStmtInsert,
    kStmtUpdate,
    kStmtDelete,
    kStmtCreate,
    kStmtDrop,
    kStmtPrepare,
    kStmtExecute,
    kStmtExport,
    kStmtRename,
    kStmtAlter,
    kStmtShow,
    kStmtTransaction
  };

    SQLStatement(StatementType type);
    SQLStatement () : type_ (kStmtError) {
    }

    virtual ~SQLStatement () {
    }

    virtual void Bind (Binder* binder) {}

    StatementType type() const;

    bool isType(StatementType type) const;

    // Shorthand for isType(type).
    bool is(StatementType type) const;

    // Length of the string in the SQL query string
    size_t stringLength;

    std::vector<Expr*>* hints;

private:
     StatementType type_;
   // END HYRISE

   public:
    virtual LogicNode   *CreatePlan (void) = 0;
    virtual std::string Explain (void* arg = nullptr) const = 0;
};

class SelectStmt : public SQLStatement {
public:
    SelectStmt () : SQLStatement ()
    {
    }

    ~SelectStmt () {
    }

    void setSelections (std::vector<Expr*>* sels) {
        std::vector<Expr*>& ev = *sels;
        Expr* ne = nullptr;
        for (int i = 0; i < sels->size (); ++i) {
            Expr* pev = ev[i]->Clone ();
            selection_.push_back (pev);
        }
    }

    void setSelections(std::vector<ColExpr*>* sels)
    {
        Expr* ne = nullptr;
        for (int i = 0; i < sels->size(); ++i) {
            Expr* pev = (*sels)[i]->Clone();
            selection_.push_back(pev);
        }
    }
    void setFrom (std::vector<TableRef*>* tbls) {
        std::vector<TableRef*>& tv = *tbls;
        for (int i = 0; i < tbls->size (); ++i) {
            TableRef* nt = tv[i]->Clone ();
           from_.push_back (nt);
        }
    }

    std::string Explain(void *arg = nullptr) const override {
       std::string ret = "select ";
        for (int i = 0; i < selection_.size (); ++i) {
           if (i > 0) ret += ", ";
            ret += selection_[i]->Explain() + " ";
        }

        ret += " FROM ";
        for (int i = 0; i < from_.size (); ++i) {
          if (i > 0)
             ret += ", ";
           ret += from_[i]->Explain();
        }

        if (where_ != nullptr) {
           ret += " WHERE ";
           ret += where_->Explain() + ";";
        }

        return ret;
    }

    void Bind(Binder* binder) override {
        // bind from clause
        bindFrom (binder);
        if (binder->GetError ()) return;
        
        bindSelections (binder);
        if (binder->GetError ()) return;

        if (where_) {
            bindWhere (binder);
            if (binder->GetError ()) return;
        }
    }

    void bindFrom (Binder*);
    void bindSelections (Binder*);
    void bindWhere (Binder*);
    void BindSelStar(Binder* binder, SelStar& ss);

    std::pmr::vector<Expr*>     selection_{currentResource_};
    std::pmr::vector<TableRef*> from_{currentResource_};
    Expr    *where_ = nullptr;

private:
    LogicNode* transformFromClause ();

public:
    LogicNode* CreatePlan () override;
};

//
// BEGIN HYRISE PARSER
// These are from hyrise/third-party/sql-parser and will go away
// one after another as integration proceeds.
enum SetType {
    kSetUnion,
    kSetIntersect,
    kSetExcept
};


typedef SelectStmt SelectStatement;
// END HYRISE PARSER

}  // namespace andb
