#define CATCH_CONFIG_MAIN  // This tells Catch to provide a main() - only do this in one cpp file
#include <benchmark/benchmark.h>

#include "parser/include/expr.h"
#include "common/catalog.h"
#include "optimizer/binder.h"
#include "optimizer/optimizer.h"
#include "parser/include/parser.h"
#include "runtime/runtime.h"

using namespace andb;

constexpr int serializeadd (int n) {
    int r = 0;
    for (int i = 0; i < n; i++) r += i;
    return r;
}

// callback as std::function, Exec() as virtual function.
// this one is very easy to write as we don't have to maintain the state
// machine. The problem is std::function's overhead and we hope compiler shall
// get better on it.
//

void AggJoinFn (benchmark::State& state, int ln, int rn) {
    auto logic = new LogicAgg (new LogicJoin (new LogicScan (new BaseTableRef ("a"), ln),
                                              new LogicScan (new BaseTableRef ("b"), rn)));
    auto physic = Optimize (logic);
    int result = 0;
    for (auto _ : state) {
        Execute (physic, [&] (Row* l) {
            result = std::get<int> ((*l)[0]);
            return false;
        });
    }
    assert (result == serializeadd (ln));
}

void AggJoin1_10 (benchmark::State& state) { AggJoinFn (state, 1, 10); }
void AggJoin1K_1M (benchmark::State& state) { AggJoinFn (state, 1 * 1024, 1 * 1024 * 1024); }
void AggJoin1M_1M (benchmark::State& state) { AggJoinFn (state, 1 * 1024 * 1024, 1 * 1024 * 1024); }

void AggFn (benchmark::State& state, int nrows, bool filter = false) {
    std::string tabName{ "a" };
    Catalog::Init();
    SelectStmt stmt;
    Binder     binder{ &stmt, nullptr };
    auto tab = new LogicScan (new BaseTableRef (&tabName), nrows);
    binder.ResolveTable(&tabName);
    std::string col1{ "a1" };
    auto logic = new LogicAgg (tab);
    BinExpr *leq = new BinExpr (BinOp::Leq, new ColExpr ((uint16_t)0, &col1), new ConstExpr (nrows));
    leq->Bind(&binder);
    if (filter)
        tab->AddFilter(leq);
    auto physic = Optimize (logic);
    int result = 0;
    for (auto _ : state) {
        Execute (physic, [&] (Row* l) {
            result = std::get<int> ((*l)[0]);
            return false;
        });
    }
    assert (result == serializeadd (nrows));
}

void Agg1 (benchmark::State& state) { AggFn (state, 1); }
void Agg1K (benchmark::State& state) { AggFn (state, 1 * 1024); }
void Agg1M (benchmark::State& state) { AggFn (state, 1 * 1024 * 1024); }
void Agg1MFilter (benchmark::State& state) { AggFn (state, 1 * 1024 * 1024, true); }

void ExprAddConst (benchmark::State& state) {
    auto expr = new BinExpr (BinOp::Add, new ConstExpr (2), new ConstExpr (7));
    SelectStmt stmt{};
    Binder       binder{ &stmt, nullptr };
    expr->Bind(&binder);
    ExprEval eval;
    eval.Open (expr);
    for (auto _ : state) {
        eval.Exec (nullptr);
    }
    eval.Close ();
}

void ExprAddRow (benchmark::State& state) {
    Catalog::Init();
    SelectStmt stmt{};
    Binder     binder{ &stmt, nullptr };
    Row r (2);
    r[0] = 2;
    r[1] = 7;
    std::string col1{ "a1" }, col2{ "a2" }, tab{ "a" };
    binder.ResolveTable(&tab);
    auto expr = new BinExpr (BinOp::Add, new ColExpr ((uint16_t)0, &col1, &tab), new ColExpr (1, &col2, &tab));

    expr->Bind(&binder);
    ExprEval eval;
    eval.Open (expr);
    for (auto _ : state) {
        eval.Exec (&r);
    }
    eval.Close ();
}

BENCHMARK (ExprAddConst);
BENCHMARK (ExprAddRow);
BENCHMARK (Agg1);
BENCHMARK (Agg1K);
BENCHMARK (Agg1M);
BENCHMARK (Agg1MFilter);
BENCHMARK (AggJoin1_10);
BENCHMARK (AggJoin1K_1M);
BENCHMARK (AggJoin1M_1M);

BENCHMARK_MAIN ();