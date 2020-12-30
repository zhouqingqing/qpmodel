#pragma once

#include <string>
#include <limits>
#include <map>

#include "common.h"
#include "runtime/datum.h"

namespace andb {
    class Expr;
    class SQLStatement;
    class SelectStmt;
    class TableDef;
    class Row;

    enum ClassTag : uint8_t {
        UnInitialized_,
        PhysicStart__,
        PhysicAgg_,
        PhysicHashJoin_,
        PhysicScan_,
        LogicStart__,
        LogicGet_,
        LogicAgg_,
        LogicScan_,
        LogicJoin_,
        ExprStart__,
        BinExpr_,
        ConstExpr_,
        ColExpr_,
        SelStar_,
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
            SQL_TYPE_NONE
            , SQL_TYPE_ANY
            , SQL_TYPE_INTEGER
            , SQL_TYPE_LONG
            , SQL_TYPE_NUMERIC
            , SQL_TYPE_DOUBLE
            , SQL_TYPE_BOOL
            , SQL_TYPE_DATETIME
            , SQL_TYPE_VARCHAR
            , SQL_TYPE_CHAR
            , SQL_TYPE_LAST
    };

    struct TypeBase {
        static constexpr SQLType precedence_[static_cast<uint64_t>(SQLType::SQL_TYPE_LAST)] {
                SQLType::SQL_TYPE_ANY
                , SQLType::SQL_TYPE_INTEGER
                , SQLType::SQL_TYPE_LONG
                , SQLType::SQL_TYPE_NUMERIC
                , SQLType::SQL_TYPE_DOUBLE
                , SQLType::SQL_TYPE_BOOL
                , SQLType::SQL_TYPE_DATETIME
                , SQLType::SQL_TYPE_VARCHAR
                , SQLType::SQL_TYPE_CHAR};
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

            std::string ToString()
            {
                static std::string udt{"Unknown data type"};

                switch (type_)
                {
                    case andb::SQLType::SQL_TYPE_NONE:
                        return "none";
                    case andb::SQLType::SQL_TYPE_ANY:
                        return "any";
                    case andb::SQLType::SQL_TYPE_INTEGER:
                        return "integer";
                    case andb::SQLType::SQL_TYPE_LONG:
                        return "long";
                    case andb::SQLType::SQL_TYPE_NUMERIC:
                        return "numeric";
                    case andb::SQLType::SQL_TYPE_DOUBLE:
                        return "double";
                    case andb::SQLType::SQL_TYPE_BOOL:
                        return "boolean";
                    case andb::SQLType::SQL_TYPE_DATETIME:
                        return "datetime";
                    case andb::SQLType::SQL_TYPE_VARCHAR:
                        return "varchar";
                    case andb::SQLType::SQL_TYPE_CHAR:
                        return "char";
                    case andb::SQLType::SQL_TYPE_LAST:
                        return "sentinel";
                    default:
                        break;
                }

                assert(!udt.size());

                return udt;
            }
    };

    class ColumnDef : public UseCurrentResource
    {
        public:
            ClassTag    classTag_;
            std::string *name_;
            ColumnType  type_;
            int         ordinal_;
            bool        nullable_;
            int         columnId_;  // take over lookup by name
            bool         quoted_;
            bool         isCloned_;

            ColumnDef(std::string *name, ColumnType type, int ordinal = -1, bool nullable = true, int columnId = -1, bool quoted = false, bool isClone = false)
                : classTag_(ColumnDef_)
                , name_(nullptr)
                , type_(type)
                , ordinal_(ordinal)
                , nullable_(nullable)
                , columnId_(columnId)
                , quoted_(quoted)
                , isCloned_(isClone)
            {
                DEBUG_CONS("ColumnDef", isCloned_);
                name_ = new std::string(name->c_str());
            }

            virtual ~ColumnDef()
            {
                DEBUG_DEST("ColumnDef", isCloned_);
                delete name_;
                name_ = nullptr;
            }

            ColumnDef* Clone ()
            {
                ColumnDef* ncd = new ColumnDef (name_, type_, ordinal_, nullable_, -1, false, true);
                ncd->isCloned_ = true;
                return ncd;
            }
    };

    class Distribution
    {
        public:
        TableDef* tableDef_;
        Distribution(TableDef* td = 0)
          : tableDef_(td)
        {}

        std::vector<Row*> heap_;
    };


    // TODO: Move distributions_ out of TableDef to avoid heavy copying of all rows
    // when TableDef is cloned, and it gets cloned enough times.
    class TableDef : public UseCurrentResource
    {
        public:
        
        enum TableSource
        {
            Table,
            Stream
        };

        enum DistributionMethod
        {
            NonDistributed,
            Distributed,
            Replicated,
            Roundrobin
        };

        ClassTag                                                  classTag_;
        std::string*                                              name_;
        std::map<std::string*, ColumnDef*, CaselessStringPtrCmp>* columns_;
        bool                                                      quoted_;
        int                         tableId_; // take over table lookup
        bool                        isCloned_;
        TableSource        source_;
        DistributionMethod distMethod_;
        std::vector<Distribution>*                                distributions_;

        TableDef(std::string*             name,
                 std::vector<ColumnDef*>& columns,
                 bool                     isClone    = false,
                 TableSource              source     = Table,
                 DistributionMethod       distMethod = NonDistributed)
          : classTag_(TableDef_)
          , name_(nullptr)
          , quoted_(false)
          , tableId_(-1)
          , isCloned_(isClone)
          , source_(source)
          , distMethod_(distMethod)
          , distributions_(nullptr) 
        {
            DEBUG_CONS("TableDef", isCloned_);
            name_ = new std::string(name->c_str());
            columns_ = DBG_NEW std::map<std::string*, ColumnDef*, CaselessStringPtrCmp>();

            for (auto c : columns) {
                ColumnDef* cdef  = c->Clone();
                auto [it, added] = columns_->insert(std::make_pair(cdef->name_, cdef));
                if (!added)
                    throw SemanticAnalyzeException("Duplicate Column Definition: " + *c->name_);
            }
        }

        TableDef* Clone()
        {
            std::vector<ColumnDef*> columns{}; // dummy
            TableDef* clone = new TableDef(name_, columns, true, source_, distMethod_);
#ifdef _DEBUG
            std::cerr << "MEMDEBUG: " << __FILE__ << ":" << __LINE__ << ": NEW TableDef : " << (void*)clone << " : " << *name_ << std::endl;
#endif // _DEBUG

            clone->columns_ = DBG_NEW std::map<std::string*, ColumnDef*, CaselessStringPtrCmp>();

            for (auto &cdr : *columns_) {
                ColumnDef* cdef  = cdr.second->Clone();
                auto [it, added] = clone->columns_->insert(std::make_pair(cdef->name_, cdef));
                if (!added)
                    throw SemanticAnalyzeException("Duplicate Column Definition: " + *cdef->name_);
            }

            clone->distributions_ = distributions_;
            return clone;
        }

        virtual ~TableDef()
        {
            DEBUG_DEST("TableDef", isCloned_);

#ifdef _DEBUG
            std::cerr << "MEMDEBUG: " << __FILE__ << ":" << __LINE__ << ": DEL TableDef : " << (void*)this << " : " << *name_ << std::endl;
#endif // _DEBUG

            if (columns_) {
                auto itb = columns_->begin(), ite = columns_->end(), itn = itb;
                while (itb != ite) {
                    ++itn;
                    auto cdef = itb->second;
                    itb->second = nullptr;
                    delete cdef;
                    itb = itn;
                }
                columns_->clear();
                delete columns_;
                columns_ = nullptr;
            }

            delete name_;
            name_ = nullptr;
        }

        void DropTable()
        {
            DEBUG_DEST("DropTable", isCloned_);

            for (auto &d : *distributions_) {
                for (auto r : d.heap_) {
                    delete r;
                }

                delete distributions_;
                distributions_ = nullptr;
            }
        }

        ColumnDef* GetColumnDef(std::string* colName)
        {
            ColumnDef* cdef = nullptr;
            auto       it   = columns_->find(colName);
            if (it != columns_->end())
                cdef = it->second;

            return cdef;
        }

        // Let there be RVO
        std::vector<ColumnDef*> ColumnsInOrder()
        {
            std::vector<ColumnDef*> inOrder;
            for (auto c : *columns_)
                inOrder.emplace_back(c.second);

            std::sort(inOrder.begin(), inOrder.end(), [](auto& lc, auto& rc) {
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

        void SetDistributions()
        {
            auto dist = new std::vector<Distribution>();
            dist->emplace_back(Distribution(const_cast<TableDef*>(this)));
            distributions_ = dist;
        }

        void insertRows(std::vector<Row*>& rows)
        {
            auto& hr = distributions_->at(0).heap_;

            for (int i = 0; i < rows.size(); ++i) {
                Row* nr = new Row(rows[i]);
                hr.emplace_back(nr);
            }
        }
    };
    }  // namespace andb
