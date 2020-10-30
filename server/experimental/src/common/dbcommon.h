#pragma once

#include "common.h"

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
    BaseTableRef_,
};

class TableRef : public UseCurrentResource {
public:
    ClassTag classTag_;
};

class BaseTableRef : public TableRef {
    char* tabname_;

public:
    BaseTableRef (char* tabname) {
        classTag_ = BaseTableRef_;
        tabname_ = tabname;
    }
};