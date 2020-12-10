#pragma once

#include <string>
#include <iostream>
#include <vector>
#include <new>

#include "common/common.h"
#include "common/dbcommon.h"
#include "optimizer/binder.h"
#include "runtime/datum.h"

namespace andb {

class Expr;
class LogicNode;
class PhysicNode;
class Binder;
class ExecContext;

class SQLStatement : public UseCurrentResource
{
    public:
    // BEGIN HYRISE
    enum StatementType
    {
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
    SQLStatement()
      : stringLength(0)
      , hints(nullptr)
      , type_(kStmtError)
      , context(nullptr)
      , logicPlan_(nullptr)
      , physicPlan_(nullptr)
      , queryOpts_(nullptr)
      , execContext_(nullptr)
    {
        DEBUG_CONS("SQLStatement", "def");
    }

    virtual ~SQLStatement()
    {
#ifdef __RUN_DELETES_
        DEBUG_DEST("SQLStatement", "@@@");

        if (hints) {
            for (auto h : *hints) {
                delete h++;
            }
            hints->clear();
            delete hints;
            hints = 0;
        }

        delete logicPlan_;
        delete physicPlan_;
        delete queryOpts_;

        logicPlan_  = nullptr;
        physicPlan_ = nullptr;
        queryOpts_  = nullptr;
#endif // __RUN_DELETES_
    }

    virtual void Bind(Binder* binder) {}

    StatementType type() const;

    bool isType(StatementType type) const;

    // Shorthand for isType(type).
    bool is(StatementType type) const;

    virtual bool Open() = 0;
    // How about the traditional Prepare(), Exec(), Fetch(), Close()?
    // Excecute the statement.
    // If it is a SELECT, then rows will have the result set, if there is one.
    virtual std::vector<Row> Exec() = 0;
    virtual bool             Close() = 0;
    virtual LogicNode  *CreatePlan(void)                   = 0;
    virtual PhysicNode *Optimize(void)                     = 0;
    virtual std::string Explain(void* arg = nullptr) const = 0;

    // Length of the string in the SQL query string
    size_t stringLength;

    std::vector<Expr*>* hints;


    StatementType type_;
    // END HYRISE

        LogicNode*    logicPlan_;
    PhysicNode*   physicPlan_;
    QueryOptions* queryOpts_;
    ExecContext*  execContext_;

    Binder*       context;

};

class SelectStmt : public SQLStatement
{
    public:
    SelectStmt()
      : SQLStatement()
    {
        DEBUG_CONS("SelectStmt", "def");
    }

    virtual ~SelectStmt()
    {
#ifdef __RUN_DELETES_
        DEBUG_DEST("SelectStmt", "def");

        for (auto s : selection_) {
            delete s++;
        }
        selection_.clear();

        for (auto f : from_) {
            delete f++;
        }
        from_.clear();
#endif // __RUN_DELETES_
    }

    std::string Explain(void* arg = nullptr) const override;
    LogicNode   *CreatePlan() override;
    PhysicNode  *Optimize(void) override;
    bool Open() override;
    
    std::vector<Row> Exec() override;
    
    bool             Close() override
    {
        return true;
    }

    void setSelections(std::vector<Expr*>* sels)
    {
        std::vector<Expr*>& ev = *sels;
        Expr*               ne = nullptr;
        for (int i = 0; i < sels->size(); ++i) {
            Expr* pev = ev[i]->Clone();
            selection_.push_back(pev);
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

    void setFrom(std::vector<TableRef*>* tbls)
    {
        std::vector<TableRef*>& tv = *tbls;
        for (int i = 0; i < tbls->size(); ++i) {
            TableRef* nt = tv[i]->Clone();
            from_.push_back(nt);
        }
    }

    void Bind(Binder* binder) override
    {
        // bind from clause
        bindFrom(binder);
        if (binder->GetError())
            return;

        bindSelections(binder);
        if (binder->GetError())
            return;

        if (where_) {
            bindWhere(binder);
            if (binder->GetError())
                return;
        }
    }

    void bindFrom(Binder*);
    void bindSelections(Binder*);
    void bindWhere(Binder*);
    void BindSelStar(Binder* binder, SelStar& ss);

    std::pmr::vector<Expr*>     selection_{ currentResource_ };
    std::pmr::vector<TableRef*> from_{ currentResource_ };
    Expr*                       where_ = nullptr;

    private:
    LogicNode* transformFromClause();
};

//
// BEGIN HYRISE PARSER
// These are from hyrise/third-party/sql-parser and will go away
// one after another as integration proceeds.
enum SetType
{
    kSetUnion,
    kSetIntersect,
    kSetExcept
};

typedef SelectStmt SelectStatement;
// END HYRISE PARSER

} // namespace andb
