#pragma once

#include "common/dbcommon.h"
#include "common/nodebase.h"
#include "parser/include/expr.h"

namespace andb {

class LogicNode : public RuntimeNodeT<LogicNode>
{
    public:
    ClassTag     classTag_ = UnInitialized_;
    virtual void Close() {}
};

class LogicAgg : public NodeBase<LogicNode, N1>
{
    public:
    explicit LogicAgg(LogicNode* child)
    {
        classTag_    = LogicAgg_;
        children_[0] = child;
    }
};

template<typename T>
class LogicGet : public NodeBase<LogicNode, N0>
{
    public:
    T*    tableref_;
    Expr* filter_ = nullptr;

    LogicGet(T* tab) { tableref_ = tab; }
    void AddFilter(Expr* filter) { filter_ = filter; }
};

class LogicScan : public LogicGet<BaseTableRef>
{
    public:
    int targetcnt_;

    public:
    explicit LogicScan(BaseTableRef* tab, int targetcnt)
      : LogicGet(tab)
    {
        classTag_  = LogicScan_;
        targetcnt_ = targetcnt;
    }

    BaseTableRef* getBaseTableRef() { return tableref_; }
};

class LogicJoin : public NodeBase<LogicNode, N2>
{
    public:
    explicit LogicJoin(LogicNode* l, LogicNode* r)
    {
        classTag_    = LogicJoin_;
        children_[0] = l;
        children_[1] = r;
    }
};
} // namespace andb
