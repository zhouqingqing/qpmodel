#pragma once

#include <deque>
#include <string>

#include "common/dbcommon.h"
#include "common/nodebase.h"
#include "runtime/datum.h"

namespace andb
{

    class BinExpr;
    class SelStar;
    class ConstExpr;
    class ColExpr;
    class Binder;
    class SQLStatement;
    class SelectStmt;

    enum BinOp : uint16_t { Add = 0, Sub, Mul, Div, Equal, Neq, Less, Leq, Great, Geq, And, Or };

    class Expr : public RuntimeNodeT<Expr>
    {
        protected:
        using base_type = Expr;

        public:
        ClassTag    classTag_;
        DataType    type_;
        std::string* alias_;

        // evaluation support
        uint32_t    slot_;

        unsigned long valueId_;  // Expr will be solly identified by this after binding.
        int          ival; // SQLParserResult uses this.
        Expr() : RuntimeNodeT<Expr>(), classTag_(Expr_), type_(D_NullFlag), alias_(nullptr), slot_(0), ival(0) {}

        Expr(DataType type, std::string* alias = 0)
            : RuntimeNodeT<Expr>()
            , classTag_(Expr_)
            , type_(type)
            , alias_(alias)
            , slot_(0)
            , valueId_(0)
            , ival(0)
        {
           DEBUG_CONS("Expr", "typed");
        }

        Expr(ClassTag classTag)
           : type_(D_NullFlag), classTag_(classTag)
        {
           DEBUG_CONS("Expr", "default");
        }

        virtual Expr* Clone()
        {
            Expr* e = new Expr();
            e->alias_ = alias_ ? new std::string(*alias_) : nullptr;
            e->type_ = type_;
            e->slot_ = slot_;
            e->valueId_ = valueId_;
            e->ival = ival;

            return e;
        }

        virtual ~Expr()
        {
           DEBUG_DEST("Expr", "@@@");
           delete alias_;
           alias_ = nullptr;
        }

        virtual std::string Explain(void* arg = nullptr) const { return {}; }

        virtual void Bind(Binder* context)
        {
            auto nchildren = childrenCount();
            for (int i = 0; i < nchildren; i++) child(i)->Bind(context);
        }

        static std::string ExplainBinOp(BinOp op)
        {
            switch (op) {
                case Add:
                    return " + ";
                case Sub:
                    return " - ";
                case Mul:
                    return " * ";
                case Div:
                    return " / ";
                case Equal:
                    return " = ";
                case Neq:
                    return " <> ";
                case Less:
                    return " < ";
                case Leq:
                    return " <= ";
                    /* Add = 0, Sub, Mul, Div, Equal, Neq, Less, Leq, Great, Geq, And, Or */
                case Great:
                    return " > ";
                case Geq:
                    return " >= ";
                case And:
                    return " AND ";
                case Or:
                    return " OR ";
                default:
                    assert("unknown op in BinOp::explain");
                    return "???";
            }
        }

        void SetType(ColumnDef* cdef)
        {
            switch (cdef->type_.type_)
            {
                case SQLType::SQL_TYPE_INTEGER:
                    type_ = DataType::Int32;
                    break;

                case SQLType::SQL_TYPE_LONG:
                    type_ = DataType::Int64;
                    break;

                case SQLType::SQL_TYPE_BOOL:
                    type_ = DataType::Bool;
                    break;

                case SQLType::SQL_TYPE_DOUBLE:
                    type_ = DataType::Double;
                    break;

                case SQLType::SQL_TYPE_CHAR:
                    type_ = DataType::String;
                    break;

                default:
                    throw SemanticAnalyzeException("Unspported type: " + cdef->type_.ToString());
                    break;
            }
        }
    };

    class SelStar : public NodeBase<Expr, N0>
    {
        public:
        std::string* tabAlias_;

        SelStar(std::string* alias = nullptr)
        {
           DEBUG_CONS("SelStar", "@@@");
            tabAlias_ = alias ? new std::string(*alias) : nullptr;
            classTag_ = SelStar_;
        }

        SelStar* Clone()
        {
            SelStar* newSel = new SelStar(alias_);

            return newSel;
        }

        void virtual Bind(Binder* context);

        virtual ~SelStar()
        {
           DEBUG_DEST("SelStar", "@@@");
           delete tabAlias_;
           tabAlias_ = nullptr;
        }
    };

    class ConstExpr : public NodeBase<Expr, N0>
    {
        public:
        Datum value_;

        explicit ConstExpr(Datum value)
        {
           DEBUG_CONS("ConstExpr", (void*)&value);
            assert(value_ == Datum{});
            classTag_ = ConstExpr_;
            value_ = value;
            type_ = (DataType)value.index();
        }

        virtual ~ConstExpr()
        {
           DEBUG_DEST("ConstExpr", (void*)&value_);
        }

        Expr* Clone() override { return new ConstExpr(value_); }

        std::string Explain(void* arg = nullptr) const override
        {
            std::string val = value_.ToString();

            return val;
        }

        void Bind(Binder* context) override
        {
        }
    };

    class ColExpr : public NodeBase<Expr, N0>
    {
        public:
        uint16_t    ordinal_;
        std::string* colname_;
        std::string* tabname_;
        std::string* schname_;
        ColumnDef* columnDef_;

        explicit ColExpr(uint16_t ordinal, std::string* colname = nullptr, std::string* tabname = nullptr, std::string* schname = nullptr, ColumnDef* columnDef = nullptr)
        {
           DEBUG_CONS("ColExpr", ordinal);
            classTag_ = ColExpr_;
            ordinal_ = ordinal;
            colname_ = colname ? new std::string(colname->c_str()) : nullptr;
            tabname_ = tabname ? new std::string(tabname->c_str()) : nullptr;
            schname_ = schname ? new std::string(schname->c_str()) : nullptr;
            columnDef_ = columnDef ? columnDef->Clone() : nullptr;
        };

        explicit ColExpr(const char* colname, const char* tabname = nullptr, const char* schname = nullptr, ColumnDef* columnDef = nullptr)
        {
           DEBUG_CONS("ColExpr(chr)", colname);
            classTag_ = ColExpr_;
            ordinal_ = UINT16_MAX;
            colname_ = new std::string(colname);
            tabname_ = tabname ? new std::string(tabname) : nullptr;
            schname_ = schname ? new std::string(schname) : nullptr;
            columnDef_ = columnDef ? columnDef->Clone() : nullptr;
            ;
        }

        explicit ColExpr(std::string* colname)
        {
           DEBUG_CONS("ColExpr(str)", colname);
            classTag_ = ColExpr_;
            ordinal_ = UINT16_MAX;
            colname_ = new std::string(colname->c_str());
            tabname_ = nullptr;
            schname_ = nullptr;
            columnDef_ = nullptr;
        }

        explicit ColExpr(uint16_t ordinal, const std::string& colname)
        {
           DEBUG_CONS("ConstExpr(ord str)", colname);
            classTag_ = ColExpr_;
            ordinal_ = ordinal;
            colname_ = new std::string(colname);
            tabname_ = nullptr;
            schname_ = nullptr;
            columnDef_ = nullptr;
        }

        ColExpr* Clone() override
        {
            return new ColExpr(ordinal_, colname_, tabname_, schname_, columnDef_);
        }

        virtual ~ColExpr()
        {
           DEBUG_DEST("ColExpr", colname_);
            delete colname_;
            delete tabname_;
            delete schname_;
            delete columnDef_;

            colname_ = nullptr;
            tabname_ = nullptr;
            schname_ = nullptr;
            columnDef_ = nullptr;
        }

        void Bind(Binder* context) override;

        std::string ToString() const
        {
            std::string ret;

            if (schname_) {
                ret = *schname_ + ".";
            }

            if (tabname_) {
                ret += *tabname_ + ".";
            }

            ret += *colname_;

            return ret;
        }

        std::string Explain(void* arg = nullptr) const override
        {
            return ToString();
        }
    };

    class BinExpr : public NodeBase<Expr, N2>
    {
        public:
        using BinFunction = Datum(*) (Datum* l, Datum* r);

        BinOp op_;
        BinFunction fn_;

        explicit BinExpr(BinOp op, Expr* l, Expr* r)
        {
           DEBUG_CONS("BinExpr", "@@@");
            classTag_ = BinExpr_;
            op_ = op;
            children_[0] = l;
            children_[1] = r;
        }

        // TODO: not setting fn_
        BinExpr* Clone() override
        {
            return new BinExpr(op_, children_[0], children_[1]);
        }

        
        virtual ~BinExpr()
        {
           DEBUG_DEST("BinExpr", "@@@");
           delete children_[0];
           delete children_[1];

           children_[0] = nullptr;
           children_[1] = nullptr;
        }

        std::string Explain(void* arg = nullptr) const override
        {
            bool addParen = false;
            switch (op_) {
                case BinOp::Add:
                case BinOp::Sub:
                case BinOp::Or:
                    addParen = true;
            }

            std::string ret;

            if (addParen) ret += "(";
            ret += child(0)->Explain() + Expr::ExplainBinOp(op_) + child(1)->Explain();
            if (addParen) ret += ")";

            return ret;
        }

        void Bind(Binder* context) override;

        void bindFunction();
    };

        // represents a base table reference or a derived table
    class TableRef : public UseCurrentResource
    {
        public:
        ClassTag classTag_;
        std::string* alias_;
        TableDef* tabDef_;
        std::vector<Expr*> columnRefs_;

        TableRef(std::string* alias)
            : classTag_(TableRef_)
            , alias_(alias ? new std::string(*alias) : nullptr)
            , tabDef_(nullptr)
        {
            std::cerr << "MEMDEBUG: " << __FILE__ << ":" << __LINE__
                      << ": NEW TableRef : " << (void*)this << " : "
                      << (alias_ ? alias_->c_str() : "NULL") << std::endl;
           DEBUG_CONS("TableRef", "alias");
            SetColumnRefs();
        }

        TableRef(ClassTag classTag, std::string* alias)
            : classTag_(classTag)
            , alias_(alias ? new std::string(*alias) : nullptr)
            , tabDef_(nullptr)
        {
            std::cerr << "MEMDEBUG: " << __FILE__ << ":" << __LINE__
                      << ": NEW TableRef : " << (void*)this << " : "
                      << (alias_ ? alias_->c_str() : "NULL") << std::endl;
           DEBUG_CONS("TableRef", "ctag");
            SetColumnRefs();
        }

        TableRef(ClassTag classTag, const char* alias)
            : classTag_(classTag), alias_(alias ? new std::string(alias) : nullptr)
            , tabDef_(nullptr)
        {
            std::cerr << "MEMDEBUG: " << __FILE__ << ":" << __LINE__
                      << ": NEW TableRef : " << (void*)this << " : "
                      << (alias_ ? alias_->c_str() : "NULL") << std::endl;
           DEBUG_CONS("TableRef", "ctag alias");
            SetColumnRefs();
        }

        TableRef(ClassTag classTag, const char* alias, TableDef* tdef)
            : classTag_(classTag), alias_(new std::string(alias))
        {
            std::cerr << "MEMDEBUG: " << __FILE__ << ":" << __LINE__
                      << ": NEW TableRef : " << (void*)this << " : "
                      << (alias_ ? alias_->c_str() : "NULL") << std::endl;
            DEBUG_CONS("TableRef", "chr tdef");
            tabDef_ = tdef->Clone();
            SetColumnRefs();
        }

        TableRef(ClassTag classTag, std::string* alias, TableDef* tdef)
            : TableRef(classTag, alias->c_str(), tdef)
        {
            std::cerr << "MEMDEBUG: " << __FILE__ << ":" << __LINE__
                      << ": NEW TableRef : " << (void*)this << " : "
                      << (alias_ ? alias_->c_str() : "NULL") << std::endl;
           DEBUG_CONS("TableRef", "str tdef");
           SetColumnRefs();
        }

        std::string* getAlias() const
        {
            return alias_;
        }

        Expr* findColumn(std::string* colname)
        {
            // all columns in the scope are not yet available
            for (auto e : columnRefs_) {
                std::string* n = e->alias_ ? e->alias_ : static_cast<ColExpr*>(e)->colname_;
                if (!n->compare(*colname)) return e;
            }

            return nullptr;
        }

        virtual TableRef* Clone()
        {
            TableRef* tr = new TableRef(alias_);
            std::cerr << "MEMDEBUG: " << __FILE__ << ":" << __LINE__ << ": NEW TableRef : " << (void*)tr << " : " << (tr->alias_ ? tr->alias_->c_str() : "NULL") << std::endl;
            tr->tabDef_  = new TableDef(*tabDef_);
            std::cerr << "MEMDEBUG: " << __FILE__ << ":" << __LINE__ << ": NEW TableDef : " << (void*)tr->tabDef_ << " : " << *(tr->tabDef_->name_) << std::endl;

            return tr;
        }

        virtual ~TableRef()
        {
            DEBUG_DEST("TableRef", columnRefs_.size());
            std::cerr << "MEMDEBUG: " << __FILE__ << ":" << __LINE__ << ": DEL TableRef : " << (void*)this << " : " << (alias_ ? alias_->c_str() : "NULL") << std::endl;
            delete alias_;
            alias_ = nullptr;

            for (int i = 0; i < columnRefs_.size(); ++i)
                delete columnRefs_[i];

            columnRefs_.clear();

            std::cerr << "MEMDEBUG: " << __FILE__ << ":" << __LINE__ << ": DEL TableDef : " << (void*)tabDef_ << " : " << (tabDef_ ? tabDef_->name_->c_str() : "NULL") << std::endl;
            delete tabDef_;
            tabDef_ = nullptr;
        }

        virtual std::string Explain(void* arg = nullptr) const
        {
            return {};
        }

        private:
        void SetColumnRefs()
        {
            if (tabDef_ && columnRefs_.empty()) {
                for (auto d : *tabDef_->columns_) {
                    std::string* tname = tabDef_->name_;
                    ColumnDef* cdef = d.second;
                    auto cref = new ColExpr(cdef->ordinal_, cdef->name_, tname, nullptr, cdef);
                    columnRefs_.emplace_back(std::move(cref));
                }
            }
        }
    };

    class BaseTableRef : public TableRef
    {
        public:
        std::string* tabName_;

        BaseTableRef(std::string* tabname, std::string* alias = nullptr)
            : TableRef(BaseTableRef_, alias), tabName_(new std::string(*tabname))
        {
           DEBUG_CONS("BaseTableRef(str)", tabname);
            if (!alias) {
                alias_ = new std::string(*tabName_);
            }
        }

        BaseTableRef(const char* tabname, const char* alias = nullptr)
            : TableRef(BaseTableRef_, alias)
            , tabName_(new std::string(tabname))
        {
           DEBUG_CONS("BaseTableRef(chr)", tabname);
            if (!alias) {
                alias_ = new std::string(*tabName_);
            }
        }

        TableRef* Clone() override
        {
            BaseTableRef* btrf = new BaseTableRef(tabName_, alias_);
            return btrf;
        }

        virtual ~BaseTableRef()
        {
           DEBUG_DEST("BaseTableRef", tabName_);
           delete tabName_;
           tabName_ = nullptr;
        }

        std::string Explain(void* arg = nullptr) const override
        {
            std::string ret = *tabName_;

            if (getAlias())
                ret += " " + *getAlias();

            return ret;
        }
    };

    class QueryRef : public TableRef
    {
        public:
        SelectStmt* query_;
        std::vector<std::string*>* colOutputNames_;

        QueryRef(SelectStmt* stmt, std::string* alias = nullptr,
                 std::vector<std::string*>* outputNames = nullptr)
            : TableRef(QueryRef_, alias), query_(stmt)
            , colOutputNames_(outputNames)
        {
        }
    };


    class ExprEval
    {
        Expr* expr_ = nullptr;
        Datum** pointer_ = nullptr;
        Datum* board_ = nullptr;

        public:
        std::pmr::deque<Expr*> queue_{currentResource_};

        public:
            // given an expression, enqueue all ops
        void Open(Expr* expr);
        Datum Exec(Row* l);
        void Close();
    };

    Expr* makeStar(std::string* alias = nullptr);
    Expr* makeOpBinary(Expr* left, BinOp op, Expr* right);
    Expr* makeNullLiteral();
    Expr* makeLiteral(const char* cval);
    Expr* makeLiteral(std::string* sval);
    Expr* makeLiteral(double dval);
    Expr* makeLiteral(int64_t ival);
    Expr* makeLiteral(bool bval);
    Expr* makeColumnRef(const char* cname, const char* alias = 0);
    Expr* makeColumnRef(std::string* cname, std::string* alias = 0);
    char* substr(const char* source, int from, int to);
}  // namespace andb
