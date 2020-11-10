#pragma once

#include <string>
#include <iostream>
#include <vector>
#include <new>

#include "common/common.h"
#include "common/dbcommon.h"

namespace andb {

class Expr;
class LogicNode;

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
    SQLStatement () : type_ (kStmtError) {}

    virtual ~SQLStatement();

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
    virtual LogicNode* CreatePlan (void) = 0;
};

class SelectStmt : public SQLStatement {
public:
    SelectStmt () : SQLStatement ()
    {}

    std::pmr::vector<TableRef*> from_{currentResource_};
    Expr* where_ = nullptr;
    std::pmr::vector<Expr*> selection_{currentResource_};

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
