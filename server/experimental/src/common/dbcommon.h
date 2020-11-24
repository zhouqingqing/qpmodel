#pragma once

#include <string>
#include <limits>
#include <map>

#include "common.h"

namespace andb {
    class Expr;
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
            int         columnId_;  // take over lookup by name

            ColumnDef(std::string* name, ColumnType type, int ordinal = -1, bool nullable = true)
                : classTag_(ColumnDef_), name_(new std::string(*name))
                , type_(type)
                , ordinal_(ordinal), nullable_(nullable), quoted_(false)
            {
                // TODO: andb scanner/parser throw away quoted qualifier, revisit
#ifdef __QUOTED_NAMES
                if (RemoveQuotes(*name_)) {
                    name_ = StdStrLower(name_);
                    quoted_ = true;
                }
#endif // __QUOTED_NAMES

                columnId_ = -1;   // for now
            }

            ColumnDef* Clone ()
            {
                ColumnDef* ncd = new ColumnDef (name_, type_, ordinal_, nullable_);
                return ncd;
            }
    };

    class TableDef : public UseCurrentResource { /* */
        public:
            ClassTag    classTag_;
            std::string *name_;
            std::map<std::string*, ColumnDef*> *columns_;
            bool        quoted_;
            int         tableId_;   // take over table lookup

            TableDef(std::string *name, std::vector<ColumnDef *>& columns)
                : classTag_(TableDef_), name_(new std::string(*name))
            {
               quoted_ = false;
#ifdef __QUOTED_NAMES
                if (RemoveQuotes(*name_)) {
                    name_ = StdStrLower(name_);
                    quoted_ = true;
                }
#endif // __QUOTED_NAMES

                columns_ = new std::map<std::string*, ColumnDef*>();

                for (auto c : columns) {
                    ColumnDef* cdef = c->Clone ();
                    auto [it, added] = columns_->insert (std::make_pair(c->name_, cdef));
                    if (!added) throw SemanticAnalyzeException("Duplicate Column Definition: " + *c->name_);
                }

                tableId_ = -1;
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

}  // namespace andb
