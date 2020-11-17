#pragma once

#include <string>

#include "common.h"

namespace andb {

class SQLStatement;
class SelectStmt;

enum ClassTag : uint8_t {
    UnInitialized_,
    PhysicStart__,
    PhysicAgg_,
    PhysicHashJoin_,
    PhysicScan_,
    LogicStart__,
    LogicAgg_,
    LogicScan_,
    LogicJoin_,
    ExprStart__,
    BinExpr_,
    ConstExpr_,
    ColExpr_,
    SQLType_,
    TypeBase_,
    ColumnType_,
    ColumnDef_,
    TableRef_,
    BaseTableRef_,
    Expr_,
    QueryRef_
};


enum class SQLType : int8_t {
   SQL_ANYTYPE
      , SQL_NUMERIC
      , SQL_DOUBLE
      , SQL_BOOL
      , SQL_DATETIME
      , SQL_VARCHAR
      , SQL_CHAR
      , SQL_LAST_TYPE
};

struct TypeBase {
    static constexpr SQLType precedence_[static_cast<uint64_t>(SQLType::SQL_LAST_TYPE)] {
          SQLType::SQL_ANYTYPE
         , SQLType::SQL_NUMERIC
         , SQLType::SQL_DOUBLE
         , SQLType::SQL_BOOL
         , SQLType::SQL_DATETIME
         , SQLType::SQL_VARCHAR
         , SQLType::SQL_CHAR};
};


class ColumnType : public UseCurrentResource {
   public:
      SQLType  type_;
      int      len_;

      ColumnType(SQLType type, int len)
         : type_(type), len_(len)
      {
      }
};


class ColumnDef : public UseCurrentResource {
   std::string name_;
   ColumnType  type_;
   bool        nullable_;
};

class TableRef : public UseCurrentResource {
public:
    ClassTag classTag_;

    TableRef () : classTag_ (TableRef_)
    {}

    virtual TableRef* Clone () { TableRef* tr = new TableRef ();
        tr->classTag_ = TableRef_;

        return tr;
    }
};

class BaseTableRef : public TableRef {
public:
    std::string* tabname_;
    std::string* alias_;

    BaseTableRef (std::string* tabname, std::string* alias = nullptr)
        : tabname_ (tabname), alias_ (alias) {
        classTag_ = BaseTableRef_;
    }

    BaseTableRef (const char* tabname, const char* alias = nullptr) {
        classTag_ = BaseTableRef_;
        tabname_ = new std::string (tabname);
        alias_ = alias ? new std::string (alias) : nullptr;
    }

    virtual TableRef *Clone () override {
        BaseTableRef* btrf = new BaseTableRef (tabname_, alias_);
        return btrf;
    }
};

class QueryRef : public TableRef {
public:
    SelectStmt* query_;
    std::string* alias_;
    std::vector<std::string*>* colOutputNames_;

    QueryRef (SelectStmt* stmt, std::string* alias = nullptr,
              std::vector<std::string*>* outputNames = nullptr)
        : query_ (stmt), alias_ (alias), colOutputNames_ (outputNames) {
        classTag_ = QueryRef_;
    }
};
}  // namespace andb
