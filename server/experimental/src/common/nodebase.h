#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "common.h"

enum NChildren : uint8_t { N0 = 0, N1, N2, NFixed = UINT8_MAX - 1, NDynamic = UINT8_MAX };

enum TraOrder : uint8_t {
    PreOrder,
    PostOrder
    // there is no InOrder as it is a multi-tree
};

namespace details {
// Node class is design to be storage and cpu efficient.
//
// Storage:
//   N0, N1, N2 are common types and they use std::array instead of std::vector to avoid overhead.
//   We also further specialize N0 to totally exclude children_ member.
//
// Performance:
//   1. Expr call (eg., "Expr* a; a->child(1)") has one branching overhead to determine if it is a
//   fixed node or dynamic one. Here we introduced NFixed to include N0, N1 and N2 as they share the
//   same binary code as they operate against same array data structure.
//   2. Expr subclass call (eg., "BinExpr* a; a->child(1)") shall have zero overhead. In the
//   example, child(0) shall be directly mapped to children_[0] without going through Expr::child()
//   interface. This is why Node class and ExprNode class having exactly the same function name.
//
//   This class shall only provide the minimal basic functions for sub-class.
//
template <class T, NChildren NC>
struct FixedStorage {
    T* children_[NC];
};

template <class T>
struct DynamicStorage {
    std::pmr::vector<T*> children_{currentResource_};
};



template <class T, NChildren NC>
class NodeBase : public T,
                 public std::conditional_t<NC == NDynamic, DynamicStorage<T>, FixedStorage<T, NC>> {
public:
    NodeBase () { nChildren_ = NC; }

    constexpr inline T* child (int n) const {
        assert (NC != N0);
        return children_[n];
    }

    constexpr inline size_t childrenCount () const {
        if constexpr (NC != NDynamic)
            return nChildren_;
        else
            return children_.size ();
    }

    template <TraOrder Order = TraOrder::PreOrder, typename Fn>
    int deepVisit (Fn&& callback) {
        if (Order == TraOrder::PreOrder) callback ((T*)this);
        // we can simply use child(i) for all cases
        if constexpr (NC != NDynamic) {
            int n = childrenCount ();
            for (int i = 0; i < n; i++) child (i)->deepVisit<Order> (callback);
        } else {
            for (auto v : children_) {
                v->deepVisit<Order> (callback);
            }
        }
        if (Order == TraOrder::PostOrder) callback ((T*)this);
        return 0;
    }

    template <TraOrder Order = TraOrder::PreOrder, typename Fn>
    void deepVisitParentChild (T* parent, int level, int nth, Fn&& callback) {
        auto This = (T*)this;
        if (Order == TraOrder::PreOrder) callback (parent, level, nth, This);
        nth = 0;
        level++;
        if constexpr (NC != NDynamic) {
            int n = childrenCount ();
            for (int i = 0; i < n; i++)
                child (i)->template deepVisitParentChild<Order> (This, level, nth++, callback);
        } else {
            for (auto v : children_) {
                v->template deepVisitParentChild<Order> (This, level, nth++, callback);
            }
        }
        if (Order == TraOrder::PostOrder) callback (parent, level, nth, This);
    }

    int childrenOrderedHash () const {
        int hash = 0;
        auto nchildren = childrenCount ();
        for (int i = 0; i < nchildren; i++)
            hash ^= child (i)->GetHashCode () + 0x9e3779b9;  // boost magic number
        return hash;
    }

    bool childrenEqual (const NodeBase<T, NC>* r) const {
        assert (classTag_ == r->classTag_);
        auto nchildren = r->childrenCount ();
        // caller shall already verify the basics
        assert (childrenCount () == nchildren && nchildren > 0);
        for (int i = 0; i < nchildren; i++)
            if (!child (i)->Equals (r->child (i))) return false;
        return true;
    }

    T* childrenClone (NodeBase<T, NC>* r) const {
        assert (classTag_ == r->classTag_);
        auto nchildren = childrenCount ();
        auto& newchildren = r->children_;
        if constexpr (NC != NDynamic) {
            for (int i = 0; i < nchildren; i++) newchildren[i] = child (i)->Clone ();
        } else {
            for (int i = 0; i < nchildren; i++) newchildren.emplace_back (child (i)->Clone ());
        }

        assert (childrenEqual (r));
        return r;
    }
};

// specialize N0 class to remove children_ storage. We don't specialize all of its functions as the
// only time they are faster than the ExprNode's implementation is that type is known at compile
// time. However, these functions (child(), childcount(), traverse(), etc) shall be rarely used for
// compile time for N0.
//
template <class T>
class NodeBase<T, NChildren::N0> : public T {
public:
    NodeBase () { nChildren_ = N0; }
};

// RuntimeNodeT is served as a runtime shared implementation for ExprNode and LogicNode etc, so they
// won't duplicate code. It turns size class into a runtime parameter to enable NodeBase to support
// runtime polymorphisms (i.e., accessing children_).
//
template <class T>
class RuntimeNodeT : public UseCurrentResource {
#define RUNTIME_DISPATCH(_fn, ...)                                                        \
    (nChildren_ != NChildren::NDynamic) ? ((NodeBase<T, NFixed>*)this)->_fn (__VA_ARGS__) \
                                        : ((NodeBase<T, NDynamic>*)this)->_fn (__VA_ARGS__)
#define RUNTIME_DISPATCH_T1(_fn, _T1, ...)                              \
    (nChildren_ != NChildren::NDynamic)                                 \
        ? ((NodeBase<T, NFixed>*)this)->template _fn<_T1> (__VA_ARGS__) \
        : ((NodeBase<T, NDynamic>*)this)->template _fn<_T1> (__VA_ARGS__)

    // NodeBase will callback RuntimeNodeT for runtime dispatch
    template <class T, NChildren NC>
    friend class NodeBase;

    template <TraOrder Order = TraOrder::PreOrder, typename Fn>
    void deepVisitParentChild (T* parent, int level, int nth, Fn&& callback) {
        RUNTIME_DISPATCH_T1 (deepVisitParentChild, Order, parent, level, nth, callback);
    }

public:
    NChildren nChildren_;

    constexpr inline int childrenCount () const { return RUNTIME_DISPATCH (childrenCount); }
    constexpr inline T* child (int n) const { return RUNTIME_DISPATCH (child, n); }
    bool childrenEqual (T* r) const { return RUNTIME_DISPATCH (childrenEqual, r); }
    T* childrenClone (T* r) const { return RUNTIME_DISPATCH (childrenClone, r); }
    int childrenOrderedHash () const { return RUNTIME_DISPATCH (childrenOrderedHash); }

    template <TraOrder Order = TraOrder::PreOrder, typename Fn>
    int deepVisit (Fn&& callback) {
        return RUNTIME_DISPATCH_T1 (deepVisit, Order, callback);
    }

    // traversal with callback(parent, level, nth, child) where child is parent's nth child
    template <TraOrder Order = TraOrder::PreOrder, typename Fn>
    void deepVisitParentChild (Fn&& callback) {
        deepVisitParentChild<Order> ((T*)this, 0, -1, callback);
    }

    inline bool Equals (T* r) const {
        if (classTag_ != r->classTag_) return false;
        return doEquals (r);
    }
    inline int GetHashCode () { return doHashCode () ^ childrenOrderedHash (); }

    virtual std::string Explain (void* arg = nullptr) { throw NotImplementedException (); }
    virtual T* Clone () const { throw NotImplementedException (); }

protected:
    virtual bool doEquals (T* r) const { throw NotImplementedException (); }
    virtual bool doHashCode () const { throw NotImplementedException (); }
};
}  // namespace details

template <class T>
using RuntimeNodeT = details::RuntimeNodeT<T>;
template <class T, NChildren NC>
using NodeBase = details::NodeBase<T, NC>;
