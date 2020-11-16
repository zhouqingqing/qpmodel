#pragma once

#include <deque>
#include <string>

#include "common/dbcommon.h"
#include "common/nodebase.h"
#include "runtime/datum.h"

namespace andb {

class BinExpr;
class SelStar;
class ConstExpr;
class ColExpr;

class BindContext {};

enum BinOp : uint16_t { Add = 0, Sub, Mul, Div, Equal, Neq, Less, Leq, Great, Geq, And, Or };

class Expr : public RuntimeNodeT<Expr>
{
   protected:
      using base_type = Expr;

   public:
      ClassTag    classTag_;
      DataType    type_;
      std::string *alias_;

      // evaluation support
      uint32_t    slot_;

      int          ival; // SQLParserResult uses this.
      Expr () : RuntimeNodeT<Expr> (), classTag_ (Expr_), type_ (D_NullFlag), alias_ (nullptr), slot_(0), ival(0) {}

      Expr (DataType type,std::string *alias = 0)
          : RuntimeNodeT<Expr> ()
          , classTag_ (Expr_)
          , type_ (type)
          , alias_ (alias)
          , slot_ (0)
          , ival (0)
      {}

      virtual std::string Explain (void* arg = nullptr) const { return {}; }
      virtual void Bind (BindContext& context) {
         auto nchildren = childrenCount ();
         for (int i = 0; i < nchildren; i++) child (i)->Bind (context);
      };

};

class SelStar : public NodeBase<Expr, N0> {
   public:
      std::string *tabAlias_;

      SelStar(std::string *alias = nullptr)
         : tabAlias_(alias)
      {
      }
};


class ConstExpr : public NodeBase<Expr, N0> {
public:
    Datum value_;

    explicit ConstExpr (Datum value) {
        assert (value_ == Datum{});
        classTag_ = ConstExpr_;
        value_ = value;
        type_ = (DataType)value.index ();
    }
};

class ColExpr : public NodeBase<Expr, N0> {
public:
    uint16_t    ordinal_;
    std::string *colname_;

    explicit ColExpr (uint16_t ordinal) {
        classTag_ = ColExpr_;
        ordinal_ = ordinal;
        colname_ = nullptr;
    };
    explicit ColExpr (char *colname) {
        classTag_ = ColExpr_;
        ordinal_ = UINT16_MAX;
        colname_ = new std::string(colname);
    };
    explicit ColExpr (std::string *colname) {
        classTag_ = ColExpr_;
        ordinal_ = UINT16_MAX;
        colname_ = new std::string(*colname);
    };
    void Bind (BindContext& context) { type_ = Int32; }
};

class BinExpr : public NodeBase<Expr, N2> {
public:
    using BinFunction = Datum (*) (Datum* l, Datum* r);

    BinOp op_;
    BinFunction fn_;

    explicit BinExpr (BinOp op, Expr* l, Expr* r) {
        classTag_ = BinExpr_;
        op_ = op;
        children_[0] = l;
        children_[1] = r;
    }

    void Bind (BindContext& context) {
        Expr::Bind (context);
        bindFunction ();
    }

    void bindFunction ();
};

class ExprEval {
    Expr* expr_ = nullptr;
    Datum** pointer_ = nullptr;
    Datum* board_ = nullptr;

public:
    std::pmr::deque<Expr*> queue_{currentResource_};

public:
    // given an expression, enqueue all ops
    void Open (Expr* expr);
    Datum Exec (Row* l);
    void Close ();
};

Expr *makeStar (std::string* alias = nullptr);
Expr *makeOpBinary (Expr* left, BinOp op, Expr* right);
Expr *makeNullLiteral ();
Expr *makeLiteral (const char *cval);
Expr *makeLiteral(std::string *sval);
Expr *makeLiteral(double dval);
Expr *makeLiteral(int64_t ival);
Expr *makeLiteral(bool bval);
Expr *makeColumnRef(const char *cname, const char *alias = 0);
Expr *makeColumnRef(std::string *cname, std::string *alias = 0);
char* substr (const char* source, int from, int to);

}  // namespace andb
