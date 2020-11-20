#pragma once

#include <string>
#include <limits>
#include <map>

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
    TableDef_,
    ColumnType_,
    ColumnDef_,
    TableRef_,
    BaseTableRef_,
    Expr_,
    QueryRef_
};


enum class SQLType : int8_t {
   SQL_ANYTYPE
      , SQL_INTEGER
      , SQL_LONG
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
         , SQLType::SQL_INTEGER
         , SQLType::SQL_LONG
         , SQLType::SQL_NUMERIC
         , SQLType::SQL_DOUBLE
         , SQLType::SQL_BOOL
         , SQLType::SQL_DATETIME
         , SQLType::SQL_VARCHAR
         , SQLType::SQL_CHAR};
};

struct TypeLenghts {
   static constexpr int lens_[] = {
      std::numeric_limits<int>::min()
      , sizeof(int)
      , sizeof(long)
      , sizeof(double)  // we will simulate NUMERIC with double for now
      , sizeof(double)
      , sizeof(bool)
      , sizeof(long)
      , std::numeric_limits<int>::max() // we don't know now
      , std::numeric_limits<int>::max() // we don't know now
      , std::numeric_limits<int>::min() // Not applicable
   };
};


// All the object names shuld not be plain strings so as to handle
// case sensitivity of quoted names.
// TODO: create a data structure to contain the name and a flag
// to indicate if it is a quoted name.
// Also change the scanner to keep the quotes so that we can deduce
// the "quoted" attribute here and deal with SQL_IDENTIFER in the scanner
// and parser regardless of the quoted-ness of it.
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
   public:
      ClassTag    classTag_;
      std::string *name_;
      ColumnType  type_;
      int         ordinal_;
      bool        nullable_;
      bool        quoted_;

      ColumnDef(const std::string* name, ColumnType type, int ordinal = -1, bool nullable = true)
         : classTag_(ColumnDef_), name_(new std::string(*name)), type_(type)
         , ordinal_(ordinal), nullable_(nullable), quoted_(false)
      {
         // TODO: andb scanner/parser throw away quoted qualifier, revisit
         if (RemoveQuotes(*name_)) {
            name_ = StdStrLower(name_);
            quoted_ = true;
         }
      }
};

class TableDef : public UseCurrentResource { /* */
   public:
      ClassTag    classTag_;
      std::string *name_;
      std::map<const std::string*, ColumnDef*> *columns_;
      bool        quoted_;

      TableDef(const std::string *name, std::vector<ColumnDef *>& columns)
         : classTag_(TableDef_), name_(new std::string(*name))
      {
         if (RemoveQuotes(*name_)) {
            name_ = StdStrLower(name_);
            quoted_ = true;
         }

         columns_ = new std::map<const std::string*, ColumnDef*>();

         for (auto c : columns) (*columns_)[c->name_] = c;
      }

      // Let there be RVO
      std::vector<ColumnDef *> ColumnsInOrder()
      {
         std::vector<ColumnDef *> inOrder;
          for (auto c : *columns_) inOrder.emplace_back (c.second);

         std::sort(inOrder.begin(), inOrder.end(),
               [](auto &lc, auto &rc) {
                  return lc->ordinal_ < rc->ordinal_;
               });

         return inOrder;
      }

      int EstRowSize()
      {
         int size = 0, cs;

         for (auto c : *columns_)
            size += ((cs = (c.second->type_).len_) > 0 ? cs : 0);

         return size;
      }
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

    virtual std::string Explain(void *arg = nullptr) const { return {};
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

    TableRef *Clone () override {
        BaseTableRef* btrf = new BaseTableRef (tabname_, alias_);
        return btrf;
    }

    std::string Explain(void *arg = nullptr) const override {
       std::string ret = *tabname_;

       if (alias_)
          ret += " " + *alias_;

       return ret;
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
