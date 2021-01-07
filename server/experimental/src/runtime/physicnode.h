#pragma once
#include <functional>

#include "common/dbcommon.h"
#include "common/nodebase.h"
#include "parser/include/logicnode.h"
#include "runtime/datum.h"

namespace andb {
class ExecContext
{};
class Row;
class PhysicNode;

// callback takes a row as input and return true if it owns the row
//
using ExecCallback = std::function<bool(Row*)>;
using ExecFunction = void (*)(PhysicNode*, ExecCallback&& callback);

struct DispatchTableEntry
{
    ClassTag     tag_;
    const char*  name_;
    ExecFunction fn_;

    static inline const DispatchTableEntry& get(ClassTag tag);
};
extern const DispatchTableEntry ExecDispatchTable_[];

inline const DispatchTableEntry& DispatchTableEntry::get(ClassTag tag)
{
    return ExecDispatchTable_[tag - PhysicStart__ - 1];
}

class PhysicNode : public RuntimeNodeT<PhysicNode>
{
    protected:
    using base_type = PhysicNode;

    // execution context is created before query execution, passed in through Open()
    // interface, and propagate to the whole execution tree. So every physical node in the
    // tree shared among execution tree nodes
    //
    ExecContext* context_ = nullptr;
    LogicNode*   logic_   = nullptr;

    public:
    ClassTag classTag_ = ClassTag::UnInitialized_;

    virtual ~PhysicNode() = default;

    virtual void Open(ExecContext* context)
    {
        assert(context_ == nullptr);
        context_       = context;
        auto nchildren = childrenCount();
        for (int i = 0; i < nchildren; i++)
            child(i)->Open(context);
    }

    // This is the working horse for virtual Exec() function in the hope that compiler may
    // support template virtual function (at least template for lambda function).
    //  1. children are runtime dynamics, so we can't avoid indirect call but at least we
    //  can avoid virtual function overhead.
    //  2. we use lambda for the ease of programming and the best way to persuade inlining
    // is to use lambda function as a template argument, but unfortunately virtual function
    // does not allow template format.
    //
    // See more comments in PhysicNLJoin.
    //
    template<typename Fn>
    inline void ExecT([[maybe_unused]] Fn&& callback)
    {
        throw NotImplementedException();
    }
    virtual void Exec(ExecCallback&& callback) { ExecT(callback); }

    virtual void Close()
    {
        assert(context_ != nullptr);
        auto nchildren = childrenCount();
        for (int i = 0; i < nchildren; i++)
            child(i)->Close();
        DBG_ONLY(context_ = nullptr);
    }

    std::string Explain(void* arg = nullptr) override
    {
        std::string str{};
        deepVisitParentChild([&](PhysicNode* parent, int level, int nth, PhysicNode* cur) {
            // format: <name> [details] <est cost> [<actual cost>]
            //
            str += "\n" + std::string(level * 2, ' ');
            str += DispatchTableEntry::get(cur->classTag_).name_;
        });
        return str;
    }
};

class PhysicAgg : public NodeBase<PhysicNode, N1>
{
    public:
    explicit PhysicAgg(LogicNode* logic, PhysicNode* child)
    {
        logic_       = logic;
        classTag_    = PhysicAgg_;
        children_[0] = child;
    }

    template<typename Fn>
    void ExecT(Fn&& callback);
    void Exec(ExecCallback&& callback) override { ExecT(callback); }
};

class PhysicScan : public NodeBase<PhysicNode, N0>
{
    ExprEval eval_;

    public:
    explicit PhysicScan(LogicScan* logic)
    {
        logic_    = logic;
        classTag_ = PhysicScan_;
    }

    void Open(ExecContext* context) override;
    void Close() override;
    template<typename Fn>
    void                     ExecT(Fn&& callback);
    void                     Exec(ExecCallback&& callback) override { ExecT(callback); }
    const std::vector<Row*>* getSourceReader(int distId = 0) const;
    std::vector<Row*>*       getSourceWriter(int distId = 0);
};

class PhysicHashJoin : public NodeBase<PhysicNode, N2>
{
    public:
    explicit PhysicHashJoin(LogicNode* logic, PhysicNode* l, PhysicNode* r)
    {
        logic_       = logic;
        classTag_    = PhysicHashJoin_;
        children_[0] = l;
        children_[1] = r;
    }

    template<typename Fn>
    void ExecT(Fn&& callback);
    void Exec(ExecCallback&& callback) override { ExecT(callback); }
};

#include "physicnode.inl"
} // namespace andb
