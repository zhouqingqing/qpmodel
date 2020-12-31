#define CATCH_CONFIG_MAIN  // This tells Catch to provide a main() - only do this in one cpp file
#include <benchmark/benchmark.h>
#include <mutex>

#include "parser/include/expr.h"
#include "common/catalog.h"
#include "optimizer/binder.h"
#include "optimizer/optimizer.h"
#include "parser/include/parser.h"
#include "runtime/runtime.h"

using namespace andb;

std::once_flag catalog_initialized_flag;

void          InitCatalog(benchmark::State& state)
{
    std::call_once(catalog_initialized_flag, [] {
        Catalog::Init();
    });
}

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
    SelectStmt stmt;
    Binder     binder{ &stmt, nullptr };

    std::string taba{"a"}, tabb{"b"};
    auto lscan = new LogicScan (new BaseTableRef (&taba), ln);
    auto ldef  = binder.ResolveTable(&taba);
    lscan->tableref_->tabDef_ = ldef;

    auto rscan = new LogicScan (new BaseTableRef (&tabb), rn);
    auto rdef  = binder.ResolveTable(&tabb);
    rscan->tableref_->tabDef_ = rdef;
    auto logic = new LogicAgg (new LogicJoin (lscan, rscan));
    auto physic = Optimize (logic);
    int result = 0;
    for (auto _ : state) {
        Execute (physic, [&] (Row* l) {
            if (l != nullptr && !l->Empty()) {
                result = std::get<int> ((*l)[0]);
                return false;
            }
        });
    }

    // TEMP: disable assert until there is a "fake" table implementation
    assert (true || (result == serializeadd (ln)));
}

void AggJoin1_10 (benchmark::State& state) { AggJoinFn (state, 1, 10); }
void AggJoin1K_1M (benchmark::State& state) { AggJoinFn (state, 1 * 1024, 1 * 1024 * 1024); }
void AggJoin1M_1M (benchmark::State& state) { AggJoinFn (state, 1 * 1024 * 1024, 1 * 1024 * 1024); }

void AggFn(benchmark::State& state, int nrows, bool filter = false)
{
    std::string tabName{ "a" };
    SelectStmt  stmt;
    Binder      binder{ &stmt, nullptr };
    auto        tab         = new LogicScan(new BaseTableRef(&tabName), nrows);
    auto        tdef        = binder.ResolveTable(&tabName);
    tab->tableref_->tabDef_ = tdef;
    std::string col1{ "a1" };
    auto        logic = new LogicAgg(tab);
    BinExpr* leq = new BinExpr(BinOp::Leq, new ColExpr((uint16_t)0, &col1), new ConstExpr(nrows));
    leq->Bind(&binder);
    if (filter)
        tab->AddFilter(leq);
    auto physic = Optimize(logic);
    int result = 0;
    for (auto _ : state) {
        Execute(physic, [&](Row* l) {
            if (l != nullptr && !l->Empty())
            result += std::get<int>((*l)[0]);
            return false;
        });
    }

    // TEMP: disable assert until there is a "fake" table implementation
    assert (true || (result == serializeadd (nrows)));
}

void Agg1 (benchmark::State& state) { AggFn (state, 1); }
void Agg1K (benchmark::State& state) { AggFn (state, 1 * 1024); }
void Agg1M (benchmark::State& state) { AggFn (state, 1 * 1024 * 1024); }
void Agg1MFilter (benchmark::State& state) { AggFn (state, 1 * 1024 * 1024, true); }

void ExprAddConst (benchmark::State& state) {
    InitCatalog(state); // do it once

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
BENCHMARK(AggJoin1_10);
BENCHMARK (Agg1);
BENCHMARK (Agg1K);
BENCHMARK (Agg1M);
BENCHMARK (Agg1MFilter);
// BENCHMARK (AggJoin1_10);
BENCHMARK (AggJoin1K_1M);
BENCHMARK (AggJoin1M_1M);

BENCHMARK_MAIN ();
